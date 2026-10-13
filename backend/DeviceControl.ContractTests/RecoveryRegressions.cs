using DeviceControl.Api;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

internal static class RecoveryRegressions
{
    public static async Task Run(string sourceRoot, List<string> errors)
    {
        var root = Path.Combine(Path.GetTempPath(), "dc-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var env = new TestEnvironment(root);
            var store = new SqliteStore(env);
            var journal = new RunJournal(env, store);
            journal.BeginCycle(new CycleStartRecord("interrupted-test", DateTimeOffset.UtcNow.ToString("O"), null, "flow-test"));
            var recoveredJournal = new RunJournal(env, store);
            Check(recoveredJournal.RecoveredInterruptions == 1, "N02 converts inflight run", errors);
            bool inFlight;
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString()))
            { c.Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT EXISTS(SELECT 1 FROM run_inflight)"; inFlight = Convert.ToInt32(q.ExecuteScalar()) != 0; }
            Check(recoveredJournal.RecoveryState is { Required: true } && !inFlight,
                "N02 recovery gate and inflight cleanup committed together", errors);

            var intent = recoveredJournal.BeginManualAction("AxisMove", "X");
            using (var c = store.OpenProbe())
            using (var q = c.CreateCommand())
            {
                q.CommandText = "CREATE TRIGGER reject_recovery_state BEFORE INSERT ON recovery_state BEGIN SELECT RAISE(ABORT,'injected write refusal'); END";
                q.ExecuteNonQuery();
            }
            try { recoveredJournal.CompleteManualAction(intent, false, "injected hardware failure"); errors.Add("N03 intent failure commit should fail under trigger"); }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
            Check(store.HasUnresolvedManualActions(), "N03 failed result transaction preserves intent", errors);
            using (var c = store.OpenProbe())
            using (var q = c.CreateCommand()) { q.CommandText = "DROP TRIGGER reject_recovery_state"; q.ExecuteNonQuery(); }
            var afterRestart = new RunJournal(env, store);
            Check(afterRestart.RecoveryState is { Required: true }, "N03 startup converts unresolved manual intent into recovery gate", errors);
            afterRestart.ClearRecoveryRequired("contract-engineer");
            Check(!store.HasUnresolvedManualActions() && afterRestart.RecoveryState is { Required: false },
                "N03 human confirmation atomically clears the checked intent", errors);
            Console.WriteLine("OK   N02/N03 SQLite recovery atomicity and manual-action intent restart");

            await TestScopedDeploymentRollback(sourceRoot, root, errors);
            TestAtomicEligibilitySnapshot(errors);
            TestInterruptedRecoveryWriteRefusal(env, errors);
            TestCycleTerminalRecoveryWriteRefusal(env, errors);
            TestAuditClassificationAndStationFilter(env, errors);
        }
        catch (Exception ex) { errors.Add("N01-N05 recovery regression exception: " + ex); }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task TestScopedDeploymentRollback(string sourceRoot, string root, List<string> errors)
    {
        var env = new TestEnvironment(Path.Combine(root, "deploy")); Directory.CreateDirectory(env.ContentRootPath);
        var cfg = StationConfig.Load(Path.Combine(sourceRoot, "backend", "DeviceControl.Api", "config", "station.json"));
        var store = new SqliteStore(env);
        var recipes = new RecipeStore(env, store);
        var flows = new FlowRevisions(store, cfg);
        var journal = new RunJournal(env, store);
        var io = new SimIoController(cfg); var interlocks = new OrdinaryInterlockMonitor(cfg, io);
        var eligibility = new RunEligibilityRegistry();
        var runtime = new DeviceRuntime(cfg, io, new SimMotionController(cfg, io, interlocks),
            new SimCylinderRegistry(cfg, io, interlocks), new SimCamera(io, interlocks), interlocks,
            journal, recipes, flows, eligibility);
        var fleet = new MultiStationRegistry(env, new FixedServices()
            .Add(cfg).Add(runtime).Add(store).Add(journal).Add(flows).Add(recipes),
            new SharedResourceCoordinator(), eligibility);
        var catalog = new StationTemplateCatalog(env);
        var manager = new StationUpgradeManager(env, fleet, catalog);
        foreach (var id in fleet.StationIds) eligibility.Register(id, recoveryId => manager.UnresolvedReason(id, recoveryId), true);
        TestCorruptHistoryIsolation(env, fleet, catalog, manager, errors);

        var template = catalog.Capture(fleet, "A", "recovery-regression", "contract");
        catalog.Validate(template.Id); catalog.Publish(template.Id);
        var plan = manager.Preflight(template.Id, "B", "contract");
        var deployed = manager.Apply(plan.Id, "contract");
        var interrupted = deployed with { Status = "Interrupted", Error = "injected restart after successful version commit" };
        File.WriteAllText(Path.Combine(env.ContentRootPath, "data", "deployments", deployed.Id + ".json"),
            System.Text.Json.JsonSerializer.Serialize(interrupted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var restartedManager = new StationUpgradeManager(env, fleet, catalog);
        eligibility.Register("B", recoveryId => restartedManager.UnresolvedReason("B", recoveryId), true);
        var station = fleet.Get("B");
        var refresh = typeof(StationUpgradeManager).GetMethod("RefreshUnresolved", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var missingDuringRefresh = 0;
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 250; i++)
            {
                refresh.Invoke(restartedManager, null);
                if (restartedManager.UnresolvedReason("B") is null) Interlocked.Increment(ref missingDuringRefresh);
            }
        });
        Check(missingDuringRefresh == 0, "N05 immutable deployment snapshot never exposes a missing blocker", errors);
        var autoBlocked = false;
        try { station.Machine.SetMode("Auto"); } catch (InvalidOperationException) { autoBlocked = true; }
        Check(autoBlocked, "N01 interrupted deployment blocks Auto", errors);
        var rolled = restartedManager.Rollback(deployed.Id, "contract-engineer");
        Check(rolled.Status == "RolledBack" &&
            System.Text.Json.JsonSerializer.Serialize(station.Flows.Active.Definition) == System.Text.Json.JsonSerializer.Serialize(deployed.BeforeFlow.Definition) &&
            station.Recipes.Active.Values == deployed.BeforeRecipe.Values,
            "N01 scoped rollback restores the recorded before versions", errors);
        Check(restartedManager.UnresolvedReason("B") is null, "N01 successful rollback clears its own blocker", errors);
        var secondPlan = restartedManager.Preflight(template.Id, "B", "contract-admin");
        var second = restartedManager.Apply(secondPlan.Id, "contract-admin");
        var falseSucceeded = second with { Status = "Succeeded", AfterFlowId = "unknown-flow", AfterRecipeId = "unknown-recipe" };
        File.WriteAllText(Path.Combine(env.ContentRootPath, "data", "deployments", second.Id + ".json"),
            System.Text.Json.JsonSerializer.Serialize(falseSucceeded, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var succeededDriftRejected = false;
        try { restartedManager.Rollback(second.Id, "contract-admin", station.Flows.Active.Id, station.Recipes.Active.Id,
            "此备注不能绕过成功部署的版本冲突"); }
        catch (InvalidOperationException) { succeededDriftRejected = true; }
        Check(succeededDriftRejected, "N01 operator evidence cannot bypass drift on a Succeeded record", errors);
        var unknownWindow = second with { Status = "Interrupted", AfterFlowId = null,
            Error = "simulated crash after flow commit before after-flow ID was journaled" };
        File.WriteAllText(Path.Combine(env.ContentRootPath, "data", "deployments", second.Id + ".json"),
            System.Text.Json.JsonSerializer.Serialize(unknownWindow, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var unknownManager = new StationUpgradeManager(env, fleet, catalog);
        eligibility.Register("B", recoveryId => unknownManager.UnresolvedReason("B", recoveryId), true);
        var observedFlow = station.Flows.Active.Id; var observedRecipe = station.Recipes.Active.Id;
        var unverifiedRejected = false;
        try { unknownManager.Rollback(second.Id, "contract-admin"); }
        catch (InvalidOperationException) { unverifiedRejected = true; }
        var wrongObservationRejected = false;
        try { unknownManager.Rollback(second.Id, "contract-admin", "stale-flow", observedRecipe,
            "确认当前工站活动版本"); }
        catch (InvalidOperationException) { wrongObservationRejected = true; }
        var observed = unknownManager.Rollback(second.Id, "contract-admin", observedFlow, observedRecipe,
            "已人工核对当前流程与配方活动版本");
        Check(unverifiedRejected && wrongObservationRejected && observed.Status == "RolledBack" &&
            observed.RecoveryObservedFlowId == observedFlow && observed.RecoveryObservedRecipeId == observedRecipe &&
            observed.RecoveryObservedBy == "contract-admin" && !string.IsNullOrWhiteSpace(observed.RecoveryObservedAt),
            "N01 unknown after-version window requires explicit matching observation and preserves its evidence", errors);
        Console.WriteLine("OK   N01 interrupted deployment rollback with scoped eligibility context");
        await Task.CompletedTask;
    }

    private static void TestInterruptedRecoveryWriteRefusal(TestEnvironment env, List<string> errors)
    {
        var store = new SqliteStore(env, "recovery-refusal");
        var journal = new RunJournal(env, store, "recovery-refusal");
        journal.BeginCycle(new CycleStartRecord("refused-recovery", DateTimeOffset.UtcNow.ToString("O"), null, "flow"));
        using (var c = store.OpenProbe()) using (var q = c.CreateCommand())
        { q.CommandText = "CREATE TRIGGER reject_recovery_gate BEFORE INSERT ON recovery_state BEGIN SELECT RAISE(ABORT,'recovery gate refusal'); END"; q.ExecuteNonQuery(); }
        var refused = false;
        try { _ = new RunJournal(env, store, "recovery-refusal"); } catch (Microsoft.Data.Sqlite.SqliteException) { refused = true; }
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString()))
        {
            c.Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT EXISTS(SELECT 1 FROM run_inflight WHERE run_id='refused-recovery')";
            Check(refused && Convert.ToInt32(q.ExecuteScalar()) == 1 && !store.Cycles().Any(x => x.Id == "refused-recovery"),
                "N02 recovery-state write refusal rolls back Interrupted row and inflight cleanup", errors);
        }
        using (var c = store.OpenProbe()) using (var q = c.CreateCommand())
        { q.CommandText = "DROP TRIGGER reject_recovery_gate"; q.ExecuteNonQuery(); }
        var retry = new RunJournal(env, store, "recovery-refusal");
        Check(retry.RecoveryState is { Required: true } && retry.RecoveredInterruptions == 1,
            "N02 next startup atomically completes recovery after storage recovers", errors);
        Console.WriteLine("OK   N02 recovery transaction refusal and retry");
    }

    private static void TestCycleTerminalRecoveryWriteRefusal(TestEnvironment env, List<string> errors)
    {
        var store = new SqliteStore(env, "cycle-terminal-refusal");
        var journal = new RunJournal(env, store, "cycle-terminal-refusal");
        journal.BeginCycle(new CycleStartRecord("failed-cycle", DateTimeOffset.UtcNow.ToString("O"), null, "flow"));
        using (var c = store.OpenProbe()) using (var q = c.CreateCommand())
        {
            q.CommandText = "CREATE TRIGGER reject_cycle_recovery BEFORE INSERT ON recovery_state BEGIN SELECT RAISE(ABORT,'recovery gate refusal'); END";
            q.ExecuteNonQuery();
        }
        var refused = false;
        try
        {
            journal.Append(new CycleRecord("failed-cycle", DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"), "Failed", 10, Array.Empty<StepRecord>(), "injected runtime failure"));
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { refused = true; }
        var stillInflight = false;
        using (var c = store.OpenProbe()) using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT EXISTS(SELECT 1 FROM run_inflight WHERE run_id='failed-cycle')";
            stillInflight = Convert.ToInt32(q.ExecuteScalar()) != 0;
        }
        Check(refused && stillInflight && store.Cycle("failed-cycle") is null && store.LoadRecoveryState() is null,
            "N03 failed cycle cannot commit or clear inflight when recovery gate write is refused", errors);
        using (var c = store.OpenProbe()) using (var q = c.CreateCommand())
        { q.CommandText = "DROP TRIGGER reject_cycle_recovery"; q.ExecuteNonQuery(); }
        journal.Append(new CycleRecord("failed-cycle", DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"), "Failed", 10, Array.Empty<StepRecord>(), "injected runtime failure"));
        Check(store.Cycle("failed-cycle")?.Outcome == "Failed" && store.LoadRecoveryState() is { Required: true } &&
            store.Cycle("failed-cycle")?.Id == "failed-cycle",
            "N03 retry atomically persists failed result and recovery requirement", errors);

        journal.BeginCycle(new CycleStartRecord("stopped-cycle", DateTimeOffset.UtcNow.ToString("O"), null, "flow"));
        journal.Append(new CycleRecord("stopped-cycle", DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"), "Stopped", 5, Array.Empty<StepRecord>(), "operator stop"));
        Check(store.LoadRecoveryState() is { Required: true, RunId: "stopped-cycle" },
            "N03 stopped cycle establishes recovery requirement before inflight cleanup", errors);
        journal.FlushEventsAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("OK   N03 failed/stopped cycles atomically commit recovery gate with results");
    }

    private static void TestCorruptHistoryIsolation(TestEnvironment env, MultiStationRegistry fleet,
        StationTemplateCatalog catalog, StationUpgradeManager upgrades, List<string> errors)
    {
        var corruptRoot = Path.Combine(env.ContentRootPath, "corrupt-history"); Directory.CreateDirectory(corruptRoot);
        var templateRoot = Path.Combine(corruptRoot, "data", "templates"); Directory.CreateDirectory(templateRoot);
        var templateFile = Path.Combine(templateRoot, "catalog.json"); File.WriteAllText(templateFile, "{");
        var damagedTemplates = new StationTemplateCatalog(new TestEnvironment(corruptRoot));
        var templateRejected = false;
        try { damagedTemplates.Validate("missing"); } catch (InvalidOperationException) { templateRejected = true; }
        Check(damagedTemplates.StartupIssues.Count == 1 && damagedTemplates.List().Count == 0 && templateRejected && File.ReadAllText(templateFile) == "{",
            "N06 corrupted template catalog is isolated, diagnosed, and preserved", errors);

        var rolloutFolder = Path.Combine(env.ContentRootPath, "data", "rollouts"); Directory.CreateDirectory(rolloutFolder);
        File.WriteAllText(Path.Combine(rolloutFolder, "roll-bad.json"), "{");
        var rollouts = new FleetRolloutManager(env, fleet, catalog, upgrades);
        var mutationRejected = false;
        try { rollouts.Create(new RolloutCreateRequest("missing", "A", ["A", "B"]), "contract"); }
        catch (InvalidOperationException) { mutationRejected = true; }
        Check(rollouts.StartupIssues.Count == 1 && rollouts.List().Length == 0 && mutationRejected,
            "N06 corrupted rollout is diagnosed while unrelated APIs stay available and rollout writes fail closed", errors);
        Console.WriteLine("OK   N06 damaged template and rollout history isolation");
    }

    private static void TestAuditClassificationAndStationFilter(TestEnvironment env, List<string> errors)
    {
        var store = new SqliteStore(env, "audit-contract"); var trail = new AuditTrail(env, store);
        using (CommandCorrelation.Push("cmd-audit-regression"))
            trail.Record("engineer", "Engineer", "POST", "/api/stations/A_B/recipes/publish", 500, "trace-failed",
                new InvalidOperationException("injected persistence refusal"));
        var failed = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(trail.Page(1, 10)))
            .RootElement.GetProperty("items")[0];
        Check(failed.GetProperty("Outcome").GetString() == "Failed" &&
            failed.GetProperty("Status").GetInt32() == 500 && failed.GetProperty("ErrorType").GetString() == typeof(InvalidOperationException).FullName &&
            failed.GetProperty("ErrorDetail").GetString()?.Contains("injected persistence refusal", StringComparison.Ordinal) == true &&
            failed.GetProperty("CommandId").GetString() == "cmd-audit-regression",
            "N07 failed outcome persists exception detail and N09 correlates audit command ID", errors);
        trail.Record("engineer", "Engineer", "POST", "/api/stations/A_B/config", 200, "trace-a_b");
        trail.Record("engineer", "Engineer", "POST", "/api/stations/AXB/config", 200, "trace-axb");
        var page = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(trail.PageForStation("A_B", 1, 20))).RootElement;
        var paths = page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("Path").GetString()).ToArray();
        Check(paths.Length == 2 && paths.All(x => x!.Contains("/A_B/", StringComparison.Ordinal)),
            "N09 station audit filtering treats underscore as a literal", errors);
        Console.WriteLine("OK   N07 failed audit outcome and N09 literal station history filtering");
    }

    private static void TestAtomicEligibilitySnapshot(List<string> errors)
    {
        var registry = new RunEligibilityRegistry();
        Check(registry.BlockingReason("missing") is not null, "N05 missing eligibility registration fails closed", errors);
        registry.Register("B", _ => "unresolved deployment", true);
        Check(registry.BlockingReason("B") == "unresolved deployment", "N05 published eligibility snapshot blocks", errors);
        Console.WriteLine("OK   N05 missing registration fails closed");
    }

    private static void Check(bool condition, string name, List<string> errors)
    {
        if (!condition) errors.Add(name + " failed");
    }

    /// <summary>R06: minimal IServiceProvider for the registry constructor - the test
    /// owns the instances directly, so the container only hands them back.</summary>
    private sealed class FixedServices : IServiceProvider
    {
        private readonly Dictionary<Type, object> _map = new();
        public FixedServices Add<T>(T value) where T : class { _map[typeof(T)] = value; return this; }
        public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var value) ? value : null;
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "DeviceControl.ContractTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
