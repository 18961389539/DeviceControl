using System.Collections.Concurrent;
using System.Diagnostics;

namespace DeviceControl.Api;

/// <summary>Simulation-only workflow scheduler. Locking is for logical process resources, NOT safety.</summary>
public sealed class FlowEngine
{
    private readonly object _sync = new();
    private readonly string _version;
    private SharedResourceCoordinator? _shared;
    private string _stationId = "A";
    public void AttachSharedCoordinator(SharedResourceCoordinator coordinator, string stationId)
    { lock (_sync) { if (_status != "Idle") throw new InvalidOperationException("流程运行中不可变更资源调度器"); _shared=coordinator; _stationId=stationId; } }
    private readonly IReadOnlyList<SequenceStep> _sequence;
    private readonly HashSet<int> _breakpoints = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutexes = new();
    private readonly Dictionary<string, string> _owners = new();
    private readonly List<FlowTrace> _traces = new();
    private readonly HashSet<string> _active = new();
    private TaskCompletionSource<string>? _gate;
    private string? _waitKind, _waitMessage;
    private int? _waitIndex;
    private string _status = "Idle";
    private bool _pauseRequested, _stepMode;
    private Func<string,CancellationToken,Task<bool>> _condition = (_,_) => Task.FromResult(false);

    public FlowEngine(StationConfig config) : this(config.Sequence,config.FlowVersion) {}
    public FlowEngine(IReadOnlyList<SequenceStep> sequence,string version)
    { _version = version; _sequence = sequence; }

    public object Snapshot()
    {
        lock (_sync) return new
        {
            version = _version, status = _status, breakpoints = _breakpoints.Order().ToArray(),
            active = _active.ToArray(), traces = _traces.ToArray(),
            resources = new Dictionary<string, string>(_owners),
            waiting = _gate is null ? null : new { kind = _waitKind, index = _waitIndex, message = _waitMessage },
            nodes = Flatten(_sequence).ToArray()
        };
    }
    // Immutable end-of-node evidence; no variable values are invented here.
    public IReadOnlyList<FlowTrace> CompletedTraces()
    {
        lock (_sync) return _traces.ToArray();
    }
    private static IEnumerable<object> Flatten(IEnumerable<SequenceStep> steps)
    {
        foreach (var node in steps)
        {
            yield return new { id = node.Id, label = node.Label, type = node.Type,
                resources = Resources(node), sharedResources = SharedResourceCoordinator.KeysFor(node), children = node.Children.Select(c => c.Id).ToArray() };
            foreach (var sub in Flatten(node.Children)) yield return sub;
        }
    }
    private static string[] Resources(SequenceStep node) => node.Type switch
    {
        "AxisMove" => ["Axis:" + node.Device],
        "CylinderExtend" or "CylinderRetract" => ["Cylinder:" + node.Device],
        "VisionLocate" => ["Camera:Main"],
        "Parallel" or "Condition" => node.Children.SelectMany(Resources).Distinct().Order().ToArray(),
        _ => []
    };
    public object Command(FlowDebugRequest request)
    {
        lock (_sync)
        {
            switch (request.Command)
            {
                case "breakpoint":
                    if (request.Index is null || request.Index < 0 || request.Index >= _sequence.Count)
                        throw new ArgumentException("断点序号不合法");
                    if (request.Enabled) _breakpoints.Add(request.Index.Value);
                    else _breakpoints.Remove(request.Index.Value);
                    break;
                case "pause":
                    if (_status != "Running") throw new InvalidOperationException("当前流程不可暂停");
                    _pauseRequested = true;
                    break;
                case "resume":
                case "step":
                    if (_status != "Paused" || _gate == null || _waitKind != "breakpoint")
                        throw new InvalidOperationException("当前没有可继续的断点");
                    _stepMode = request.Command == "step";
                    _pauseRequested = false;
                    ReleaseGate("continue");
                    break;
                case "retry":
                case "abort":
                    if (_status != "Paused" || _gate == null || _waitKind != "error")
                        throw new InvalidOperationException("当前没有待人工处理的异常");
                    ReleaseGate(request.Command);
                    break;
                default: throw new ArgumentException("未知流程调试操作");
            }
        }
        return Snapshot();
    }
    private void ReleaseGate(string action)
    {
        // Called under _sync; RunContinuationsAsynchronously avoids an inline reentrancy.
        var gate = _gate;
        _gate = null; _waitKind = null; _waitMessage = null; _waitIndex = null; _status = "Running";
        gate?.TrySetResult(action);
    }
    public void Stop()
    {
        lock (_sync) if (_gate != null) ReleaseGate("abort");
    }
    private async Task<string> PauseAsync(string kind, int index, string message, CancellationToken ct)
    {
        Task<string> waiting;
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (_gate is not null) throw new InvalidOperationException("已有暂停中的流程");
            _status = "Paused"; _waitKind = kind; _waitIndex = index; _waitMessage = message;
            _gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting = _gate.Task;
        }
        return await waiting.WaitAsync(ct);
    }
    private async Task BoundaryAsync(int index, SequenceStep node, CancellationToken ct)
    {
        bool stop;
        lock (_sync)
        {
            stop = _pauseRequested || _stepMode || _breakpoints.Contains(index);
            if (stop) { _stepMode = false; _pauseRequested = false; }
        }
        if (!stop) return;
        var outcome = await PauseAsync("breakpoint", index, node.Label, ct);
        if (outcome != "continue") throw new OperationCanceledException(ct);
    }
    private async Task ExecuteNodeAsync(SequenceStep node, int index, string? parent,
        Func<SequenceStep,CancellationToken,Task> execute,
        Action<int,SequenceStep,string,long,string?,int> complete, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        string status = "Completed"; string? error = null; var attempt = 0;
        lock (_sync) _active.Add(node.Id);
        try
        {
            if (node.Type == "Condition")
            {
                // This predicate is evaluated once at entry; no implicit spinning loops.
                var ok = await _condition(node.Device,ct);
                await ExecuteNodeAsync(node.Children[ok?0:1],index,node.Id,execute,complete,ct);
            }
            else if (node.Type == "Parallel")
            {
                using var childrenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tasks = node.Children.Select(async child =>
                {
                    try { await ExecuteNodeAsync(child, index, node.Id, execute, complete, childrenCts.Token); }
                    catch { await childrenCts.CancelAsync(); throw; }
                }).ToArray();
                await Task.WhenAll(tasks);
            }
            else
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested(); attempt++;
                    var keys = Resources(node);
                    var acquired = new List<(string, SemaphoreSlim)>();
                    SharedResourceCoordinator.Lease? sharedLease = null;
                    try
                    {
                        var sharedKeys = SharedResourceCoordinator.KeysFor(node);
                        if (_shared != null && sharedKeys.Length > 0)
                            sharedLease = await _shared.AcquireAsync(_stationId,node.Id,sharedKeys,node.ResourceWaitTimeoutMs,ct);
                        foreach (var key in keys)
                        {
                            var mutex = _mutexes.GetOrAdd(key, _ => new SemaphoreSlim(1,1));
                            await mutex.WaitAsync(ct);
                            acquired.Add((key, mutex));
                            lock (_sync) _owners[key] = node.Id;
                        }
                        await execute(node, ct);
                        break;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException && ex is not SharedResourceTimeoutException)
                    {
                        if (node.OnError == "Retry" && attempt <= node.MaxRetries) continue;
                        if (node.OnError == "WaitForOperator")
                        {
                            var decision = await PauseAsync("error", index, ex.Message, ct);
                            if (decision == "retry") continue;
                            throw new OperationCanceledException(ct);
                        }
                        throw;
                    }
                    finally
                    {
                        foreach (var (key, sem) in acquired.AsEnumerable().Reverse())
                        { lock (_sync) _owners.Remove(key); sem.Release(); }
                        sharedLease?.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        { status = ct.IsCancellationRequested || ex is OperationCanceledException ? "Canceled" : "Failed"; error = ex.Message; throw; }
        finally
        {
            timer.Stop();
            int traceOrdinal;
            lock (_sync)
            {
                _active.Remove(node.Id);
                _traces.Add(new FlowTrace(node.Id, parent, node.Label, node.Type, index, attempt, status,
                    timer.ElapsedMilliseconds, error, DateTimeOffset.UtcNow.ToString("O")));
                traceOrdinal = _traces.Count - 1;
                if (_traces.Count > 500) throw new InvalidOperationException("流程轨迹超过安全上限，拒绝丢失历史");
            }
            complete(index,node,status,timer.ElapsedMilliseconds,error,traceOrdinal);
        }
    }
    public async Task RunAsync(Func<SequenceStep,CancellationToken,Task> execute,
        Action<int,SequenceStep,string,long,string?,int> complete,
        Action<int,SequenceStep> started, CancellationToken ct,
        Func<string,CancellationToken,Task<bool>>? condition=null)
    {
        lock (_sync)
        {
            _condition=condition??((_,_)=>Task.FromResult(false));
            _status = "Running"; _active.Clear(); _traces.Clear(); _owners.Clear();
            _pauseRequested = false; _stepMode = false;
        }
        try
        {
            for (var index = 0; index < _sequence.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var node = _sequence[index];
                await BoundaryAsync(index,node,ct);
                ct.ThrowIfCancellationRequested(); started(index,node);
                await ExecuteNodeAsync(node,index,null,execute,complete,ct);
            }
        }
        finally
        {
            lock (_sync)
            {
                _status = "Idle";
                if (_gate != null) ReleaseGate("abort");
                _status = "Idle"; _active.Clear(); _owners.Clear();
            }
        }
    }
}
public record FlowTrace(string Id, string? ParentId, string Label, string Type, int Index, int Attempt,
    string Status, long DurationMs, string? Error, string EndedAt);
public record FlowDebugRequest(string Command, int? Index = null, bool Enabled = false);
