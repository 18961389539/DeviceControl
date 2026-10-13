"""V1.2 C# source seam checks; NOT a substitute for .NET compiler/runtime tests."""
from pathlib import Path
p=Path(__file__).resolve().parents[1]/'backend'/'DeviceControl.Api'
reg=(p/'MultiStationRegistry.cs').read_text(encoding='utf-8')
api=(p/'Program.cs').read_text(encoding='utf-8')
paths=(p/'StationStoragePaths.cs').read_text(encoding='utf-8')
perm=(p/'Governance.cs').read_text(encoding='utf-8')
engine=(p/'FlowEngine.cs').read_text(encoding='utf-8')
coordinator=(p/'SharedResourceCoordinator.cs').read_text(encoding='utf-8')
config=(p/'StationConfig.cs').read_text(encoding='utf-8')
assert 'BuildStation("B", bCfg)' in reg
assert 'new SqliteStore(_environment, id)' in reg
assert 'new RunJournal(_environment, db, id)' in reg
assert 'new RecipeStore(_environment, db, id)' in reg
assert 'runtime.AttachSharedResources(coordinator, "A")' in reg
assert 'machine.AttachSharedResources(_coordinator, id)' in reg
assert 'Get(stationId).Machine.StartSingleCycle()' in reg
assert 'fleet.Start("A")' in api and 'fleet.Stop("A")' in api
assert 'app.MapGet("/api/stations/resources"' in api
assert 'parts.Skip(3)' in perm
assert 'Path.Combine(baseFolder, "stations", scope)' in paths
assert 'Regex.IsMatch' in paths
assert 'SharedResourceCoordinator.KeysFor(node)' in engine
# Q05/Q06: the acquisition is awaited through the monitored wait, and release is
# idempotent so the operator gate can release before an unbounded human wait.
assert '_shared.AcquireAsync(' in engine and 'WaitMonitoredAsync(' in engine
assert 'void ReleaseAcquired()' in engine and 'ReleaseAcquired();' in engine
assert 'lease?.Dispose()' in engine
assert 'request.Keys.Any(_held.ContainsKey)' in coordinator
assert 'await request.Signal.Task.WaitAsync(' in coordinator
assert 'SharedResources' in config and 'ResourceWaitTimeoutMs' in config
assert (p/'config/station-B.json').exists()
print('PASS V1.4 existing station/static scheduler C# seams (compile not claimed)')
