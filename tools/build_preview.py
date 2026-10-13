"""Synchronize the offline browser demo from the backend simulation config."""
from pathlib import Path
import json

ROOT = Path(__file__).resolve().parents[1]
FRONT = ROOT / 'frontend'
CONFIG = ROOT / 'backend' / 'DeviceControl.Api' / 'config' / 'station.json'
config = json.loads(CONFIG.read_text(encoding='utf-8'))
config_b = json.loads((CONFIG.parent / 'station-B.json').read_text(encoding='utf-8'))
if any(c.get('driver') != 'Simulation' for c in (config, config_b)):
    raise SystemExit('Offline preview permits Simulation config only')
(FRONT / 'station-data.js').write_text(
    '// Generated from backend/DeviceControl.Api/config/station.json; offline simulation only.\n'
     + 'window.STATION_CONFIG=' + json.dumps(config, ensure_ascii=False, separators=(',', ':')) + ';\n'
    + 'window.STATION_CONFIG_B=' + json.dumps(config_b, ensure_ascii=False, separators=(',', ':')) + ';\n',
    encoding='utf-8')
html = (FRONT / 'index.html').read_text(encoding='utf-8')
html = html.replace('<link rel="stylesheet" href="./styles.css">',
                    '<style>\n' + (FRONT / 'styles.css').read_text(encoding='utf-8') + '\n</style>')
for filename in ['station-data.js', 'api-client.js', 'station-workspace.js', 'shared-resource-coordinator.js', 'workflow-engine.js', 'mock-runtime.js', 'workflow-studio.js', 'governance-mock.js', 'workflow-edit-history.js', 'workflow-replay.js', 'workflow-quality.js', 'workflow-quality-ui.js', 'flow-editor.js', 'station-console.js', 'station-template-studio.js', 'station-upgrade-studio.js', 'deployment-recovery.js', 'fleet-rollout-studio.js', 'deployment-sandbox-ui.js', 'app.js']:
    html = html.replace(f'<script src="./{filename}"></script>',
                        '<script>\n' + (FRONT / filename).read_text(encoding='utf-8') + '\n</script>')
out = ROOT / 'tests'/'fixtures'/'DeviceControl_V1.9_Preview.html'
out.write_text(html, encoding='utf-8')
print('Built', out)
