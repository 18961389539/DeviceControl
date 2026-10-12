using System.Diagnostics;

namespace DeviceControl.Api;

/// <summary>Simulation-only IO driver; these are NOT safety-rated IO points.</summary>
public sealed class SimIoController : IIoController
{
    private readonly object _sync = new();
    private readonly StationConfig _config;
    private readonly Dictionary<string, bool> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _faults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _injectable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _opposites = new(StringComparer.OrdinalIgnoreCase);

    public SimIoController(StationConfig config)
    {
        _config = config;
        foreach (var s in config.Signals)
        {
            if (s.Direction == "DI") { _inputs.Add(s.Name, s.Initial); if (s.Injectable) _injectable.Add(s.Name); }
            else _outputs.Add(s.Name, false);
        }
        foreach (var c in config.Cylinders)
        {
            _faults.Add(c.Id + "Stuck", false);
            _opposites.Add(c.ExtendOutput, c.RetractOutput);
            _opposites.Add(c.RetractOutput, c.ExtendOutput);
        }
        foreach (var axis in config.Axes) _faults.Add(axis.Id + "Stuck", false);
    }
    public bool ReadInput(string name) { lock (_sync) return _inputs[name]; }
    public IReadOnlyDictionary<string, bool> ReadInputs() { lock (_sync) return new Dictionary<string, bool>(_inputs); }
    public IReadOnlyDictionary<string, bool> ReadOutputs() { lock (_sync) return new Dictionary<string, bool>(_outputs); }
    public IReadOnlyDictionary<string, bool> ReadFaults() { lock (_sync) return new Dictionary<string, bool>(_faults); }
    public bool HasFault(string name) { lock (_sync) return _faults[name]; }
    public void SetSimulationInput(string name, bool value)
    {
        if (!_injectable.Contains(name)) throw new ArgumentException("该输入不可由软件模拟写入: " + name);
        lock (_sync) _inputs[name] = value;
    }
    public void SetSimulationFault(string name, bool value)
    {
        lock (_sync)
        {
            if (!_faults.ContainsKey(name)) throw new ArgumentException("未定义仿真故障: " + name);
            _faults[name] = value;
        }
    }
    public void SetCylinderFeedback(CylinderConfig c, bool extended, bool retracted)
    {
        lock (_sync) { _inputs[c.ExtendedInput] = extended; _inputs[c.RetractedInput] = retracted; }
    }
    public Task WriteOutputAsync(string name, bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_outputs.ContainsKey(name)) throw new ArgumentException("未知输出: " + name);
            if (value && _opposites.TryGetValue(name, out var opposite) && _outputs[opposite])
                throw new InvalidOperationException("不允许正反电磁阀同时输出: " + name);
            _outputs[name] = value;
        }
        return Task.CompletedTask;
    }
}

public sealed class OrdinaryInterlockMonitor
{
    private readonly StationConfig _config;
    private readonly SimIoController _io;
    public OrdinaryInterlockMonitor(StationConfig config, SimIoController io) { _config = config; _io = io; }
    public IReadOnlyList<InterlockState> States() => _config.Interlocks.Select(r =>
        new InterlockState(r.Action, r.Device, r.Signal, r.Required, _io.ReadInput(r.Signal),
            _io.ReadInput(r.Signal) == r.Required, r.Message)).ToArray();
    public void Check(string action, string device)
    {
        if (!_io.ReadInput("GuardClosed")) throw new InvalidOperationException("普通联锁：防护门状态异常");
        if (!_io.ReadInput("AirPressureOk")) throw new InvalidOperationException("普通联锁：气压不足");
        foreach (var rule in _config.Interlocks)
            if (rule.Action == action && (rule.Device == "*" || rule.Device == device)
                && _io.ReadInput(rule.Signal) != rule.Required)
                throw new InvalidOperationException("普通互锁：" + rule.Message);
    }
}

public sealed class SimCylinder : ICylinder
{
    private readonly SimIoController _io;
    private readonly OrdinaryInterlockMonitor _interlocks;
    private readonly CylinderConfig _config;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private volatile bool _busy;
    public SimCylinder(CylinderConfig config, SimIoController io, OrdinaryInterlockMonitor interlocks)
    { _config = config; _io = io; _interlocks = interlocks; }
    public CylinderStatus Status
    {
        get
        {
            var e = _io.ReadInput(_config.ExtendedInput);
            var r = _io.ReadInput(_config.RetractedInput);
            return new(e && !r ? "Extended" : r && !e ? "Retracted" : "Unknown", e, r, _busy);
        }
    }
    public Task ExtendAsync(CancellationToken ct) => ActAsync(true, ct);
    public Task RetractAsync(CancellationToken ct) => ActAsync(false, ct);
    private async Task ActAsync(bool extend, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        _busy = true;
        var action = extend ? "CylinderExtend" : "CylinderRetract";
        var line = extend ? _config.ExtendOutput : _config.RetractOutput;
        try
        {
            _interlocks.Check(action, _config.Id);
            await _io.WriteOutputAsync(_config.ExtendOutput, false, CancellationToken.None);
            await _io.WriteOutputAsync(_config.RetractOutput, false, CancellationToken.None);
            if (Status.Position == (extend ? "Extended" : "Retracted")) return;
            await _io.WriteOutputAsync(line, true, ct);
            _io.SetCylinderFeedback(_config, false, false);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < _config.TimeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                _interlocks.Check(action, _config.Id);
                if (!_io.HasFault(_config.Id + "Stuck") && watch.ElapsedMilliseconds >= 420)
                {
                    _io.SetCylinderFeedback(_config, extend, !extend);
                    return;
                }
                await Task.Delay(35, ct);
            }
            throw new TimeoutException(_config.Label + "动作超时：未收到到位反馈");
        }
        finally
        {
            // Only demonstrates ordinary output cleanup, never an emergency-stop function.
            await _io.WriteOutputAsync(_config.ExtendOutput, false, CancellationToken.None);
            await _io.WriteOutputAsync(_config.RetractOutput, false, CancellationToken.None);
            _busy = false;
            _mutex.Release();
        }
    }
}

public sealed class SimAxis : IAxis
{
    private readonly SimIoController _io;
    private readonly OrdinaryInterlockMonitor _interlocks;
    private readonly AxisConfig _config;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private bool _enabled, _homed, _moving;
    private double _position;
    private string _error = "";
    public SimAxis(AxisConfig config, SimIoController io, OrdinaryInterlockMonitor interlocks)
    { _config = config; _io = io; _interlocks = interlocks; _position = config.HomeMm; }
    public AxisStatus Status { get { lock (_sync) return new(_enabled, _homed, _moving, Math.Round(_position, 3), _error); } }
    public Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_moving) throw new InvalidOperationException("运动中禁止更改使能");
            _enabled = enabled;
            if (!enabled) _homed = false;
        }
        return Task.CompletedTask;
    }
    public void InvalidateHome() { lock (_sync) _homed = false; }
    public async Task HomeAsync(CancellationToken ct)
    {
        await MoveCoreAsync(_config.HomeMm, true, ct);
        lock (_sync) _homed = true;
    }
    public Task MoveAbsoluteAsync(double positionMm, CancellationToken ct) => MoveCoreAsync(positionMm, false, ct);
    private async Task MoveCoreAsync(double target, bool homing, CancellationToken ct)
    {
        if (!double.IsFinite(target) || target < _config.MinMm || target > _config.MaxMm)
            throw new ArgumentOutOfRangeException(nameof(target), _config.Label + "目标位置超出配置行程");
        await _mutex.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                if (!_enabled) throw new InvalidOperationException(_config.Label + "尚未使能");
                if (!homing && !_homed) throw new InvalidOperationException(_config.Label + "尚未回零");
                _moving = true;
                _error = "";
            }
            try
            {
                _interlocks.Check("AxisMove", _config.Id);
                var origin = Status.PositionMm;
                var duration = Math.Max(0.2, Math.Abs(target - origin) / _config.SpeedMmPerSec);
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < duration || _io.HasFault(_config.Id + "Stuck"))
                {
                    ct.ThrowIfCancellationRequested();
                    _interlocks.Check("AxisMove", _config.Id);
                    if (_io.HasFault(_config.Id + "Stuck"))
                    {
                        await Task.Delay(75, ct);
                        if (watch.Elapsed.TotalSeconds > Math.Max(2.0, duration + 1.0))
                            throw new TimeoutException(_config.Label + "模拟故障：位置未更新");
                        continue;
                    }
                    var progress = Math.Min(1, watch.Elapsed.TotalSeconds / duration);
                    lock (_sync) _position = origin + (target - origin) * progress;
                    await Task.Delay(30, ct);
                }
                lock (_sync) _position = target;
            }
            catch { lock (_sync) _homed = false; throw; }
            finally { lock (_sync) _moving = false; }
        }
        finally { _mutex.Release(); }
    }
}

public sealed class SimMotionController : IMotionController
{
    private readonly Dictionary<string, IAxis> _axes;
    public SimMotionController(StationConfig config, SimIoController io, OrdinaryInterlockMonitor monitor)
        => _axes = config.Axes.ToDictionary(a => a.Id, a => (IAxis)new SimAxis(a, io, monitor), StringComparer.OrdinalIgnoreCase);
    public IAxis GetAxis(string name) => _axes.TryGetValue(name, out var axis) ? axis : throw new ArgumentException("未配置运动轴: " + name);
}
public sealed class SimCylinderRegistry : ICylinderRegistry
{
    private readonly Dictionary<string, ICylinder> _cylinders;
    public SimCylinderRegistry(StationConfig config, SimIoController io, OrdinaryInterlockMonitor monitor)
        => _cylinders = config.Cylinders.ToDictionary(c => c.Id, c => (ICylinder)new SimCylinder(c, io, monitor), StringComparer.OrdinalIgnoreCase);
    public ICylinder GetCylinder(string name) => _cylinders.TryGetValue(name, out var cylinder) ? cylinder : throw new ArgumentException("未配置气缸: " + name);
}
public sealed class SimCamera : ICamera
{
    private readonly SimIoController _io;
    private readonly OrdinaryInterlockMonitor _monitor;
    public SimCamera(SimIoController io, OrdinaryInterlockMonitor monitor) { _io = io; _monitor = monitor; }
    public async Task<VisionResult> LocateAsync(CancellationToken ct)
    {
        _monitor.Check("VisionLocate", "Camera");
        await Task.Delay(320, ct);
        if (!_io.ReadInput("PartPresent")) return new(false, 0, 0, "未检测到产品");
        return new(true, 80.0, 0.987, "模拟视觉定位 OK");
    }
}
