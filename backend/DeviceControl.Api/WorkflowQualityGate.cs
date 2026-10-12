using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceControl.Api;

/// <summary>
/// Deterministic, side-effect-free scenario evaluator for a simulation workflow.
/// Does not operate DeviceRuntime, change DI, run timers, or control hardware.
/// Keep scenario outcomes compatible with frontend/workflow-quality.js.
/// </summary>
public static class WorkflowQualityGate
{
    public sealed record CaseResult(string Id, string Name, string Expected, string Actual,
        bool Pass, string? Reason, IReadOnlyList<string> VisitedNodes);
    public sealed record Report(string Id, string FlowId, string DefinitionSha256, bool Pass,
        string EvaluatedUtc, IReadOnlyList<CaseResult> Results,
        IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, string ReportSha256);

    public static Report Evaluate(StationConfig config, FlowRevision revision)
    {
        var errors = new List<string>();
        try
        {
            var candidate = new StationConfig
            {
                ProfileId = config.ProfileId, Driver = config.Driver, FlowVersion = revision.Id,
                Signals = config.Signals, Axes = config.Axes, Cylinders = config.Cylinders,
                Interlocks = config.Interlocks, Sequence = revision.Definition
            };
            candidate.Validate();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            errors.Add(e.Message);
        }

        var warnings = StaticWarnings(revision.Definition);
        var normal = config.Signals.Where(s => s.Direction == "DI")
            .ToDictionary(s => s.Name, s => s.Initial, StringComparer.OrdinalIgnoreCase);
        var cases = new List<(string Id, string Name, Dictionary<string, bool> Inputs, bool Vision, string Expected)>
        {
            ("normal", "正常路径", new Dictionary<string,bool>(normal, StringComparer.OrdinalIgnoreCase), true, "Completed"),
            ("missing", "产品缺失", With(normal, "PartPresent", false), true, "Rejected"),
            ("air", "气压异常", With(normal, "AirPressureOk", false), true, "Rejected")
        };
        if (revision.Definition.Any(s => s.Type == "VisionLocate"))
            cases.Add(("camera", "视觉 NG", new Dictionary<string,bool>(normal, StringComparer.OrdinalIgnoreCase), false, "Failed"));
        var results = cases.Select(s => Simulate(revision.Definition, config, s.Id, s.Name,
            s.Inputs, s.Vision, s.Expected)).ToArray();
        // A static warning for uncertain vision dataflow is a hard publishing error.
        var fatalWarnings = warnings.Any(w => w.Contains("未经过必经视觉节点", StringComparison.Ordinal)
                                              || w.Contains("未确认存在的视觉目标", StringComparison.Ordinal));
        var pass = errors.Count == 0 && !fatalWarnings && results.All(r => r.Pass);
        var evaluated = DateTimeOffset.UtcNow.ToString("O");
        var fingerprint = Hash(JsonSerializer.Serialize(revision.Definition));
        // Hash an immutable report payload (independent of report ID) for reproducible integrity checks.
        var digest = Digest(revision.Id, fingerprint, evaluated, pass, results, errors, warnings);
        return new Report("gate-" + Guid.NewGuid().ToString("N"), revision.Id, fingerprint, pass,
            evaluated, results, errors, warnings, digest);
    }

    public static bool Verify(Report report) =>
        string.Equals(report.ReportSha256,
            Digest(report.FlowId, report.DefinitionSha256, report.EvaluatedUtc, report.Pass,
                report.Results, report.Errors, report.Warnings), StringComparison.OrdinalIgnoreCase);

    private static string Digest(string flowId, string fingerprint, string evaluated, bool pass,
        IReadOnlyList<CaseResult> results, IReadOnlyList<string> errors, IReadOnlyList<string> warnings) =>
        Hash(JsonSerializer.Serialize(new { Id = flowId, Fingerprint = fingerprint,
            Evaluated = evaluated, Pass = pass, Results = results, Errors = errors, Warnings = warnings }));

    private static Dictionary<string, bool> With(Dictionary<string, bool> source, string key, bool value)
    {
        var copy = new Dictionary<string, bool>(source, StringComparer.OrdinalIgnoreCase);
        copy[key] = value;
        return copy;
    }
    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static IReadOnlyList<string> StaticWarnings(IEnumerable<SequenceStep> nodes)
    {
        var warnings = new List<string>();
        var precedingVision = false;
        foreach (var node in nodes)
        {
            if (node.Type == "AxisMove" && node.UseVisionX && !precedingVision)
                warnings.Add(node.Label + " 在未经过必经视觉节点时引用了视觉目标");
            if (node.Type == "VisionLocate") precedingVision = true;
            if (node.Type == "Parallel")
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in node.Children)
                {
                    string? resource = child.Type == "AxisMove" ? "axis:" + child.Device :
                        child.Type.StartsWith("Cylinder", StringComparison.Ordinal) ? "cylinder:" + child.Device : null;
                    if (resource != null && !used.Add(resource))
                        warnings.Add(node.Label + " 的并行分支竞争相同资源 " + resource + "，运行时会串行化");
                    if (child.Type == "AxisMove" && child.UseVisionX && !precedingVision)
                        warnings.Add(child.Label + " 使用未确认存在的视觉目标");
                }
            }
        }
        return warnings;
    }

    private static CaseResult Simulate(IReadOnlyList<SequenceStep> nodes, StationConfig config, string id,
        string name, Dictionary<string, bool> inputs, bool visionOk, string expected)
    {
        var visits = new List<string>();
        var outcome = "Completed";
        string? reason = "全部模型步骤完成";
        var visionAvailable = false;
        var cylinders = config.Cylinders.ToDictionary(c => c.Id, _ => false, StringComparer.OrdinalIgnoreCase);
        if (!inputs.GetValueOrDefault("GuardClosed") || !inputs.GetValueOrDefault("AirPressureOk")
            || !inputs.GetValueOrDefault("PartPresent"))
        {
            outcome = "Rejected"; reason = "启动前置条件未满足";
        }
        else
        {
            try { foreach (var node in nodes) Run(node); }
            catch (ModelFailure e) { outcome = "Failed"; reason = e.Message; }
        }
        return new CaseResult(id, name, expected, outcome, expected == outcome, reason, visits);

        void Fail(string message) => throw new ModelFailure(message);
        void Run(SequenceStep node)
        {
            visits.Add(node.Id);
            if (node.Type == "Condition")
            {
                Run(node.Children[inputs.GetValueOrDefault(node.Device) ? 0 : 1]);
                return;
            }
            if (node.Type == "Parallel")
            {
                // Deterministic functional branch evaluation, not a real scheduling model.
                foreach (var child in node.Children) Run(child);
                return;
            }
            if (node.Type == "WaitInput" && !inputs.GetValueOrDefault(node.Device))
                Fail("输入条件不满足: " + node.Device);
            if (node.Type == "VisionLocate")
            {
                if (!visionOk) Fail("视觉检测 NG");
                visionAvailable = true;
            }
            if (node.Type is "AxisMove" or "CylinderExtend" or "CylinderRetract")
            {
                foreach (var rule in config.Interlocks.Where(r => r.Action == node.Type &&
                             (r.Device == "*" || string.Equals(r.Device, node.Device, StringComparison.OrdinalIgnoreCase))))
                    if (inputs.GetValueOrDefault(rule.Signal) != rule.Required)
                        Fail("动作互锁未满足: " + rule.Message);
            }
            if (node.Type == "AxisMove")
            {
                if (node.UseVisionX && !visionAvailable) Fail("视觉变量未赋值，禁止引用");
                var axis = config.Axes.FirstOrDefault(a => a.Id == node.Device);
                var position = node.UseVisionX ? 80 : node.PositionMm;
                if (axis is null || !double.IsFinite(position) || position < axis.MinMm || position > axis.MaxMm)
                    Fail("轴目标不在类型/量程范围");
            }
            if (node.Type is "CylinderExtend" or "CylinderRetract")
            {
                var cylinder = config.Cylinders.FirstOrDefault(c => c.Id == node.Device);
                if (cylinder is null) Fail("气缸不存在");
                var extended = node.Type == "CylinderExtend";
                cylinders[node.Device] = extended;
                inputs[cylinder!.ExtendedInput] = extended;
                inputs[cylinder.RetractedInput] = !extended;
            }
        }
    }

    private sealed class ModelFailure(string message) : Exception(message) { }
}
