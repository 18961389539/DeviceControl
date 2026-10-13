"""Q05 real .NET HTTP contract: a pause is part of the cycle lifecycle.

While the flow is paused at a breakpoint (or at an operator gate), the same
ordinary-condition monitor that covers long steps keeps running. A guard loss
during the pause is LATCHED: the cycle fails with recovery required, the pause
cannot simply be resumed, and the run never records Completed - even when the
signal is restored immediately after the loss.

Must run with .NET 10 SDK; without it prints RESULT: BLOCKED and exits 77.
"""
import json, os, shutil, socket, sys, tempfile, time, urllib.error, urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))
from dotnet_harness import start_api, stop_api

if not shutil.which('dotnet'):
    print('RESULT: BLOCKED Q05 HTTP: .NET 10 SDK absent')
    sys.exit(77)


def make_base(temp, prefix):
    data = Path(temp)
    (data / 'config').mkdir(parents=True, exist_ok=True)
    for name in ('station.json', 'station-B.json'):
        shutil.copy(ROOT / 'backend/DeviceControl.Api/config' / name, data / 'config' / name)
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        base = f'http://127.0.0.1:{sock.getsockname()[1]}'
    env = {**os.environ, 'SimListenUrl': base, 'ASPNETCORE_CONTENTROOT': str(data)}

    def call(path, body=None, token=None):
        req = urllib.request.Request(base + path,
            data=json.dumps(body).encode() if body is not None else None,
            headers={**({'Content-Type': 'application/json'} if body is not None else {}),
                     **({'X-Sim-Token': token} if token else {})})
        try:
            with urllib.request.urlopen(req, timeout=20) as res:
                return res.status, json.load(res)
        except urllib.error.HTTPError as res:
            return res.code, json.load(res)

    return data, env, call


def wait_healthy(call, proc):
    for _ in range(160):
        if proc.poll() is not None:
            raise AssertionError(f'server exited before healthy (code {proc.returncode})')
        try:
            if call('/api/health')[0] == 200:
                return
        except Exception:
            pass
        time.sleep(0.3)
    raise AssertionError('HTTP health timed out')


def login(call):
    code, row = call('/api/auth/login', {'username': 'admin', 'password': 'demo123'})
    assert code == 200, (code, row)
    return row['token']


def poll(fn, deadline=8.0, interval=0.05):
    end = time.monotonic() + deadline
    last = None
    while time.monotonic() < end:
        last = fn()
        if last:
            return last
        time.sleep(interval)
    raise AssertionError(f'condition not reached; last={last}')


# --- Case 1: guard loss while paused at a breakpoint is latched, never Completed. ---
with tempfile.TemporaryDirectory(prefix='dc-v27-a-') as temp:
    data, env, call = make_base(temp, 'a')
    proc = start_api(data, env)
    try:
        wait_healthy(call, proc)
        token = login(call)
        definition = [{'id': 'delay', 'type': 'Delay', 'label': '暂停后保持', 'durationMs': 100}]
        code, row = call('/api/flows/draft', {'definition': definition}, token)
        assert code == 200, (code, row)
        fid = row['data']['id']
        assert call(f'/api/flows/{fid}/validate', {}, token)[0] == 200
        assert call(f'/api/flows/{fid}/publish', {}, token)[0] == 200
        for axis in ('X', 'Y'):
            assert call(f'/api/manual/axes/{axis}/enable', {'enabled': True}, token)[0] == 200
            assert call(f'/api/manual/axes/{axis}/home', {}, token)[0] == 200
        assert call('/api/mode', {'mode': 'Auto'}, token)[0] == 200
        assert call('/api/flow/debug', {'command': 'breakpoint', 'index': 0, 'enabled': True}, token)[0] == 200
        assert call('/api/cycle/start', {}, token)[0] == 200
        poll(lambda: call('/api/flow', token=token)[1].get('status') == 'Paused')
        # Lose the guard while paused, then restore it quickly (the old defect: not latched).
        assert call('/api/sim/input', {'name': 'GuardClosed', 'value': False}, token)[0] == 200
        mid = poll(lambda: call('/api/snapshot', token=token)[1]['recoveryRequired'] or None)
        assert call('/api/sim/input', {'name': 'GuardClosed', 'value': True}, token)[0] == 200
        resumed = call('/api/flow/debug', {'command': 'resume'}, token)
        # Resume must be rejected: the pause was already aborted by the latched condition loss.
        assert resumed[0] == 409, resumed
        poll(lambda: (lambda s: s if not s['busy'] else None)(call('/api/snapshot', token=token)[1]))
        code, snap = call('/api/snapshot', token=token)
        assert snap['state'] == 'Fault' and snap['recoveryRequired'] is True, snap
        assert snap['alarm'] and ('联锁' in snap['alarm'] or '互锁' in snap['alarm']), snap['alarm']
        assert snap['cycles'] == 0, snap['cycles']  # no false completion count
        code, runs = call('/api/runs', token=token)
        assert code == 200 and runs and runs[0]['outcome'] == 'Failed', runs
        code, flow = call('/api/flow', token=token)
        assert flow['status'] == 'Idle', flow['status']
        print('PASS Q05 case 1: guard loss while paused is latched -> Fault + recovery; '
              'resume refused; run Failed, never Completed')
    finally:
        stop_api(proc)

# --- Case 2: guard loss during an operator gate aborts the gate without a manual resume. ---
with tempfile.TemporaryDirectory(prefix='dc-v27-b-') as temp:
    data, env, call = make_base(temp, 'b')
    cfg_path = data / 'config/station-B.json'
    cfg = json.loads(cfg_path.read_text(encoding='utf-8'))
    cfg['sequence'] = [{'id': 'vision-wait', 'type': 'VisionLocate',
                        'label': '视觉人工处理', 'onError': 'WaitForOperator'}]
    cfg_path.write_text(json.dumps(cfg), encoding='utf-8')
    proc = start_api(data, env)
    try:
        wait_healthy(call, proc)
        token = login(call)
        for axis in ('X', 'Y'):
            assert call(f'/api/stations/B/manual/axes/{axis}/enable', {'enabled': True}, token)[0] == 200
            assert call(f'/api/stations/B/manual/axes/{axis}/home', {}, token)[0] == 200
        assert call('/api/stations/B/mode', {'mode': 'Auto'}, token)[0] == 200
        assert call('/api/stations/B/cycle/start', {}, token)[0] == 200
        time.sleep(0.08)
        assert call('/api/stations/B/sim/input', {'name': 'PartPresent', 'value': False}, token)[0] == 200
        poll(lambda: call('/api/stations/B/flow', token=token)[1].get('waiting') or None)
        # Lose the guard while the operator gate is open: the gate must abort on its own.
        assert call('/api/stations/B/sim/input', {'name': 'GuardClosed', 'value': False}, token)[0] == 200
        snap = poll(lambda: (lambda s: s if s['recoveryRequired'] else None)(call('/api/stations/B/snapshot', token=token)[1]))
        assert snap['state'] == 'Fault', snap
        poll(lambda: call('/api/stations/B/flow', token=token)[1].get('waiting') is None)
        assert call('/api/stations/B/flow/debug', {'command': 'retry'}, token)[0] == 409  # gate already gone
        print('PASS Q05 case 2: operator gate aborts itself when the ordinary condition is lost; '
              'manual retry after the fact is refused')
    finally:
        stop_api(proc)

print('RESULT: PASS Q05 HTTP: pause waits are monitored and latched, never silently resumed')
