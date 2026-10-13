"""R06 real .NET HTTP contract: a corrupt fixed-station config or database must not
kill the process. Read-only diagnostics stay reachable, the affected station's routes
are refused with the concrete reason, and the remaining fleet keeps working.

Must run with .NET 10 SDK; without it prints RESULT: BLOCKED and exits 77 (never PASS).
"""
import json, os, shutil, socket, sys, tempfile, time, urllib.error, urllib.request
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api
if not shutil.which('dotnet'):
    print('RESULT: BLOCKED R06 HTTP: .NET 10 SDK absent'); sys.exit(77)


def make_base(temp, corrupt_b=False, corrupt_a=False, corrupt_managed=False):
    data = Path(temp)
    (data / 'config').mkdir(parents=True, exist_ok=True)
    shutil.copy(ROOT / 'backend/DeviceControl.Api/config/station.json', data / 'config/station.json')
    shutil.copy(ROOT / 'backend/DeviceControl.Api/config/station-B.json', data / 'config/station-B.json')
    if corrupt_b: (data / 'config/station-B.json').write_text('{', encoding='utf8')
    if corrupt_a: (data / 'config/station.json').write_text('{', encoding='utf8')
    if corrupt_managed:
        managed = data / 'config/managed-stations'; managed.mkdir(exist_ok=True)
        (managed / 'C.json').write_text('{', encoding='utf8')
    return data


def probe(data):
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

    return env, call


def wait_healthy(call, proc):
    for _ in range(120):
        if proc.poll() is not None:
            raise AssertionError('server exited before healthy')
        try:
            if call('/api/health')[0] == 200: return
        except Exception: pass
        time.sleep(0.3)
    raise AssertionError('HTTP health timed out')


def login(call, role):
    code, row = call('/api/auth/login', {'username': role, 'password': 'demo123'})
    assert code == 200, (code, row)
    return row['token']


# --- Case 1: fixed station B and a managed station are corrupt; A keeps working. ---
with tempfile.TemporaryDirectory(prefix='dc-r06-b-') as temp:
    data = make_base(temp, corrupt_b=True, corrupt_managed=True)
    (data / 'data' / 'deployments').mkdir(parents=True, exist_ok=True)
    (data / 'data/deployments' / ('dep-' + 'a' * 32 + '.json')).write_text('{', encoding='utf8')
    env, call = probe(data)
    proc = start_api(data, env)
    try:
        wait_healthy(call, proc)
        engineer = login(call, 'engineer')
        code, diag = call('/api/diagnostics/stations', token=engineer)
        assert code == 200, (code, diag)
        assert 'B' in diag['failedStations'] and 'C' in diag['failedStations'], diag['failedStations']
        assert diag['deploymentStartupIssues'], diag['deploymentStartupIssues']
        code, ready = call('/api/ready', token=engineer)
        assert code == 503 and not ready['ready'], (code, ready)
        assert 'B' in ready['failedStations'], ready['failedStations']
        code, stations = call('/api/stations', token=engineer)
        assert code == 200 and [s['id'] for s in stations['stations']] == ['A'], stations
        code, snap = call('/api/snapshot', token=engineer)
        assert code == 200 and snap['profileId'], (code, snap)
        code, row = call('/api/stations/B/snapshot', token=engineer)
        assert code == 503 and row.get('stationUnavailable') is True, (code, row)
        assert 'B' in row['error'] and row['error'], row
        code, row = call('/api/stations/C/snapshot', token=engineer)
        assert code == 503 and row.get('stationUnavailable') is True, (code, row)
        print('PASS R06 case 1: corrupt fixed B + managed C fail closed, A and diagnostics stay up')
    finally:
        stop_api(proc)

# --- Case 2: the primary station config is corrupt; diagnostics still reachable. ---
with tempfile.TemporaryDirectory(prefix='dc-r06-a-') as temp:
    data = make_base(temp, corrupt_a=True)
    env, call = probe(data)
    proc = start_api(data, env)
    try:
        wait_healthy(call, proc)
        engineer = login(call, 'engineer')
        code, diag = call('/api/diagnostics/stations', token=engineer)
        assert code == 200 and 'A' in diag['failedStations'], (code, diag)
        code, ready = call('/api/ready', token=engineer)
        assert code == 503 and not ready['ready'], (code, ready)
        for path, body in (('/api/snapshot', None), ('/api/config', None), ('/api/mode', {'mode': 'Manual'})):
            code, row = call(path, body, engineer)
            assert code == 503 and row.get('stationUnavailable') is True, (path, code, row)
            assert 'A' in row['error'], (path, row)
        code, stations = call('/api/stations', token=engineer)
        assert code == 200 and [s['id'] for s in stations['stations']] == ['B'], stations
        code, snap = call('/api/stations/B/snapshot', token=engineer)
        assert code == 200, (code, snap)
        print('PASS R06 case 2: corrupt fixed A fails closed, legacy routes refused with reason, B alive')
    finally:
        stop_api(proc)

# --- Case 3: the primary database is corrupt; read-only diagnostics survive. ---
with tempfile.TemporaryDirectory(prefix='dc-r06-db-') as temp:
    data = make_base(temp)
    db_dir = data / 'data'; db_dir.mkdir(parents=True, exist_ok=True)
    (db_dir / 'devicecontrol.db').write_bytes(b'this is not a sqlite database')
    env, call = probe(data)
    proc = start_api(data, env)
    try:
        wait_healthy(call, proc)
        engineer = login(call, 'engineer')
        code, diag = call('/api/diagnostics/stations', token=engineer)
        assert code == 200, (code, diag)
        assert 'A' in diag['failedStations'], diag['failedStations']
        assert diag['audit'].get('unavailable') is True or diag['audit'].get('degraded') is True, diag['audit']
        code, ready = call('/api/ready', token=engineer)
        assert code == 503 and not ready['ready'], (code, ready)
        code, row = call('/api/snapshot', token=engineer)
        assert code == 503 and row.get('stationUnavailable') is True, (code, row)
        code, stations = call('/api/stations', token=engineer)
        assert code == 200 and [s['id'] for s in stations['stations']] == ['B'], stations
        code, snap = call('/api/stations/B/snapshot', token=engineer)
        assert code == 200, (code, snap)
        print('PASS R06 case 3: corrupt A database fails A closed, diagnostics and B survive')
    finally:
        stop_api(proc)
