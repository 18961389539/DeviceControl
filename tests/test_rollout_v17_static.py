"""V1.7 contract seams; never substitute for C# compile or live HTTP."""
from pathlib import Path
r=Path(__file__).resolve().parents[1]
m=(r/'backend/DeviceControl.Api/FleetRolloutManager.cs').read_text(encoding='utf-8')
a=(r/'backend/DeviceControl.Api/Program.cs').read_text(encoding='utf-8')
g=(r/'backend/DeviceControl.Api/Governance.cs').read_text(encoding='utf-8')
f=(r/'frontend/fleet-rollout-studio.js').read_text(encoding='utf-8')
h=(r/'tests'/'fixtures'/'DeviceControl_V1.7_Preview.html').read_text(encoding='utf-8')
for k in ('NormalizeWindow','WindowOpen(value, DateTimeOffset.UtcNow)', 'DateTimeOffset.TryParse',
          'public RolloutDiagnostics Diagnose','public FleetRollout Reconcile',
          '_upgrades.History(stationId)', 'step.ExpectedFlowId','step.ExpectedRecipeId',
          'RecoveryActor = actor','ReconciledAt = Stamp()', 'File.Move(tmp, path, true)'):
 assert k in m,k
for k in ('/api/rollouts/{id}/diagnostics','/api/rollouts/{id}/reconcile','version = "2.0-rc"'):
 assert k in a,k
assert 'path.StartsWith("/api/rollouts"' in g
for k in ('rolloutWindowStart','rolloutWindowEnd','rolloutReconcileStation', 'rolloutDiagnostics',
          'windowOpen(row)','diagnose(id)','reconcile(id,',"data-rollout=\"reconcile\""):
 assert k in (h+f),k
print('PASS V1.7 static UTC window, diagnostics, baseline-safe reconciliation, RBAC routes (not C# compilation)')
