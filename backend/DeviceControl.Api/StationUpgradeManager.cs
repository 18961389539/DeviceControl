using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceControl.Api;

public sealed record StationUpgradePlan(string Id, string TemplateId, string StationId,
    string ExpectedFlowId, string ExpectedRecipeId, string TemplateHash, string CreatedAt,
    string ExpiresAt, string Actor, bool Ready, string[] Blockers);
public sealed record UpgradePreflightRequest(string TemplateId, string StationId);
public sealed record UpgradeApplyRequest(string PlanId);
public sealed record UpgradeRollbackRequest(string DeploymentId);
public sealed record BackupIntegrity(string DeploymentId, string Status, string? ActualSha256, string Message);
public sealed record StationDeploymentRecord(string Id, string StationId, string TemplateId, string PlanId,
    string Actor, string StartedAt, string? FinishedAt, string Status, string? Error,
    FlowRevision BeforeFlow, RecipeRevision BeforeRecipe,
    string? AfterFlowId, string? AfterRecipeId, string? BackupFile = null, string? BackupSha256 = null);

/// <summary>
/// V1.5: Simulation-only existing-station software upgrade (FLOW+RECIPE ONLY).
/// Station structure (axes/IO/cylinders/interlocks) must be identical. A plan
/// has an expiring optimistic version token. Durable intent precedes writes.
/// Deployment is NOT an atomic transaction across SQLite flow and recipe stores.
/// An interrupted deployment must be explicitly recovered, never silently retried.
/// </summary>
public sealed class StationUpgradeManager
{
    private readonly object _gate = new();
    private readonly MultiStationRegistry _fleet;
    private readonly StationTemplateCatalog _catalog;
    private readonly string _folder;
    private readonly Dictionary<string, StationUpgradePlan> _plans = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public StationUpgradeManager(IHostEnvironment environment, MultiStationRegistry fleet, StationTemplateCatalog catalog)
    {
        _fleet = fleet; _catalog = catalog;
        _folder = Path.Combine(environment.ContentRootPath, "data", "deployments");
        Directory.CreateDirectory(_folder);
        MarkInterruptedAtStartup();
    }
    // Replay ONLY the durable intent status. Never retry, infer success, or roll back on boot.
    // An incomplete journal entry is an explicit manual recovery condition.
    private void MarkInterruptedAtStartup()
    {
        foreach (var path in Directory.GetFiles(_folder, "dep-*.json"))
        {
            var row = ReadRecord(Path.GetFileNameWithoutExtension(path));
            if (row.Status == "InProgress")
                Persist(row with { Status = "Interrupted", Error =
                    "服务重启发现部署意图未完结；禁止自动重试，核对备份与现行版本" });
        }
    }
    public BackupIntegrity InspectBackup(string deploymentId)
    {
        lock (_gate)
        {
            var record = ReadRecord(deploymentId);
            if (string.IsNullOrWhiteSpace(record.BackupFile) || string.IsNullOrWhiteSpace(record.BackupSha256))
                return new BackupIntegrity(record.Id, "Missing", null,
                    "没有完整的备份文件和哈希记录；不得将其视为可恢复备份");
            // Reject a forged station identifier in the persisted journal before
            // deriving a filesystem path; canonical IDs cannot contain dot segments.
            if (!System.Text.RegularExpressions.Regex.IsMatch(record.StationId ?? "", @"\A[A-Z][A-Z0-9_-]{0,15}\z"))
                return new BackupIntegrity(record.Id, "InvalidPath", null, "部署日志工站编号非法");
            // A journal must reference THIS deployment's backup only. Prefix checks alone
            // allowed a forged journal to borrow a different station's valid backup.
            // Reject reparse points at EVERY existing component below our data folder.
            var root = Path.GetFullPath(_folder);
            var expected = Path.GetFullPath(Path.Combine(root, "backups", record.StationId, record.Id + ".db"));
            var relative = record.BackupFile;
            if (Path.IsPathFullyQualified(relative) ||
                !string.Equals(Path.GetFullPath(Path.Combine(root, relative)), expected, StringComparison.Ordinal) ||
                new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                (Directory.Exists(Path.Combine(root, "backups")) &&
                    new DirectoryInfo(Path.Combine(root, "backups")).Attributes.HasFlag(FileAttributes.ReparsePoint)) ||
                (Directory.Exists(Path.GetDirectoryName(expected)!) &&
                    new DirectoryInfo(Path.GetDirectoryName(expected)!).Attributes.HasFlag(FileAttributes.ReparsePoint)) ||
                (File.Exists(expected) && new FileInfo(expected).Attributes.HasFlag(FileAttributes.ReparsePoint)))
                return new BackupIntegrity(record.Id, "InvalidPath", null, "备份路径不是本次部署专用文件或包含符号链接");
            var absolute = expected;
            if (!File.Exists(absolute)) return new BackupIntegrity(record.Id, "Missing", null, "备份文件不存在");
            try
            {
                using var stream = File.OpenRead(absolute);
                var sha = Convert.ToHexString(SHA256.HashData(stream));
                return sha.Equals(record.BackupSha256, StringComparison.OrdinalIgnoreCase)
                    ? new BackupIntegrity(record.Id, "Verified", sha, "备份 SHA-256 校验通过；仍需检查 SQLite 完整性")
                    : new BackupIntegrity(record.Id, "Mismatch", sha, "备份 SHA-256 不匹配，禁止按此文件恢复");
            }
            catch (IOException ex) { return new BackupIntegrity(record.Id, "Unreadable", null, ex.Message); }
        }
    }
    private string PathFor(string id) => Path.Combine(_folder, id + ".json");
    private static string Token() => Guid.NewGuid().ToString("N");
    private static T Clone<T>(T v) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v))!;
    private static string StructuralJson(StationConfig config)
    {
        var c = Clone(config);
        c.ProfileId = "IGNORED";
        c.FlowVersion = "IGNORED";
        return JsonSerializer.Serialize(new { c.Driver, c.Signals, c.Axes, c.Cylinders, c.Interlocks });
    }
    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    private static string[] Blockers(StationTemplateRevision template, StationContext station)
    {
        var problems = new List<string>();
        if (template.Status != "Published") problems.Add("模板必须已发布");
        var state = station.Machine.Snapshot();
        if (state.State != "Idle" || state.Busy) problems.Add("工站必须空闲");
        if (state.Mode != "Manual") problems.Add("工站必须处于手动模式");
        if (state.RecoveryRequired || state.Alarm is not null) problems.Add("工站存在报警或待恢复状态");
        if (StructuralJson(template.Config) != StructuralJson(station.Config))
            problems.Add("机构/信号/互锁定义不同；V1.5 不支持覆盖运行时结构");
        return problems.ToArray();
    }
    private void Persist(StationDeploymentRecord value)
    {
        var path = PathFor(value.Id);
        var temp = path + "." + Token() + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, _json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private StationDeploymentRecord ReadRecord(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"\Adep-[a-f0-9]{32}\z"))
            throw new ArgumentException("无效部署编号");
        var path = PathFor(id);
        if (!File.Exists(path)) throw new ArgumentException("部署记录不存在");
        return JsonSerializer.Deserialize<StationDeploymentRecord>(File.ReadAllText(path), _json)
            ?? throw new InvalidOperationException("部署记录损坏");
    }
    private StationDeploymentRecord[] ReadAll() => Directory.GetFiles(_folder, "dep-*.json")
        .Select(p => ReadRecord(System.IO.Path.GetFileNameWithoutExtension(p)))
        .OrderByDescending(x => x.StartedAt).ToArray();
    private void AssertNoUnresolved(string stationId)
    {
        if (ReadAll().Any(x => x.StationId == stationId && (x.Status is "InProgress" or "Interrupted" or "ManualRecoveryRequired")))
            throw new InvalidOperationException("存在未解决的中断/失败部署，必须先人工检查并回滚");
    }
    public IReadOnlyList<StationDeploymentRecord> History(string? stationId = null)
    {
        lock (_gate)
        {
            if (stationId is not null) _fleet.Get(stationId);
            return ReadAll().Where(x => stationId == null || x.StationId.Equals(stationId, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }
    public StationUpgradePlan Preflight(string templateId, string stationId, string actor)
    {
        lock (_gate)
        {
            var station = _fleet.Get(stationId);
            var template = _catalog.Get(templateId);
            var blockers = Blockers(template, station).ToList();
            if (ReadAll().Any(x => x.StationId == station.Id && (x.Status is "InProgress" or "Interrupted" or "ManualRecoveryRequired")))
                blockers.Add("存在中断部署或未解决的失败记录");
            var now = DateTimeOffset.UtcNow;
            var plan = new StationUpgradePlan("plan-" + Token(), templateId, station.Id,
                station.Flows.Active.Id, station.Recipes.Active.Id, template.Sha256,
                now.ToString("O"), now.AddMinutes(10).ToString("O"), actor,
                blockers.Count == 0, blockers.ToArray());
            // Preflight has no lasting authority. Plan expires and can be used once.
            if (plan.Ready) _plans.Add(plan.Id, plan);
            return plan;
        }
    }
    public StationDeploymentRecord Apply(string planId, string actor)
    {
        lock (_gate)
        {
            if (!_plans.Remove(planId, out var plan) || !plan.Ready)
                throw new InvalidOperationException("部署预检不存在、失败或已使用");
            if (DateTimeOffset.UtcNow > DateTimeOffset.Parse(plan.ExpiresAt))
                throw new InvalidOperationException("部署预检已过期，请重新检查");
            var station = _fleet.Get(plan.StationId);
            var template = _catalog.Get(plan.TemplateId);
            AssertNoUnresolved(station.Id);
            if (template.Sha256 != plan.TemplateHash || template.Status != "Published" ||
                station.Flows.Active.Id != plan.ExpectedFlowId || station.Recipes.Active.Id != plan.ExpectedRecipeId)
                throw new InvalidOperationException("工站版本或模板已改变，需要重新预检");
            var blocked = Blockers(template, station);
            if (blocked.Length > 0) throw new InvalidOperationException(string.Join("；", blocked));
            return station.Machine.WithConfigurationChange(() =>
            {
                // Hold runtime's lock over BOTH publications; cycle starts cannot interleave.
                var beforeFlow = Clone(station.Flows.Active);
                var beforeRecipe = Clone(station.Recipes.Active);
                var record = new StationDeploymentRecord("dep-" + Token(), station.Id, template.Id, plan.Id,
                    actor, DateTimeOffset.UtcNow.ToString("O"), null, "InProgress", null,
                    beforeFlow, beforeRecipe, null, null);
                Persist(record); // durable recovery evidence BEFORE either write
                try
                {
                    // V1.5 online SQLite backup; never copy a live WAL file with File.Copy.
                    var backupDir = Path.Combine(_folder, "backups", station.Id);
                    var backup = Path.Combine(backupDir, record.Id + ".db");
                    var digest = station.Store.CreateVerifiedBackup(backup);
                    record = record with { BackupFile = Path.GetRelativePath(_folder, backup), BackupSha256 = digest };
                    Persist(record);
                    var flowDraft = station.Flows.Draft(Clone(template.Config.Sequence), actor, beforeFlow.Id);
                    station.Flows.Validate(flowDraft.Id);
                    var activeFlow = station.Machine.PublishFlow(flowDraft.Id);
                    record = record with { AfterFlowId = activeFlow.Id }; Persist(record);
                    var recipeDraft = station.Recipes.Draft(Clone(template.Recipe), actor);
                    station.Recipes.Validate(recipeDraft.Id);
                    var activeRecipe = station.Recipes.Publish(recipeDraft.Id, station.Machine);
                    record = record with { AfterRecipeId = activeRecipe.Id, Status = "Succeeded",
                        FinishedAt = DateTimeOffset.UtcNow.ToString("O") };
                    Persist(record);
                    return record;
                }
                catch (Exception ex)
                {
                    var failures = new List<string>();
                    // Restore only what has changed. Preserve a visible recovery-required state on failure.
                    try { if (station.Recipes.Active.Id != beforeRecipe.Id)
                            station.Recipes.Rollback(beforeRecipe.Id, actor, station.Machine); }
                    catch (Exception rollbackEx) { failures.Add("Recipe: " + rollbackEx.Message); }
                    try { if (station.Flows.Active.Id != beforeFlow.Id)
                            station.Machine.RollbackFlow(beforeFlow.Id, actor); }
                    catch (Exception rollbackEx) { failures.Add("Flow: " + rollbackEx.Message); }
                    record = record with { Status = failures.Count == 0 ? "FailedCompensated" : "ManualRecoveryRequired",
                        Error = ex.Message + (failures.Count == 0 ? "" : "；回滚错误：" + string.Join("；", failures)),
                        FinishedAt = DateTimeOffset.UtcNow.ToString("O"),
                        AfterFlowId = station.Flows.Active.Id, AfterRecipeId = station.Recipes.Active.Id };
                    Persist(record);
                    // Surface failure to caller, preserve recovery evidence in durable journal.
                    throw new InvalidOperationException("部署失败 (" + record.Id + ")：" + record.Error, ex);
                }
            });
        }
    }
    public StationDeploymentRecord Rollback(string deploymentId, string actor)
    {
        lock (_gate)
        {
            var record = ReadRecord(deploymentId);
            if (record.Status is not ("Succeeded" or "InProgress" or "Interrupted" or "ManualRecoveryRequired"))
                throw new InvalidOperationException("此部署记录不允许回滚");
            var station = _fleet.Get(record.StationId);
            return station.Machine.WithConfigurationChange(() =>
            {
                // If someone has published a newer version, a blind rollback is unsafe.
                if (record.Status == "Succeeded" &&
                    (station.Flows.Active.Id != record.AfterFlowId || station.Recipes.Active.Id != record.AfterRecipeId))
                    throw new InvalidOperationException("发布版本已变化，拒绝覆盖；先人工核对");
                try
                {
                    if (station.Recipes.Active.Id != record.BeforeRecipe.Id)
                        station.Recipes.Rollback(record.BeforeRecipe.Id, actor, station.Machine);
                    if (station.Flows.Active.Id != record.BeforeFlow.Id)
                        station.Machine.RollbackFlow(record.BeforeFlow.Id, actor);
                    record = record with { Status = "RolledBack", Error = null,
                        FinishedAt = DateTimeOffset.UtcNow.ToString("O") };
                    Persist(record); return record;
                }
                catch (Exception ex)
                {
                    record = record with { Status = "ManualRecoveryRequired", Error = ex.Message,
                        FinishedAt = DateTimeOffset.UtcNow.ToString("O") };
                    Persist(record); throw;
                }
            });
        }
    }
}
