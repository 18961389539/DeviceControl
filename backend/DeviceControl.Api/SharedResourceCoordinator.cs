using System.Text.RegularExpressions;

namespace DeviceControl.Api;

/// <summary>
/// Simulation-only, process-local, atomic multi-key FIFO resource coordinator.
/// All keys for one request are granted together: no partial acquisition or circular hold-and-wait.
/// Lease.Dispose() is required in a finally path. Not a safety controller or distributed lock.
/// </summary>
public sealed class SharedResourceCoordinator
{
    internal sealed class Request
    {
        public required string Ticket { get; init; }
        public required string Station { get; init; }
        public required string Node { get; init; }
        public required string[] Keys { get; init; }
        public required DateTimeOffset Since { get; init; }
        public TaskCompletionSource<bool> Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Granted { get; set; }
    }
    private readonly object _gate = new();
    private readonly Queue<Request> _queue = new();
    private readonly Dictionary<string, Request> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<object> _events = new();
    private static readonly Regex KeyPattern = new("^[A-Z][A-Z0-9_-]{0,31}$", RegexOptions.Compiled);
    public static string[] CanonicalKeys(IEnumerable<string> keys)
    {
        var values = keys.Select(k => (k ?? "").Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order().ToArray();
        if (values.Length > 8 || values.Any(k => !KeyPattern.IsMatch(k)))
            throw new ArgumentException("共享资源名称仅允许 1-32 位大写字母/数字/_/-，单节点最多 8 个");
        return values;
    }
    public static string[] KeysFor(SequenceStep node)
    {
        // Protect persisted V1.1 flows that did not yet have sharedResources metadata.
        var configured = node.SharedResources ?? new List<string>();
        return CanonicalKeys(node.Type == "VisionLocate" ? configured.Append("CELL-SHARED") : configured);
    }
    private void AddEvent(string action, Request request)
    {
        _events.Enqueue(new { at = DateTimeOffset.UtcNow.ToString("O"), action,
            stationId = request.Station, nodeId = request.Node, ticket = request.Ticket, resources = request.Keys });
        while (_events.Count > 60) _events.Dequeue();
    }
    private void Dispatch()
    {
        // Strict FIFO, including head-of-line blocking. Never grant individual keys incrementally.
        while (_queue.Count > 0)
        {
            var request = _queue.Peek();
            if (request.Keys.Any(_held.ContainsKey)) break;
            _queue.Dequeue(); request.Granted = true;
            foreach (var key in request.Keys) _held.Add(key, request);
            AddEvent("granted", request);
            request.Signal.TrySetResult(true);
        }
    }
    private void Release(Request request)
    {
        lock (_gate)
        {
            if (!request.Granted) return;
            foreach (var key in request.Keys)
                if (_held.TryGetValue(key, out var holder) && ReferenceEquals(holder, request)) _held.Remove(key);
            request.Granted = false;
            AddEvent("released", request);
            Dispatch();
        }
    }
    public sealed class Lease : IDisposable
    {
        private SharedResourceCoordinator? _owner;
        private readonly Request _request;
        internal Lease(SharedResourceCoordinator owner, Request request) { _owner = owner; _request = request; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_request);
    }
    public async Task<Lease> AcquireAsync(string station, string node, IEnumerable<string> resources,
        int timeoutMs, CancellationToken ct)
    {
        var keys = CanonicalKeys(resources);
        if (keys.Length == 0) throw new ArgumentException("资源申请不能为空");
        if (timeoutMs is < 100 or > 30000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        ct.ThrowIfCancellationRequested();
        var request = new Request { Ticket = Guid.NewGuid().ToString("N")[..10], Station = station,
            Node = node, Keys = keys, Since = DateTimeOffset.UtcNow };
        lock (_gate)
        {
            _queue.Enqueue(request);
            AddEvent("queued", request);
            Dispatch();
        }
        try
        {
            await request.Signal.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
            ct.ThrowIfCancellationRequested();
            return new Lease(this, request);
        }
        catch
        {
            lock (_gate)
            {
                if (request.Granted)
                {
                    // Grant-vs-cancel race: release even if the caller never received its lease.
                    foreach (var key in request.Keys)
                        if (_held.TryGetValue(key, out var holder) && ReferenceEquals(holder, request)) _held.Remove(key);
                    request.Granted = false;
                    AddEvent("released-after-cancel", request);
                }
                else
                {
                    var pending = _queue.ToArray(); _queue.Clear();
                    foreach (var item in pending) if (!ReferenceEquals(item, request)) _queue.Enqueue(item);
                    AddEvent(ct.IsCancellationRequested ? "canceled" : "timed-out", request);
                }
                Dispatch();
            }
            if (!ct.IsCancellationRequested) throw new SharedResourceTimeoutException("资源等待超时：" + string.Join(",", keys));
            throw;
        }
    }
    public object Snapshot()
    {
        lock (_gate)
            return new { policy = "AtomicFIFO", mode = "Simulation", safetyRated = false,
                leases = _held.OrderBy(x => x.Key).Select(x => new { resource = x.Key, stationId = x.Value.Station,
                    nodeId = x.Value.Node, ticket = x.Value.Ticket, since = x.Value.Since.ToString("O") }).ToArray(),
                queue = _queue.Select((x,i) => new { position = i+1, stationId = x.Station, nodeId = x.Node,
                    ticket = x.Ticket, resources = x.Keys, since = x.Since.ToString("O") }).ToArray(),
                events = _events.ToArray() };
    }
    public string? OwnerOf(string resource)
    { lock (_gate) return _held.TryGetValue(resource, out var request) ? request.Station : null; }
}

public sealed class SharedResourceTimeoutException : TimeoutException
{ public SharedResourceTimeoutException(string message) : base(message) {} }
