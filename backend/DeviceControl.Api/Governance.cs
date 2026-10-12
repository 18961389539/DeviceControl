using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeviceControl.Api;

// STRICTLY a localhost SIMULATION demonstrator. In-memory sessions and fixed demo credentials
// are deliberately unsuitable for deployment to a production machine or a shared network.
public sealed record SimUser(string Name, string Role);
public sealed class DemoSessions
{
    private readonly ConcurrentDictionary<string, (SimUser User, DateTimeOffset Expires)> _sessions = new();
    private static readonly IReadOnlyDictionary<string, string> Roles = new Dictionary<string, string>
    {
        ["operator"] = "Operator", ["engineer"] = "Engineer", ["admin"] = "Admin"
    };
    public (string Token, SimUser User) Login(string username, string password)
    {
        if (!Roles.TryGetValue(username, out var role) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes("demo123")))
            throw new UnauthorizedAccessException("账号或密码错误（仅演示账号）");
        // F12: actively purge expired sessions instead of waiting for token access.
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _sessions.Keys)
            if (_sessions.TryGetValue(key, out var entry) && entry.Expires <= now)
                _sessions.TryRemove(key, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var user = new SimUser(username, role);
        _sessions[token] = (user, now.AddHours(8));
        return (token, user);
    }
    public SimUser? Get(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_sessions.TryGetValue(token, out var entry)) return null;
        if (entry.Expires > DateTimeOffset.UtcNow) return entry.User;
        _sessions.TryRemove(token, out _); return null;
    }
    public void Logout(string? token)
    {
        if (token != null) _sessions.TryRemove(token, out _);
    }
}
public static class Permissions
{
    public static int Rank(string role) => role switch { "Operator" => 1, "Engineer" => 2, "Admin" => 3, _ => 0 };
    public static int Required(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        // Strip the explicit station prefix before applying legacy per-action RBAC.
        // All unrecognized mutations remain Admin-only (fail closed).
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("stations", StringComparison.OrdinalIgnoreCase))
            path = "/api/" + string.Join("/", parts.Skip(3));
        var verb = request.Method;
        if (verb == "GET") return path.StartsWith("/api/audit", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/upgrades/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/rollouts", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (path.StartsWith("/api/rollouts", StringComparison.OrdinalIgnoreCase)) return 3;
        if (path == "/api/upgrades/preflight") return 2;
        if (path.StartsWith("/api/upgrades/")) return 3;
        if (path == "/api/templates/capture" || (path.StartsWith("/api/templates/") && path.EndsWith("/validate"))) return 2;
        if (path.StartsWith("/api/templates/")) return 3;
        if (path == "/api/auth/logout" || path == "/api/cycle/start" || path == "/api/cycle/stop") return 1;
        if (path == "/api/mode") return 1;
        if (path == "/api/fault/reset" || path == "/api/recovery/confirm" || path == "/api/flow/debug") return 2;
        if (path == "/api/flows/draft" || (path.StartsWith("/api/flows/") && path.EndsWith("/validate"))) return 2;
        if (path.StartsWith("/api/flows/")) return 3;
        if (path.StartsWith("/api/manual/") || path.StartsWith("/api/sim/")) return 2;
        if (path == "/api/recipes/draft" || path.EndsWith("/validate")) return 2;
        if (path.EndsWith("/publish") || path == "/api/recipes/rollback") return 3;
        return 3; // fail closed: new mutating APIs need explicit permission design
    }
}
public sealed record AuditEntry(string Id, string Time, string Actor, string Role, string Method, string Path,
    int Status, string TraceId, string Outcome);
public sealed class AuditTrail
{
    private readonly object _lock = new();
    private readonly string _file;
    private readonly SqliteStore _sqlite;
    private readonly List<AuditEntry> _items = new();
    public AuditTrail(IHostEnvironment env,SqliteStore sqlite)
    {
        var folder = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(folder);
        _file = Path.Combine(folder, "audit.jsonl");
        _sqlite=sqlite;
        if (File.Exists(_file)) foreach (var line in File.ReadLines(_file))
            try { var item = JsonSerializer.Deserialize<AuditEntry>(line); if (item != null) _items.Add(item); }
            catch (JsonException) { /* incomplete trailing line */ }
        foreach(var item in _items)_sqlite.UpsertAudit(item); // idempotent legacy JSONL import
        _items.Clear();_items.AddRange(_sqlite.Audits());
        if (_items.Count > 2000) _items.RemoveRange(0, _items.Count - 2000);
    }
    public void Record(string actor, string role, string verb, string path, int status, string traceId)
    {
        var row = new AuditEntry(Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow.ToString("O"),
            actor, role, verb, path, status, traceId, status < 400 ? "Accepted" : "Rejected");
        lock (_lock)
        {
            // Commit before surfacing the action as audited.
            _sqlite.UpsertAudit(row);_items.Add(row);
            if (_items.Count > 2000) _items.RemoveRange(0, _items.Count - 2000);
        }
    }
    // V1.3 workstation view: preserve the global append-only trail, but explicitly
    // filter by station identity. Legacy unscoped machine routes belong only to A.
    public object PageForStation(string station, int page, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(station)) throw new UnknownStationException(station);
        lock (_lock)
        {
            page=Math.Max(1,page);pageSize=Math.Clamp(pageSize,1,100);
            var rows=_items.Where(e => e.Path.StartsWith("/api/stations/"+station+"/",StringComparison.OrdinalIgnoreCase)
                || (station=="A" && e.Path.StartsWith("/api/",StringComparison.OrdinalIgnoreCase)
                    && !e.Path.StartsWith("/api/stations/",StringComparison.OrdinalIgnoreCase)
                    && !e.Path.StartsWith("/api/auth/",StringComparison.OrdinalIgnoreCase)
                    && !e.Path.StartsWith("/api/audit",StringComparison.OrdinalIgnoreCase))).Reverse().ToArray();
            return new {total=rows.Length,page,pageSize,items=rows.Skip((page-1)*pageSize).Take(pageSize).ToArray()};
        }
    }
    public object Page(int page, int pageSize)
    {
        lock (_lock)
        {
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
            return new { total = _items.Count, page, pageSize,
                items = _items.AsEnumerable().Reverse().Skip((page - 1) * pageSize).Take(pageSize).ToArray() };
        }
    }
}

public sealed record RecipeValues(string Name, double VisionOffsetMm, int ExtraDwellMs);
public sealed record RecipeRevision(string Id, string? SourceId, string Status, string CreatedAt,
    string Author, RecipeValues Values, string Hash, string Reason);
public sealed class RecipeStore
{
    private readonly object _lock = new();
    private readonly string _filename;
    private readonly SqliteStore _db;
    private List<RecipeRevision> _revisions;
    private string _active;
    public RecipeStore(IHostEnvironment env,SqliteStore db, string? scope = null)
    {
        _db=db;
        var folder = StationStoragePaths.Folder(env, scope);
        _filename = Path.Combine(folder, "recipes.json");
        var sqliteState=_db.ReadRecipes();
        if(sqliteState != null)
        {
            _revisions=sqliteState.Value.Revisions;_active=sqliteState.Value.ActiveId;
        }
        else if (File.Exists(_filename))
        {
            var data = JsonSerializer.Deserialize<RecipeState>(File.ReadAllText(_filename))
                ?? throw new InvalidOperationException("配方版本文件不正确");
            _revisions = data.Revisions; _active = data.ActiveId;
            if (!_revisions.Any(r => r.Id == _active)) throw new InvalidOperationException("找不到已发布配方");
            Persist(); // migrate legacy recipes.json once
        }
        else
        {
            var initial = new RecipeValues("默认工艺", 0, 0);
            _revisions = new() { Make(initial, "system", "Published", null, "初始化基准版本") };
            _active = _revisions[0].Id; Persist();
        }
    }
    public IReadOnlyList<RecipeRevision> List() { lock (_lock) return _revisions.AsEnumerable().Reverse().ToArray(); }
    public RecipeRevision Active { get { lock (_lock) return _revisions.First(v => v.Id == _active); } }
    public RecipeRevision Draft(RecipeValues values, string author, string? expectedActiveVersionId = null)
    {
        lock (_lock)
        {
            // F03: optional optimistic token, mirroring FlowRevisions.Draft.
            if (expectedActiveVersionId != null && expectedActiveVersionId != _active)
                throw new InvalidOperationException("版本冲突：配方草稿的基准版本已被替换，请刷新后重试");
            var row = Make(values, author, "Draft", _active, "待验证");
            // F02: commit first; swap memory only after the transaction succeeds.
            var candidate = new List<RecipeRevision>(_revisions) { row };
            _db.SaveRecipes(_active, candidate);
            _revisions = candidate;
            return row;
        }
    }
    public RecipeRevision Validate(string id)
    {
        lock (_lock)
        {
            var (r, i) = Find(id, "Draft");
            Check(r.Values);
            var next = r with { Status = "Validated", Reason = "静态校验通过" };
            var candidate = new List<RecipeRevision>(_revisions);
            candidate[i] = next;
            _db.SaveRecipes(_active, candidate);
            _revisions = candidate;
            return next;
        }
    }
    public RecipeRevision Publish(string id, DeviceRuntime runtime)
    {
        return runtime.WithConfigurationChange(() =>
        {
            lock (_lock)
            {
            var (r, i) = Find(id, "Validated");
            // F03: reject stale drafts whose baseline was replaced, same as FlowRevisions.
            if (r.SourceId != _active)
                throw new InvalidOperationException("版本冲突：配方草稿的基准版本已被替换，拒绝发布");
            Check(r.Values);
            var previous = _revisions.FindIndex(x => x.Id == _active);
            var candidate = new List<RecipeRevision>(_revisions);
            if (previous >= 0) candidate[previous] = candidate[previous] with { Status = "Superseded" };
            var next = r with { Status = "Published", Reason = "经管理员发布" };
            candidate[i] = next;
            // F02: the whole set (supersede + publish + active pointer) commits atomically
            // before any in-memory state changes.
            _db.SaveRecipes(next.Id, candidate);
            _revisions = candidate; _active = next.Id;
            return next;
            }
        });
    }
    public RecipeRevision Rollback(string id, string actor, DeviceRuntime runtime)
    {
        return runtime.WithConfigurationChange(() =>
        {
            lock (_lock)
            {
            var source = _revisions.FirstOrDefault(x => x.Id == id)
                ?? throw new ArgumentException("找不到指定配方版本");
            if (source.Status is not ("Published" or "Superseded")) throw new InvalidOperationException("只能回滚到曾经发布过的版本");
            Check(source.Values);
            var previous = _revisions.FindIndex(x => x.Id == _active);
            var release = Make(source.Values, actor, "Published", id, "回滚发布自 " + id);
            var candidate = new List<RecipeRevision>(_revisions);
            if (previous >= 0) candidate[previous] = candidate[previous] with { Status = "Superseded" };
            candidate.Add(release);
            _db.SaveRecipes(release.Id, candidate);
            _revisions = candidate; _active = release.Id;
            return release;
            }
        });
    }
    private (RecipeRevision, int) Find(string id, string expected)
    {
        var i = _revisions.FindIndex(x => x.Id == id);
        if (i < 0) throw new ArgumentException("找不到配方版本");
        if (_revisions[i].Status != expected) throw new InvalidOperationException("当前版本状态不允许此操作");
        return (_revisions[i], i);
    }
    private static RecipeRevision Make(RecipeValues v, string author, string status, string? sourceId, string reason) =>
        new(Guid.NewGuid().ToString("N")[..10], sourceId, status, DateTimeOffset.UtcNow.ToString("O"),
            author, v, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(v))))[..16], reason);
    private static void Check(RecipeValues v)
    {
        if (string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > 50 ||
            !double.IsFinite(v.VisionOffsetMm) || Math.Abs(v.VisionOffsetMm) > 10 ||
            v.ExtraDwellMs < 0 || v.ExtraDwellMs > 1000)
            throw new ArgumentException("配方校验失败：名称 1~50 字，视觉偏移 -10~10 mm，附加延时 0~1000 ms");
    }
    private void Persist() => _db.SaveRecipes(_active,_revisions);
    private sealed record RecipeState(string ActiveId, List<RecipeRevision> Revisions);
}
public sealed record LoginRequest(string Username, string Password);
public sealed record RecipeDraftRequest(string Name, double VisionOffsetMm, int ExtraDwellMs,
    string? ExpectedActiveVersionId = null);
public sealed record RollbackRequest(string Id);
