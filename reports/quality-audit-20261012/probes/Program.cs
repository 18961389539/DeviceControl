using DeviceControl.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;

// Audit-only observations, isolated temporary data. Does not alter product code.
var root = Path.GetFullPath(args[0]);
var observations = new List<object>();
var temp = Path.Combine(Path.GetTempPath(), "dc-quality-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temp, "config"));
foreach (var file in new[] { "station.json", "station-B.json" })
    File.Copy(Path.Combine(root, "backend/DeviceControl.Api/config", file), Path.Combine(temp, "config", file));
var env = new AuditEnvironment(temp);
try
{
    var cfg = StationConfig.Load(Path.Combine(temp, "config/station.json"));
    var db = new SqliteStore(env);
    var recipes = new RecipeStore(env, db);
    var flows = new FlowRevisions(db, cfg);
    DeviceRuntime Build(RunJournal j) {
        var io = new SimIoController(cfg); var interlocks = new OrdinaryInterlockMonitor(cfg, io);
        return new DeviceRuntime(cfg, io, new SimMotionController(cfg, io, interlocks),
            new SimCylinderRegistry(cfg, io, interlocks), new SimCamera(io, interlocks), interlocks, j, recipes, flows);
    }
    db.BeginCycle(new CycleStartRecord("audit-interrupted", DateTimeOffset.UtcNow.ToString("O"), recipes.Active.Id, flows.Active.Id));
    var firstJournal = new RunJournal(env, db);
    var first = Build(firstJournal);
    var secondJournal = new RunJournal(env, db);
    var runtime = Build(secondJournal);
    observations.Add(new { id="F01", firstBootRecovery=first.Snapshot().RecoveryRequired,
        secondBootRecovery=runtime.Snapshot().RecoveryRequired, persistedOutcome=db.Cycle("audit-interrupted")!.Outcome });

    // Two validated drafts from the same baseline: publish newer then stale.
    var older=recipes.Draft(new RecipeValues("older", 1, 0), "engineer-1"); recipes.Validate(older.Id);
    var newer=recipes.Draft(new RecipeValues("newer", 2, 0), "engineer-2"); recipes.Validate(newer.Id);
    recipes.Publish(newer.Id, runtime);
    recipes.Publish(older.Id, runtime);
    observations.Add(new { id="F03", staleDraftPublished=recipes.Active.Id==older.Id, activeName=recipes.Active.Values.Name });

    var candidate=recipes.Draft(new RecipeValues("write-failed", 3, 0), "audit"); recipes.Validate(candidate.Id);
    var before=db.ReadRecipes()!.Value.ActiveId;
    using (var conn=new SqliteConnection("Data Source="+db.DatabasePath)) {
        conn.Open(); using var cmd=conn.CreateCommand();
        cmd.CommandText="CREATE TRIGGER audit_fail_recipe BEFORE INSERT ON recipe_revisions BEGIN SELECT RAISE(ABORT, 'audit injected write failure'); END;";
        cmd.ExecuteNonQuery();
    }
    string? failure=null;
    try { recipes.Publish(candidate.Id, runtime); } catch (Exception ex) { failure=ex.GetType().Name; }
    observations.Add(new { id="F02", failure, memoryActive=recipes.Active.Id,
        diskActive=db.ReadRecipes()!.Value.ActiveId, originalActive=before,
        runtimeRecovery=runtime.Snapshot().RecoveryRequired });
    using (var conn=new SqliteConnection("Data Source="+db.DatabasePath)) {
        conn.Open(); using var cmd=conn.CreateCommand(); cmd.CommandText="DROP TRIGGER audit_fail_recipe"; cmd.ExecuteNonQuery();
    }
    // Reload the committed recipe before the next independent observation.
    recipes=new RecipeStore(env,db);
    runtime=Build(new RunJournal(env,db));

    // An unresolved deployment is recorded, but normal cycle admission has no such guard.
    var fleet=new MultiStationRegistry(env,cfg,runtime,db,secondJournal,flows,recipes,new SharedResourceCoordinator());
    var catalog=new StationTemplateCatalog(env);
    var deployment="dep-"+new string('a',32);
    Directory.CreateDirectory(Path.Combine(temp,"data/deployments"));
    var record=new StationDeploymentRecord(deployment,"A","audit-template","audit-plan","audit",
        DateTimeOffset.UtcNow.ToString("O"),null,"InProgress",null,flows.Active,recipes.Active,null,null);
    File.WriteAllText(Path.Combine(temp,"data/deployments",deployment+".json"),JsonSerializer.Serialize(record));
    var upgrades=new StationUpgradeManager(env,fleet,catalog);
    foreach(var axis in cfg.Axes) { await runtime.SetServoEnabledAsync(axis.Id,true); await runtime.HomeAsync(axis.Id); }
    runtime.SetMode("Auto");
    fleet.Start("A");
    observations.Add(new { id="F04", deploymentStatus=upgrades.History("A")[0].Status,
        cycleAccepted=runtime.Snapshot().Busy, state=runtime.Snapshot().State });
    runtime.RequestStop();
    for(var i=0;i<100 && runtime.Snapshot().Busy;i++) await Task.Delay(20);
    var stopped=runtime.Snapshot();
    var afterStop=Build(new RunJournal(env,db));
    observations.Add(new { id="F01-stop", beforeRestartRecovery=stopped.RecoveryRequired,
        afterRestartRecovery=afterStop.Snapshot().RecoveryRequired });

    var cameraIo=new SimIoController(cfg); var monitor=new OrdinaryInterlockMonitor(cfg,cameraIo);
    var camera=new SimCamera(cameraIo,monitor);
    var pending=camera.LocateAsync(CancellationToken.None);
    await Task.Delay(50); cameraIo.SetSimulationInput("GuardClosed",false);
    var result=await pending;
    observations.Add(new { id="F05", guardClosed=cameraIo.ReadInput("GuardClosed"), visionOk=result.Ok });

    using (var conn=new SqliteConnection("Data Source="+db.DatabasePath)) {
        conn.Open(); using var cmd=conn.CreateCommand();
        cmd.CommandText="""
          WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<50000)
          INSERT INTO audit_events(id,time_utc,actor,role,method,path,http_status,trace_id,payload_json)
          SELECT printf('%012d',x),'2025-01-01T00:00:00Z','audit','Admin','POST','/api/mode',200,'test',
          json_object('Id',printf('%012d',x),'Time','2025-01-01T00:00:00Z','Actor','audit','Role','Admin',
          'Method','POST','Path','/api/mode','Status',200,'TraceId','test','Outcome','Accepted') FROM n;
          """;
        cmd.ExecuteNonQuery();
    }
    db.UpsertAudit(new AuditEntry("latest-audit","2100-01-01T00:00:00Z","latest-user","Admin",
        "POST","/api/mode",200,"latest-trace","Accepted"));
    var loaded=db.Audits();
    observations.Add(new { id="F07", inserted=50001, returned=loaded.Count,
        latestEventReturned=loaded.Any(x=>x.Id=="latest-audit") });
    Console.WriteLine(JsonSerializer.Serialize(observations,new JsonSerializerOptions{WriteIndented=true}));
}
finally {
    SqliteConnection.ClearAllPools();
    var cleanupTarget=Path.GetFullPath(temp);
    var allowedParent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if (!cleanupTarget.StartsWith(allowedParent,StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(cleanupTarget).StartsWith("dc-quality-audit-",StringComparison.Ordinal))
        throw new InvalidOperationException("Audit cleanup target is outside its dedicated temporary directory");
    Directory.Delete(temp, true);
}

sealed class AuditEnvironment(string path):IHostEnvironment {
    public string EnvironmentName {get;set;}="Audit";
    public string ApplicationName {get;set;}="AuditProbes";
    public string ContentRootPath {get;set;}=path;
    public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
}
