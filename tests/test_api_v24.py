"""R01 real .NET HTTP contract (conservative path): after a single-station rollback
restores CONTENT with NEW version IDs, the old batch can never return to its recorded
baseline. The refusal must carry an actionable close-and-rebuild message, and the
close + rebuild path must actually work.

Must run with .NET 10 SDK; without it prints RESULT: BLOCKED and exits 77 (never PASS).
"""
import json, os, shutil, socket, sys, tempfile, time, urllib.error, urllib.request
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api
if not shutil.which('dotnet'):
    print('RESULT: BLOCKED R01 HTTP: .NET 10 SDK absent'); sys.exit(77)

with tempfile.TemporaryDirectory(prefix='dc-r01-http-') as temp:
    data = Path(temp); (data / 'config').mkdir()
    for cfg in ('station.json', 'station-B.json'):
        shutil.copy(ROOT / 'backend/DeviceControl.Api/config' / cfg, data / 'config' / cfg)
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); port = s.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    env = {**os.environ, 'SimListenUrl': base, 'ASPNETCORE_CONTENTROOT': str(data)}

    def call(path, body=None, token=None):
        payload = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(base + path, data=payload,
            method='POST' if body is not None else 'GET',
            headers={**({'Content-Type': 'application/json'} if body is not None else {}),
                     **({'X-Sim-Token': token} if token else {})})
        try:
            with urllib.request.urlopen(req, timeout=15) as res:
                return res.status, json.load(res)
        except urllib.error.HTTPError as e:
            return e.code, json.load(e)

    def login(role):
        code, row = call('/api/auth/login', {'username': role, 'password': 'demo123'})
        assert code == 200, (code, row)
        return row['token']

    proc = start_api(data, env)
    try:
        for _ in range(180):
            if proc.poll() is not None:
                raise AssertionError('server exited before healthy')
            try:
                if call('/api/health')[0] == 200: break
            except Exception: pass
            time.sleep(0.3)
        else: raise AssertionError('HTTP health timed out')
        admin = login('admin'); engineer = login('engineer')

        # Template published from A; batch created with A as pilot (records baseline IDs).
        code, row = call('/api/templates/capture', {'stationId': 'A', 'name': 'R01 baseline'}, engineer)
        assert code == 200, (code, row); tid = row['data']['id']
        assert call('/api/templates/' + tid + '/validate', {}, engineer)[0] == 200
        assert call('/api/templates/' + tid + '/publish', {}, admin)[0] == 200
        code, batch = call('/api/rollouts', {'templateId': tid, 'pilotStationId': 'A', 'stationIds': ['A', 'B']}, admin)
        assert code == 200, (code, batch); rid = batch['data']['id']
        baseline_a = batch['data']['steps'][0]
        assert baseline_a['stationId'] == 'A', baseline_a

        # Single-station rollback on A restores content but publishes NEW version IDs.
        code, plan = call('/api/upgrades/preflight', {'templateId': tid, 'stationId': 'A'}, engineer)
        assert code == 200 and plan['data']['ready'], (code, plan)
        code, dep = call('/api/upgrades/apply', {'planId': plan['data']['id']}, admin)
        assert code == 200, (code, dep); dep_id = dep['data']['id']
        code, rolled = call('/api/upgrades/rollback', {'deploymentId': dep_id}, admin)
        assert code == 200, (code, rolled)

        # Advancing hits the changed baseline: step fails, batch pauses with guidance.
        code, advanced = call('/api/rollouts/' + rid + '/advance', {}, admin)
        assert code == 200, (code, advanced)
        step_a = [s for s in advanced['data']['steps'] if s['stationId'] == 'A'][0]
        assert advanced['data']['status'] == 'Paused' and step_a['status'] == 'Failed', advanced['data']
        assert '重建' in (step_a['error'] or ''), step_a['error']
        assert '重建' in (advanced['data']['reason'] or ''), advanced['data']['reason']

        # Reconcile must refuse with the actionable close-and-rebuild message.
        code, refused = call('/api/rollouts/' + rid + '/reconcile',
            {'stationId': 'A', 'note': '单站回滚后内容已恢复，核对新版本 ID'}, admin)
        assert code == 409, (code, refused)
        message = refused.get('error') or ''
        assert '不在批次原始版本基线上' in message, message
        assert '内容恢复不等于 ID 恢复' in message, message
        assert '关闭本批次' in message and '重建' in message, message
        assert '禁止盲目重试' in message or '放宽基线比较' in message, message

        # The batch is unchanged by the refusal; close + rebuild is the documented path.
        code, current = call('/api/rollouts/' + rid, token=admin)
        assert code == 200 and current['data']['status'] == 'Paused', current['data']
        assert [s for s in current['data']['steps'] if s['stationId'] == 'A'][0]['status'] == 'Failed'
        code, aborted = call('/api/rollouts/' + rid + '/abort', {'reason': '基线已变化；按指引关闭重建'}, admin)
        assert code == 200 and aborted['data']['status'] == 'Aborted', (code, aborted)
        code, rebuilt = call('/api/rollouts', {'templateId': tid, 'pilotStationId': 'A', 'stationIds': ['A', 'B']}, admin)
        assert code == 200, (code, rebuilt)
        new_step_a = rebuilt['data']['steps'][0]
        assert new_step_a['expectedFlowId'] != baseline_a['expectedFlowId'], (new_step_a, baseline_a)
        assert new_step_a['expectedFlowId'] == current['data']['steps'][0]['afterFlowId'] or new_step_a['expectedFlowId'], new_step_a
        print('PASS R01 .NET HTTP: baseline refusal carries close-and-rebuild guidance; close + rebuild works')
    finally:
        stop_api(proc)
