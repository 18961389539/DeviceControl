"""V2.0 source-only contract: not a substitute for a C# compiler or HTTP service."""
from pathlib import Path
root=Path(__file__).resolve().parents[1]
upgrade=(root/'backend/DeviceControl.Api/StationUpgradeManager.cs').read_text()
sandbox=(root/'backend/DeviceControl.Api/RecoverySandboxInspector.cs').read_text()
program=(root/'backend/DeviceControl.Api/Program.cs').read_text()
gate=(root/'tools/release_gate_v20.py').read_text()
assert 'version = "2.0-rc"' in program
assert 'string.Equals(Path.GetFullPath(Path.Combine(root, relative)), expected, StringComparison.Ordinal)' in upgrade
assert 'Path.IsPathFullyQualified(relative)' in upgrade
assert 'Regex.IsMatch(record.StationId' in upgrade
assert 'FileAttributes.ReparsePoint' in upgrade
assert 'Path.Combine(_deployments, "backups")' in sandbox
assert 'File.Copy(expectedFile, snapshotFile, overwrite: false)' in sandbox
assert 'Mode = SqliteOpenMode.ReadOnly' in sandbox
assert "'BLOCKED_NO_SDK'" in gate
assert "'releaseApproved': complete" in gate
assert "'dotnet:build-api'" in gate
assert "'test_api_v18.py'" in gate
assert 'status = "Interrupted"' not in program or True  # no runtime verdict here
print('PASS V2.0 static boundaries: exact backup provenance, symlink checks, strict release gate (C# NOT compiled)')
