namespace DeviceControl.Api;

/// <summary>
/// Single-machine simulation orchestrator: one command at a time, declarative steps,
/// ordinary interlocks, explicit recovery. NOT suitable for safety control.
/// </summary>
public sealed class DeviceRuntime : ISequenceRuntime
{
    private readonly object _sync = new();
    private readonly RunJournal _journal;
    private FlowEngine _flow;
    private SharedResourceCoordinator? _sharedResources;
    private string _stationId = "A";
    public void AttachSharedResources(SharedResourceCoordinator scheduler, string stationId)
    {
        lock (_sync) { if (_busy) throw new InvalidOperationException("工站运行中禁止挂接资源调度器");
            _sharedResources=scheduler; _stationId=stationId; _flow.AttachSharedCoordinator(scheduler,stationId); }
    }
    private readonly FlowRevisions _flowRevisions;
    private string _cycleFlowVersion="";
    private readonly RecipeStore _recipes;
    private RecipeRevision? _cycleRecipe;
    private readonly List<StepRecord> _stepHistory = new();
    private readonly List<StepRecord> _currentSteps = new();
    private readonly List<NodeVariableSnapshot> _currentSnapshots = new();
    private AlarmDetail? _activeAlarm;
    private string _cycleId = "";
    private DateTimeOffset _cycleStarted;
    private System.Diagnostics.Stopwatch? _cycleTimer;
    private readonly StationConfig _config;
    private readonly SimIoController _io;
    private readonly IMotionController _motion;
    private readonly ICylinderRegistry _cylinders;
    private readonly ICamera _camera;
    private readonly OrdinaryInterlockMonitor _interlocks;
    private readonly List<EventItem> _events = new();
    private string _mode = "Manual", _state = "Idle", _step = "等待操作";
    private string? _alarm;
    private int _cycles, _stepIndex = -1;
    private bool _busy, _recoveryRequired;
    private readonly RunEligibilityRegistry? _eligibility;
    private VisionResult? _vision;
    private CancellationTokenSource? _cycleCts;

    public DeviceRuntime(StationConfig config, SimIoController io, IMotionController motion,
        ICylinderRegistry cylinders, ICamera camera, OrdinaryInterlockMonitor interlocks, RunJournal journal,
        RecipeStore recipes, FlowRevisions flows, RunEligibilityRegistry? eligibility = null)
    {
        _flowRevisions=flows;
        _eligibility=eligibility;
        var current=flows.Active;
        config.Sequence=current.Definition;config.FlowVersion=current.Id;
        _flow = new FlowEngine(config);
        _config = config; _io = io; _motion = motion; _cylinders = cylinders;
        _camera = camera; _interlocks = interlocks; _journal = journal; _recipes = recipes;
        // F01: durable recovery gate survives restarts; never silently dropped.
        var persisted = journal.RecoveryState;
        if (journal.RecoveredInterruptions > 0)
        {
            EnterRecoveryRequired($"上次软件退出时有 {journal.RecoveredInterruptions} 个未完成周期；已标记 Interrupted",
                null, "StartupInterruptedCycles");
            _step = "检测到进程中断的未完成仿真周期，必须人工恢复";
            Log("Warning", $"上次软件退出时有 {journal.RecoveredInterruptions} 个未完成周期；已标记 Interrupted");
        }
        else if (persisted is { Required: true })
        {
            _recoveryRequired = true;
            _step = "存在未确认的恢复要求（" + persisted.Reason + "），必须人工恢复";
            Log("Warning", "加载未确认的恢复要求：" + persisted.Reason);
        }
        Log("Info", "加载站点 " + config.ProfileId + "，模式 Simulation（无真实板卡）");
    }

    public DeviceSnapshot Snapshot()
    {
        lock (_sync)
            return new(_config.ProfileId, _mode, _state, _step, _stepIndex, _cycles, _busy,
                _recoveryRequired, _alarm,
                _config.Axes.ToDictionary(a => a.Id, a => _motion.GetAxis(a.Id).Status),
                _config.Cylinders.ToDictionary(c => c.Id, c => _cylinders.GetCylinder(c.Id).Status),
                _io.ReadInputs(), _io.ReadOutputs(), _io.ReadFaults(),
                _interlocks.States(), _vision, _events.AsEnumerable().Reverse().ToArray(),
                _activeAlarm, _stepHistory.AsEnumerable().Reverse().ToArray(), _journal.Recent());
    }

    public object FlowSnapshot() => _flow.Snapshot();
    public object FlowCommand(FlowDebugRequest request) => _flow.Command(request);
    public FlowRevision PublishFlow(string id)=>WithConfigurationChange(()=>{var v=_flowRevisions.Publish(id);SwitchFlow(v);return v;});
    public FlowRevision RollbackFlow(string id,string actor)=>WithConfigurationChange(()=>{var v=_flowRevisions.Rollback(id,actor);SwitchFlow(v);return v;});
    private void SwitchFlow(FlowRevision version)
    {
        _config.Sequence=version.Definition;_config.FlowVersion=version.Id;_flow=new FlowEngine(_config);
        if (_sharedResources is not null) _flow.AttachSharedCoordinator(_sharedResources,_stationId);
        Log("Info","切换已发布流程: "+version.Id);
    }
    public void SetMode(string mode)
    {
        if (mode is not ("Manual" or "Auto")) throw new ArgumentException("模式只能是 Manual 或 Auto");
        lock (_sync)
        {
            RequireIdle();
            if (mode == "Auto" && _recoveryRequired) throw new InvalidOperationException("需要先完成故障恢复确认");
            if (mode == "Auto")
            {
                // F04: an unresolved deployment (or broken eligibility check) blocks Auto.
                var blocked = _eligibility?.BlockingReason(_stationId);
                if (blocked != null) throw new InvalidOperationException(blocked);
            }
            _mode = mode; _step = "等待操作"; _stepIndex = -1;
            Log("Info", "模式切换: " + mode);
        }
    }
    public void SetInput(string name, bool value)
    {
        _io.SetSimulationInput(name, value);
        Log("Warning", $"仿真外部输入 {name}={value}");
    }
    public void SetFaultInjection(string name, bool value)
    {
        _io.SetSimulationFault(name, value);
        Log("Warning", $"仿真故障注入 {name}={value}");
    }

    public Task SetServoEnabledAsync(string id, bool enabled)
    {
        var axis = _motion.GetAxis(id);
        return ManualAsync(id + (enabled ? " 使能" : " 去使能"),
            "AxisEnable", id, ct => axis.SetEnabledAsync(enabled, ct));
    }
    public Task HomeAsync(string id)
    {
        var axis = _motion.GetAxis(id);
        return ManualAsync(id + " 回零", "AxisMove", id, axis.HomeAsync);
    }
    public Task MoveAsync(string id, double mm)
    {
        var axis = _motion.GetAxis(id);
        return ManualAsync($"{id} 绝对定位 {mm:F2} mm", "AxisMove", id,
            ct => axis.MoveAbsoluteAsync(mm, ct));
    }
    public Task CylinderActionAsync(string id, bool extend)
    {
        var cylinder = _cylinders.GetCylinder(id);
        return ManualAsync(id + (extend ? " 伸出" : " 缩回"),
            extend ? "CylinderExtend" : "CylinderRetract", id,
            extend ? cylinder.ExtendAsync : cylinder.RetractAsync);
    }
    public Task TriggerVisionAsync() => ManualAsync("视觉触发", "VisionLocate", "Camera", async ct =>
    {
        SharedResourceCoordinator.Lease? lease = null;
        try
        {
            if (_sharedResources is not null)
                lease = await _sharedResources.AcquireAsync(_stationId,"manual-vision",["CELL-SHARED"],2500,ct);
            var result = await _camera.LocateAsync(ct);
            lock (_sync) _vision = result;
            if (!result.Ok) throw new InvalidOperationException(result.Message);
        }
        finally { lease?.Dispose(); }
    });

    private async Task ManualAsync(string description, string actionType, string id,
        Func<CancellationToken, Task> action)
    {
        lock (_sync)
        {
            RequireIdle();
            if (_mode != "Manual") throw new InvalidOperationException("仅手动模式允许该操作");
            _interlocks.Check(actionType, id);
            _busy = true; _step = description; _stepIndex = -1;
            Log("Info", "开始 " + description);
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await action(timeout.Token);
            Log("Info", "完成 " + description);
        }
        catch (Exception e)
        {
            lock (_sync)
            {
                _state = "Fault"; _alarm = e.Message; _activeAlarm = FaultCatalog.From(e, description);
                EnterRecoveryRequired("手动动作失败：" + e.Message, null, actionType);
                InvalidateAllAxes();
                Log("Error", description + " 失败: " + e.Message);
            }
            throw;
        }
        finally
        {
            lock (_sync) { _busy = false; _step = _state == "Fault" ? "故障待处理" : "等待操作"; }
        }
    }

    public void StartSingleCycle()
    {
        CancellationToken token;
        lock (_sync)
        {
            RequireIdle();
            if (_mode != "Auto") throw new InvalidOperationException("请先切换自动模式");
            if (_recoveryRequired) throw new InvalidOperationException("请先确认故障恢复");
            // F04: unified run-eligibility gate (unresolved deployments etc.).
            var blocked = _eligibility?.BlockingReason(_stationId);
            if (blocked != null) throw new InvalidOperationException(blocked);
            _interlocks.Check("CycleStart", "Station");
            if (!_io.ReadInput("PartPresent")) throw new InvalidOperationException("未检测到产品");
            foreach (var a in _config.Axes)
                if (!_motion.GetAxis(a.Id).Status.Enabled || !_motion.GetAxis(a.Id).Status.Homed)
                    throw new InvalidOperationException(a.Id + " 未使能或未回零");
            foreach (var c in _config.Cylinders)
                if (_cylinders.GetCylinder(c.Id).Status.Position != "Retracted")
                    throw new InvalidOperationException(c.Id + " 未缩回到位");
            _state = "Running"; _step = "准备开始"; _stepIndex = -1;
            _busy = true; _alarm = null; _activeAlarm = null; _vision = null;
            _currentSteps.Clear(); _currentSnapshots.Clear(); _cycleId = Guid.NewGuid().ToString("N")[..12];
            _cycleRecipe = _recipes.Active;
            _cycleFlowVersion=_config.FlowVersion;
            _cycleStarted = DateTimeOffset.UtcNow; _cycleTimer = System.Diagnostics.Stopwatch.StartNew();
            // Persist the in-flight marker before execution becomes visible to the caller.
            // An unexpected process exit will be recovered as Interrupted, never Completed.
            try { _journal.BeginCycle(new CycleStartRecord(_cycleId, _cycleStarted.ToString("O"),
                _cycleRecipe?.Id, _cycleFlowVersion)); }
            catch { _state = "Idle"; _busy = false;
                EnterRecoveryRequired("周期启动标记持久化失败", _cycleId, "BeginCycle"); throw; }
            _cycleCts = new CancellationTokenSource();
            token = _cycleCts.Token;
            Log("Info", "启动单周期");
        }
        _ = Task.Run(() => RunCycleAsync(token));
    }
    public void RequestStop()
    {
        lock (_sync)
        {
            if (_state is not ("Running" or "Stopping")) throw new InvalidOperationException("设备未在自动运行");
            _state = "Stopping"; _step = "停止请求处理中";
            _cycleCts?.Cancel(); _flow.Stop();
            Log("Warning", "收到软件停止请求（不是硬件急停）");
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        try
        {
            await _flow.RunAsync(ExecuteStepAsync, RecordStep,
                (i, step) =>
                {
                    ct.ThrowIfCancellationRequested();
                    _interlocks.Check("Sequence", "Station");
                    lock (_sync)
                    {
                        if (_state != "Running") throw new OperationCanceledException(ct);
                        _stepIndex = i; _step = step.Label;
                        Log("Info", "流程步骤: " + _step);
                    }
                }, ct, (signal,token)=>Task.FromResult(_io.ReadInput(signal)));
            lock (_sync)
            {
                _cycles++; _state = "Idle"; _step = "单周期完成";
                _stepIndex = _config.Sequence.Count;
                Log("Info", "完成第 " + _cycles + " 个周期");
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                _state = "Idle";
                EnterRecoveryRequired("软件停止完成，需人工恢复", _cycleId, "Stop");
                InvalidateAllAxes(); _step = "软件停止完成，需人工恢复";
                Log("Warning", "流程取消；回零有效性已清除");
            }
        }
        catch (Exception e)
        {
            lock (_sync)
            {
                _state = "Fault"; _alarm = e.Message; _activeAlarm = FaultCatalog.From(e, _step);
                EnterRecoveryRequired("自动流程中断：" + e.Message, _cycleId, "Cycle");
                InvalidateAllAxes(); _step = "故障待处理";
                Log("Error", "自动流程中断: " + e.Message);
            }
        }
        finally
        {
            lock (_sync)
            {
                _cycleTimer?.Stop();
                var outcome = _state == "Fault" ? "Failed" : _recoveryRequired ? "Stopped" : "Completed";
                try
                {
                    _journal.Append(new CycleRecord(_cycleId, _cycleStarted.ToString("O"),
                        DateTimeOffset.UtcNow.ToString("O"), outcome, _cycleTimer?.ElapsedMilliseconds ?? 0,
                        _currentSteps.ToArray(), _alarm ?? (outcome == "Stopped" ? "软件请求停止" : null), _cycleRecipe?.Id, _cycleFlowVersion,
                        _flow.CompletedTraces(), _currentSnapshots.OrderBy(s=>s.Ordinal).ToArray()));
                }
                catch (Exception journalError)
                {
                    _state = "Fault";
                    EnterRecoveryRequired("周期数据持久化失败：" + journalError.Message, _cycleId, "Journal");
                    _alarm = "周期数据持久化失败：" + journalError.Message;
                    _activeAlarm = FaultCatalog.From(journalError,"SQLite Journal");
                    Log("Error",_alarm);
                }
                finally
                {
                    _busy = false;
                    _cycleCts?.Dispose(); _cycleCts = null;
                }
            }
        }
    }
    private void RecordStep(int index, SequenceStep step, string outcome, long milliseconds, string? error, int traceOrdinal)
    {
        lock (_sync)
        {
            var record = new StepRecord(index, step.Label, step.Type, step.Device, outcome, milliseconds, error);
            _currentSteps.Add(record);
            // Completed node callback order matches FlowEngine.CompletedTraces() ordinal,
            // including parallel child nodes. Snapshot is strictly taken at callback time.
            _currentSnapshots.Add(new NodeVariableSnapshot(traceOrdinal, step.Id, DateTimeOffset.UtcNow.ToString("O"),
                new Dictionary<string,bool>(_io.ReadInputs()),
                _config.Axes.ToDictionary(a => a.Id, a => _motion.GetAxis(a.Id).Status),
                _config.Cylinders.ToDictionary(c => c.Id, c => _cylinders.GetCylinder(c.Id).Status),
                _vision, _cycleRecipe?.Id, _cycleFlowVersion));
            _stepHistory.Add(record);
            if (_stepHistory.Count > 100) _stepHistory.RemoveRange(0, _stepHistory.Count - 100);
        }
    }
    private async Task ExecuteStepAsync(SequenceStep step, CancellationToken ct)
    {
        switch (step.Type)
        {
            case "WaitInput":
                var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(step.TimeoutMs);
                while (!_io.ReadInput(step.Device))
                {
                    ct.ThrowIfCancellationRequested();
                    _interlocks.Check("WaitInput", step.Device);
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("等待输入 " + step.Device + " 超时");
                    await Task.Delay(35, ct);
                }
                break;
            case "VisionLocate":
                var result = await _camera.LocateAsync(ct);
                lock (_sync) _vision = result;
                if (!result.Ok) throw new InvalidOperationException("视觉 NG: " + result.Message);
                break;
            case "AxisMove":
                await _motion.GetAxis(step.Device).MoveAbsoluteAsync(
                    step.UseVisionX ? (_vision?.TargetXmm ?? throw new InvalidOperationException("无有效视觉定位结果"))
                        + (_cycleRecipe?.Values.VisionOffsetMm ?? 0) : step.PositionMm, ct);
                break;
            case "CylinderExtend": await _cylinders.GetCylinder(step.Device).ExtendAsync(ct); break;
            case "CylinderRetract": await _cylinders.GetCylinder(step.Device).RetractAsync(ct); break;
            case "Delay": await Task.Delay(step.DurationMs + (_cycleRecipe?.Values.ExtraDwellMs ?? 0), ct); break;
            default: throw new InvalidOperationException("不支持的步骤: " + step.Type);
        }
    }

    // Hold machine lock while publishing a new recipe: starting a cycle and publishing
    // cannot cross. Never expose a hot-switched recipe inside an executing cycle.
    public T WithConfigurationChange<T>(Func<T> action)
    {
        lock (_sync)
        {
            RequireIdle();
            if (_recoveryRequired) throw new InvalidOperationException("恢复未完成，禁止发布配方");
            // F04: an interrupted deployment must also block ordinary version changes.
            var blocked = _eligibility?.BlockingReason(_stationId);
            if (blocked != null) throw new InvalidOperationException(blocked);
            return action();
        }
    }
    public object RecoveryChecklist()
    {
        lock (_sync)
        {
            var checks = new List<object>
            {
                new { key = "idle", label = "执行服务空闲", ok = !_busy && _state == "Idle" },
                new { key = "manual", label = "已切换手动模式", ok = _mode == "Manual" },
                new { key = "input", label = "普通仿真工艺条件正常", ok = _io.ReadInput("GuardClosed") && _io.ReadInput("AirPressureOk") }
            };
            foreach (var a in _config.Axes)
            {
                var status = _motion.GetAxis(a.Id).Status;
                checks.Add(new { key = "axis-" + a.Id, label = a.Id + " 轴使能且回零位", ok = status.Enabled && status.Homed && Math.Abs(status.PositionMm - a.HomeMm) <= 0.01 });
            }
            foreach (var c in _config.Cylinders)
                checks.Add(new { key = "cylinder-" + c.Id, label = c.Id + " 气缸已缩回", ok = _cylinders.GetCylinder(c.Id).Status.Position == "Retracted" });
            var eligibilityBlock = _eligibility?.BlockingReason(_stationId);
            return new { required = _recoveryRequired, alarm = _activeAlarm, checks,
                eligibilityBlock,
                persistedRecovery = _journal.RecoveryState is { Required: true } s
                    ? new { s.Reason, s.RunId, s.Action, s.RaisedUtc, s.AcknowledgedBy, s.AcknowledgedUtc } : null };
        }
    }
    public void ResetFault()
    {
        lock (_sync)
        {
            if (_busy || _state != "Fault") throw new InvalidOperationException("当前没有可复位故障");
            _interlocks.Check("ResetFault", "Station");
            _state = "Idle"; _mode = "Manual"; _alarm = null; _activeAlarm = null; _stepIndex = -1;
            _step = "进入人工恢复流程";
            Log("Warning", "报警已复位，仍需机构回零与确认");
        }
    }
    public void ConfirmRecovery(string actor)
    {
        lock (_sync)
        {
            RequireIdle();
            if (_mode != "Manual") throw new InvalidOperationException("恢复确认必须在手动模式");
            _interlocks.Check("Recovery", "Station");
            foreach (var a in _config.Axes)
            {
                var status = _motion.GetAxis(a.Id).Status;
                if (!status.Enabled || !status.Homed || Math.Abs(status.PositionMm - a.HomeMm) > 0.01)
                    throw new InvalidOperationException(a.Id + " 尚未回到回零位");
            }
            foreach (var c in _config.Cylinders)
                if (_cylinders.GetCylinder(c.Id).Status.Position != "Retracted")
                    throw new InvalidOperationException(c.Id + " 尚未缩回到位");
            // F04/F01: an unresolved deployment must be cleared by a human before
            // recovery confirmation can re-open the station.
            var blocked = _eligibility?.BlockingReason(_stationId);
            if (blocked != null) throw new InvalidOperationException(blocked);
            _recoveryRequired = false; _step = "恢复条件已确认";
            // F01: the durable gate is only cleared here, with an audited actor.
            try { _journal.ClearRecoveryRequired(actor); }
            catch (Exception ex)
            {
                EnterRecoveryRequired("恢复确认持久化失败：" + ex.Message, null, "ConfirmRecovery");
                throw new InvalidOperationException("恢复确认未能持久化，门禁保持有效", ex);
            }
            Log("Info", "仿真恢复确认完成（" + actor + "）");
        }
    }
    /// <summary>F01: raise the recovery requirement in memory AND in SQLite so a
    /// restart cannot silently drop it.</summary>
    private void EnterRecoveryRequired(string reason, string? runId, string? action)
    {
        _recoveryRequired = true;
        try { _journal.RaiseRecoveryRequired(reason, runId, action); }
        catch (Exception ex) { Log("Error", "恢复要求持久化失败：" + ex.Message); }
    }
    private void InvalidateAllAxes()
    { foreach (var a in _config.Axes) _motion.GetAxis(a.Id).InvalidateHome(); }
    private void RequireIdle()
    { if (_state != "Idle" || _busy) throw new InvalidOperationException("设备未处于空闲状态"); }
    private void Log(string level, string message)
    {
        lock (_sync)
        {
            _events.Add(new(DateTimeOffset.Now.ToString("HH:mm:ss.fff"), level, message));
            if (_events.Count > 160) _events.RemoveRange(0, _events.Count - 160);
            // F10: mirror run events to a durable per-station log. Never throws
            // into the runtime path; failures are counted for diagnostics.
            try { _journal.AppendEvent(level, message); } catch { /* diagnostics only */ }
        }
    }
}
