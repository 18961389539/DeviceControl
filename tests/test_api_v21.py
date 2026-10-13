"""R02 real .NET HTTP contract: ordinary process conditions lost during long steps.

A guard/air-pressure loss during a tail Delay or a branch Delay must cancel the
cycle and record Failed with recovery required - never Completed. A software stop
during a long step records Stopped. The snapshot, run journal, step records and
event log must stay consistent with each other.

Must run with .NET 10 SDK; without it prints RESULT: BLOCKED and exits 77 (never PASS).
"""
import json, os, shutil, socket, sys, tempfile, time, urllib.error, urllib.request
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api
if not shutil.which('dotnet'):
    print('RESULT: BLOCKED R02 HTTP: .NET 10 SDK absent'); sys.exit(77)

TAIL = [{"id": "tail-dwell", "type": "Delay", "label": "末尾工艺保持", "durationMs": 600}]
BRANCH = [{
    "id": "branch", "type": "Condition", "label": "分支保持", "device": "PartPresent",
    "children": [
        {"id": "dwell-long", "type": "Delay", "label": "真分支保持", "durationMs": 600},
        {"id": "dwell-short", "type": "Delay", "label": "假分支保持", "durationMs": 50}
    ]
}]
STOPFLOW = [{"id": "stop-dwell", "type": "Delay", "label": "可停止保持", "durationMs": 1500}]


def latest_runs(call, token):
    code, runs = call('/api/runs', token=token)
    assert code == 200, (code, runs)
    assert isinstance(runs, list) and runs, runs
    return runs[0]


def wait_idle(call, token, timeout=15):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        code, snap = call('/api/snapshot', token=token)
        assert code == 200, (code, snap)
        last = snap
        if not snap['busy'] and snap['state'] in ('Idle', 'Fault'):
            return snap
        time.sleep(0.05)
    raise AssertionError('cycle did not settle: ' + json.dumps(last, ensure_ascii=False))


with tempfile.TemporaryDirectory(prefix='dc-r02-http-') as temp:
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
        admin = login('admin')

        def publish_flow(definition):
            code, row = call('/api/flows/draft', {'definition': definition}, admin)
            assert code == 200, (code, row)
            fid = row['data']['id']
            assert call('/api/flows/' + fid + '/validate', {}, admin)[0] == 200
            code, row = call('/api/flows/' + fid + '/publish', {}, admin)
            assert code == 200, (code, row)
            return row['data']['id']

        def prime_auto():
            for axis in ('X', 'Y'):
                assert call(f'/api/manual/axes/{axis}/enable', {'enabled': True}, admin)[0] == 200
                assert call(f'/api/manual/axes/{axis}/home', {}, admin)[0] == 200
            assert call('/api/mode', {'mode': 'Auto'}, admin)[0] == 200

        def clear_station():
            # Restore ordinary conditions, reset any fault, return to Manual, re-home,
            # and confirm recovery so the next scenario starts from a clean Idle.
            assert call('/api/sim/input', {'name': 'GuardClosed', 'value': True}, admin)[0] == 200
            assert call('/api/sim/input', {'name': 'AirPressureOk', 'value': True}, admin)[0] == 200
            code, snap = call('/api/snapshot', token=admin)
            assert code == 200
            if snap['state'] == 'Fault':
                assert call('/api/fault/reset', {}, admin)[0] == 200
            code, snap = call('/api/snapshot', token=admin)
            if snap['mode'] == 'Auto':
                assert call('/api/mode', {'mode': 'Manual'}, admin)[0] == 200
            for axis in ('X', 'Y'):
                assert call(f'/api/manual/axes/{axis}/home', {}, admin)[0] == 200
            assert call('/api/recovery/confirm', {}, admin)[0] == 200
            code, snap = call('/api/snapshot', token=admin)
            assert snap['state'] == 'Idle' and not snap['recoveryRequired'], snap

        # Scenario 1: guard lost during the TAIL Delay of a single-node flow.
        publish_flow(TAIL)
        prime_auto()
        assert call('/api/cycle/start', {}, admin)[0] == 200
        time.sleep(0.08)
        assert call('/api/sim/input', {'name': 'GuardClosed', 'value': False}, admin)[0] == 200
        snap = wait_idle(call, admin)
        assert snap['state'] == 'Fault', snap['state']
        assert snap['recoveryRequired'] is True, snap
        assert snap['cycles'] == 0, snap['cycles']
        assert '联锁' in (snap['alarm'] or '') or '互锁' in (snap['alarm'] or ''), snap['alarm']
        assert any('联锁' in e['message'] or '互锁' in e['message'] for e in snap['events']), snap['events']
        run = latest_runs(call, admin)
        assert run['outcome'] == 'Failed', run
        assert run['steps'] and run['steps'][0]['outcome'] == 'Failed', run['steps']

        # Scenario 2: guard lost during a Delay inside a Condition branch.
        clear_station()
        publish_flow(BRANCH)
        prime_auto()
        assert call('/api/cycle/start', {}, admin)[0] == 200
        time.sleep(0.15)
        assert call('/api/sim/input', {'name': 'GuardClosed', 'value': False}, admin)[0] == 200
        snap = wait_idle(call, admin)
        assert snap['state'] == 'Fault', snap['state']
        assert snap['recoveryRequired'] is True and snap['cycles'] == 0, snap
        run = latest_runs(call, admin)
        assert run['outcome'] == 'Failed', run
        assert any('联锁' in e['message'] or '互锁' in e['message'] for e in snap['events']), snap['events']

        # Scenario 3: software stop during a long Delay records Stopped, not Completed.
        clear_station()
        publish_flow(STOPFLOW)
        prime_auto()
        assert call('/api/cycle/start', {}, admin)[0] == 200
        time.sleep(0.2)
        assert call('/api/cycle/stop', {}, admin)[0] == 200
        snap = wait_idle(call, admin)
        assert snap['state'] == 'Idle', snap['state']
        assert snap['recoveryRequired'] is True and snap['cycles'] == 0, snap
        run = latest_runs(call, admin)
        assert run['outcome'] == 'Stopped', run

        # A clean cycle on the same station still completes and is counted.
        clear_station()
        publish_flow(TAIL)
        prime_auto()
        assert call('/api/cycle/start', {}, admin)[0] == 200
        snap = wait_idle(call, admin)
        assert snap['state'] == 'Idle' and snap['cycles'] == 1, snap
        run = latest_runs(call, admin)
        assert run['outcome'] == 'Completed', run
        print('PASS R02 .NET HTTP: tail Delay, branch Delay, stop request, clean cycle')
    finally:
        stop_api(proc)
