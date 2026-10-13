"""R03/R04/R05 real .NET HTTP contract: command-ID lifecycle lookup, explicit
station attribution for global upgrade routes, and audit-degradation gating
with spooled-record recovery.

Must run with .NET 10 SDK; without it prints RESULT: BLOCKED and exits 77 (never PASS).
"""
import json, os, shutil, socket, sqlite3, sys, tempfile, time, urllib.error, urllib.request
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api
if not shutil.which('dotnet'):
    print('RESULT: BLOCKED R03/R04/R05 HTTP: .NET 10 SDK absent'); sys.exit(77)

with tempfile.TemporaryDirectory(prefix='dc-v22-http-') as temp:
    data = Path(temp); (data / 'config').mkdir()
    for cfg in ('station.json', 'station-B.json'):
        shutil.copy(ROOT / 'backend/DeviceControl.Api/config' / cfg, data / 'config' / cfg)
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); port = s.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    env = {**os.environ, 'SimListenUrl': base, 'ASPNETCORE_CONTENTROOT': str(data)}

    def call(path, body=None, token=None, command_id=None):
        payload = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(base + path, data=payload,
            method='POST' if body is not None else 'GET',
            headers={**({'Content-Type': 'application/json'} if body is not None else {}),
                     **({'X-Sim-Token': token} if token else {}),
                     **({'X-Command-Id': command_id} if command_id else {})})
        try:
            with urllib.request.urlopen(req, timeout=15) as res:
                return res.status, json.load(res), dict(res.headers)
        except urllib.error.HTTPError as e:
            return e.code, json.load(e), dict(e.headers)

    def login(role):
        code, row, _ = call('/api/auth/login', {'username': role, 'password': 'demo123'})
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
        admin = login('admin'); engineer = login('engineer'); operator = login('operator')

        # --- R03: a write with an explicit command ID is reconcilable by that ID. ---
        code, _, _ = call('/api/sim/input', {'name': 'GuardClosed', 'value': True}, admin, command_id='test-cmd-manual')
        assert code == 200, code
        code, row, _ = call('/api/commands/test-cmd-manual', token=engineer)
        assert code == 200, (code, row)
        row = row['data']
        assert row['found'] and row['audit'], row
        assert row['audit'][0]['status'] == 200 and row['audit'][0]['outcome'] == 'Accepted', row['audit'][0]
        assert row['audit'][0]['stationId'] == 'A', row['audit'][0]
        assert row['audit'][0]['commandId'] == 'test-cmd-manual', row['audit'][0]
        code, scoped, _ = call('/api/stations/A/commands/test-cmd-manual', token=engineer)
        assert code == 200 and scoped['data']['found'], (code, scoped)
        assert call('/api/commands/test-cmd-manual', token=operator)[0] == 403
        assert call('/api/commands/test-cmd-manual')[0] == 401
        assert call('/api/commands/bad%20id', token=engineer)[0] in (400, 409)

        # --- R03: a cycle started under a command ID exposes its durable outcome. ---
        for axis in ('X', 'Y'):
            assert call(f'/api/manual/axes/{axis}/enable', {'enabled': True}, admin)[0] == 200
            assert call(f'/api/manual/axes/{axis}/home', {}, admin)[0] == 200
        assert call('/api/mode', {'mode': 'Auto'}, admin)[0] == 200
        assert call('/api/cycle/start', {}, admin, command_id='test-cmd-cycle')[0] == 200
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            code, snap, _ = call('/api/snapshot', token=admin)
            if code == 200 and not snap['busy'] and snap['state'] == 'Idle' and snap['cycles'] >= 1:
                break
            time.sleep(0.1)
        else: raise AssertionError('cycle did not complete')
        code, row, _ = call('/api/commands/test-cmd-cycle', token=engineer)
        runs = row['data']['runs']
        assert runs and runs[0]['outcome'] == 'Completed', runs
        assert any(x['path'] == '/api/cycle/start' and x['status'] == 200 for x in row['data']['audit']), row['data']['audit']
        events = row['data']['events']
        assert any('周期' in (e.get('message') or '') for e in events), events

        # --- R05: B's rollback through the global route is attributed to B, not A. ---
        code, row, _ = call('/api/templates/capture', {'stationId': 'A', 'name': 'V22 attribution'}, engineer)
        assert code == 200, (code, row); tid = row['data']['id']
        assert call('/api/templates/' + tid + '/validate', {}, engineer)[0] == 200
        assert call('/api/templates/' + tid + '/publish', {}, admin)[0] == 200
        code, plan, _ = call('/api/upgrades/preflight', {'templateId': tid, 'stationId': 'B'}, engineer)
        assert code == 200 and plan['data']['ready'], (code, plan)
        code, dep, _ = call('/api/upgrades/apply', {'planId': plan['data']['id']}, admin)
        assert code == 200, (code, dep); dep_id = dep['data']['id']
        code, _, _ = call('/api/upgrades/rollback', {'deploymentId': dep_id}, admin, command_id='test-cmd-rollback-b')
        assert code == 200, code
        code, row, _ = call('/api/commands/test-cmd-rollback-b', token=engineer)
        audit = row['data']['audit']
        assert audit and all(x['stationId'] == 'B' for x in audit), audit
        assert any(x['deploymentId'] == dep_id for x in audit), audit
        code, pageb, _ = call('/api/stations/B/audit?page=1&pageSize=60', token=engineer)
        assert any(x['commandId'] == 'test-cmd-rollback-b' for x in pageb['items']), pageb['items']
        code, pagea, _ = call('/api/stations/A/audit?page=1&pageSize=60', token=engineer)
        assert not any(x['commandId'] == 'test-cmd-rollback-b' for x in pagea['items']), 'B operation must not appear in A audit'

        # --- R08: correlation headers are exposed to the allow-listed web origin. ---
        req = urllib.request.Request(base + '/api/health', method='GET',
            headers={'Origin': 'http://127.0.0.1:5173'})
        with urllib.request.urlopen(req, timeout=10) as res:
            expose = res.headers.get('Access-Control-Expose-Headers') or ''
            assert 'X-Command-Id' in expose and 'X-Trace-Id' in expose, expose

        # --- R04: audit degradation is visible, gates critical changes, recovers. ---
        db_path = data / 'data' / 'devicecontrol.db'
        conn = sqlite3.connect(str(db_path), timeout=5)
        conn.execute('PRAGMA busy_timeout=5000')
        conn.execute("CREATE TRIGGER block_audit BEFORE INSERT ON audit_events BEGIN SELECT RAISE(ABORT,'blocked'); END")
        conn.commit()
        code, _, headers = call('/api/sim/input', {'name': 'GuardClosed', 'value': True}, admin)
        assert code == 200, code
        code, _, headers = call('/api/sim/input', {'name': 'GuardClosed', 'value': True}, admin)
        assert headers.get('X-Audit-Status') == 'degraded', headers
        draft = {'definition': [{'id': 'stash', 'type': 'Delay', 'label': '暂存保持', 'durationMs': 50}]}
        code, row, _ = call('/api/flows/draft', draft, admin)
        assert code == 503 and row.get('auditDegraded') is True, (code, row)
        code, diag, _ = call('/api/diagnostics/stations', token=engineer)
        assert code == 200 and diag['audit']['degraded'] and diag['audit']['pending'] >= 1, diag
        code, ready, _ = call('/api/ready', token=admin)
        assert code == 503 and ready['audit']['degraded'], (code, ready)
        conn.execute('DROP TRIGGER block_audit'); conn.commit(); conn.close()
        code, _, headers = call('/api/sim/input', {'name': 'GuardClosed', 'value': True}, admin)
        assert code == 200, code
        code, diag, _ = call('/api/diagnostics/stations', token=engineer)
        assert code == 200 and not diag['audit']['degraded'] and diag['audit']['pending'] == 0, diag
        code, row, _ = call('/api/flows/draft', draft, admin)
        assert code == 200, (code, row)
        code, page, _ = call('/api/audit?page=1&pageSize=200', token=engineer)
        assert any(x['path'] == '/api/flows/draft' and x['status'] == 503 for x in page['items']), 'spooled refusal audit must be replayed'
        print('PASS R03/R04/R05 .NET HTTP: command lookup, B attribution, audit degradation gating and recovery')
    finally:
        stop_api(proc)
