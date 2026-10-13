"""Q03 real .NET HTTP contract: the managed-station folder must never be able to
prevent the minimal diagnostic host from starting.

Case 1: an invalid file name, a case-equivalent duplicate, an over-capacity file
and a valid-name/corrupt-content file are ALL isolated per file. The host starts,
health/ready/diagnostics answer, the affected routes are refused with the concrete
reason, unaffected stations keep working, and every original file stays on disk.
Case 2: the managed-stations path itself is blocked by a regular file; the folder
failure is reported and A/B still start.

Must run with .NET 10 SDK; without it prints RESULT: BLOCKED and exits 77.
"""
import json, os, shutil, socket, sys, tempfile, time, urllib.error, urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))
from dotnet_harness import start_api, stop_api

if not shutil.which('dotnet'):
    print('RESULT: BLOCKED Q03 HTTP: .NET 10 SDK absent')
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
    code, row = call('/api/auth/login', {'username': 'engineer', 'password': 'demo123'})
    assert code == 200, (code, row)
    return row['token']


# --- Case 1: per-file isolation for name, duplicate, capacity and content errors. ---
with tempfile.TemporaryDirectory(prefix='dc-v26-a-') as temp:
    data, env, call = make_base(temp)
    managed = data / 'config/managed-stations'
    managed.mkdir()
    bad_content = (data / 'config/station-B.json').read_text(encoding='utf-8')
    (managed / 'bad name.json').write_text(bad_content, encoding='utf-8')  # invalid file name
    (managed / 'b.json').write_text(bad_content, encoding='utf-8')        # case-equivalent duplicate of B
    (managed / 'B2.json').write_text('{', encoding='utf-8')               # valid name, corrupt content
    for i in range(1, 12):
        (managed / f'C{i:02}.json').write_text(bad_content, encoding='utf-8')  # C01..C11: C11 exceeds capacity
    original_files = {p.name for p in managed.iterdir()}
    proc = start_api(data, env)
    try:
        wait_healthy(call, proc)
        token = login(call)
        code, diag = call('/api/diagnostics/stations', token=token)
        assert code == 200, (code, diag)
        failed = diag['failedStations']
        assert 'bad name' in failed and '文件名不合法' in failed['bad name'], failed
        assert 'b' in failed and '重复' in failed['b'], failed
        assert 'C11' in failed and '最大工站数' in failed['C11'], failed
        assert 'B2' in failed and '最大工站数' not in failed['B2'], failed
        code, stations = call('/api/stations', token=token)
        ids = [s['id'] for s in stations['stations']]
        assert ids == ['A', 'B'] + [f'C{i:02}' for i in range(1, 11)], ids
        assert call('/api/stations/C01/snapshot', token=token)[0] == 200
        code, row = call('/api/stations/B2/snapshot', token=token)
        assert code == 503 and row.get('stationUnavailable') is True, (code, row)
        code, row = call('/api/stations/bad%20name/snapshot', token=token)
        assert code == 503 and row.get('stationUnavailable') is True, (code, row)
        assert 'bad name' in row['error'] or '不合法' in row['error'], row
        assert call('/api/snapshot', token=token)[0] == 200  # legacy A routes still work
        code, ready = call('/api/ready', token=token)
        assert code == 503 and 'bad name' in ready['failedStations'], (code, ready)
        # No original file may be deleted or rewritten by startup isolation.
        assert {p.name for p in managed.iterdir()} == original_files
        assert (managed / 'bad name.json').read_text(encoding='utf-8') == bad_content
        print('PASS Q03 case 1: bad name / duplicate / capacity / corrupt content isolated; '
              'host healthy, reasons visible, files preserved')
    finally:
        stop_api(proc)

# --- Case 2: the managed folder path itself is a regular file. ---
with tempfile.TemporaryDirectory(prefix='dc-v26-b-') as temp:
    data, env, call = make_base(temp)
    (data / 'config/managed-stations').write_text('not a directory', encoding='utf-8')
    proc = start_api(data, env)
    try:
        wait_healthy(call, proc)
        token = login(call)
        code, diag = call('/api/diagnostics/stations', token=token)
        assert code == 200, (code, diag)
        assert '(managed-stations)' in diag['failedStations'], diag['failedStations']
        code, stations = call('/api/stations', token=token)
        assert [s['id'] for s in stations['stations']] == ['A', 'B'], stations
        assert (data / 'config/managed-stations').is_file()  # never removed or replaced
        print('PASS Q03 case 2: blocked managed-stations path reported; A/B still start; file preserved')
    finally:
        stop_api(proc)

print('RESULT: PASS Q03 HTTP: managed-station load failures are per-file isolated and never fatal')
