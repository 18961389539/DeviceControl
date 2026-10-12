"""V1.6 static integration consistency; NOT evidence of C# compilation."""
from pathlib import Path
root=Path(__file__).resolve().parents[1]
manager=(root/'backend/DeviceControl.Api/FleetRolloutManager.cs').read_text()
app=(root/'backend/DeviceControl.Api/Program.cs').read_text()
perms=(root/'backend/DeviceControl.Api/Governance.cs').read_text()
html=(root/'tests'/'fixtures'/'DeviceControl_V1.6_Preview.html').read_text()
for term in ('"PilotPending"','"AwaitingEvidence"','"WaveReady"','"Paused"','"Completed"','"Interrupted"',
             'StationUpgradeManager','WorkflowQualityGate.Evaluate','station.Store.Cycles()',
             'AfterFlowId','AfterRecipeId','DateTimeOffset.TryParse','ExpectedFlowId','ExpectedRecipeId','File.Move(tmp, path, true)',
             '"Applying"','"Failed"','"Aborted"','Assert' if False else 'RolloutEvidence'):
 assert term in manager,term
for term in ('/api/rollouts','/api/rollouts/{id}/advance','/api/rollouts/{id}/verify',
             '/api/rollouts/{id}/pause','/api/rollouts/{id}/resume','/api/rollouts/{id}/abort'):
 assert term in app,term
assert 'version = "2.0-rc"' in app
assert 'path.StartsWith("/api/rollouts"' in perms
for term in ('rolloutDashboard','rolloutIds','rolloutPilot','rolloutSteps','rolloutProgress','OfflineRollouts','FleetRolloutStudio'):
 assert term in html,term
assert 'fleet-rollout-studio.js' in (root/'tools/build_preview.py').read_text()
print('PASS V1.6 static routing/RBAC/state/evidence/journal seams (NOT C# compilation)')
