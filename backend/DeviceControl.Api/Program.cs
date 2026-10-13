using DeviceControl.Api;

var builder = WebApplication.CreateBuilder(args);
var listenUrl = builder.Configuration["SimListenUrl"] ?? "http://127.0.0.1:5088";
if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri) || !listenUri.IsLoopback || listenUri.Scheme != "http")
    throw new InvalidOperationException("仿真 API 仅允许绑定 HTTP loopback 地址");
builder.WebHost.UseUrls(listenUrl); // SIMULATION, localhost-only; not production auth.
// R08/Q04: correlation headers must be readable by the real cross-port web frontend;
// only these allow-listed identifiers are exposed. X-Audit-Status is required by the
// main status bar's audit-degradation indicator - a native header is not enough.
builder.Services.AddCors(o => o.AddPolicy("LocalDev", p => p
    .WithOrigins("http://127.0.0.1:5173", "http://localhost:5173")
    .AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("X-Command-Id", "X-Trace-Id", "X-Request-Id", "X-Audit-Status")));
var configPath = Path.Combine(builder.Environment.ContentRootPath, "config", "station.json");
// R06: config loading is fail-closed, never fatal. A corrupt fixed-station config is
// reported through the diagnostics endpoints and refuses that station's routes.
builder.Services.AddSingleton<StationBootstrap>();
builder.Services.AddSingleton<StationConfig>(sp => sp.GetRequiredService<StationBootstrap>().RequirePrimaryConfig());
builder.Services.AddSingleton<SimIoController>();
builder.Services.AddSingleton<OrdinaryInterlockMonitor>();
builder.Services.AddSingleton<IMotionController, SimMotionController>();
builder.Services.AddSingleton<ICylinderRegistry, SimCylinderRegistry>();
builder.Services.AddSingleton<ICamera, SimCamera>();
builder.Services.AddSingleton<SqliteStore>();
builder.Services.AddSingleton<FlowRevisions>();
builder.Services.AddSingleton<RunJournal>();
builder.Services.AddSingleton<DemoSessions>();
builder.Services.AddSingleton<AuditTrail>();
builder.Services.AddSingleton<RecipeStore>();
builder.Services.AddSingleton<DeviceRuntime>();
builder.Services.AddSingleton<SharedResourceCoordinator>();
builder.Services.AddSingleton<RunEligibilityRegistry>();
builder.Services.AddSingleton<MultiStationRegistry>();
builder.Services.AddSingleton<StationTemplateCatalog>();
builder.Services.AddSingleton<StationUpgradeManager>();
builder.Services.AddSingleton<FleetRolloutManager>();
builder.Services.AddSingleton<RecoverySandboxInspector>();
var app = builder.Build();
app.UseCors("LocalDev");
// API RBAC lives server-side: a disabled UI button is not an authorization check.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var commandId = ctx.Request.Headers["X-Command-Id"].ToString();
    if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 80 ||
        !System.Text.RegularExpressions.Regex.IsMatch(commandId, @"\A[A-Za-z0-9._:-]+\z"))
        commandId = "cmd-" + Guid.NewGuid().ToString("N");
    ctx.Response.Headers["X-Command-Id"] = commandId;
    using var commandScope = CommandCorrelation.Push(commandId);
    var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;
    ctx.Response.Headers["X-Trace-Id"] = traceId;
    ctx.Response.Headers["X-Request-Id"] = ctx.TraceIdentifier;
    if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        ctx.Request.Method == "OPTIONS" || path is "/api/health" or "/api/ready" or "/api/auth/login")
    { await next(); return; }
    var sessions = ctx.RequestServices.GetRequiredService<DemoSessions>();
    // R06: an unavailable audit store must not make every request fail; the condition
    // is reported through diagnostics and gates critical mutations instead.
    AuditTrail? audit = null;
    try { audit = ctx.RequestServices.GetRequiredService<AuditTrail>(); } catch { /* reported as unavailable */ }
    var token = ctx.Request.Headers["X-Sim-Token"].ToString();
    var user = sessions.Get(token);
    var required = Permissions.Required(ctx.Request);
    var denied = user == null ? 401 : Permissions.Rank(user.Role) < required ? 403 : 0;
    // R05: one audit row per target station. Explicit business attribution (set by
    // endpoints whose target is not in the path) wins over the path heuristic.
    void RecordAudit(int status, Exception? exception)
    {
        if (audit is null) return;
        var actor = user?.Name ?? "anonymous"; var role = user?.Role ?? "None";
        var attribution = ctx.Items["AuditAttribution"] as AuditAttribution;
        var deployment = attribution?.DeploymentId;
        try
        {
            if (attribution?.Stations is { Length: > 0 } targets)
                foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
                    audit.Record(actor, role, ctx.Request.Method, path, status, traceId, exception, commandId, target, deployment);
            else
                audit.Record(actor, role, ctx.Request.Method, path, status, traceId, exception, commandId,
                    attribution?.StationId ?? AuditAttributionRules.StationFromPath(path), deployment);
        }
        catch (Exception auditError) { ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Audit").LogError(auditError, "Failed to persist audit"); }
    }
    if (denied != 0)
    {
        ctx.Response.StatusCode = denied;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = denied == 401 ? "请登录仿真账号" : "当前角色无权执行此操作" });
        if (ctx.Request.Method != "GET") RecordAudit(ctx.Response.StatusCode, null);
    }
    else
    {
        ctx.Items["SimUser"] = user;
        // Q02: ordinary traffic doubles as a throttled recovery probe, so a restored
        // audit channel is detected even when no new mutation is being attempted.
        try { audit?.TryRecover(); } catch { /* recovery probe must never fail a request */ }
        ctx.Response.Headers["X-Audit-Status"] = audit is null ? "unavailable" : audit.Degraded ? "degraded" : "ok";
        // R06: a station that failed to initialize refuses its routes with the concrete
        // reason instead of an opaque failure from a broken service dependency.
        if (AuditAttributionRules.StationFromPath(path) is { } routeStation)
        {
            MultiStationRegistry? routeFleet = null;
            try { routeFleet = ctx.RequestServices.GetService<MultiStationRegistry>(); } catch { }
            if (routeFleet is not null && !routeFleet.StationIds.Contains(routeStation, StringComparer.OrdinalIgnoreCase) &&
                routeFleet.FailedStations.TryGetValue(routeStation, out var reason))
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsJsonAsync(new { ok = false, stationUnavailable = true,
                    error = "工站 " + routeStation + " 初始化失败，已停止服务：" + reason });
                if (ctx.Request.Method != "GET") RecordAudit(503, null);
                return;
            }
        }
        // R04: while audit rows are still unpersisted, version/deployment mutations are
        // refused. Ordinary machine-operation responses carry X-Audit-Status instead.
        var gatePath = path;
        var gateParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (gateParts.Length >= 4 && gateParts[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            gateParts[1].Equals("stations", StringComparison.OrdinalIgnoreCase))
            gatePath = "/api/" + string.Join("/", gateParts.Skip(3));
        if (ctx.Request.Method != "GET" && (audit is null || audit.Degraded) &&
            new[] { "/api/flows", "/api/recipes", "/api/upgrades", "/api/rollouts", "/api/templates" }
                .Any(prefix => gatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = audit is null
                    ? "审计存储不可用：已拒绝执行版本/部署类变更。请先恢复审计存储，再重试。"
                    : "审计通道降级：存在未落盘的关键审计记录，已拒绝执行版本/部署类变更。请先恢复审计存储并确认补录，再重试。",
                auditDegraded = true
            });
            RecordAudit(503, null);
            return;
        }
        // Q02: when no durable audit channel exists at all (database and every spool
        // slot failed, or records are memory-only), no NEW traceable change may be
        // accepted - otherwise its record can be lost with no recovery path. Stop,
        // fault reset and recovery confirmation stay callable so an operator can
        // always bring the station to a safe, acknowledged state.
        if (ctx.Request.Method != "GET" && (audit is null || audit.TraceabilityUnavailable) &&
            !SurvivalOperation(gatePath))
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = audit is null
                    ? "审计存储不可用：已拒绝执行需要留痕的新变更。停止、故障复位与恢复确认仍可用；请先恢复审计存储。"
                    : "审计无法持久化（数据库与全部待补文件均不可写）：已拒绝执行需要留痕的新变更。停止、故障复位与恢复确认仍可用；请先恢复审计存储。",
                auditUnavailable = true
            });
            RecordAudit(503, null);
            return;
        }
        var audited = false;
        try { await next(); }
        catch (OperationCanceledException ex)
        {
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 499;
            if (ctx.Request.Method != "GET") { RecordAudit(499, ex); audited = true; }
            throw;
        }
        catch (StationUnavailableException ex)
        {
            if (ctx.Request.Method != "GET") { RecordAudit(503, ex); audited = true; }
            if (ctx.Response.HasStarted) throw;
            ctx.Response.Clear();
            ctx.Response.Headers["X-Command-Id"] = commandId;
            ctx.Response.Headers["X-Trace-Id"] = traceId;
            ctx.Response.Headers["X-Request-Id"] = ctx.TraceIdentifier;
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, stationUnavailable = true, error = ex.Message });
        }
        catch (Exception ex)
        {
            var responseStarted = ctx.Response.HasStarted;
            if (ctx.Request.Method != "GET") { RecordAudit(500, ex); audited = true; }
            if (responseStarted) throw; // The client may have received a partial response; do not invent a replacement.
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Api").LogError(ex,
                "API command failed; command {CommandId}, trace {TraceId}", commandId, traceId);
            ctx.Response.Clear();
            ctx.Response.Headers["X-Command-Id"] = commandId;
            ctx.Response.Headers["X-Trace-Id"] = traceId;
            ctx.Response.Headers["X-Request-Id"] = ctx.TraceIdentifier;
            ctx.Response.Headers["X-Audit-Status"] = audit is null ? "unavailable" : audit.Degraded ? "degraded" : "ok";
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = "请求处理失败；请使用命令和追踪 ID 核对审计记录",
                commandId,
                traceId
            });
        }
        finally
        {
            if (!audited && ctx.Request.Method != "GET") RecordAudit(ctx.Response.StatusCode, null);
        }
    }
});
app.MapGet("/api/health", () =>
{
    // Liveness only: deliberately resolvable without any station/database dependency
    // so diagnostics stay reachable even when a station failed to initialize (R06).
    return Results.Ok(new { ok = true, mode = "Simulation", safetyRated = false, version = "2.0-rc" });
});
app.MapGet("/api/ready", (IServiceProvider services) =>
{
    // Q03: readiness must stay reachable even when the fleet registry itself cannot
    // be constructed; it reports the concrete reason instead of failing the request.
    var fleet = TryResolve<MultiStationRegistry>(services);
    var templates = TryResolve<StationTemplateCatalog>(services);
    var upgrades = TryResolve<StationUpgradeManager>(services);
    var rollouts = TryResolve<FleetRolloutManager>(services);
    if (fleet is null || upgrades is null)
        return Results.Json(new { ready = false, mode = "Simulation", fleetUnavailable = true,
            error = "工站注册表不可用：服务以最小诊断宿主运行，请查看 /api/diagnostics/stations",
            audit = AuditHealthSnapshot(services).View }, statusCode: 503);
    var stationReadiness = fleet.StationIds.Select(id =>
    {
        var station = fleet.Get(id); string? storeError = null;
        try { using var c = station.Store.OpenProbe(); } catch (Exception ex) { storeError = ex.Message; }
        var block = upgrades.UnresolvedReason(id);
        return new { stationId = id, ready = storeError is null && block is null && !station.Machine.Snapshot().RecoveryRequired,
            storeError, blockingReason = block, recoveryRequired = station.Machine.Snapshot().RecoveryRequired };
    }).ToArray();
    var issues = (templates?.StartupIssues ?? Array.Empty<string>())
        .Concat(upgrades.StartupIssues).Concat(rollouts?.StartupIssues ?? Array.Empty<string>()).ToArray();
    // R04/R06: readiness also requires an intact audit trail, and reading it must be
    // defensive so the readiness endpoint itself stays reachable while degraded.
    var (auditOk, auditHealth) = AuditHealthSnapshot(services);
    var ready = stationReadiness.All(x => x.ready) && issues.Length == 0 && fleet.FailedStations.Count == 0 && auditOk;
    return Results.Json(new { ready, mode = "Simulation", audit = auditHealth, stations = stationReadiness,
        failedStations = fleet.FailedStations, startupIssues = issues }, statusCode: ready ? 200 : 503);
});
app.MapPost("/api/auth/login", (DemoSessions sessions, LoginRequest request, HttpContext ctx) =>
{
    // R06: login must stay available while the audit store is down, otherwise the
    // read-only diagnostics endpoints could not be reached by an engineer.
    AuditTrail? audit = null;
    try { audit = ctx.RequestServices.GetRequiredService<AuditTrail>(); } catch { /* reported as unavailable */ }
    try
    {
        var (token, user) = sessions.Login(request.Username, request.Password);
        audit?.Record(user.Name, user.Role, "POST", "/api/auth/login", 200, ctx.TraceIdentifier);
        return Results.Ok(new { token, user });
    }
    catch (UnauthorizedAccessException)
    {
        audit?.Record(request.Username, "None", "POST", "/api/auth/login", 401, ctx.TraceIdentifier);
        return Results.Json(new { ok = false, error = "账号或密码不正确" }, statusCode: 401);
    }
});
app.MapPost("/api/auth/logout", (DemoSessions sessions, HttpContext ctx) =>
{
    sessions.Logout(ctx.Request.Headers["X-Sim-Token"].ToString());
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/auth/me", (HttpContext ctx) => Results.Ok(ctx.Items["SimUser"]));
app.MapGet("/api/config", (StationConfig config) => Results.Ok(config));
app.MapGet("/api/snapshot", (DeviceRuntime machine) => Results.Ok(machine.Snapshot()));
// Read-only stored evidence. Never run steps in order to serve a history request.
app.MapGet("/api/runs", (SqliteStore store) => Results.Ok(store.Cycles()));
app.MapGet("/api/runs/{id}", (SqliteStore store,string id) =>
    store.Cycle(id) is { } run ? (IResult)Results.Ok(run) : Results.NotFound(new { ok=false,error="未找到该周期" }));
app.MapGet("/api/runs/{id}/snapshots", (SqliteStore store,string id) =>
    store.Cycle(id) is null ? (IResult)Results.NotFound(new { ok=false,error="未找到该周期" }) :
    Results.Ok(store.Snapshots(id)));

// V1.4 governed software templates. A/B and provisioned stations remain isolated.
app.MapGet("/api/templates", (StationTemplateCatalog templates) => Results.Ok(templates.List()));
app.MapGet("/api/templates/{id}", (StationTemplateCatalog templates, string id) =>
    ExecuteValue(() => templates.Get(id)));
app.MapPost("/api/templates/capture", (StationTemplateCatalog templates, MultiStationRegistry fleet,
    CaptureTemplateRequest r, HttpContext ctx) =>
    ExecuteValue(() => { AttributeAudit(ctx, r.StationId); return templates.Capture(fleet, r.StationId, r.Name, ((SimUser)ctx.Items["SimUser"]!).Name); }));
app.MapPost("/api/templates/{id}/validate", (StationTemplateCatalog templates, string id) =>
    ExecuteValue(() => templates.Validate(id)));
app.MapPost("/api/templates/{id}/publish", (StationTemplateCatalog templates, string id) =>
    ExecuteValue(() => templates.Publish(id)));
app.MapGet("/api/templates/{id}/diff/{stationId}", (StationTemplateCatalog templates,
    MultiStationRegistry fleet, string id, string stationId) =>
    InStation(fleet, stationId, station => ExecuteValue(() => templates.Diff(id, station))));
app.MapPost("/api/templates/{id}/deploy", (StationTemplateCatalog templates, MultiStationRegistry fleet,
    string id, DeployTemplateRequest request, HttpContext ctx) =>
    ExecuteValue(() => { AttributeAudit(ctx, null, null, request.StationIds); return templates.Deploy(id, request.StationIds, fleet); }));

// V1.5: existing-station workflow/recipe upgrade, with optimistic preflight and durable intent.
app.MapPost("/api/upgrades/preflight", (StationUpgradeManager upgrades, UpgradePreflightRequest request, HttpContext ctx) =>
    ExecuteValue(() => { var plan = upgrades.Preflight(request.TemplateId, request.StationId, ((SimUser)ctx.Items["SimUser"]!).Name); AttributeAudit(ctx, plan.StationId); return plan; }));
app.MapPost("/api/upgrades/apply", (StationUpgradeManager upgrades, UpgradeApplyRequest request, HttpContext ctx) =>
    ExecuteValue(() => { var record = upgrades.Apply(request.PlanId, ((SimUser)ctx.Items["SimUser"]!).Name); AttributeAudit(ctx, record.StationId, record.Id); return record; }));
app.MapPost("/api/upgrades/rollback", (StationUpgradeManager upgrades, UpgradeRollbackRequest request, HttpContext ctx) =>
    ExecuteValue(() => { var record = upgrades.Rollback(request.DeploymentId, ((SimUser)ctx.Items["SimUser"]!).Name,
        request.ExpectedCurrentFlowId, request.ExpectedCurrentRecipeId, request.RecoveryNote);
        AttributeAudit(ctx, record.StationId, record.Id); return record; }));
app.MapGet("/api/upgrades/history", (StationUpgradeManager upgrades, string? stationId) =>
    ExecuteValue(() => upgrades.History(stationId)));
app.MapGet("/api/upgrades/{id}/integrity", (StationUpgradeManager upgrades, string id) =>
    ExecuteValue(() => upgrades.InspectBackup(id)));
// V1.9 read-only inspection of a temporary SQLite backup COPY. No restore endpoint.
app.MapGet("/api/upgrades/{id}/sandbox", (RecoverySandboxInspector sandbox, string id) =>
    ExecuteValue(() => sandbox.Inspect(id)));

// V1.6 stepwise fleet rollout; evidence only from persisted simulation cycles.
app.MapGet("/api/rollouts", (FleetRolloutManager rollouts) => ExecuteValue(rollouts.List));
app.MapGet("/api/rollouts/{id}", (FleetRolloutManager rollouts, string id) => ExecuteValue(() => rollouts.Get(id)));
app.MapGet("/api/rollouts/{id}/evidence", (FleetRolloutManager rollouts, string id) =>
    ExecuteValue(() => rollouts.InspectEvidence(id)));
app.MapGet("/api/rollouts/{id}/diagnostics", (FleetRolloutManager rollouts, string id) =>
    ExecuteValue(() => rollouts.Diagnose(id)));
app.MapGet("/api/rollouts/{id}/recovery-report", (FleetRolloutManager rollouts, string id) =>
    ExecuteValue(() => rollouts.RecoveryReport(id)));
app.MapPost("/api/rollouts/{id}/reconcile", (FleetRolloutManager rollouts, string id,
    RolloutReconcileRequest request, HttpContext ctx) =>
    ExecuteValue(() => { var value = rollouts.Reconcile(id, request, ((SimUser)ctx.Items["SimUser"]!).Name);
        AttributeAudit(ctx, null, id, value.Steps.Select(s => s.StationId).ToArray()); return value; }));
app.MapPost("/api/rollouts", (FleetRolloutManager rollouts, RolloutCreateRequest request, HttpContext ctx) =>
    ExecuteValue(() => { var value = rollouts.Create(request, ((SimUser)ctx.Items["SimUser"]!).Name);
        AttributeAudit(ctx, null, value.Id, value.Steps.Select(s => s.StationId).ToArray()); return value; }));
app.MapPost("/api/rollouts/{id}/advance", (FleetRolloutManager rollouts, string id, HttpContext ctx) =>
    ExecuteValue(() => { var value = rollouts.Advance(id, ((SimUser)ctx.Items["SimUser"]!).Name);
        AttributeAudit(ctx, null, id, value.Steps.Select(s => s.StationId).ToArray()); return value; }));
app.MapPost("/api/rollouts/{id}/verify", (FleetRolloutManager rollouts, string id, HttpContext ctx) =>
    ExecuteValue(() => { var value = rollouts.Verify(id);
        AttributeAudit(ctx, null, id, value.Steps.Select(s => s.StationId).ToArray()); return value; }));
app.MapPost("/api/rollouts/{id}/pause", (FleetRolloutManager rollouts, string id, RolloutNote request, HttpContext ctx) =>
    ExecuteValue(() => { var value = rollouts.Pause(id, request.Reason);
        AttributeAudit(ctx, null, id, value.Steps.Select(s => s.StationId).ToArray()); return value; }));
app.MapPost("/api/rollouts/{id}/resume", (FleetRolloutManager rollouts, string id, HttpContext ctx) =>
    ExecuteValue(() => { var value = rollouts.Resume(id);
        AttributeAudit(ctx, null, id, value.Steps.Select(s => s.StationId).ToArray()); return value; }));
app.MapPost("/api/rollouts/{id}/abort", (FleetRolloutManager rollouts, string id, RolloutNote request, HttpContext ctx) =>
    ExecuteValue(() => { var value = rollouts.Abort(id, request.Reason);
        AttributeAudit(ctx, null, id, value.Steps.Select(s => s.StationId).ToArray()); return value; }));

// V1.2 multi-station APIs. Old /api/* single-station endpoints remain bound to A.
app.MapGet("/api/stations", (MultiStationRegistry fleet) => Results.Ok(fleet.Summary()));
app.MapGet("/api/stations/resources", (MultiStationRegistry fleet) => Results.Ok(fleet.Resources()));
app.MapGet("/api/stations/{id}/snapshot", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Results.Ok(s.Machine.Snapshot())));
app.MapGet("/api/stations/{id}/config", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Results.Ok(s.Config)));
app.MapGet("/api/stations/{id}/flow", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Results.Ok(s.Machine.FlowSnapshot())));
app.MapGet("/api/stations/{id}/flows", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Results.Ok(s.Flows.List())));
app.MapGet("/api/stations/{id}/audit", (MultiStationRegistry fleet, AuditTrail audit, string id, int? page, int? pageSize) =>
    InStation(fleet, id, s => Results.Ok(audit.PageForStation(s.Id, page ?? 1, pageSize ?? 40))));
app.MapGet("/api/stations/{id}/recipes", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Results.Ok(new { active = s.Recipes.Active, versions = s.Recipes.List() })));
app.MapGet("/api/stations/{id}/runs", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Results.Ok(s.Store.Cycles())));
app.MapGet("/api/stations/{id}/runs/{runId}", (MultiStationRegistry fleet, string id, string runId) =>
    InStation(fleet, id, s => s.Store.Cycle(runId) is { } cycle ?
        Results.Ok(cycle) : Results.NotFound(new { ok = false, error = "未找到该工站周期" })));
app.MapGet("/api/stations/{id}/runs/{runId}/snapshots", (MultiStationRegistry fleet, string id, string runId) =>
    InStation(fleet, id, s => s.Store.Cycle(runId) is null ?
        Results.NotFound(new { ok = false, error = "未找到该工站周期" }) : Results.Ok(s.Store.Snapshots(runId))));
app.MapPost("/api/stations/{id}/mode", (MultiStationRegistry fleet, string id, ModeRequest r, HttpContext ctx) =>
    InStation(fleet, id, s => Execute(() => {
        if (r.Mode == "Manual" && ((SimUser)ctx.Items["SimUser"]!).Role == "Operator")
            throw new UnauthorizedAccessException("操作员不能切换至手动模式");
        s.Machine.SetMode(r.Mode);
    })));
app.MapPost("/api/stations/{id}/cycle/start", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Execute(() => fleet.Start(s.Id))));
app.MapPost("/api/stations/{id}/cycle/stop", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Execute(() => fleet.Stop(s.Id))));
app.MapPost("/api/stations/{id}/fault/reset", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Execute(s.Machine.ResetFault)));
app.MapPost("/api/stations/{id}/recovery/confirm", (MultiStationRegistry fleet, string id, HttpContext ctx) =>
    InStation(fleet, id, s => Execute(() => s.Machine.ConfirmRecovery(((SimUser)ctx.Items["SimUser"]!).Name))));
app.MapPost("/api/stations/{id}/sim/input", (MultiStationRegistry fleet, string id, ToggleRequest r) =>
    InStation(fleet, id, s => Execute(() => s.Machine.SetInput(r.Name, r.Value))));
app.MapPost("/api/stations/{id}/sim/fault", (MultiStationRegistry fleet, string id, ToggleRequest r) =>
    InStation(fleet, id, s => Execute(() => s.Machine.SetFaultInjection(r.Name, r.Value))));
app.MapPost("/api/stations/{id}/manual/axes/{axis}/enable", (MultiStationRegistry fleet, string id, string axis, AxisEnableRequest r) =>
    InStationAsync(fleet, id, s => ExecuteAsync(() => s.Machine.SetServoEnabledAsync(axis, r.Enabled))));
app.MapPost("/api/stations/{id}/manual/axes/{axis}/home", (MultiStationRegistry fleet, string id, string axis) =>
    InStationAsync(fleet, id, s => ExecuteAsync(() => s.Machine.HomeAsync(axis))));
app.MapPost("/api/stations/{id}/manual/axes/{axis}/move", (MultiStationRegistry fleet, string id, string axis, AxisMoveRequest r) =>
    InStationAsync(fleet, id, s => ExecuteAsync(() => s.Machine.MoveAsync(axis, r.PositionMm))));
app.MapGet("/api/stations/{id}/recovery/checklist", (MultiStationRegistry fleet, string id) =>
    InStation(fleet, id, s => Results.Ok(s.Machine.RecoveryChecklist())));
app.MapPost("/api/stations/{id}/manual/cylinders/{cylinder}/extend", (MultiStationRegistry fleet, string id, string cylinder) =>
    InStationAsync(fleet, id, s => ExecuteAsync(() => s.Machine.CylinderActionAsync(cylinder, true))));
app.MapPost("/api/stations/{id}/manual/cylinders/{cylinder}/retract", (MultiStationRegistry fleet, string id, string cylinder) =>
    InStationAsync(fleet, id, s => ExecuteAsync(() => s.Machine.CylinderActionAsync(cylinder, false))));
app.MapPost("/api/stations/{id}/manual/vision/trigger", (MultiStationRegistry fleet, string id) =>
    InStationAsync(fleet, id, s => ExecuteAsync(s.Machine.TriggerVisionAsync)));
app.MapPost("/api/stations/{id}/flow/debug", (MultiStationRegistry fleet, string id, FlowDebugRequest r) =>
    InStation(fleet, id, s => ExecuteValue(() => s.Machine.FlowCommand(r))));
app.MapPost("/api/stations/{id}/flows/draft", (MultiStationRegistry fleet, string id, FlowDraftRequest r, HttpContext ctx) =>
    InStation(fleet, id, s => ExecuteValue(() => s.Flows.Draft(r.Definition, ((SimUser)ctx.Items["SimUser"]!).Name, r.ExpectedActiveVersionId))));
app.MapPost("/api/stations/{id}/flows/{version}/validate", (MultiStationRegistry fleet, string id, string version) =>
    InStation(fleet, id, s => ExecuteValue(() => s.Flows.Validate(version))));
app.MapPost("/api/stations/{id}/flows/{version}/publish", (MultiStationRegistry fleet, string id, string version) =>
    InStation(fleet, id, s => ExecuteValue(() => s.Machine.PublishFlow(version))));
app.MapPost("/api/stations/{id}/flows/rollback", (MultiStationRegistry fleet, string id, RollbackRequest r, HttpContext ctx) =>
    InStation(fleet, id, s => ExecuteValue(() => s.Machine.RollbackFlow(r.Id, ((SimUser)ctx.Items["SimUser"]!).Name))));
app.MapPost("/api/stations/{id}/recipes/draft", (MultiStationRegistry fleet, string id, RecipeDraftRequest r, HttpContext ctx) =>
    InStation(fleet, id, s => ExecuteValue(() => s.Recipes.Draft(new RecipeValues(r.Name, r.VisionOffsetMm, r.ExtraDwellMs),
        ((SimUser)ctx.Items["SimUser"]!).Name))));
app.MapPost("/api/stations/{id}/recipes/{version}/validate", (MultiStationRegistry fleet, string id, string version) =>
    InStation(fleet, id, s => ExecuteValue(() => s.Recipes.Validate(version))));
app.MapPost("/api/stations/{id}/recipes/{version}/publish", (MultiStationRegistry fleet, string id, string version) =>
    InStation(fleet, id, s => ExecuteValue(() => s.Recipes.Publish(version, s.Machine))));

app.MapPost("/api/stations/{id}/recipes/rollback", (MultiStationRegistry fleet, string id, RollbackRequest r, HttpContext ctx) =>
    InStation(fleet, id, s => ExecuteValue(() => s.Recipes.Rollback(r.Id, ((SimUser)ctx.Items["SimUser"]!).Name, s.Machine))));
app.MapGet("/api/flows", (FlowRevisions flows)=>Results.Ok(flows.List()));
app.MapGet("/api/flows/{id}/quality", (FlowRevisions flows,string id)=>
    ExecuteValue(()=>flows.QualityReports(id)));
app.MapPost("/api/flows/draft", (FlowRevisions flows,FlowDraftRequest request,HttpContext ctx)=>
    ExecuteValue(()=>flows.Draft(request.Definition,((SimUser)ctx.Items["SimUser"]!).Name,request.ExpectedActiveVersionId)));
app.MapPost("/api/flows/{id}/validate", (FlowRevisions flows,string id)=>ExecuteValue(()=>flows.Validate(id)));
app.MapPost("/api/flows/{id}/publish", (DeviceRuntime machine,string id)=>ExecuteValue(()=>machine.PublishFlow(id)));
app.MapPost("/api/flows/rollback", (DeviceRuntime machine,RollbackRequest request,HttpContext ctx)=>
    ExecuteValue(()=>machine.RollbackFlow(request.Id,((SimUser)ctx.Items["SimUser"]!).Name)));
app.MapGet("/api/flow", (DeviceRuntime machine) => Results.Ok(machine.FlowSnapshot()));
app.MapPost("/api/flow/debug", (DeviceRuntime machine, FlowDebugRequest request) =>
    ExecuteValue(() => machine.FlowCommand(request)));
app.MapGet("/api/audit", (AuditTrail trail, int? page, int? pageSize) => Results.Ok(trail.Page(page ?? 1, pageSize ?? 40)));
// Q02: explicit human acknowledgement of unrecoverable audit loss. Refused while any
// record is still unconfirmed; requires a working durable database channel first.
app.MapPost("/api/audit/acknowledge-loss", (AuditTrail trail, AcknowledgeLossRequest request, HttpContext ctx) =>
    ExecuteValue(() => trail.AcknowledgeLoss(((SimUser)ctx.Items["SimUser"]!).Name, request.Note)));
// R03: command-lifecycle lookup for writes whose response was lost. Read-only;
// returns the durable acceptance/result evidence for one command ID.
app.MapGet("/api/commands/{commandId}", (AuditTrail audit, SqliteStore store, RunJournal journal, string commandId) =>
    ExecuteValue(() => audit.QueryCommand(commandId, store, journal)));
app.MapGet("/api/stations/{id}/commands/{commandId}", (MultiStationRegistry fleet, AuditTrail audit, string id, string commandId) =>
    InStation(fleet, id, s => ExecuteValue(() => audit.QueryCommand(commandId, s.Store, s.Journal))));
app.MapGet("/api/recipes", (RecipeStore recipes) => Results.Ok(new { active = recipes.Active, versions = recipes.List() }));
app.MapPost("/api/recipes/draft", (RecipeStore recipes, RecipeDraftRequest request, HttpContext ctx) =>
    ExecuteValue(() => recipes.Draft(new RecipeValues(request.Name, request.VisionOffsetMm, request.ExtraDwellMs),
        ((SimUser)ctx.Items["SimUser"]!).Name, request.ExpectedActiveVersionId)));
app.MapPost("/api/recipes/{id}/validate", (RecipeStore recipes, string id) => ExecuteValue(() => recipes.Validate(id)));
app.MapPost("/api/recipes/{id}/publish", (RecipeStore recipes, DeviceRuntime machine, string id) =>
    ExecuteValue(() => recipes.Publish(id, machine)));
app.MapPost("/api/recipes/rollback", (RecipeStore recipes, DeviceRuntime machine, RollbackRequest request, HttpContext ctx) =>
    ExecuteValue(() => recipes.Rollback(request.Id, ((SimUser)ctx.Items["SimUser"]!).Name, machine)));
app.MapGet("/api/recovery/checklist", (DeviceRuntime machine) => Results.Ok(machine.RecoveryChecklist()));
app.MapPost("/api/mode", (DeviceRuntime machine, ModeRequest r, HttpContext ctx) =>
    Execute(() => { if (r.Mode == "Manual" && ((SimUser)ctx.Items["SimUser"]!).Role == "Operator")
        throw new UnauthorizedAccessException("操作员不能切换至手动模式"); machine.SetMode(r.Mode); }));
app.MapPost("/api/sim/input", (DeviceRuntime machine, ToggleRequest r) => Execute(() => machine.SetInput(r.Name, r.Value)));
app.MapPost("/api/sim/fault", (DeviceRuntime machine, ToggleRequest r) => Execute(() => machine.SetFaultInjection(r.Name, r.Value)));
app.MapPost("/api/manual/axes/{id}/enable", (DeviceRuntime machine, string id, AxisEnableRequest r) => ExecuteAsync(() => machine.SetServoEnabledAsync(id, r.Enabled)));
app.MapPost("/api/manual/axes/{id}/home", (DeviceRuntime machine, string id) => ExecuteAsync(() => machine.HomeAsync(id)));
app.MapPost("/api/manual/axes/{id}/move", (DeviceRuntime machine, string id, AxisMoveRequest r) => ExecuteAsync(() => machine.MoveAsync(id, r.PositionMm)));
app.MapPost("/api/manual/cylinders/{id}/extend", (DeviceRuntime machine, string id) => ExecuteAsync(() => machine.CylinderActionAsync(id, true)));
app.MapPost("/api/manual/cylinders/{id}/retract", (DeviceRuntime machine, string id) => ExecuteAsync(() => machine.CylinderActionAsync(id, false)));
app.MapPost("/api/manual/vision/trigger", (DeviceRuntime machine) => ExecuteAsync(machine.TriggerVisionAsync));
app.MapPost("/api/cycle/start", (MultiStationRegistry fleet) => Execute(() => fleet.Start("A")));
app.MapPost("/api/cycle/stop", (MultiStationRegistry fleet) => Execute(() => fleet.Stop("A")));
app.MapPost("/api/fault/reset", (DeviceRuntime machine) => Execute(machine.ResetFault));
app.MapPost("/api/recovery/confirm", (DeviceRuntime machine, HttpContext ctx) =>
    Execute(() => machine.ConfirmRecovery(((SimUser)ctx.Items["SimUser"]!).Name)));
// Eager initialization ensures interrupted-cycle recovery happens before health is served.
// Q03: this block must never prevent the minimal diagnostic host from starting. A
// failure here is logged and surfaced through diagnostics; the host still reaches
// app.Run() and /api/health, /api/diagnostics/stations stay answerable.
try
{
    var fleet = app.Services.GetRequiredService<MultiStationRegistry>();
    var upgrades = app.Services.GetRequiredService<StationUpgradeManager>();
    _ = app.Services.GetRequiredService<FleetRolloutManager>();
    // F04: register the unified run-eligibility check for every station (existing and future).
    var eligibility = app.Services.GetRequiredService<RunEligibilityRegistry>();
    foreach (var id in fleet.StationIds) eligibility.Register(id, recoveryId => upgrades.UnresolvedReason(id, recoveryId), true);
    fleet.OnStationAdded = id => eligibility.Register(id, recoveryId => upgrades.UnresolvedReason(id, recoveryId), true);
    fleet.OnStationRemoved = eligibility.Unregister;
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "工站初始化或运行资格注册失败；继续以最小诊断宿主启动，详情见 /api/diagnostics/stations");
}
// F13/Q03: read-only diagnostics for stations that failed closed at startup. Every
// dependency is resolved defensively so this endpoint answers even when the fleet
// registry could not be constructed at all.
app.MapGet("/api/diagnostics/stations", (HttpContext ctx, IServiceProvider services) =>
{
    var user = ctx.Items["SimUser"] as SimUser;
    if (user == null || Permissions.Rank(user.Role) < 2)
        return Results.Json(new { ok = false, error = "需要工程师或管理员角色" }, statusCode: 403);
    var fleetNow = TryResolve<MultiStationRegistry>(services);
    var upgradesNow = TryResolve<StationUpgradeManager>(services);
    var templatesNow = TryResolve<StationTemplateCatalog>(services);
    var rolloutsNow = TryResolve<FleetRolloutManager>(services);
    return Results.Ok(new
    {
        ok = true,
        fleetAvailable = fleetNow is not null,
        failedStations = fleetNow?.FailedStations ?? new Dictionary<string, string>(),
        deploymentStartupIssues = (object?)upgradesNow?.StartupIssues ?? Array.Empty<string>(),
        templateStartupIssues = (object?)templatesNow?.StartupIssues ?? Array.Empty<string>(),
        rolloutStartupIssues = (object?)rolloutsNow?.StartupIssues ?? Array.Empty<string>(),
        eventWriteFailures = fleetNow is null ? new Dictionary<string, int>() :
            fleetNow.StationIds.ToDictionary(id => id, id =>
            {
                try { return fleetNow.Get(id).Journal.EventWriteFailures; }
                catch { return -1; }
            }),
        audit = AuditHealthSnapshot(services).View
    });
});
app.Run();
static IResult ExecuteValue<T>(Func<T> action)
{
    try { return Results.Ok(new { ok = true, data = action() }); }
    catch (UnknownStationException e)
    { return Results.NotFound(new { ok = false, error = e.Message }); }
    catch (UnauthorizedAccessException e)
    { return Results.Json(new { ok = false, error = e.Message }, statusCode: 403); }
    catch (Exception e) when (e is InvalidOperationException or ArgumentException)
    { return Results.Conflict(new { ok = false, error = e.Message }); }
}
/// <summary>R05: explicit business attribution for endpoints whose target is not in
/// the HTTP path (global upgrade/rollout/template routes).</summary>
static void AttributeAudit(HttpContext ctx, string? stationId = null, string? deploymentId = null, IEnumerable<string>? stations = null)
    => ctx.Items["AuditAttribution"] = new AuditAttribution(stationId, deploymentId, stations?.ToArray());
/// <summary>R04/R06: audit health read defensively - an unavailable audit store is
/// itself a degradation signal, not a reason to fail the diagnostics endpoint.</summary>
static (bool Ok, object View) AuditHealthSnapshot(IServiceProvider services)
{
    try
    {
        var audit = services.GetRequiredService<AuditTrail>();
        return (!audit.Degraded, audit.Health());
    }
    catch (Exception ex)
    {
        return (false, new { degraded = true, unavailable = true, pending = 0, failures = 0, lastError = ex.Message });
    }
}
/// <summary>Q02: operations that must remain callable while traceable changes are
/// refused because no durable audit channel exists (stop / fault reset / recovery /
/// loss acknowledgement).</summary>
static bool SurvivalOperation(string gatePath) => gatePath is
    "/api/cycle/stop" or "/api/fault/reset" or "/api/recovery/confirm" or "/api/audit/acknowledge-loss";
/// <summary>Q03: defensive service resolution for diagnostics - a broken dependency
/// must degrade one field, not fail the endpoint.</summary>
static T? TryResolve<T>(IServiceProvider services) where T : class
{
    try { return services.GetService<T>(); } catch { return null; }
}
static IResult Execute(Action action) => ExecuteValue(() => { action(); return true; });
static async Task<IResult> ExecuteAsync(Func<Task> action)
{
    try { await action(); return Results.Ok(new { ok = true }); }
    catch (Exception e) when (e is InvalidOperationException or ArgumentException or TimeoutException or OperationCanceledException)
    { return Results.Conflict(new { ok = false, error = e.Message }); }
}

static IResult InStation(MultiStationRegistry fleet, string id, Func<StationContext, IResult> handler)
{
    try { return handler(fleet.Get(id)); }
    catch (UnknownStationException) { return Results.NotFound(new { ok = false, error = "工站不存在" }); }
}
static async Task<IResult> InStationAsync(MultiStationRegistry fleet, string id, Func<StationContext, Task<IResult>> handler)
{
    try { return await handler(fleet.Get(id)); }
    catch (UnknownStationException) { return Results.NotFound(new { ok = false, error = "工站不存在" }); }
}
