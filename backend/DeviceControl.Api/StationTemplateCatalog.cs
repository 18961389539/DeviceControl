using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceControl.Api;

public sealed record StationTemplateRevision(string Id, string Name, string Status, string SourceStation,
    string Author, string CreatedAt, string Sha256, StationConfig Config, RecipeValues Recipe);
public sealed record CaptureTemplateRequest(string StationId, string Name);
public sealed record DeployTemplateRequest(List<string> StationIds);
public sealed record ConfigDifference(string Path, string? TemplateValue, string? StationValue);

/// <summary>
/// Simulation-only immutable station templates. Draft->Validated->Published is explicit;
/// only a published snapshot can instantiate a NEW station, never hot-swap a running one.
/// </summary>
public sealed class StationTemplateCatalog
{
    private readonly object _gate = new();
    private readonly string _file;
    private List<StationTemplateRevision> _versions;
    private readonly List<string> _startupIssues = new();
    public IReadOnlyList<string> StartupIssues { get { lock (_gate) return _startupIssues.ToArray(); } }
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public StationTemplateCatalog(IHostEnvironment env)
    {
        var folder = Path.Combine(env.ContentRootPath, "data", "templates");
        Directory.CreateDirectory(folder);
        _file = Path.Combine(folder, "catalog.json");
        _versions = new();
        if (File.Exists(_file))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<StationTemplateRevision>>(File.ReadAllText(_file), Json)
                    ?? throw new InvalidOperationException("模板目录为空或损坏");
                foreach (var row in loaded)
                {
                    ValidateSnapshot(row);
                    if (row.Status is not ("Draft" or "Validated" or "Published"))
                        throw new InvalidOperationException("模板目录包含未知状态");
                }
                _versions = loaded;
            }
            catch (Exception ex) { _startupIssues.Add(Path.GetFileName(_file) + ": " + ex.Message); }
        }
    }
    private void EnsureHealthy()
    { if (_startupIssues.Count > 0) throw new InvalidOperationException("模板目录损坏，已保留原文件并禁用模板修改；请先人工修复或归档"); }
    public IReadOnlyList<StationTemplateRevision> List()
    {
        lock (_gate) return _versions.AsEnumerable().Reverse().Select(Clone).ToArray();
    }
    public StationTemplateRevision Get(string id)
    {
        lock (_gate) return Clone(Find(id));
    }
    private StationTemplateRevision Find(string id) => _versions.FirstOrDefault(x => x.Id == id)
        ?? throw new ArgumentException("模板版本不存在");
    private static T DeepCopy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static StationTemplateRevision Clone(StationTemplateRevision revision) => DeepCopy(revision);
    private static string Hash(StationConfig config, RecipeValues recipe)
    {
        var snapshot = JsonSerializer.Serialize(new { config, recipe });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
    }
    private static void ValidateSnapshot(StationTemplateRevision revision)
    {
        revision.Config.Validate();
        if (revision.Recipe.Name is null || revision.Recipe.Name.Length is < 1 or > 50 ||
            !double.IsFinite(revision.Recipe.VisionOffsetMm) || Math.Abs(revision.Recipe.VisionOffsetMm) > 10 ||
            revision.Recipe.ExtraDwellMs is < 0 or > 1000)
            throw new InvalidOperationException("模板配方参数越界");
        if (revision.Sha256 != Hash(revision.Config, revision.Recipe))
            throw new InvalidOperationException("模板快照哈希校验失败");
    }
    private static void AssertFlowQuality(StationTemplateRevision revision)
    {
        var flowBytes = JsonSerializer.Serialize(revision.Config.Sequence);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(flowBytes)));
        var flow = new FlowRevision(revision.Id, "Validated", null, revision.Author,
            revision.CreatedAt, hash, revision.Config.Sequence, "Template preflight");
        var report = WorkflowQualityGate.Evaluate(revision.Config, flow);
        if (!report.Pass)
            throw new InvalidOperationException("模板流程未通过质量门禁：" +
                (report.Errors.FirstOrDefault() ?? report.Results.FirstOrDefault(r => !r.Pass)?.Name ?? "未知原因"));
    }
    // F02: persist the candidate list first; swap memory only after the file commit
    // succeeds so a failed write can never leave a phantom in-memory revision.
    private void Persist(List<StationTemplateRevision> rows)
    {
        var tmp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(rows, Json));
            File.Move(tmp, _file, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public StationTemplateRevision Capture(MultiStationRegistry fleet, string stationId, string name, string author)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60)
            throw new ArgumentException("模板名称长度必须为 1~60 字符");
        var source = fleet.Get(stationId);
        var config = DeepCopy(source.Config);
        // The published flow, not the seed config, is the material to reproduce.
        config.Sequence = DeepCopy(source.Flows.Active.Definition);
        config.FlowVersion = "template-candidate";
        config.Validate();
        var recipe = DeepCopy(source.Recipes.Active.Values);
        var revision = new StationTemplateRevision("tmpl-" + Guid.NewGuid().ToString("N")[..12],
            name.Trim(), "Draft", source.Id, author, DateTimeOffset.UtcNow.ToString("O"),
            Hash(config, recipe), config, recipe);
        lock (_gate)
        {
            EnsureHealthy();
            var candidate = new List<StationTemplateRevision>(_versions) { revision };
            Persist(candidate);
            _versions = candidate;
            return Clone(revision);
        }
    }
    public StationTemplateRevision Validate(string id)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var r = Find(id);
            if (r.Status != "Draft") throw new InvalidOperationException("只能验证模板草稿");
            ValidateSnapshot(r);
            AssertFlowQuality(r);
            var next = r with { Status = "Validated" };
            var candidate = new List<StationTemplateRevision>(_versions);
            candidate[candidate.IndexOf(r)] = next;
            Persist(candidate);
            _versions = candidate;
            return Clone(next);
        }
    }
    public StationTemplateRevision Publish(string id)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var r = Find(id);
            if (r.Status != "Validated") throw new InvalidOperationException("只能发布已验证的模板");
            ValidateSnapshot(r);
            AssertFlowQuality(r);
            var next = r with { Status = "Published" };
            var candidate = new List<StationTemplateRevision>(_versions);
            candidate[candidate.IndexOf(r)] = next;
            Persist(candidate);
            _versions = candidate;
            return Clone(next);
        }
    }
    public object Diff(string id, StationContext station)
    {
        var revision = Get(id);
        var desired = DeepCopy(revision.Config);
        desired.ProfileId = station.Config.ProfileId; // each deployment keeps its unique station identity
        desired.FlowVersion = station.Config.FlowVersion;
        var a = Flatten(JsonSerializer.SerializeToElement(new { config = desired, recipe = revision.Recipe }, Json));
        var current = DeepCopy(station.Config);
        current.Sequence = DeepCopy(station.Flows.Active.Definition);
        var b = Flatten(JsonSerializer.SerializeToElement(new { config = current, recipe = station.Recipes.Active.Values }, Json));
        var changes = a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal)
            .Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k))
            .Select(k => new ConfigDifference(k, a.GetValueOrDefault(k), b.GetValueOrDefault(k))).Take(150).ToArray();
        return new { templateId = revision.Id, stationId = station.Id, changes, truncated = changes.Length >= 150,
            note = "只读对比；现有工站不允许在线覆盖机构配置" };
    }
    private static Dictionary<string, string?> Flatten(JsonElement root)
    {
        var rows = new Dictionary<string, string?>(StringComparer.Ordinal);
        void Visit(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var p in value.EnumerateObject()) Visit(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name);
            else if (value.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var p in value.EnumerateArray()) Visit(p, path + "[" + i++ + "]");
            }
            else rows[path] = value.ToString();
        }
        Visit(root, ""); return rows;
    }
    public object Deploy(string id, List<string> stationIds, MultiStationRegistry fleet)
    {
        var revision = Get(id);
        if (revision.Status != "Published") throw new InvalidOperationException("必须先发布模板，才能创建工站");
        ValidateSnapshot(revision);
        AssertFlowQuality(revision);
        // One request is an atomic admission check but not a filesystem transaction.
        // Never imply that multi-station provisioning can be rolled back automatically.
        if (stationIds is null || stationIds.Count is < 1 or > 5)
            throw new ArgumentException("单次创建数量为 1~5 个工站");
        var ids = stationIds.Select(MultiStationRegistry.CanonicalStationId).ToArray();
        fleet.ValidateNewIds(ids);
        var created = new List<string>();
        foreach (var key in ids)
        {
            try { fleet.Create(key, revision.Config, revision.Recipe); created.Add(key); }
            catch (Exception ex)
            {
                return new { ok = false, created, failed = key, error = ex.Message,
                    note = "已创建的工站不会自动删除；部署非跨文件系统原子事务" };
            }
        }
        return new { ok = true, created, failed = (string?)null, error = (string?)null,
            note = "新工站已创建，工作流和配方使用模板快照；未改变已有工站" };
    }
}
