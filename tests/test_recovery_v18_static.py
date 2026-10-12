from pathlib import Path
p=Path(__file__).resolve().parents[1]
upgrade=(p/'backend/DeviceControl.Api/StationUpgradeManager.cs').read_text(encoding='utf-8')
roll=(p/'backend/DeviceControl.Api/FleetRolloutManager.cs').read_text(encoding='utf-8')
api=(p/'backend/DeviceControl.Api/Program.cs').read_text(encoding='utf-8')
assert 'MarkInterruptedAtStartup();' in upgrade and 'MarkInterruptedAtStartup();' in roll
assert 'row.Status == "InProgress"' in upgrade and 'Status = "Interrupted"' in upgrade
assert 'record.Status == "Deploying"' in roll and 'Save(record with { Status = "Interrupted"' in roll
assert 'SHA256.HashData(stream)' in upgrade and 'InvalidPath' in upgrade and 'Mismatch' in upgrade
assert 'ReadOnly' in roll or 'bool ReadOnly' in roll
assert '/api/rollouts/{id}/recovery-report' in api and '/api/upgrades/{id}/integrity' in api
assert 'GetRequiredService<StationUpgradeManager>()' in api and 'GetRequiredService<FleetRolloutManager>()' in api
assert 'version = "2.0-rc"' in api
policy=(p/'backend/DeviceControl.Api/Governance.cs').read_text(encoding='utf-8')
assert 'path.StartsWith("/api/upgrades/", StringComparison.OrdinalIgnoreCase)' in policy
assert not (p/'backend/DeviceControl.Api/.netcompile-proved').exists()
print('PASS V1.8 static C# integration seams (NOT dotnet compilation)')
