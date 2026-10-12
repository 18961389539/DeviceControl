using System.Text.Json;

namespace DeviceControl.Api;

public sealed class StationConfig
{
    public string ProfileId { get; set; } = "";
    public string Driver { get; set; } = "";
    public string FlowVersion { get; set; } = "unversioned";
    public List<SignalConfig> Signals { get; set; } = new();
    public List<AxisConfig> Axes { get; set; } = new();
    public List<CylinderConfig> Cylinders { get; set; } = new();
    public List<InterlockConfig> Interlocks { get; set; } = new();
    public List<SequenceStep> Sequence { get; set; } = new();

    public static StationConfig Load(string filename)
    {
        var cfg = JsonSerializer.Deserialize<StationConfig>(File.ReadAllText(filename),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("配置文件为空");
        cfg.Validate();
        return cfg;
    }

    public void Validate()
    {
        if (Driver != "Simulation") throw new InvalidOperationException("V0.2 仅支持 Simulation；禁止将未知板卡当作真实驱动启用");
        if (string.IsNullOrWhiteSpace(ProfileId)) throw new InvalidOperationException("必须配置 ProfileId");
        if (Axes.Count == 0 || Cylinders.Count == 0) throw new InvalidOperationException("至少配置一个轴与气缸");
        RequireDistinct(Signals.Select(x => x.Name), "信号名称");
        RequireDistinct(Signals.Select(x => x.Direction + ":" + x.Channel), "物理通道");
        RequireDistinct(Axes.Select(x => x.Id), "轴 ID");
        RequireDistinct(Cylinders.Select(x => x.Id), "气缸 ID");
        if (Axes.Any(x => x.MinMm >= x.MaxMm || x.HomeMm < x.MinMm || x.HomeMm > x.MaxMm || x.SpeedMmPerSec <= 0))
            throw new InvalidOperationException("轴行程/回零位/速度配置错误");
        var inputs = Signals.Where(x => x.Direction == "DI").Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputs = Signals.Where(x => x.Direction == "DO").Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Signals.Any(x => x.Direction is not ("DI" or "DO") || string.IsNullOrWhiteSpace(x.Channel) || string.IsNullOrWhiteSpace(x.Name)))
            throw new InvalidOperationException("信号方向、地址或名称不合法");
        foreach (var name in new[] { "GuardClosed", "AirPressureOk", "PartPresent" })
            if (!inputs.Contains(name)) throw new InvalidOperationException("缺少必要 DI: " + name);
        foreach (var c in Cylinders)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || c.TimeoutMs < 200 ||
                !outputs.Contains(c.ExtendOutput) || !outputs.Contains(c.RetractOutput) ||
                !inputs.Contains(c.ExtendedInput) || !inputs.Contains(c.RetractedInput) ||
                c.ExtendOutput == c.RetractOutput || c.ExtendedInput == c.RetractedInput)
                throw new InvalidOperationException("气缸映射错误: " + c.Id);
        }
        RequireDistinct(Cylinders.SelectMany(c => new[] { c.ExtendOutput, c.RetractOutput }), "气缸 DO 占用");
        RequireDistinct(Cylinders.SelectMany(c => new[] { c.ExtendedInput, c.RetractedInput }), "气缸 DI 占用");
        foreach (var rule in Interlocks)
            if (!inputs.Contains(rule.Signal) || (rule.Device != "*" && !Axes.Any(a => a.Id == rule.Device) && !Cylinders.Any(c => c.Id == rule.Device)))
                throw new InvalidOperationException("普通互锁配置错误: " + rule.Message);
        if (string.IsNullOrWhiteSpace(FlowVersion)) throw new InvalidOperationException("必须设置流程版本");
        if (Sequence.Count is < 1 or > 60) throw new InvalidOperationException("顶层流程节点必须为 1～60 个");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visionAvailable = false;
        foreach (var step in Sequence) ValidateNode(step, 0, ref visionAvailable, ids, inputs);
        if(ids.Count>140)throw new InvalidOperationException("流程节点总数不能超过 140");
    }
    private void ValidateNode(SequenceStep step, int depth, ref bool visionAvailable,
        HashSet<string> ids, HashSet<string> inputs)
    {
        if (string.IsNullOrWhiteSpace(step.Id) || !ids.Add(step.Id) || string.IsNullOrWhiteSpace(step.Label))
            throw new InvalidOperationException("流程节点 ID 重复/缺失或名称为空");
        if (step.ResourceWaitTimeoutMs is < 100 or > 30000) throw new InvalidOperationException("共享资源等待超时限制为 100～30000ms: " + step.Id);
        // Also check type validity before hierarchy checks. All leaf requests are atomic.
        SharedResourceCoordinator.CanonicalKeys(step.SharedResources ?? new List<string>());
        if ((step.Type is "Parallel" or "Condition") && step.SharedResources?.Count > 0)
            throw new InvalidOperationException("分支容器不能直接锁共享资源，请配置子节点: " + step.Id);
        var policy = step.OnError;
        if (policy is not ("Abort" or "Retry" or "WaitForOperator")) throw new InvalidOperationException("未知异常策略: " + step.Id);
        if (policy != "Abort" && step.Type is not ("WaitInput" or "VisionLocate" or "Delay"))
            throw new InvalidOperationException("非幂等执行机构动作不得自动重试: " + step.Id);
        if (policy == "Retry" && (step.MaxRetries < 1 || step.MaxRetries > 2))
            throw new InvalidOperationException("重试次数上限必须是 1～2");
        if (step.Type is "Parallel" or "Condition")
        {
            if (depth != 0 || (step.Type == "Parallel" ? step.Children.Count < 2 || step.Children.Count > 4 : step.Children.Count != 2))
                throw new InvalidOperationException("仅支持一层并行/条件分支，条件必须有真与假子节点");
            if (step.Type == "Condition" && !inputs.Contains(step.Device))
                throw new InvalidOperationException("条件输入信号不存在: " + step.Device);
            // Branches execute independently or in parallel; do not infer that a
            // vision result from only one branch is available to later stages.
            var previousVision=visionAvailable;
            foreach (var child in step.Children)
            {
                var branchVision=previousVision;
                ValidateNode(child, depth + 1, ref branchVision, ids, inputs);
            }
            return;
        }
        if (step.Children.Count != 0) throw new InvalidOperationException("非并行节点不应包含子节点");
        if (step.Type == "WaitInput" && inputs.Contains(step.Device) && step.TimeoutMs > 0) return;
        if (step.Type == "VisionLocate") { visionAvailable = true; return; }
        if (step.Type == "AxisMove")
        {
            var axis = Axes.FirstOrDefault(a => a.Id == step.Device);
            if (axis != null && (step.UseVisionX
                    ? step.Device == "X" && visionAvailable
                    : step.PositionMm >= axis.MinMm && step.PositionMm <= axis.MaxMm)) return;
        }
        if ((step.Type is "CylinderExtend" or "CylinderRetract") && Cylinders.Any(c => c.Id == step.Device)) return;
        if (step.Type == "Delay" && step.DurationMs is >= 0 and <= 30000) return;
        throw new InvalidOperationException("流程步骤配置错误: " + step.Label);
    }

    private static void RequireDistinct(IEnumerable<string> items, string kind)
    {
        var list = items.ToList();
        if (list.Count != list.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidOperationException("存在重复的" + kind);
    }
}

public sealed class SignalConfig
{
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Initial { get; set; }
    public bool Injectable { get; set; }
}

public sealed class AxisConfig
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public double MinMm { get; set; }
    public double MaxMm { get; set; }
    public double HomeMm { get; set; }
    public double SpeedMmPerSec { get; set; }
}

public sealed class CylinderConfig
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string ExtendOutput { get; set; } = "";
    public string RetractOutput { get; set; } = "";
    public string ExtendedInput { get; set; } = "";
    public string RetractedInput { get; set; } = "";
    public int TimeoutMs { get; set; }
}

public sealed class InterlockConfig
{
    public string Action { get; set; } = "";
    public string Device { get; set; } = "";
    public string Signal { get; set; } = "";
    public bool Required { get; set; }
    public string Message { get; set; } = "";
}

public sealed class SequenceStep
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string OnError { get; set; } = "Abort";
    public int MaxRetries { get; set; }
    public List<SequenceStep> Children { get; set; } = new();
    public string Label { get; set; } = "";
    public string Device { get; set; } = "";
    public bool UseVisionX { get; set; }
    public double PositionMm { get; set; }
    public int TimeoutMs { get; set; }
    public int DurationMs { get; set; }
    public List<string> SharedResources { get; set; } = new();
    public int ResourceWaitTimeoutMs { get; set; } = 2500;
}
