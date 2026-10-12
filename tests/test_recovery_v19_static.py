from pathlib import Path
r=Path(__file__).resolve().parents[1]
api=(r/'backend/DeviceControl.Api/Program.cs').read_text(encoding='utf-8')
source=(r/'backend/DeviceControl.Api/RecoverySandboxInspector.cs').read_text(encoding='utf-8')
roles=(r/'backend/DeviceControl.Api/Governance.cs').read_text(encoding='utf-8')
assert '/api/upgrades/{id}/sandbox' in api
assert 'builder.Services.AddSingleton<RecoverySandboxInspector>();' in api
assert 'path.StartsWith("/api/upgrades/", StringComparison.OrdinalIgnoreCase)' in roles
assert ' ? 2 : 1;' in roles
for guard in ('Mode = SqliteOpenMode.ReadOnly','File.Copy(expectedFile, snapshotFile, overwrite: false)',
              'SHA256.HashData(stream)', 'PRAGMA integrity_check', 'Directory.Delete(scratch, recursive: true)'):
 assert guard in source,guard
assert 'WritesProduction: false' in source and 'RequiresManualReview: true' in source
print('PASS V1.9 C# source contract (not a replacement for compiling with dotnet)')
