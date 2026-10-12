from pathlib import Path
root=Path(__file__).resolve().parents[1]
cs=(root/'backend/DeviceControl.Api/StationUpgradeManager.cs').read_text(encoding='utf-8')
app=(root/'backend/DeviceControl.Api/Program.cs').read_text(encoding='utf-8')
perms=(root/'backend/DeviceControl.Api/Governance.cs').read_text(encoding='utf-8')
html=(root/'tests'/'fixtures'/'DeviceControl_V1.5_Preview.html').read_text(encoding='utf-8')
for s in ('WithConfigurationChange','FailedCompensated','ManualRecoveryRequired','BeforeFlow','BeforeRecipe','AssertNoUnresolved','DateTimeOffset.Parse(plan.ExpiresAt)','TemplateHash','flowDraft.Id','CreateVerifiedBackup','BackupSha256'):
 assert s in cs,s
for route in ['/api/upgrades/preflight','/api/upgrades/apply','/api/upgrades/rollback','/api/upgrades/history']:
 assert route in app,route
assert 'version = "2.0-rc"' in app
assert 'path == "/api/upgrades/preflight"' in perms
assert 'path.StartsWith("/api/upgrades/")' in perms
for name in ('upgradePreflight','upgradeHistory','upgradeStation','data-upgrade="apply"','StationUpgradeStudio'):
 assert name in html,name
print('PASS V1.5 static integration seams (NOT a C# compilation test)')
