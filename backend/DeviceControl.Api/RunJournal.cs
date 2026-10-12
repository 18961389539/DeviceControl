using System.Text.Json;

namespace DeviceControl.Api;

// SQLite-backed simulation-only cycle journal. It is not certified or tamper-proof.
public sealed class RunJournal
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly SqliteStore _db;
    private readonly List<CycleRecord> _cycles = new();
    public int RecoveredInterruptions { get; }
    public RunJournal(IHostEnvironment environment,SqliteStore db, string? scope = null)
    {
        var dir = StationStoragePaths.Folder(environment, scope);
        _path = Path.Combine(dir, "cycles.jsonl");
        _db=db;
        RecoveredInterruptions = _db.RecoverInterruptedCycles();
        if (File.Exists(_path)) foreach (var line in File.ReadLines(_path))
        {
            try
            {
                var item = JsonSerializer.Deserialize<CycleRecord>(line);
                if (item is not null) _db.UpsertCycle(item);
            }
            catch (JsonException) { /* tolerate a truncated final record */ }
        }
        _cycles.AddRange(_db.Cycles().AsEnumerable().Reverse());
        if (_cycles.Count > 100) _cycles.RemoveRange(0, _cycles.Count - 100);
    }
    public IReadOnlyList<CycleRecord> Recent()
    {
        lock (_sync) return _cycles.AsEnumerable().Reverse().ToArray();
    }
    public void BeginCycle(CycleStartRecord record) => _db.BeginCycle(record);
    public void Append(CycleRecord record)
    {
        lock (_sync)
        {
            _db.UpsertCycle(record);
            _cycles.Add(record);
            if (_cycles.Count > 100) _cycles.RemoveRange(0, _cycles.Count - 100);
        }
    }
}

public record StepRecord(int Index, string Label, string Type, string Device,
    string Outcome, long DurationMs, string? Error);
public record CycleRecord(string Id, string StartedAt, string EndedAt, string Outcome,
    long DurationMs, IReadOnlyList<StepRecord> Steps, string? Reason, string? RecipeId = null, string? FlowVersion = null,
    IReadOnlyList<FlowTrace>? Trace = null,
    IReadOnlyList<NodeVariableSnapshot>? Snapshots = null);

public sealed record CycleStartRecord(string Id, string StartedAt, string? RecipeId, string? FlowVersion);
public sealed record NodeVariableSnapshot(int Ordinal, string NodeId, string CapturedUtc,
    IReadOnlyDictionary<string,bool> Inputs,
    IReadOnlyDictionary<string,AxisStatus> Axes,
    IReadOnlyDictionary<string,CylinderStatus> Cylinders,
    VisionResult? Vision, string? RecipeId, string? FlowVersion);
public record AlarmDetail(string Code, string Message, string Suggestion, string Source,
    string RaisedAt);

public static class FaultCatalog
{
    public static AlarmDetail From(Exception error, string source)
    {
        var m = error.Message;
        var (code, suggestion) = m switch
        {
            _ when error is TimeoutException && m.Contains("输入") =>
                ("SEQ-WAIT-TIMEOUT", "检查对应输入条件和工艺等待时间，再按恢复流程处理。"),
            _ when m.Contains("气缸") && m.Contains("超时") =>
                ("ACT-CYL-TIMEOUT", "检查仿真卡滞注入、气缸反馈状态，并执行人工恢复确认。"),
            _ when m.Contains("轴") && (m.Contains("故障") || m.Contains("超时")) =>
                ("ACT-AXIS-FAULT", "检查轴仿真故障、当前位置和回零状态。"),
            _ when m.Contains("视觉") =>
                ("VIS-RESULT-NG", "检查工件在位输入和视觉返回结果。"),
            _ when m.Contains("互锁") || m.Contains("联锁") =>
                ("SEQ-INTERLOCK", "打开机构与互锁页，定位未满足的普通工艺条件。"),
            _ when m.Contains("行程") || m.Contains("范围") =>
                ("ACT-LIMIT", "检查目标位置及站点轴行程参数。"),
            _ => ("SYS-UNCLASSIFIED", "查看步骤记录与错误信息，完成必要的人工恢复。")
        };
        return new AlarmDetail(code, m, suggestion, source, DateTimeOffset.Now.ToString("O"));
    }
}
