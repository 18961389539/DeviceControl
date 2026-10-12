"""Static structural checks only. Not a replacement for dotnet build/HTTP tests."""
from pathlib import Path
r=Path(__file__).resolve().parents[1]
api=(r/'backend/DeviceControl.Api/Program.cs').read_text(encoding='utf-8')
reg=(r/'backend/DeviceControl.Api/MultiStationRegistry.cs').read_text(encoding='utf-8')
cat=(r/'backend/DeviceControl.Api/StationTemplateCatalog.cs').read_text(encoding='utf-8')
perm=(r/'backend/DeviceControl.Api/Governance.cs').read_text(encoding='utf-8')
assert all(f'"/api/templates{path}"' in api for path in ('','/capture','/{id}/validate','/{id}/publish','/{id}/diff/{stationId}','/{id}/deploy'))
assert 'AssertFlowQuality(r)' in cat and 'WorkflowQualityGate.Evaluate' in cat
assert 'Status != "Published"' in cat
assert 'AssertFlowQuality(revision)' in cat
assert 'FileMode.CreateNew' in reg
assert 'config", "managed-stations"' in reg
assert 'ValidateNewIds' in reg and 'CanonicalStationId' in reg
assert 'if (path.StartsWith("/api/templates/")) return 3;' in perm
assert 'if (verb == "GET")' in perm
assert 'new StationTemplateStudio' not in api
print('PASS V1.4 static endpoint wiring, role guards, immutable provision and quality gate (no C# compilation claimed)')
