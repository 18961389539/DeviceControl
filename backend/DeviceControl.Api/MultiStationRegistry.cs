using System.Text.Json;

namespace DeviceControl.Api;

public sealed record StationOverview(string Id, string ProfileId, string State, string Mode,
    int Cycles, bool Busy, bool RecoveryRequired, string? Alarm, string FlowVersion, string RecipeId);

public sealed class UnknownStationException : Exception
{
    public UnknownStationException(string stationId) : base("未知工站：" + stationId) { }
}

public sealed class StationContext
{
    public string Id { get; }
    public StationConfig Config { get; }
    public DeviceRuntime Machine { get; }
    public SqliteStore Store { get; }
    public RunJournal Journal { get; }
    public FlowRevisions Flows { get; }
    public RecipeStore Recipes { get; }
    public StationContext(string id, StationConfig cfg, DeviceRuntime machine, SqliteStore store,
        RunJournal journal, FlowRevisions flows, RecipeStore recipes)
    {
        Id = id; Config = cfg; Machine = machine; Store = store;
        Journal = journal; Flows = flows; Recipes = recipes;
    }
    public StationOverview Overview()
    {
        var s = Machine.Snapshot();
        return new StationOverview(Id, Config.ProfileId, s.State, s.Mode, s.Cycles,
            s.Busy, s.RecoveryRequired, s.Alarm, Flows.Active.Id, Recipes.Active.Id);
    }
}

/// <summary>
/// V1.2: isolated A/B simulation runtimes with one process-local node-level
/// shared resource coordinator; old A routes are retained. The coordinator
/// never allows partial multi-key acquisition. Not a real safety interlock.
/// </summary>
public sealed class MultiStationRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StationContext> _stations;
    private readonly Dictionary<string, string> _failedStations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SharedResourceCoordinator _coordinator;
    private readonly IHostEnvironment _environment;
    private readonly string _managedFolder;
    private readonly RunEligibilityRegistry _eligibility;
    private const int MaxStations = 12;

    public MultiStationRegistry(IHostEnvironment environment, StationConfig cfg,
        DeviceRuntime runtime, SqliteStore store, RunJournal journal,
        FlowRevisions flows, RecipeStore recipes, SharedResourceCoordinator coordinator,
        RunEligibilityRegistry eligibility)
    {
        _environment = environment;
        _coordinator = coordinator;
        _eligibility = eligibility;
        runtime.AttachSharedResources(coordinator, "A");
        _stations = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new StationContext("A", cfg, runtime, store, journal, flows, recipes)
        };
        var bConfigPath = Path.Combine(environment.ContentRootPath, "config", "station-B.json");
        var bCfg = File.Exists(bConfigPath) ? StationConfig.Load(bConfigPath) :
            JsonSerializer.Deserialize<StationConfig>(JsonSerializer.Serialize(cfg))!;
        if (!File.Exists(bConfigPath)) { bCfg.ProfileId = "DEMO-STATION-B"; bCfg.Validate(); }
        _stations.Add("B", BuildStation("B", bCfg));
        _managedFolder = Path.Combine(environment.ContentRootPath, "config", "managed-stations");
        Directory.CreateDirectory(_managedFolder);
        foreach (var path in Directory.GetFiles(_managedFolder, "*.json").OrderBy(x => x))
        {
            var id = CanonicalStationId(Path.GetFileNameWithoutExtension(path));
            if (_stations.ContainsKey(id) || _stations.Count >= MaxStations)
                throw new InvalidOperationException("持久化工站目录包含重复 ID 或超过容量");
            // F13: one corrupt managed-station file fails that station closed and is
            // recorded for diagnosis; it must not take the whole service down.
            try { _stations.Add(id, BuildStation(id, StationConfig.Load(path))); }
            catch (Exception ex) { _failedStations[id] = ex.Message; }
        }
    }
    /// <summary>F13: stations that failed to load, with the reason. They stay out of
    /// the fleet (fail closed) and are visible through the diagnostics endpoint.</summary>
    public IReadOnlyDictionary<string, string> FailedStations
    { get { lock (_gate) return new Dictionary<string, string>(_failedStations); } }
    public IReadOnlyList<string> StationIds
    { get { lock (_gate) return _stations.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(); } }
    /// <summary>F04: invoked for every newly created station so the caller can register
    /// its run-eligibility check (the upgrade manager does not exist yet at construction).</summary>
    public Action<string>? OnStationAdded { get; set; }

    public static string CanonicalStationId(string id)
    {
        if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[A-Za-z][A-Za-z0-9_-]{0,15}\z"))
            throw new ArgumentException("工站 ID 应以字母开头，仅限字母、数字、连字符及下划线，长度 1~16");
        return id.ToUpperInvariant();
    }
    private StationContext BuildStation(string id, StationConfig cfg)
    {
        cfg.Validate();
        var io = new SimIoController(cfg);
        var interlocks = new OrdinaryInterlockMonitor(cfg, io);
        var motion = new SimMotionController(cfg, io, interlocks);
        var cylinders = new SimCylinderRegistry(cfg, io, interlocks);
        var camera = new SimCamera(io, interlocks);
        var db = new SqliteStore(_environment, id);
        var recipes = new RecipeStore(_environment, db, id);
        var flows = new FlowRevisions(db, cfg);
        var journal = new RunJournal(_environment, db, id);
        var machine = new DeviceRuntime(cfg, io, motion, cylinders,
            camera, interlocks, journal, recipes, flows, _eligibility);
        machine.AttachSharedResources(_coordinator, id);
        return new StationContext(id, cfg, machine, db, journal, flows, recipes);
    }
    public void ValidateNewIds(IReadOnlyCollection<string> ids)
    {
        lock (_gate)
        {
            if (ids.Count != ids.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                throw new ArgumentException("创建列表中包含重复工站 ID");
            if (_stations.Count + ids.Count > MaxStations)
                throw new InvalidOperationException("最多允许 12 个仿真工站");
            if (ids.Any(id => _stations.ContainsKey(id)))
                throw new InvalidOperationException("工站 ID 已存在，禁止覆盖现有运行配置");
        }
    }
    public StationContext Create(string id, StationConfig template, RecipeValues recipe)
    {
        id = CanonicalStationId(id);
        lock (_gate)
        {
            ValidateNewIds(new[] { id });
            var cfg = JsonSerializer.Deserialize<StationConfig>(JsonSerializer.Serialize(template))!;
            cfg.ProfileId = "DEMO-STATION-" + id;
            cfg.FlowVersion = "template-" + id;
            cfg.Validate();
            var file = Path.Combine(_managedFolder, id + ".json");
            var dataDir = Path.Combine(_environment.ContentRootPath, "data", "stations", id);
            if (Directory.Exists(dataDir))
                throw new InvalidOperationException("工站数据目录已经存在，拒绝重用历史数据");
            // Persist the immutable software configuration with CreateNew; never overwrite.
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, cfg);
            try
            {
                var station = BuildStation(id, cfg);
                var draft = station.Recipes.Draft(recipe, "template-deploy");
                station.Recipes.Validate(draft.Id);
                station.Recipes.Publish(draft.Id, station.Machine);
                _stations.Add(id, station);
                OnStationAdded?.Invoke(id);
                return station;
            }
            catch
            {
                File.Delete(file);
                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
                throw;
            }
        }
    }

    public StationContext Get(string id)
    {
        lock (_gate) return _stations.TryGetValue(id, out var station) ? station :
            throw new UnknownStationException(id);
    }

    public object Summary()
    {
        lock (_gate)
            return new { mode = "Simulation", coordination = "NodeScopedFIFO",
                sharedResource = "CELL-SHARED", owner = _coordinator.OwnerOf("CELL-SHARED"),
                stations = _stations.Values.OrderBy(s => s.Id).Select(s => s.Overview()).ToArray() };
    }
    public object Resources() => _coordinator.Snapshot();
    public void Start(string stationId)
    {
        lock (_gate)
        {
            // No cycle-wide ownership. Shared steps arbitrate atomically in the flow engine.
            // The pre-start lock only serializes command admission and does not span execution.
            Get(stationId).Machine.StartSingleCycle();
        }
    }
    public void Stop(string stationId)
    {
        // Stop only the addressed runtime. Never release the shared lease before
        // the stopped runtime has finished and marked itself not busy.
        Get(stationId).Machine.RequestStop();
    }
}
