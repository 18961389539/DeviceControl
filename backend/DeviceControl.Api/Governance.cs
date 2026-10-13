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
            path.StartsWith("/api/commands", StringComparison.OrdinalIgnoreCase) ||
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
    int Status, string TraceId, string Outcome, string? ErrorType = null, string? ErrorMessage = null,
    string? ErrorDetail = null, string? CommandId = null, string? StationId = null, string? DeploymentId = null);

/// <summary>R05: explicit business attribution for audit rows. Endpoints whose target
/// is not derivable from the HTTP path (global upgrade/rollout routes) set this on
/// the request context; batch operations carry their full target set.</summary>
public sealed record AuditAttribution(string? StationId, string? DeploymentId = null, string[]? Stations = null);
public static class AuditAttributionRules
{
    private static readonly string[] GlobalPrefixes =
        ["/api/stations/","/api/auth/","/api/audit","/api/templates","/api/upgrades","/api/rollouts",
         "/api/diagnostics","/api/commands","/api/health","/api/ready"];
    /// <summary>Path-derived station for legacy single-station machine routes ("A") and
    /// explicit station routes. Fleet/governance routes return null and must attribute
    /// their target explicitly - never guess a station from the path.</summary>
    public static string? StationFromPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("stations", StringComparison.OrdinalIgnoreCase))
            return parts[2].Equals("resources", StringComparison.OrdinalIgnoreCase) ? null : parts[2];
        if (parts.Length == 2 && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("stations", StringComparison.OrdinalIgnoreCase)) return null; // fleet summary
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var prefix in GlobalPrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return "A";
    }
}

public static class CommandCorrelation
{
    private static readonly AsyncLocal<string?> CurrentValue = new();
    public static string? Current => CurrentValue.Value;
    public static IDisposable Push(string commandId)
    {
        var prior = CurrentValue.Value;
        CurrentValue.Value = commandId;
        return new Restore(() => CurrentValue.Value = prior);
    }
    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
public sealed class AuditTrail
{
    private readonly object _lock = new();
    private readonly string _file;
    private readonly string[] _spoolFiles;
    private readonly string _lossFile;
    private readonly SqliteStore _sqlite;
    private readonly List<AuditEntry> _items = new();
    // Q01: _pending is only a bounded VIEW over unconfirmed records. The spool files
    // below are the durable queue itself; trimming the view never discards facts.
    private readonly List<AuditEntry> _pending = new();
    // Q02: records that reached no durable channel at all. Kept in a bounded rescue
    // buffer and counted in the loss marker; never silently forgotten.
    private readonly List<AuditEntry> _rescue = new();
    private int _pendingTotal;      // authoritative unconfirmed count (spool + rescue)
    private int _spooled;           // subset of _pendingTotal durably staged in spool files
    private long _unrecoverable;    // records that lost durable staging entirely
    private int _failures;
    private string? _lastError;
    private bool _dbOk = true;      // outcome of the most recent database audit write
    private bool _spoolOk = true;   // outcome of the most recent spool write
    private int _spoolSlot;         // sticky slot index that last accepted an append
    private DateTimeOffset _lastRecoverAttempt = DateTimeOffset.MinValue;
    private string? _acknowledgedBy;
    private string? _acknowledgedUtc;
    private const int ViewLimit = 2000;
    private const int RescueLimit = 10000;
    private const int SpoolCapacity = 100000;
    private sealed record LossMarker(int Schema, int Unpersisted, long Unrecoverable, string LastUtc,
        string? LastError, string? AcknowledgedBy, string? AcknowledgedUtc);

    public AuditTrail(IHostEnvironment env,SqliteStore sqlite)
    {
        var folder = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(folder);
        _file = Path.Combine(folder, "audit.jsonl");
        // Q01: the pending queue is a set of ordered spool slots. A damaged or
        // corrupted first slot (e.g. replaced by a directory) must not disable
        // persistence; appends fall through to the next writable slot.
        _spoolFiles = new[]
        {
            Path.Combine(folder, "audit-pending.jsonl"),
            Path.Combine(folder, "audit-pending.1.jsonl"),
            Path.Combine(folder, "audit-pending.2.jsonl")
        };
        _lossFile = Path.Combine(folder, "audit-loss.json");
        _sqlite=sqlite;
        var legacy = new List<AuditEntry>();
        if (File.Exists(_file)) foreach (var line in File.ReadLines(_file))
            try { var item = JsonSerializer.Deserialize<AuditEntry>(line); if (item != null) legacy.Add(item); }
            catch (JsonException) { /* incomplete trailing line */ }
        try
        {
            foreach(var item in legacy)_sqlite.UpsertAudit(item); // idempotent legacy JSONL import
            _items.AddRange(_sqlite.Audits());
            _dbOk = true;
        }
        catch (Exception ex)
        {
            // R04/Q02: a degraded audit store must not take the host down. Legacy
            // rows stay in audit.jsonl and are retried from a durable position.
            _failures++; _lastError = ex.Message; _dbOk = false;
            foreach (var item in legacy) { AddPending(item, view: false); AddRescue(item); }
        }
        if (_items.Count > ViewLimit) _items.RemoveRange(0, _items.Count - ViewLimit);
        // Q01: load the whole durable queue (all slots, no memory cap that could
        // silently drop loaded rows); the view is trimmed, the queue is not.
        var spooled = ReadSpool();
        foreach (var item in spooled) _pending.Add(item);
        _pendingTotal += spooled.Count;
        _spooled = spooled.Count;
        TrimPendingView();
        LoadLossMarker();
        TryFlushPending();
    }
    public void Record(string actor, string role, string verb, string path, int status, string traceId,
        Exception? exception = null, string? commandId = null, string? stationId = null, string? deploymentId = null)
    {
        var row = new AuditEntry(Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow.ToString("O"),
            actor, role, verb, path, status, traceId,
            status >= 500 ? "Failed" : status == 499 ? "Cancelled" : status >= 400 ? "Rejected" : "Accepted",
            exception?.GetType().FullName, exception?.Message, exception?.ToString(),
            commandId ?? CommandCorrelation.Current, stationId, deploymentId);
        lock (_lock)
        {
            // R04/Q01: commit before surfacing the action as audited. A failed write is
            // staged in the durable spool (commit-confirmed, never truncated before
            // persistence) or, failing that, tracked as an explicit loss fact.
            try
            {
                _sqlite.UpsertAudit(row);_items.Add(row);
                if (_items.Count > ViewLimit) _items.RemoveRange(0, _items.Count - ViewLimit);
                _lastError = null; _dbOk = true;
                TryFlushPending();
            }
            catch (Exception ex)
            {
                _failures++; _lastError = ex.Message; _dbOk = false;
                AddPending(row, view: true);
                if (TrySpool(row)) _spooled++;
                else { AddRescue(row); UpdateLossMarker(); }
            }
        }
    }
    /// <summary>R04/Q01: health view used by /api/ready, diagnostics and the request
    /// admission gate. Degraded means earlier audit rows are still unpersisted or a
    /// loss of durable staging has not been acknowledged by a human.</summary>
    public bool Degraded { get { lock (_lock) return _pendingTotal > 0 || LossActive(); } }
    /// <summary>Q02: true when a traceable change must not be accepted because its
    /// audit trail cannot be persisted: both the database and every spool slot failed,
    /// records are memory-only, or an unacknowledged loss from an earlier incident is
    /// still on record (fail closed until a human reviews it).</summary>
    public bool TraceabilityUnavailable { get { lock (_lock) return (!_dbOk && !_spoolOk) || _rescue.Count > 0 || _unrecoverable > 0; } }
    public object Health()
    {
        lock (_lock) return new
        {
            degraded = _pendingTotal > 0 || LossActive(),
            pending = _pendingTotal,
            spooled = _spooled,
            memoryOnly = _rescue.Count,
            lost = _unrecoverable,
            db = _dbOk ? "ok" : "failed",
            spool = _spoolOk ? "ok" : "failed",
            failures = _failures, lastError = _lastError,
            acknowledgedBy = _acknowledgedBy, acknowledgedUtc = _acknowledgedUtc
        };
    }
    /// <summary>Q02: throttled recovery probe. Ordinary requests call this so a
    /// recovered audit channel is detected without needing a new mutation.</summary>
    public void TryRecover()
    {
        lock (_lock)
        {
            if (_pendingTotal == 0 && _rescue.Count == 0) return;
            var now = DateTimeOffset.UtcNow;
            if (now - _lastRecoverAttempt < TimeSpan.FromSeconds(1)) return;
            _lastRecoverAttempt = now;
            TryFlushPending();
        }
    }
    /// <summary>Q01/Q02: replay the durable queue into SQLite. Confirmed rows are only
    /// removed from the spool AFTER the database write succeeded; a partial failure
    /// keeps every unconfirmed row for the next attempt. Idempotent by row id.</summary>
    private void TryFlushPending()
    {
        // Called under _lock. Best-effort replay of earlier audit failures.
        if (_pendingTotal == 0) return;
        var diskRows = _spooled > 0 ? ReadSpool() : new List<AuditEntry>();
        var remaining = new List<AuditEntry>();
        var confirmedDisk = false; var failedOnce = false;
        for (var i = 0; i < diskRows.Count; i++)
        {
            try { _sqlite.UpsertAudit(diskRows[i]); confirmedDisk = true; _dbOk = true; }
            catch (Exception ex)
            {
                _failures++; _lastError = ex.Message; _dbOk = false; failedOnce = true;
                remaining.Add(diskRows[i]);
                // Channel-down short circuit: with zero successes so far, do not spin
                // through the entire backlog one failure at a time.
                if (!confirmedDisk) { for (var j = i + 1; j < diskRows.Count; j++) remaining.Add(diskRows[j]); break; }
            }
        }
        var rescueRemaining = new List<AuditEntry>();
        if (failedOnce && !confirmedDisk) rescueRemaining.AddRange(_rescue);
        else
            foreach (var row in _rescue)
            {
                try { _sqlite.UpsertAudit(row); _dbOk = true; }
                catch (Exception ex) { _failures++; _lastError = ex.Message; _dbOk = false; rescueRemaining.Add(row); }
            }
        if (confirmedDisk)
        {
            // Commit-confirmed truncation: only drop spool rows after their database
            // write succeeded. Failure to rewrite keeps the old files (replay is
            // idempotent), never loses an unconfirmed row.
            if (remaining.Count == 0) DeleteSpool(); else WriteSpool(remaining);
        }
        _spooled = remaining.Count;
        _rescue.Clear(); _rescue.AddRange(rescueRemaining);
        _pendingTotal = remaining.Count + rescueRemaining.Count;
        RebuildPendingView(remaining, rescueRemaining);
        UpdateLossMarker();
    }
    private bool TrySpool(AuditEntry row)
    {
        if (_spooled >= SpoolCapacity) { _spoolOk = false; _lastError = "待补审计队列达到容量上限 " + SpoolCapacity; return false; }
        for (var pass = 0; pass < _spoolFiles.Length; pass++)
        {
            var slot = (_spoolSlot + pass) % _spoolFiles.Length;
            try
            {
                File.AppendAllText(_spoolFiles[slot], JsonSerializer.Serialize(row) + "\n");
                _spoolSlot = slot; _spoolOk = true; return true;
            }
            catch (Exception ex)
            {
                _spoolOk = false;
                _lastError = "待补审计落盘失败（" + Path.GetFileName(_spoolFiles[slot]) + "）：" + ex.Message;
            }
        }
        return false;
    }
    private List<AuditEntry> ReadSpool()
    {
        var rows = new List<AuditEntry>();
        foreach (var file in _spoolFiles)
        {
            try
            {
                if (!File.Exists(file)) continue;
                foreach (var line in File.ReadLines(file))
                    try { var item = JsonSerializer.Deserialize<AuditEntry>(line); if (item != null) rows.Add(item); }
                    catch (JsonException) { /* incomplete trailing line after a crash */ }
            }
            catch (Exception ex) { _failures++; _lastError = "待补审计读取失败（" + Path.GetFileName(file) + "）：" + ex.Message; }
        }
        return rows;
    }
    private void WriteSpool(List<AuditEntry> rows)
    {
        try
        {
            var temp = _spoolFiles[0] + ".tmp";
            File.WriteAllText(temp,
                string.Concat(rows.Select(r => JsonSerializer.Serialize(r) + "\n")));
            File.Move(temp, _spoolFiles[0], true);
            for (var i = 1; i < _spoolFiles.Length; i++)
                try { if (File.Exists(_spoolFiles[i])) File.Delete(_spoolFiles[i]); } catch { /* retried later */ }
            _spoolSlot = 0; _spoolOk = true;
        }
        catch (Exception ex)
        {
            // Old files stay on disk; the next successful replay re-confirms them
            // (idempotent) and truncates again. No unconfirmed row is ever dropped.
            _spoolOk = false; _lastError = "待补审计回写失败：" + ex.Message;
        }
    }
    private void DeleteSpool()
    {
        foreach (var file in _spoolFiles)
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) { _spoolOk = false; _lastError = "待补审计清理失败：" + ex.Message; }
    }
    private void AddPending(AuditEntry row, bool view)
    {
        _pendingTotal++;
        if (!view) return;
        _pending.Add(row);
        if (_pending.Count > ViewLimit) _pending.RemoveRange(0, _pending.Count - ViewLimit);
    }
    private void AddRescue(AuditEntry row)
    {
        _rescue.Add(row);
        if (_rescue.Count > RescueLimit)
        {
            // Bounded memory: the oldest memory-only record leaves the rescue buffer
            // and becomes an explicit, counted loss - it is never silently dropped.
            var dropped = _rescue.Count - RescueLimit;
            _rescue.RemoveRange(0, dropped);
            _unrecoverable += dropped;
        }
    }
    private void TrimPendingView()
    { if (_pending.Count > ViewLimit) _pending.RemoveRange(0, _pending.Count - ViewLimit); }
    private void RebuildPendingView(List<AuditEntry> remaining, List<AuditEntry> rescue)
    {
        _pending.Clear(); _pending.AddRange(remaining); _pending.AddRange(rescue); TrimPendingView();
    }
    private bool LossActive() => _unrecoverable > 0 || _rescue.Count > 0;
    private void LoadLossMarker()
    {
        if (!File.Exists(_lossFile)) return;
        try
        {
            var marker = JsonSerializer.Deserialize<LossMarker>(File.ReadAllText(_lossFile));
            if (marker is null) return;
            _acknowledgedBy = marker.AcknowledgedBy; _acknowledgedUtc = marker.AcknowledgedUtc;
            if (string.IsNullOrEmpty(marker.AcknowledgedBy))
            {
                // Unacknowledged loss survives restarts: readiness must not silently
                // recover while accepted records were never durably staged.
                var lost = marker.Unpersisted + marker.Unrecoverable;
                if (lost > 0) { _unrecoverable += lost; _failures += (int)Math.Min(int.MaxValue, lost); _lastError ??= marker.LastError; }
            }
        }
        catch (Exception ex) { _failures++; _lastError = "审计丢失标记读取失败：" + ex.Message; }
    }
    private void UpdateLossMarker()
    {
        // Written on loss-state transitions and after an acknowledgement. Skipped
        // when there is nothing to record and no marker exists, so a healthy system
        // never grows a loss file; best effort - if even this marker cannot be
        // written, the in-memory state still blocks traceable mutations.
        if (_rescue.Count == 0 && _unrecoverable == 0 && _acknowledgedBy is null && !File.Exists(_lossFile)) return;
        try
        {
            var marker = new LossMarker(1, _rescue.Count, _unrecoverable,
                DateTimeOffset.UtcNow.ToString("O"), _lastError, _acknowledgedBy, _acknowledgedUtc);
            var temp = _lossFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(marker));
            File.Move(temp, _lossFile, true);
        }
        catch (Exception ex) { _lastError = "审计丢失标记写入失败：" + ex.Message; }
    }
    /// <summary>Q02: a human acknowledges that unrecoverable audit records were lost.
    /// Refused while any record is still unconfirmed; the acknowledgement itself is
    /// written through the durable database channel first, so a broken store cannot
    /// clear the loss state.</summary>
    public object AcknowledgeLoss(string actor, string? note)
    {
        lock (_lock)
        {
            if (_pendingTotal > 0 || _rescue.Count > 0)
                throw new InvalidOperationException("仍有未补齐的审计记录（" + _pendingTotal + " 条）：请先恢复审计存储并完成补录，再确认丢失范围");
            if (_unrecoverable <= 0 && !File.Exists(_lossFile))
                return new { ok = true, acknowledged = false, note = "没有待确认的审计丢失标记" };
            var probe = new AuditEntry(Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow.ToString("O"),
                actor, "Admin", "POST", "/api/audit/acknowledge-loss", 200, CommandCorrelation.Current ?? "audit-loss-ack",
                "Accepted", null, note, null, CommandCorrelation.Current);
            try { _sqlite.UpsertAudit(probe); }
            catch (Exception ex)
            {
                _failures++; _lastError = ex.Message; _dbOk = false;
                throw new InvalidOperationException("审计数据库通道仍不可写，不能确认丢失：" + ex.Message, ex);
            }
            _dbOk = true;
            _acknowledgedBy = actor; _acknowledgedUtc = DateTimeOffset.UtcNow.ToString("O");
            _unrecoverable = 0;
            UpdateLossMarker();
            return new { ok = true, acknowledged = true, acknowledgedBy = actor, acknowledgedUtc = _acknowledgedUtc };
        }
    }
    /// <summary>R03/Q02: command lifecycle lookup over the durable records - the audit
    /// trail (acceptance/result), manual-action intents, persisted cycles and run
    /// events. Unconfirmed (pending) audit rows are reported separately with their
    /// durability state; "not found" never implies the command did not arrive.
    /// Used to reconcile a write whose response was lost; never re-executes.</summary>
    public object QueryCommand(string commandId, SqliteStore? stationStore = null, RunJournal? journal = null)
    {
        if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 80 ||
            !System.Text.RegularExpressions.Regex.IsMatch(commandId, @"\A[A-Za-z0-9._:-]+\z"))
            throw new ArgumentException("命令 ID 格式不合法：仅限字母、数字、点、冒号、下划线与连字符，最长 80");
        var store = stationStore ?? _sqlite;
        var audit = AuditsByCommand(commandId);
        var intents = store.ManualIntentsByCommand(commandId);
        var runs = store.CyclesByCommand(commandId);
        IReadOnlyList<RunEventEntry> events = journal?.EventsForCommand(commandId, 100) ?? Array.Empty<RunEventEntry>();
        bool uncertain; int pendingCount; long lostCount;
        lock (_lock) { uncertain = LossActive(); pendingCount = _pendingTotal; lostCount = _unrecoverable; }
        var pending = PendingAuditsByCommand(commandId);
        var found = audit.Count > 0 || pending.Count > 0 || intents.Count > 0 || runs.Count > 0 || events.Count > 0;
        var guidance = found
            ? pending.Count > 0
                ? "记录已被接受但尚未确认落盘（见 pendingAudit）：以命令 ID 核对为准，禁止盲目重发。"
                : "以上为持久记录；结果未知时以此为准，禁止盲目重发。"
            : uncertain
                ? "未找到该命令 ID 的任何记录，但审计通道存在未持久化记录（待补 " + pendingCount + " 条，未能落盘 " + lostCount + " 条），无法排除该命令属于丢失范围；请人工核查后再决定，禁止自动重发。"
                : "未找到该命令 ID 的任何记录（审计未命中）：可能尚未到达服务端、ID 输入有误，或该命令早于本次审计迁移。禁止据此自动重发。";
        return new
        {
            commandId, found,
            uncertain,
            pending = pendingCount,
            lost = lostCount,
            audit,
            pendingAudit = pending,
            manualIntents = intents,
            runs = runs.Select(r => new { r.Id, r.Outcome, r.StartedAt, r.EndedAt, r.DurationMs, r.FlowVersion, r.RecipeId, r.Reason }),
            events,
            guidance
        };
    }
    /// <summary>Q02: unconfirmed audit rows for one command ID - memory view, rescue
    /// buffer and (when needed) the durable spool on disk. Reported separately from
    /// confirmed rows so "accepted but not yet persisted" is a visible state.</summary>
    public List<AuditEntry> PendingAuditsByCommand(string commandId)
    {
        lock (_lock)
        {
            var rows = new List<AuditEntry>();
            var seen = new HashSet<string>();
            void Consider(IEnumerable<AuditEntry> source)
            {
                foreach (var item in source)
                    if (string.Equals(item.CommandId, commandId, StringComparison.Ordinal) && seen.Add(item.Id))
                        rows.Add(item);
            }
            Consider(_pending);
            Consider(_rescue);
            if (rows.Count == 0 && _spooled > 0) Consider(ReadSpool());
            rows.Sort((a, b) => string.Compare(b.Time, a.Time, StringComparison.Ordinal));
            return rows;
        }
    }
    public List<AuditEntry> AuditsByCommand(string commandId)
    {
        var rows = _sqlite.AuditsByCommand(commandId);
        lock (_lock)
            foreach (var item in _items)
                if (string.Equals(item.CommandId, commandId, StringComparison.Ordinal) && !rows.Any(x => x.Id == item.Id))
                    rows.Add(item);
        rows.Sort((a, b) => string.Compare(b.Time, a.Time, StringComparison.Ordinal));
        return rows;
    }
    // V1.3 workstation view: preserve the global append-only trail, but explicitly
    // filter by station identity. Legacy unscoped machine routes belong only to A.
    public object PageForStation(string station, int page, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(station)) throw new UnknownStationException(station);
        return _sqlite.PageAudits(page, pageSize, station);
    }
    public object Page(int page, int pageSize)
    {
        return _sqlite.PageAudits(page, pageSize);
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
/// <summary>Q02: human acknowledgement of unrecoverable audit records.</summary>
public sealed record AcknowledgeLossRequest(string? Note);
