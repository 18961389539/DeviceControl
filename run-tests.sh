#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
if [[ "${1:-}" == "--require-dotnet" ]] && ! command -v dotnet >/dev/null 2>&1; then
  echo "FAILED: .NET SDK is required for release verification" >&2
  exit 2
fi
python tools/build_preview.py
for js in frontend/*.js; do node --check "$js"; done
for test in tests/test_mock.mjs tests/test_governance.mjs tests/test_workflow_v05.mjs tests/test_workflow_v06.mjs tests/test_workflow_v07.mjs tests/test_workflow_v08.mjs; do
  node "$test"
done
node tests/generate_quality_contract.mjs
node tests/test_replay_v10.mjs
node tests/test_station_v11.mjs
node tests/test_resources_v12.mjs
node tests/test_workspace_v13.mjs
node tests/test_templates_v14.mjs
node tests/test_upgrade_rules_v15.mjs
node tests/test_recovery_v18.mjs
python tests/test_recovery_sandbox_v19.py
python tests/test_recovery_v19_static.py
python tests/test_recovery_v18_static.py
python tests/test_rollout_files_v16.py
python tests/test_rollout_v17_static.py
python tests/test_station_files_v12.py
python tests/test_template_files_v14.py
python tests/test_upgrade_files_v15.py
for test in tests/test_sqlite_schema.py tests/test_sqlite_migration_v08.py tests/test_sqlite_reliability.py tests/test_sqlite_migration_v09.py tests/test_sqlite_v10.py; do
  python "$test"
done
# Chromium smoke runs as a separate CI step so a browser process cannot stall
# Node/SQLite evidence collection or mask the result of the C# release gate.
if command -v dotnet >/dev/null 2>&1; then
  dotnet build backend/DeviceControl.Api/DeviceControl.Api.csproj
  dotnet run --project backend/DeviceControl.ContractTests/DeviceControl.ContractTests.csproj
  python tests/test_api_v09.py
  python tests/test_api_v10.py
  
  python tests/test_api_v12.py
  python tests/test_api_v13.py
  python tests/test_api_v14.py
  python tests/test_api_v15.py
  python tests/test_api_v16.py
  python tests/test_api_v17.py
  python tests/test_api_v18.py
  python tests/test_api_v19.py
else
  printf '\nUNVERIFIED: .NET SDK absent; C# compilation, contract runner and HTTP API integration have NOT been executed.\n'
fi
printf '\nV1.9 available runtime tests passed; consult README for test scope.\n'
