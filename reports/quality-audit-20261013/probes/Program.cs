using DeviceControl.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;

// Audit observations, not a passing acceptance suite. All writes use a fresh temp root.
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.."));
var temp = Path.Combine(Path.GetTempPath(),"dc-quality-20261013-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temp,"config"));
foreach(var file in new[]{"station.json","station-B.json"})
    File.Copy(Path.Combine(root,"backend/DeviceControl.Api/config",file),Path.Combine(temp,"config",file));
var env = new AuditEnvironment(temp);
var observations = new List<object>();
try
{
    var cfg=StationConfig.Load(Path.Combine(temp,"config/station.json"));
    var db=new SqliteStore(env);
    var recipes=new RecipeStore(env,db);
    var flows=new FlowRevisions(db,cfg);
    var eligibility=new RunEligibilityRegistry();
    DeviceRuntime Build(RunJournal journal) {
        var io=new SimIoController(cfg); var monitor=new OrdinaryInterlockMonitor(cfg,io);
        return new DeviceRuntime(cfg,io,new SimMotionController(cfg,io,monitor),
            new SimCylinderRegistry(cfg,io,monitor),new SimCamera(io,monitor),monitor,journal,recipes,flows,eligibility);
    }
    void Sql(string sql) {
        using var c=new SqliteConnection("Data Source="+db.DatabasePath);
        c.Open(); using var q=c.CreateCommand(); q.CommandText=sql; q.ExecuteNonQuery();
    }
    // Boot aborts after RunJournal's recovery transaction, before DeviceRuntime construction.
    db.BeginCycle(new CycleStartRecord("startup-window",DateTimeOffset.UtcNow.ToString("O"),recipes.Active.Id,flows.Active.Id));
    var interruptedBootJournal=new RunJournal(env,db);
    var nextJournal=new RunJournal(env,db);
    var runtime=Build(nextJournal);
    observations.Add(new {id="N02",firstRecoveryCount=interruptedBootJournal.RecoveredInterruptions,
        nextRecoveryCount=nextJournal.RecoveredInterruptions,recoveryRequired=runtime.Snapshot().RecoveryRequired,
        persistedOutcome=db.Cycle("startup-window")!.Outcome});
    // Ordinary complete boot: previous fix does hold across repeated boots.
    db.BeginCycle(new CycleStartRecord("normal-interruption",DateTimeOffset.UtcNow.ToString("O"),recipes.Active.Id,flows.Active.Id));
    var normal=Build(new RunJournal(env,db));
    var second=Build(new RunJournal(env,db));
    var third=Build(new RunJournal(env,db));
    observations.Add(new {id="old-F01",first=normal.Snapshot().RecoveryRequired,
        second=second.Snapshot().RecoveryRequired,third=third.Snapshot().RecoveryRequired});
    // Explicitly clear probe state to keep following observations independent.
    db.ClearRecoveryState("audit-isolation");
    var journal=new RunJournal(env,db); runtime=Build(journal);
    var old=recipes.Draft(new RecipeValues("old",1,0),"audit"); recipes.Validate(old.Id);
    var newer=recipes.Draft(new RecipeValues("new",2,0),"audit"); recipes.Validate(newer.Id);
    recipes.Publish(newer.Id,runtime);
    string? staleError=null;
    try {recipes.Publish(old.Id,runtime);} catch(Exception ex){staleError=ex.Message;}
    observations.Add(new {id="old-F03",rejected=staleError!=null,activePreserved=recipes.Active.Id==newer.Id,error=staleError});
    var candidate=recipes.Draft(new RecipeValues("write-failed",3,0),"audit"); recipes.Validate(candidate.Id);
    var original=recipes.Active.Id;
    Sql("CREATE TRIGGER audit_fail_recipe BEFORE INSERT ON recipe_revisions BEGIN SELECT RAISE(ABORT,'audit write failure'); END;");
    string? writeError=null;
    try {recipes.Publish(candidate.Id,runtime);}catch(Exception ex){writeError=ex.GetType().Name;}
    observations.Add(new {id="old-F02",error=writeError,memoryPreserved=recipes.Active.Id==original,
        diskPreserved=db.ReadRecipes()!.Value.ActiveId==original});
    Sql("DROP TRIGGER audit_fail_recipe;");
    // Persisted manual failure gate cannot be written: swallowed failure permits next boot.
    Sql("CREATE TRIGGER audit_fail_recovery BEFORE INSERT ON recovery_state BEGIN SELECT RAISE(ABORT,'audit recovery write failure'); END;");
    runtime.SetFaultInjection("XStuck",true);
    await runtime.SetServoEnabledAsync("X",true);
    string? manualError=null;
    try {await runtime.HomeAsync("X");}catch(Exception ex){manualError=ex.Message;}
    var afterManual=Build(new RunJournal(env,db));
    observations.Add(new {id="N03",manualError,beforeRestart=runtime.Snapshot().RecoveryRequired,
        afterRestart=afterManual.Snapshot().RecoveryRequired});
    Sql("DROP TRIGGER audit_fail_recovery;");
    runtime=afterManual;
    var fleet=new MultiStationRegistry(env,cfg,runtime,db,journal,flows,recipes,new SharedResourceCoordinator(),eligibility);
    var catalog=new StationTemplateCatalog(env);
    var dep="dep-"+new string('a',32);
    Directory.CreateDirectory(Path.Combine(temp,"data/deployments"));
    var record=new StationDeploymentRecord(dep,"A","audit-template","audit-plan","audit",
        DateTimeOffset.UtcNow.ToString("O"),null,"InProgress",null,flows.Active,recipes.Active,null,null);
    File.WriteAllText(Path.Combine(temp,"data/deployments",dep+".json"),JsonSerializer.Serialize(record));
    var upgrades=new StationUpgradeManager(env,fleet,catalog);
    eligibility.Register("A",()=>upgrades.UnresolvedReason("A"));
    string? modeError=null;string? rollbackError=null;
    try{runtime.SetMode("Auto");}catch(Exception ex){modeError=ex.Message;}
    try{upgrades.Rollback(dep,"audit-admin");}catch(Exception ex){rollbackError=ex.Message;}
    observations.Add(new {id="N01",status=upgrades.History("A")[0].Status,
        autoBlocked=modeError!=null,rollbackBlocked=rollbackError!=null,rollbackError});
    // Exercise the exact snapshot refresh method concurrently with its public reader.
    // Reflection only invokes the existing method; it does not replace implementation.
    var refresh=typeof(StationUpgradeManager).GetMethod("RefreshUnresolved",
        System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
    var misses=0;var reads=0;
    var writer=Task.Run(()=>{for(var i=0;i<1000;i++)refresh.Invoke(upgrades,null);});
    while(!writer.IsCompleted){reads++;if(upgrades.UnresolvedReason("A")==null)misses++;}
    await writer;
    observations.Add(new{id="N05",reads,missingBlockObservations=misses,
        finalBlockPresent=upgrades.UnresolvedReason("A")!=null});
    // One corrupt rollout still takes down its entire eagerly initialized manager.
    Directory.CreateDirectory(Path.Combine(temp,"data/rollouts"));
    File.WriteAllText(Path.Combine(temp,"data/rollouts/roll-"+new string('b',32)+".json"),"{");
    string? rolloutError=null;
    try{_ = new FleetRolloutManager(env,fleet,catalog,upgrades);}catch(Exception ex){rolloutError=ex.GetType().Name;}
    observations.Add(new{id="N06",managerConstructionFailed=rolloutError!=null,error=rolloutError});
    var ioCamera=new SimIoController(cfg);var monitorCamera=new OrdinaryInterlockMonitor(cfg,ioCamera);
    var camera=new SimCamera(ioCamera,monitorCamera);
    var pending=camera.LocateAsync(CancellationToken.None);
    await Task.Delay(50);ioCamera.SetSimulationInput("GuardClosed",false);
    string? cameraError=null;
    try{await pending;}catch(Exception ex){cameraError=ex.Message;}
    observations.Add(new{id="old-F05",rejected=cameraError!=null,error=cameraError});
    observations.Add(new{id="eligibility-missing",blockingReason=eligibility.BlockingReason("UNREGISTERED")});
    Sql("""
      WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<50000)
      INSERT INTO audit_events(id,time_utc,actor,role,method,path,http_status,trace_id,payload_json)
      SELECT printf('%012d',x),'2025-01-01T00:00:00Z','audit','Admin','POST','/api/mode',200,'test',
      json_object('Id',printf('%012d',x),'Time','2025-01-01T00:00:00Z','Actor','audit','Role','Admin',
      'Method','POST','Path','/api/mode','Status',200,'TraceId','test','Outcome','Accepted') FROM n;
      """);
    db.UpsertAudit(new AuditEntry("latest-audit","2100-01-01T00:00:00Z","audit","Admin","POST","/api/mode",200,"trace","Accepted"));
    observations.Add(new{id="old-F07",latestReturned=db.Audits().Any(x=>x.Id=="latest-audit")});
    using(var c=new SqliteConnection("Data Source="+db.DatabasePath)) {
        c.Open();using var q=c.CreateCommand();q.CommandText="SELECT sqlite_version()";
        observations.Add(new{id="native-sqlite",version=q.ExecuteScalar()});
    }
    Console.WriteLine(JsonSerializer.Serialize(observations,new JsonSerializerOptions{WriteIndented=true}));
}
finally
{
    SqliteConnection.ClearAllPools();
    var target=Path.GetFullPath(temp);
    var parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if(!target.StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(target).StartsWith("dc-quality-20261013-"))
        throw new InvalidOperationException("Unsafe audit cleanup target");
    Directory.Delete(target,true);
}
sealed class AuditEnvironment(string path):IHostEnvironment {
    public string EnvironmentName{get;set;}="Audit";
    public string ApplicationName{get;set;}="AuditProbes";
    public string ContentRootPath{get;set;}=path;
    public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
}
