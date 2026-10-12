"""Static seam checks only: no .NET SDK present locally."""
from pathlib import Path
p=Path(__file__).resolve().parents[1]/'backend'/'DeviceControl.Api'
reg=(p/'MultiStationRegistry.cs').read_text()
api=(p/'Program.cs').read_text()
paths=(p/'StationStoragePaths.cs').read_text()
perm=(p/'Governance.cs').read_text()
assert 'new SimIoController(bCfg)' in reg
assert 'new SqliteStore(environment, "B")' in reg
assert 'new RunJournal(environment, bStore, "B")' in reg
assert 'new RecipeStore(environment, bStore, "B")' in reg
assert 'ReconcileOwner();' in reg
assert 'station.Machine.StartSingleCycle();' in reg
assert 'fleet.Start("A")' in api  # legacy start must share the same gate
assert 'fleet.Stop("A")' in api
assert 'parts.Skip(3)' in perm
assert 'Path.Combine(baseFolder, "stations", scope)' in paths
assert 'Regex.IsMatch' in paths
assert (p/'config/station-B.json').exists()
print('PASS V1.1 static seams (C# compilation is NOT claimed)')
