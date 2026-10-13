using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceControl.Api;

public sealed record StationUpgradePlan(string Id, string TemplateId, string StationId,
    string ExpectedFlowId, string ExpectedRecipeId, string TemplateHash, string CreatedAt,
    string ExpiresAt, string Actor, bool Ready, string[] Blockers);
public sealed record UpgradePreflightRequest(string TemplateId, string StationId);
public sealed record UpgradeApplyRequest(string PlanId);
public sealed record UpgradeRollbackRequest(string DeploymentId, string? ExpectedCurrentFlowId = null,
    string? ExpectedCurrentRecipeId = null, string? RecoveryNote = null);
public sealed record BackupIntegrity(string DeploymentId, string Status, string? ActualSha256, string Message);
public sealed record StationDeploymentRecord(string Id, string StationId, string TemplateId, string PlanId,
    string Actor, string StartedAt, string? FinishedAt, string Status, string? Error,
    FlowRevision BeforeFlow, RecipeRevision BeforeRecipe,
    string? AfterFlowId, string? AfterRecipeId, string? BackupFile = null, string? BackupSha256 = null,
    string? RecoveryNote = null, string? RecoveryObservedBy = null, string? RecoveryObservedAt = null,
    string? RecoveryObservedFlowId = null, string? RecoveryObservedRecipeId = null);

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
    private readonly List<string> _startupIssues = new();
    // F04: lock-free unresolved-deployment snapshot. DeviceRuntime consults this on
    // cycle start / Auto / configuration change; taking _gate there would invert the
    // lock order against Apply (which holds _gate across the machine lock).
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _unresolved =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    private string? _activeApplyId;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public StationUpgradeManager(IHostEnvironment environment, MultiStationRegistry fleet, StationTemplateCatalog catalog)
    {
        _fleet = fleet; _catalog = catalog;
        _folder = Path.Combine(environment.ContentRootPath, "data", "deployments");
        Directory.CreateDirectory(_folder);
        MarkInterruptedAtStartup();
    }
    public IReadOnlyList<string> StartupIssues { get { lock (_gate) return _startupIssues.ToArray(); } }
    // Replay ONLY the durable intent status. Never retry, infer success, or roll back on boot.
    // An incomplete journal entry is an explicit manual recovery condition.
    private void MarkInterruptedAtStartup()
    {
        foreach (var path in Directory.GetFiles(_folder, "dep-*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var row = ReadRecord(id);
                if (row.Status == "InProgress")
                    Persist(row with { Status = "Interrupted", Error =
                        "服务重启发现部署意图未完结；禁止自动重试，核对备份与现行版本" });
            }
            catch (Exception ex)
            {
                // F13: an unreadable deployment record must never be silently ignored.
                // It fails closed (see UnresolvedReason) and is surfaced for diagnosis.
                lock (_gate) _startupIssues.Add(id + ": " + ex.Message);
            }
        }
        RefreshUnresolved();
    }
    /// <summary>F04: station-scoped unresolved-deployment reason, or null. An unreadable
    /// record fails closed for every station (key "") because its owner is unknown.</summary>
    public string? UnresolvedReason(string stationId, string? exceptDeploymentId = null)
    {
        var snapshot = Volatile.Read(ref _unresolved);
        if (snapshot.TryGetValue("", out var global) && global.Any(x => x.Key != exceptDeploymentId))
            return global.First(x => x.Key != exceptDeploymentId).Value;
        if (!snapshot.TryGetValue(stationId, out var station)) return null;
        return station.FirstOrDefault(x => x.Key != exceptDeploymentId).Value;
    }
    private void RefreshUnresolved()
    {
        var found = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(_folder, "dep-*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var row = ReadRecord(id);
                if (row.Id == _activeApplyId) continue;
                if (row.Status is "InProgress" or "Interrupted" or "ManualRecoveryRequired")
                {
                    var key = string.IsNullOrWhiteSpace(row.StationId) ? "" : row.StationId;
                    if (!found.TryGetValue(key, out var entries)) found[key] = entries = new(StringComparer.Ordinal);
                    entries[row.Id] = $"存在未解决的部署 {row.Id}（{row.Status}），必须先人工检查并回滚";
                }
            }
            catch
            {
                if (!found.TryGetValue("", out var entries)) found[""] = entries = new(StringComparer.Ordinal);
                entries[id] = $"部署记录 {id} 损坏且无法读取，已阻止所有工站运行；请人工核对后修复或归档该记录";
            }
        }
        Volatile.Write(ref _unresolved, found.ToDictionary(x => x.Key,
            x => (IReadOnlyDictionary<string, string>)x.Value, StringComparer.OrdinalIgnoreCase));
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
            if (string.IsNullOrEmpty(record.StationId) ||
                !System.Text.RegularExpressions.Regex.IsMatch(record.StationId, @"\A[A-Z][A-Z0-9_-]{0,15}\z"))
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
            // F12: drop expired plans instead of letting the dictionary grow forever.
            var purgeBefore = DateTimeOffset.UtcNow;
            foreach (var stale in _plans.Where(kv => purgeBefore > DateTimeOffset.Parse(kv.Value.ExpiresAt))
                                       .Select(kv => kv.Key).ToList())
                _plans.Remove(stale);
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
                // F04: this deployment owns its own InProgress record while applying;
                // the eligibility snapshot must not block the apply itself.
                _activeApplyId = record.Id;
                try
                {
                    Persist(record); // durable recovery evidence BEFORE either write
                    RefreshUnresolved();
                    // V1.5 online SQLite backup; never copy a live WAL file with File.Copy.
                    var backupDir = Path.Combine(_folder, "backups", station.Id);
                    var backup = Path.Combine(backupDir, record.Id + ".db");
                    var digest = station.Store.CreateVerifiedBackup(backup);
                    record = record with { BackupFile = Path.GetRelativePath(_folder, backup), BackupSha256 = digest };
                    Persist(record); RefreshUnresolved();
                    var flowDraft = station.Flows.Draft(Clone(template.Config.Sequence), actor, beforeFlow.Id);
                    station.Flows.Validate(flowDraft.Id);
                    var activeFlow = station.Machine.PublishFlow(flowDraft.Id);
                    record = record with { AfterFlowId = activeFlow.Id }; Persist(record); RefreshUnresolved();
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
                finally { _activeApplyId = null; RefreshUnresolved(); }
            });
        }
    }
    public StationDeploymentRecord Rollback(string deploymentId, string actor,
        string? expectedCurrentFlowId = null, string? expectedCurrentRecipeId = null, string? recoveryNote = null)
    {
        lock (_gate)
        {
            var record = ReadRecord(deploymentId);
            if (record.Status is not ("Succeeded" or "InProgress" or "Interrupted" or "ManualRecoveryRequired"))
                throw new InvalidOperationException("此部署记录不允许回滚");
            var station = _fleet.Get(record.StationId);
            var recovering = record.Status is "InProgress" or "Interrupted" or "ManualRecoveryRequired";
            if (recovering)
            {
                var state = station.Machine.Snapshot();
                if (state.Busy || state.State != "Idle" || state.Mode != "Manual")
                    throw new InvalidOperationException("部署恢复要求工站空闲且处于手动模式");
                var currentFlow = station.Flows.Active.Id;
                var currentRecipe = station.Recipes.Active.Id;
                var flowKnown = currentFlow == record.BeforeFlow.Id || currentFlow == record.AfterFlowId;
                var recipeKnown = currentRecipe == record.BeforeRecipe.Id || currentRecipe == record.AfterRecipeId;
                if (!flowKnown || !recipeKnown)
                {
                    if (string.IsNullOrWhiteSpace(recoveryNote) || recoveryNote.Trim().Length < 8 || recoveryNote.Trim().Length > 500 ||
                        expectedCurrentFlowId != currentFlow || expectedCurrentRecipeId != currentRecipe)
                        throw new InvalidOperationException("现行版本与部署日志结果未知；须由管理员提交与现行版本完全一致的核对值及至少 8 字符说明后才可受控恢复");
                    record = record with { RecoveryNote = recoveryNote.Trim(), RecoveryObservedBy = actor,
                        RecoveryObservedAt = DateTimeOffset.UtcNow.ToString("O"),
                        RecoveryObservedFlowId = currentFlow, RecoveryObservedRecipeId = currentRecipe };
                    Persist(record);
                }
                if ((currentFlow != record.BeforeFlow.Id || currentRecipe != record.BeforeRecipe.Id) &&
                    InspectBackup(record.Id).Status != "Verified")
                    throw new InvalidOperationException("部署备份缺失或校验失败，拒绝回滚");
            }
            Func<StationDeploymentRecord> rollback = () => station.Machine.WithConfigurationChange(() =>
                {
                    // For completed deployments, refuse any drift from the exact recorded after-state.
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
                        Persist(record); RefreshUnresolved(); return record;
                    }
                    catch (Exception ex)
                    {
                        record = record with { Status = "ManualRecoveryRequired", Error = ex.ToString(),
                            FinishedAt = DateTimeOffset.UtcNow.ToString("O") };
                        Persist(record); RefreshUnresolved(); throw;
                    }
                });
            return recovering ? station.Machine.WithDeploymentRecoveryContext(record.Id, rollback) : rollback();
        }
    }
}
