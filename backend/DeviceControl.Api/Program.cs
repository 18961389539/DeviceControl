using DeviceControl.Api;

var builder = WebApplication.CreateBuilder(args);
var listenUrl = builder.Configuration["SimListenUrl"] ?? "http://127.0.0.1:5088";
if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var listenUri) || !listenUri.IsLoopback || listenUri.Scheme != "http")
    throw new InvalidOperationException("仿真 API 仅允许绑定 HTTP loopback 地址");
builder.WebHost.UseUrls(listenUrl); // SIMULATION, localhost-only; not production auth.
builder.Services.AddCors(o => o.AddPolicy("LocalDev", p => p
    .WithOrigins("http://127.0.0.1:5173", "http://localhost:5173")
    .AllowAnyHeader().AllowAnyMethod()));
var configPath = Path.Combine(builder.Environment.ContentRootPath, "config", "station.json");
builder.Services.AddSingleton(StationConfig.Load(configPath));
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
    if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        ctx.Request.Method == "OPTIONS" || path is "/api/health" or "/api/auth/login")
    { await next(); return; }
    var sessions = ctx.RequestServices.GetRequiredService<DemoSessions>();
    var audit = ctx.RequestServices.GetRequiredService<AuditTrail>();
    var token = ctx.Request.Headers["X-Sim-Token"].ToString();
    var user = sessions.Get(token);
    var required = Permissions.Required(ctx.Request);
    var denied = user == null ? 401 : Permissions.Rank(user.Role) < required ? 403 : 0;
    ctx.Response.Headers["X-Trace-Id"] = ctx.TraceIdentifier;
    if (denied != 0)
    {
        ctx.Response.StatusCode = denied;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = denied == 401 ? "请登录仿真账号" : "当前角色无权执行此操作" });
        if (ctx.Request.Method != "GET")
            audit.Record(user?.Name ?? "anonymous", user?.Role ?? "None", ctx.Request.Method, path,
                ctx.Response.StatusCode, ctx.TraceIdentifier);
    }
    else
    {
        ctx.Items["SimUser"] = user;
        try { await next(); }
        finally
        {
            // F10: an unhandled exception must not skip the audit row.
            if (ctx.Request.Method != "GET")
                audit.Record(user?.Name ?? "anonymous", user?.Role ?? "None", ctx.Request.Method, path,
                    ctx.Response.StatusCode, ctx.TraceIdentifier);
        }
    }
});
app.MapGet("/api/health", (SqliteStore store, MultiStationRegistry fleet) =>
{
    // F10: health must prove the store answers, not return a constant.
    string? storeError = null;
    try { using var c = store.OpenProbe(); } catch (Exception ex) { storeError = ex.Message; }
    return Results.Ok(new { ok = storeError == null, mode = "Simulation", safetyRated = false, version = "2.0-rc",
        store = storeError == null ? "reachable" : "unreachable", storeError,
        stations = fleet.StationIds.Count });
});
app.MapPost("/api/auth/login", (DemoSessions sessions, LoginRequest request, HttpContext ctx, AuditTrail audit) =>
{
    try
    {
        var (token, user) = sessions.Login(request.Username, request.Password);
        audit.Record(user.Name, user.Role, "POST", "/api/auth/login", 200, ctx.TraceIdentifier);
        return Results.Ok(new { token, user });
    }
    catch (UnauthorizedAccessException)
    {
        audit.Record(request.Username, "None", "POST", "/api/auth/login", 401, ctx.TraceIdentifier);
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
    ExecuteValue(() => templates.Capture(fleet, r.StationId, r.Name, ((SimUser)ctx.Items["SimUser"]!).Name)));
app.MapPost("/api/templates/{id}/validate", (StationTemplateCatalog templates, string id) =>
    ExecuteValue(() => templates.Validate(id)));
app.MapPost("/api/templates/{id}/publish", (StationTemplateCatalog templates, string id) =>
    ExecuteValue(() => templates.Publish(id)));
app.MapGet("/api/templates/{id}/diff/{stationId}", (StationTemplateCatalog templates,
    MultiStationRegistry fleet, string id, string stationId) =>
    InStation(fleet, stationId, station => ExecuteValue(() => templates.Diff(id, station))));
app.MapPost("/api/templates/{id}/deploy", (StationTemplateCatalog templates, MultiStationRegistry fleet,
    string id, DeployTemplateRequest request) =>
    ExecuteValue(() => templates.Deploy(id, request.StationIds, fleet)));

// V1.5: existing-station workflow/recipe upgrade, with optimistic preflight and durable intent.
app.MapPost("/api/upgrades/preflight", (StationUpgradeManager upgrades, UpgradePreflightRequest request, HttpContext ctx) =>
    ExecuteValue(() => upgrades.Preflight(request.TemplateId, request.StationId, ((SimUser)ctx.Items["SimUser"]!).Name)));
app.MapPost("/api/upgrades/apply", (StationUpgradeManager upgrades, UpgradeApplyRequest request, HttpContext ctx) =>
    ExecuteValue(() => upgrades.Apply(request.PlanId, ((SimUser)ctx.Items["SimUser"]!).Name)));
app.MapPost("/api/upgrades/rollback", (StationUpgradeManager upgrades, UpgradeRollbackRequest request, HttpContext ctx) =>
    ExecuteValue(() => upgrades.Rollback(request.DeploymentId, ((SimUser)ctx.Items["SimUser"]!).Name)));
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
    ExecuteValue(() => rollouts.Reconcile(id, request, ((SimUser)ctx.Items["SimUser"]!).Name)));
app.MapPost("/api/rollouts", (FleetRolloutManager rollouts, RolloutCreateRequest request, HttpContext ctx) =>
    ExecuteValue(() => rollouts.Create(request, ((SimUser)ctx.Items["SimUser"]!).Name)));
app.MapPost("/api/rollouts/{id}/advance", (FleetRolloutManager rollouts, string id, HttpContext ctx) =>
    ExecuteValue(() => rollouts.Advance(id, ((SimUser)ctx.Items["SimUser"]!).Name)));
app.MapPost("/api/rollouts/{id}/verify", (FleetRolloutManager rollouts, string id) =>
    ExecuteValue(() => rollouts.Verify(id)));
app.MapPost("/api/rollouts/{id}/pause", (FleetRolloutManager rollouts, string id, RolloutNote request) =>
    ExecuteValue(() => rollouts.Pause(id, request.Reason)));
app.MapPost("/api/rollouts/{id}/resume", (FleetRolloutManager rollouts, string id) =>
    ExecuteValue(() => rollouts.Resume(id)));
app.MapPost("/api/rollouts/{id}/abort", (FleetRolloutManager rollouts, string id, RolloutNote request) =>
    ExecuteValue(() => rollouts.Abort(id, request.Reason)));

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
var fleet = app.Services.GetRequiredService<MultiStationRegistry>();
var upgrades = app.Services.GetRequiredService<StationUpgradeManager>();
var rollouts = app.Services.GetRequiredService<FleetRolloutManager>();
// F04: register the unified run-eligibility check for every station (existing and future).
var eligibility = app.Services.GetRequiredService<RunEligibilityRegistry>();
foreach (var id in fleet.StationIds) eligibility.Register(id, () => upgrades.UnresolvedReason(id));
fleet.OnStationAdded = id => eligibility.Register(id, () => upgrades.UnresolvedReason(id));
// F13: read-only diagnostics for stations that failed closed at startup.
app.MapGet("/api/diagnostics/stations", (HttpContext ctx) =>
{
    var user = ctx.Items["SimUser"] as SimUser;
    if (user == null || Permissions.Rank(user.Role) < 2)
        return Results.Json(new { ok = false, error = "需要工程师或管理员角色" }, statusCode: 403);
    return Results.Ok(new
    {
        ok = true,
        failedStations = fleet.FailedStations,
        deploymentStartupIssues = upgrades.StartupIssues,
        eventWriteFailures = fleet.StationIds.ToDictionary(id => id, id =>
        {
            try { return fleet.Get(id).Journal.EventWriteFailures; }
            catch { return -1; }
        })
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
