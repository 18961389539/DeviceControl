"""Q06 real .NET HTTP contract: shared-resource lease lifecycle around human waits.

1. A node waiting at an operator gate must NOT keep holding its shared lease:
   another station's manual vision trigger succeeds instead of timing out, and the
   waiting station's flow snapshot shows no held resources.
2. A shared-resource contention timeout that happens BEFORE any action started is
   rejected as a conflict without invalidating axis homing or requiring mechanism
   recovery (nothing moved); a software stop still releases a holder.

Must run with .NET 10 SDK; without it prints RESULT: BLOCKED and exits 77.
"""
import json, os, shutil, socket, sys, tempfile, time, urllib.error, urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))
from dotnet_harness import start_api, stop_api

if not shutil.which('dotnet'):
    print('RESULT: BLOCKED Q06 HTTP: .NET 10 SDK absent')
    sys.exit(77)


def make_base(temp):
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


def poll(fn, deadline=10.0, interval=0.05):
    end = time.monotonic() + deadline
    last = None
    while time.monotonic() < end:
        last = fn()
        if last:
            return last
        time.sleep(interval)
    raise AssertionError(f'condition not reached; last={last}')


def setup_b(call, token, sequence):
    for axis in ('X', 'Y'):
        assert call(f'/api/stations/B/manual/axes/{axis}/enable', {'enabled': True}, token)[0] == 200
        assert call(f'/api/stations/B/manual/axes/{axis}/home', {}, token)[0] == 200
    assert call('/api/stations/B/mode', {'mode': 'Auto'}, token)[0] == 200
    assert call('/api/stations/B/cycle/start', {}, token)[0] == 200


# --- Case 1: an operator wait releases CELL-SHARED; another station is not dragged into a fault. ---
with tempfile.TemporaryDirectory(prefix='dc-v28-a-') as temp:
    data, env, call = make_base(temp)
    cfg_path = data / 'config/station-B.json'
    cfg = json.loads(cfg_path.read_text(encoding='utf-8'))
    cfg['sequence'] = [{'id': 'vision-wait', 'type': 'VisionLocate',
                        'label': '视觉人工处理', 'onError': 'WaitForOperator'}]
    cfg_path.write_text(json.dumps(cfg), encoding='utf-8')
    proc = start_api(data, env)
    try:
        wait_healthy(call, proc)
        token = login(call)
        setup_b(call, token, None)
        time.sleep(0.08)
        assert call('/api/stations/B/sim/input', {'name': 'PartPresent', 'value': False}, token)[0] == 200
        poll(lambda: call('/api/stations/B/flow', token=token)[1].get('waiting') or None)
        code, res = call('/api/stations/resources', token=token)
        assert code == 200 and not any(x['resource'] == 'CELL-SHARED' for x in res['leases']), res
        code, bflow = call('/api/stations/B/flow', token=token)
        assert bflow['resources'] == {}, bflow['resources']
        started = time.perf_counter()
        code, body = call('/api/manual/vision/trigger', {}, token)
        elapsed = time.perf_counter() - started
        assert code == 200, (code, body)
        assert elapsed < 2.0, elapsed  # the old defect: blocked for the full 2.5 s timeout
        code, a = call('/api/snapshot', token=token)
        assert a['state'] == 'Idle' and a['recoveryRequired'] is False, a
        assert call('/api/stations/B/cycle/stop', {}, token)[0] == 200
        poll(lambda: (lambda s: s if not s['busy'] else None)(call('/api/stations/B/snapshot', token=token)[1]))
        code, res = call('/api/stations/resources', token=token)
        assert not res['leases'], res
        print('PASS Q06 case 1: operator gate releases the shared lease; cross-station manual vision '
              'succeeds fast; stop leaves no held resource')
    finally:
        stop_api(proc)

# --- Case 2: pre-action contention is a clean conflict; homing and recovery state stay valid. ---
with tempfile.TemporaryDirectory(prefix='dc-v28-b-') as temp:
    data, env, call = make_base(temp)
    cfg_path = data / 'config/station-B.json'
    cfg = json.loads(cfg_path.read_text(encoding='utf-8'))
    cfg['sequence'] = [{'id': 'hold', 'type': 'Delay', 'label': '占用共享资源',
                        'durationMs': 10000, 'sharedResources': ['CELL-SHARED']}]
    cfg_path.write_text(json.dumps(cfg), encoding='utf-8')
    proc = start_api(data, env)
    try:
        wait_healthy(call, proc)
        token = login(call)
        for axis in ('X', 'Y'):  # A is homed first so invalidation would be observable
            assert call(f'/api/manual/axes/{axis}/enable', {'enabled': True}, token)[0] == 200
            assert call(f'/api/manual/axes/{axis}/home', {}, token)[0] == 200
        setup_b(call, token, None)
        poll(lambda: any(x['resource'] == 'CELL-SHARED' and x['stationId'] == 'B'
                         for x in call('/api/stations/resources', token=token)[1]['leases']))
        started = time.perf_counter()
        code, body = call('/api/manual/vision/trigger', {}, token)
        elapsed = time.perf_counter() - started
        assert code == 409 and '资源等待超时' in (body.get('error') or ''), (code, body)
        assert 2.0 < elapsed < 5.0, elapsed  # bounded by the 2500 ms acquire timeout
        code, a = call('/api/snapshot', token=token)
        assert a['state'] == 'Idle' and a['recoveryRequired'] is False, a
        assert a['axes']['X']['homed'] is True and a['axes']['Y']['homed'] is True, a['axes']
        assert call('/api/stations/B/cycle/stop', {}, token)[0] == 200
        poll(lambda: not call('/api/stations/resources', token=token)[1]['leases'])
        print('PASS Q06 case 2: no-motion contention timeout keeps the station Idle with valid homing; '
              'no unfounded fault propagation')
    finally:
        stop_api(proc)

print('RESULT: PASS Q06 HTTP: lease lifecycle defined for human waits and pre-action contention')
