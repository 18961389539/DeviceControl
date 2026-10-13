using System.Text.Json;

namespace DeviceControl.Api;

public sealed record RolloutCreateRequest(string TemplateId, string PilotStationId, string[] StationIds, int WaveSize = 2, string? WindowStartUtc = null, string? WindowEndUtc = null);
public sealed record RolloutStep(string StationId, string ExpectedFlowId, string ExpectedRecipeId,
    string Status = "Pending", string? DeploymentId = null, string? AppliedAt = null,
    string? AfterFlowId = null, string? AfterRecipeId = null, string? EvidenceCycleId = null,
    string? Error = null, string? RecoveryNote = null, string? RecoveryActor = null, string? ReconciledAt = null);
public sealed record FleetRollout(string Id, string TemplateId, string TemplateHash, string PilotStationId,
    int WaveSize, string Actor, string CreatedAt, string UpdatedAt, string Status,
    RolloutStep[] Steps, string? Reason = null, string? WindowStartUtc = null, string? WindowEndUtc = null);
public sealed record RolloutNote(string Reason = "");
public sealed record RolloutEvidence(bool Pass, string[] Blockers, string[] VerifiedStations);
public sealed record RolloutReconcileRequest(string StationId, string Note);
public sealed record RolloutStationDiagnostic(string StationId, string StepStatus, string Mode,
    string MachineState, bool Busy, string? Alarm, bool RecoveryRequired,
    string ActiveFlowId, string ActiveRecipeId, string[] UnresolvedDeployments, string[] Blockers);
public sealed record RecoveryStationReport(string StationId, string StepStatus, string Category,
    bool BaselineMatches, bool TargetMatches, string? DeploymentId,
    string? DeploymentStatus, string? BackupStatus, string[] Blockers);
public sealed record RolloutRecoveryReport(string RolloutId, string Status, string InspectedAt,
    bool ReadOnly, bool RequiresManualReview, RecoveryStationReport[] Stations, string Disclaimer);
public sealed record RolloutDiagnostics(string RolloutId, string Status, bool WindowOpen,
    string? WindowStartUtc, string? WindowEndUtc, string[] Blockers,
    RolloutStationDiagnostic[] Stations);


/// <summary>
/// Simulation-only V1.6 graduated rollout. Explicit, synchronous HTTP transitions:
/// pilot -> recorded completed cycle + deterministic quality gate -> waves (1..3)
/// -> evidence at every wave -> next wave. Journaled BEFORE any station mutation.
/// No background jobs, no multi-station atomicity, no safety control.
/// </summary>
public sealed class FleetRolloutManager
{
    private readonly object _gate = new();
    private readonly MultiStationRegistry _fleet;
    private readonly StationTemplateCatalog _catalog;
    private readonly StationUpgradeManager _upgrades;
    private readonly string _folder;
    private readonly List<string> _startupIssues = new();
    public IReadOnlyList<string> StartupIssues { get { lock (_gate) return _startupIssues.ToArray(); } }
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public FleetRolloutManager(IHostEnvironment env, MultiStationRegistry fleet,
        StationTemplateCatalog catalog, StationUpgradeManager upgrades)
    {
        _fleet = fleet; _catalog = catalog; _upgrades = upgrades;
        _folder = Path.Combine(env.ContentRootPath, "data", "rollouts");
        Directory.CreateDirectory(_folder);
        MarkInterruptedAtStartup();
    }
    private void MarkInterruptedAtStartup()
    {
        // Startup-only replay. Never start a station or retry a deployment.
        foreach (var path in Directory.GetFiles(_folder, "roll-*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var record = JsonSerializer.Deserialize<FleetRollout>(File.ReadAllText(path), _json)
                    ?? throw new InvalidOperationException("灰度部署记录为空");
                if (record.Status == "Deploying")
                    Save(record with { Status = "Interrupted",
                        Reason = "启动时发现未完成灰度部署；人工检查单站日志后处理" });
            }
            catch (Exception ex) { lock (_gate) _startupIssues.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
    }
    private void EnsureHealthy()
    { if (_startupIssues.Count > 0) throw new InvalidOperationException("灰度历史存在损坏记录；为避免覆盖未知工站状态，已禁用灰度变更。请先人工修复或归档原文件"); }
    public RolloutRecoveryReport RecoveryReport(string id)
    {
        lock (_gate)
        {
            var batch = Load(id);
            var stations = batch.Steps.Select(step =>
            {
                var station = _fleet.Get(step.StationId);
                var state = station.Machine.Snapshot();
                var flow = station.Flows.Active.Id;
                var recipe = station.Recipes.Active.Id;
                var deployments = _upgrades.History(step.StationId);
                var latest = deployments.FirstOrDefault(d => d.Id == step.DeploymentId) ??
                    (step.Status is "Applying" or "Failed" ? deployments.FirstOrDefault(d =>
                        d.StartedAt.CompareTo(batch.CreatedAt) >= 0) : null);
                var baseMatch = flow == step.ExpectedFlowId && recipe == step.ExpectedRecipeId;
                var targetMatch = step.AfterFlowId != null && step.AfterRecipeId != null &&
                    flow == step.AfterFlowId && recipe == step.AfterRecipeId;
                var blockers = new List<string>();
                if (step.Status is "Applying" or "Failed") blockers.Add("中断/失败步骤需要人工恢复核对");
                if (batch.Status == "Interrupted") blockers.Add("批次在部署阶段遭遇进程中断");
                if (state.Busy || state.State != "Idle" || state.Mode != "Manual" ||
                    state.RecoveryRequired || state.Alarm != null) blockers.Add("运行状态不允许恢复操作");
                if (!baseMatch && !targetMatch) blockers.Add("当前流程/配方既非原始基线也非已记录目标版本");
                if (latest != null && (latest.Status is "Interrupted" or "InProgress" or "ManualRecoveryRequired"))
                    blockers.Add("单站部署日志尚未完结");
                BackupIntegrity? backup = latest == null ? null : _upgrades.InspectBackup(latest.Id);
                if (backup != null && backup.Status != "Verified") blockers.Add("部署备份不可验证：" + backup.Status);
                var category = blockers.Count > 0 ? "ManualReview" :
                    step.Status == "Pending" ? "NotStarted" : "ReviewEvidence";
                return new RecoveryStationReport(step.StationId, step.Status, category, baseMatch,
                    targetMatch, latest?.Id, latest?.Status, backup?.Status,
                    blockers.ToArray());
            }).ToArray();
            return new RolloutRecoveryReport(batch.Id, batch.Status, Stamp(), true,
                stations.Any(s => s.Category == "ManualReview"), stations,
                "此报告只读，不会自动部署、重试、确认完成或回滚。必须人工核实版本与日志。" );
        }
    }
    private static string Stamp() => DateTimeOffset.UtcNow.ToString("O");
    private static string Id() => "roll-" + Guid.NewGuid().ToString("N");
    private string PathFor(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id ?? "", @"\Aroll-[a-f0-9]{32}\z"))
            throw new ArgumentException("无效灰度批次编号");
        return Path.Combine(_folder, id + ".json");
    }
    // Explicit UTC interval, no background execution or hidden time-zone conversions.
    private static DateTimeOffset? ParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt) || dt.Offset != TimeSpan.Zero ||
            !(text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || text.EndsWith("+00:00", StringComparison.Ordinal)))
            throw new ArgumentException("维护窗口必须使用带 Z 或 +00:00 时区的 UTC ISO-8601 时间");
        return dt;
    }
    private static (string? Start, string? End) NormalizeWindow(string? start, string? end)
    {
        var a = ParseUtc(start); var b = ParseUtc(end);
        if (a.HasValue != b.HasValue) throw new ArgumentException("维护窗口必须同时指定开始与结束");
        if (a.HasValue && (b <= a || b - a > TimeSpan.FromDays(7)))
            throw new ArgumentException("维护窗口必须为正时长且最长 7 天");
        return (a?.ToString("O"), b?.ToString("O"));
    }
    private static bool WindowOpen(FleetRollout batch, DateTimeOffset at)
    {
        if (batch.WindowStartUtc is null || batch.WindowEndUtc is null) return true;
        return at >= DateTimeOffset.Parse(batch.WindowStartUtc) && at < DateTimeOffset.Parse(batch.WindowEndUtc);
    }
    private FleetRollout Load(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) throw new ArgumentException("灰度批次不存在");
        FleetRollout value;
        try { value = JsonSerializer.Deserialize<FleetRollout>(File.ReadAllText(path), _json)
            ?? throw new InvalidOperationException("灰度部署记录损坏"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { throw new InvalidOperationException("灰度记录 " + Path.GetFileName(path) + " 损坏，原文件已保留，需人工修复或归档：" + ex.Message, ex); }
        // An interrupted process must not silently continue the next deployment.
        if (value.Status == "Deploying") return value with { Status = "Interrupted",
            Reason = "部署进程中断；核对单站部署历史后人工处理" };
        return value;
    }
    private void Save(FleetRollout value)
    {
        var path = PathFor(value.Id);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value with { UpdatedAt = Stamp() }, _json));
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public FleetRollout[] List() { lock (_gate)
    {
        var rows = new List<FleetRollout>();
        foreach (var file in Directory.GetFiles(_folder, "roll-*.json"))
        { try { rows.Add(Load(Path.GetFileNameWithoutExtension(file))); }
          catch (Exception ex) { var issue = Path.GetFileName(file) + ": " + ex.Message; if (!_startupIssues.Contains(issue)) _startupIssues.Add(issue); } }
        return rows.OrderByDescending(x => x.CreatedAt, StringComparer.Ordinal).ToArray();
    } }
    public FleetRollout Get(string id) { lock (_gate) return Load(id); }
    private static bool IsOpen(string status) => status is not ("Completed" or "Aborted");
    public FleetRollout Create(RolloutCreateRequest request, string actor)
    {
        lock (_gate)
        {
            EnsureHealthy();
            if (request.StationIds == null || request.StationIds.Length is < 2 or > 12)
                throw new ArgumentException("灰度部署要求 2～12 个工站（包含试点）");
            if (request.WaveSize is < 1 or > 3) throw new ArgumentException("每批最多 3 个工站");
            var ids = request.StationIds.Select(MultiStationRegistry.CanonicalStationId).ToArray();
            var pilot = MultiStationRegistry.CanonicalStationId(request.PilotStationId);
            if (ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length || !ids.Contains(pilot))
                throw new ArgumentException("工站编号必须唯一，且包含试点工站");
            var template = _catalog.Get(request.TemplateId);
            if (template.Status != "Published") throw new InvalidOperationException("仅支持已发布的模板");
            var active = List().Where(x => IsOpen(x.Status)).ToArray();
            if (active.Any(x => x.Steps.Any(s => ids.Contains(s.StationId, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidOperationException("目标工站已被其它未关闭的灰度批次占用");
            var order = new[] { pilot }.Concat(ids.Where(x => x != pilot)).ToArray();
            var steps = order.Select(id =>
            {
                var station = _fleet.Get(id);
                return new RolloutStep(id, station.Flows.Active.Id, station.Recipes.Active.Id);
            }).ToArray();
            var window = NormalizeWindow(request.WindowStartUtc, request.WindowEndUtc);
            var now = Stamp();
            var value = new FleetRollout(Id(), template.Id, template.Sha256, pilot,
                request.WaveSize, actor, now, now, "PilotPending", steps, null, window.Start, window.End);
            Save(value); return value;
        }
    }
    public RolloutDiagnostics Diagnose(string id)
    {
        lock (_gate)
        {
            var row = Load(id);
            var blockers = new List<string>();
            var open = WindowOpen(row, DateTimeOffset.UtcNow);
            if (!open) blockers.Add("当前不在 UTC 维护窗口内；仅阻止新部署，不阻止诊断/验收");
            if (row.Status is "Paused" or "Interrupted") blockers.Add("批次已暂停或中断，须先进行人工核查");
            var stations = row.Steps.Select(step =>
            {
                var station = _fleet.Get(step.StationId);
                var snap = station.Machine.Snapshot();
                var currentFlow = station.Flows.Active.Id;
                var currentRecipe = station.Recipes.Active.Id;
                var errors = new List<string>();
                if (snap.Busy || snap.State != "Idle" || snap.Mode != "Manual") errors.Add("工站不是空闲手动模式");
                if (snap.RecoveryRequired || snap.Alarm is not null) errors.Add("工站存在报警或恢复要求");
                var expectedFlow = step.Status is "Verified" or "AwaitingEvidence" ? step.AfterFlowId : step.ExpectedFlowId;
                var expectedRecipe = step.Status is "Verified" or "AwaitingEvidence" ? step.AfterRecipeId : step.ExpectedRecipeId;
                if (currentFlow != expectedFlow || currentRecipe != expectedRecipe) errors.Add("流程/配方与预期版本不一致");
                var unresolved = _upgrades.History(step.StationId)
                    .Where(d => d.Status is "InProgress" or "Interrupted" or "ManualRecoveryRequired")
                    .Select(d => d.Id + ":" + d.Status).ToArray();
                if (unresolved.Length > 0) errors.Add("存在未解决的单站部署日志");
                return new RolloutStationDiagnostic(step.StationId, step.Status, snap.Mode, snap.State,
                    snap.Busy, snap.Alarm, snap.RecoveryRequired, currentFlow, currentRecipe, unresolved, errors.ToArray());
            }).ToArray();
            return new RolloutDiagnostics(row.Id, row.Status, open, row.WindowStartUtc,
                row.WindowEndUtc, blockers.ToArray(), stations);
        }
    }
    // Only a manually investigated failed step still at its ORIGINAL baseline is eligible.
    // Never infer that an ambiguous Applying operation succeeded; never auto-rollback.
    public FleetRollout Reconcile(string id, RolloutReconcileRequest request, string actor)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var row = Load(id);
            if (row.Status is not ("Paused" or "Interrupted"))
                throw new InvalidOperationException("只能在暂停或中断批次执行人工恢复核对");
            if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Trim().Length < 8 || request.Note.Length > 500)
                throw new ArgumentException("人工核对备注必须为 8～500 字符");
            var stationId = MultiStationRegistry.CanonicalStationId(request.StationId);
            var index = Array.FindIndex(row.Steps, s => s.StationId == stationId);
            if (index < 0 || row.Steps[index].Status is not ("Failed" or "Applying"))
                throw new InvalidOperationException("该工站没有待人工核对的失败/中断步骤");
            var step = row.Steps[index];
            var station = _fleet.Get(stationId);
            var snap = station.Machine.Snapshot();
            if (snap.Busy || snap.State != "Idle" || snap.Mode != "Manual" || snap.RecoveryRequired || snap.Alarm is not null)
                throw new InvalidOperationException("工站必须空闲、手动、无报警且无恢复请求");
            if (station.Flows.Active.Id != step.ExpectedFlowId || station.Recipes.Active.Id != step.ExpectedRecipeId)
                // R01 (conservative): a single-station rollback restores CONTENT but
                // publishes NEW version IDs, so the old batch can never return to its
                // recorded baseline by ID. State the alternative path explicitly
                // instead of leaving the operator to guess what the 409 means.
                throw new InvalidOperationException("工站 " + stationId + " 不在批次原始版本基线上（单站回滚恢复内容会发布新的版本 ID，内容恢复不等于 ID 恢复）。旧批次不能在此工站续跑：请先关闭本批次（终止 Abort），再基于当前恢复后的版本重建灰度批次；禁止盲目重试或放宽基线比较。");
            if (_upgrades.History(stationId).Any(d => d.Status is "InProgress" or "Interrupted" or "ManualRecoveryRequired"))
                throw new InvalidOperationException("仍存在未解决的单站部署记录；请先使用单站日志人工恢复");
            var updated = row.Steps.ToArray();
            updated[index] = step with { Status = "Pending", Error = null,
                RecoveryNote = request.Note.Trim(), RecoveryActor = actor, ReconciledAt = Stamp() };
            row = row with { Steps = updated, Status = "Paused", Reason = "人工版本核对完成；确认后点击继续" };
            Save(row);
            return row;
        }
    }
    public FleetRollout Advance(string id, string actor)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var value = Load(id);
            if (value.Status is not ("PilotPending" or "WaveReady"))
                throw new InvalidOperationException("必须完成当前批次的运行证据验证，或先解除暂停");
            if (!WindowOpen(value, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("当前不在 UTC 维护窗口内，拒绝开始部署");
            foreach (var verified in value.Steps.Where(x => x.Status == "Verified"))
            {
                var previous = _fleet.Get(verified.StationId);
                var now = previous.Machine.Snapshot();
                if (previous.Flows.Active.Id != verified.AfterFlowId ||
                    previous.Recipes.Active.Id != verified.AfterRecipeId ||
                    now.RecoveryRequired || now.Alarm is not null)
                    throw new InvalidOperationException(verified.StationId + " 的已验收版本/状态发生漂移，禁止扩批");
            }
            var template = _catalog.Get(value.TemplateId);
            if (template.Status != "Published" || template.Sha256 != value.TemplateHash)
                throw new InvalidOperationException("模板版本已变化，禁止继续扩批");
            var targets = value.Steps.Where(x => x.Status == "Pending")
                .Take(value.Status == "PilotPending" ? 1 : value.WaveSize).ToArray();
            if (targets.Length == 0) throw new InvalidOperationException("没有待部署工站");
            value = value with { Status = "Deploying", Reason = null };
            Save(value);
            foreach (var target in targets)
            {
                // Re-check before EVERY station, not only at the start of a wave.
                // A window expiring mid-wave pauses safely before the next mutation.
                if (!WindowOpen(value, DateTimeOffset.UtcNow))
                {
                    value = value with { Status = "Paused", Reason = "维护窗口已关闭；已升级工站保留待验收，未升级工站不自动重试" };
                    Save(value); return value;
                }
                var idx = Array.FindIndex(value.Steps, x => x.StationId == target.StationId);
                var step = value.Steps[idx];
                var station = _fleet.Get(step.StationId);
                // Optimistic baseline prevents an unrelated publish from being overwritten.
                if (station.Flows.Active.Id != step.ExpectedFlowId || station.Recipes.Active.Id != step.ExpectedRecipeId)
                {
                    value.Steps[idx] = step with { Status = "Failed", Error = "流程或配方基线已变化（可能是单站回滚恢复发布了新版本 ID）；旧批次不能在此站续跑，需关闭批次后基于当前版本重建" };
                    value = value with { Status = "Paused", Reason = step.StationId + " 的版本发生冲突；旧批次不能续跑，请关闭批次并基于当前版本重建" };
                    Save(value); return value;
                }
                // Persist applying intent before entering V1.5 deployment transaction.
                value.Steps[idx] = step with { Status = "Applying" }; Save(value);
                try
                {
                    var plan = _upgrades.Preflight(value.TemplateId, step.StationId, actor);
                    if (!plan.Ready) throw new InvalidOperationException(string.Join("；", plan.Blockers));
                    var deployment = _upgrades.Apply(plan.Id, actor);
                    value.Steps[idx] = step with { Status = "AwaitingEvidence", DeploymentId = deployment.Id,
                        AppliedAt = deployment.FinishedAt ?? Stamp(), AfterFlowId = deployment.AfterFlowId,
                        AfterRecipeId = deployment.AfterRecipeId };
                    Save(value);
                }
                catch (Exception ex)
                {
                    value.Steps[idx] = step with { Status = "Failed", Error = ex.Message };
                    value = value with { Status = "Paused", Reason = step.StationId + " 部署失败：" + ex.Message };
                    Save(value); return value;
                }
            }
            value = value with { Status = "AwaitingEvidence" }; Save(value);
            return value;
        }
    }
    public RolloutEvidence InspectEvidence(string id)
    {
        lock (_gate) return Evidence(Load(id));
    }
    private RolloutEvidence Evidence(FleetRollout value)
    {
        var problems = new List<string>();
        var verified = new List<string>();
        var targets = value.Steps.Where(x => x.Status == "AwaitingEvidence").ToArray();
        if (targets.Length == 0) problems.Add("当前批次没有待验证的工站");
        foreach (var step in targets)
        {
            var station = _fleet.Get(step.StationId);
            var state = station.Machine.Snapshot();
            if (state.Busy || state.State != "Idle" || state.RecoveryRequired || state.Alarm is not null)
            { problems.Add(step.StationId + " 运行状态/报警不满足验收条件"); continue; }
            if (station.Flows.Active.Id != step.AfterFlowId || station.Recipes.Active.Id != step.AfterRecipeId)
            { problems.Add(step.StationId + " 运行版本与部署记录不一致"); continue; }
            var gate = WorkflowQualityGate.Evaluate(station.Config, station.Flows.Active);
            if (!gate.Pass || !WorkflowQualityGate.Verify(gate))
            { problems.Add(step.StationId + " 流程质量门禁未通过"); continue; }
            var since = DateTimeOffset.Parse(step.AppliedAt!);
            var candidates = station.Store.Cycles().Where(c =>
                DateTimeOffset.TryParse(c.StartedAt, out var started) && started >= since).ToArray();
            if (candidates.Any(c => c.Outcome is "Failed" or "Interrupted" or "Stopped"))
            { problems.Add(step.StationId + " 部署后存在失败/中断周期"); continue; }
            var completed = candidates.FirstOrDefault(c => c.Outcome == "Completed" &&
                c.FlowVersion == step.AfterFlowId && c.RecipeId == step.AfterRecipeId);
            if (completed == null)
            { problems.Add(step.StationId + " 缺少部署后使用目标流程与配方的已完成仿真周期"); continue; }
            verified.Add(step.StationId + ":" + completed.Id);
        }
        return new RolloutEvidence(problems.Count == 0, problems.ToArray(), verified.ToArray());
    }
    public FleetRollout Verify(string id)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var value = Load(id);
            if (value.Status != "AwaitingEvidence")
                throw new InvalidOperationException("当前没有可确认的部署批次");
            var evidence = Evidence(value);
            if (!evidence.Pass) throw new InvalidOperationException(string.Join("；", evidence.Blockers));
            var updated = value.Steps.Select(s => s.Status == "AwaitingEvidence"
                ? s with { Status = "Verified", EvidenceCycleId = evidence.VerifiedStations
                    .First(x => x.StartsWith(s.StationId + ":", StringComparison.Ordinal)).Split(':', 2)[1] }
                : s).ToArray();
            value = value with { Steps = updated,
                Status = updated.All(s => s.Status == "Verified") ? "Completed" : "WaveReady" };
            Save(value); return value;
        }
    }
    public FleetRollout Pause(string id, string reason)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var value = Load(id);
            if (value.Status is not ("PilotPending" or "WaveReady" or "AwaitingEvidence"))
                throw new InvalidOperationException("当前状态不能暂停");
            value = value with { Status = "Paused", Reason = string.IsNullOrWhiteSpace(reason) ? "管理员暂停" : reason[..Math.Min(200, reason.Length)] };
            Save(value); return value;
        }
    }
    public FleetRollout Resume(string id)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var value = Load(id);
            if (value.Status != "Paused") throw new InvalidOperationException("当前不是已暂停状态");
            // A failed / interrupted station needs manual reconciliation, not blind retries.
            if (value.Steps.Any(s => s.Status is "Failed" or "Applying"))
                throw new InvalidOperationException("存在失败/中断工站，必须人工处理并关闭批次，不能盲目重试");
            var next = value.Steps.Any(s => s.Status == "AwaitingEvidence") ? "AwaitingEvidence" :
                value.Steps[0].Status == "Pending" ? "PilotPending" : "WaveReady";
            value = value with { Status = next, Reason = null }; Save(value); return value;
        }
    }
    public FleetRollout Abort(string id, string reason)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var value = Load(id);
            if (!IsOpen(value.Status)) throw new InvalidOperationException("批次已经结束");
            value = value with { Status = "Aborted", Reason = "人工结束：" + (reason ?? "") };
            // Abort DOES NOT rollback deployments; admins must use individual deployment journal.
            Save(value); return value;
        }
    }
}
