namespace DeviceControl.Api;

public interface IIoController
{
    bool ReadInput(string name);
    IReadOnlyDictionary<string, bool> ReadInputs();
    IReadOnlyDictionary<string, bool> ReadOutputs();
    Task WriteOutputAsync(string name, bool value, CancellationToken cancellationToken);
}
public interface IAxis
{
    AxisStatus Status { get; }
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
    Task HomeAsync(CancellationToken cancellationToken);
    Task MoveAbsoluteAsync(double positionMm, CancellationToken cancellationToken);
    void InvalidateHome();
}
public interface IMotionController { IAxis GetAxis(string name); }
public interface ICylinder
{
    CylinderStatus Status { get; }
    Task ExtendAsync(CancellationToken cancellationToken);
    Task RetractAsync(CancellationToken cancellationToken);
}
public interface ICylinderRegistry { ICylinder GetCylinder(string name); }
public interface ICamera { Task<VisionResult> LocateAsync(CancellationToken cancellationToken); }
public interface ISequenceRuntime { void StartSingleCycle(); void RequestStop(); }
public record AxisStatus(bool Enabled, bool Homed, bool Moving, double PositionMm, string Error);
public record CylinderStatus(string Position, bool ExtendedSensor, bool RetractedSensor, bool Busy);
public record VisionResult(bool Ok, double TargetXmm, double Score, string Message);
public record EventItem(string Time, string Level, string Message);
public record InterlockState(string Action, string Device, string Signal, bool Required, bool Actual, bool Satisfied, string Message);
public record DeviceSnapshot(
    string ProfileId, string Mode, string State, string Step, int StepIndex, int Cycles,
    bool Busy, bool RecoveryRequired, string? Alarm,
    IReadOnlyDictionary<string, AxisStatus> Axes,
    IReadOnlyDictionary<string, CylinderStatus> Cylinders,
    IReadOnlyDictionary<string, bool> Inputs,
    IReadOnlyDictionary<string, bool> Outputs,
    IReadOnlyDictionary<string, bool> FaultInjection,
    IReadOnlyList<InterlockState> Interlocks,
    VisionResult? Vision, IReadOnlyList<EventItem> Events,
    AlarmDetail? ActiveAlarm, IReadOnlyList<StepRecord> StepHistory,
    IReadOnlyList<CycleRecord> CycleHistory);
public record ModeRequest(string Mode);
public record AxisEnableRequest(bool Enabled);
public record AxisMoveRequest(double PositionMm);
public record ToggleRequest(string Name, bool Value);
