"""Q01/Q02 real .NET HTTP contract: audit durability under database rejection,
spool damage and restart.

A. 10000 staged spool rows + new failures replay EXACTLY ONCE - no memory-view
   eviction may discard a durable fact, and the spool is only dropped after the
   database confirmed every row.
B. A damaged primary spool slot must not disable persistence (a staging slot
   takes over); unconfirmed rows are visible in the command lookup as
   pendingAudit, never as 'found=false', and a restart replays them.
C. When every durable channel is gone, traceable changes are refused (503),
   the loss is recorded across a restart (readiness stays 503), the lookup
   reports the uncertain loss window, and only an explicit human acknowledgement
   re-opens normal service.

Must run with .NET 10 SDK; without it prints RESULT: BLOCKED and exits 77.
"""
import json, os, shutil, socket, sqlite3, sys, tempfile, time, urllib.error, urllib.request
from contextlib import closing
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))
from dotnet_harness import start_api, stop_api

if not shutil.which('dotnet'):
    print('RESULT: BLOCKED Q01/Q02 HTTP: .NET 10 SDK absent')
    sys.exit(77)


class Server:
    def __init__(self, prefix):
        self.tmp = tempfile.TemporaryDirectory(prefix=prefix)
        self.root = Path(self.tmp.name)
        (self.root / 'config').mkdir()
        for name in ('station.json', 'station-B.json'):
            shutil.copy(ROOT / 'backend/DeviceControl.Api/config' / name, self.root / 'config' / name)
        self.spool = self.root / 'data/audit-pending.jsonl'
        self.slot1 = self.root / 'data/audit-pending.1.jsonl'
        self.slot2 = self.root / 'data/audit-pending.2.jsonl'
        self.loss = self.root / 'data/audit-loss.json'
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            self.base = f'http://127.0.0.1:{sock.getsockname()[1]}'
        self.proc = None

    def start(self):
        self.proc = start_api(self.root, {**os.environ, 'SimListenUrl': self.base,
                                          'ASPNETCORE_CONTENTROOT': str(self.root)})
        assert self.call('/api/health')[0] == 200

    def stop(self):
        if self.proc:
            stop_api(self.proc)
            self.proc = None

    def close(self):
        self.stop()
        self.tmp.cleanup()

    def call(self, path, body=None, token=None, cid=None, origin=None, timeout=90):
        headers = {'Content-Type': 'application/json'}
        if token: headers['X-Sim-Token'] = token
        if cid: headers['X-Command-Id'] = cid
        if origin: headers['Origin'] = origin
        req = urllib.request.Request(self.base + path,
            data=json.dumps(body).encode() if body is not None else None, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.status, json.load(r), dict(r.headers)
        except urllib.error.HTTPError as r:
            return r.code, json.load(r), dict(r.headers)

    def login(self):
        code, row, _ = self.call('/api/auth/login', {'username': 'admin', 'password': 'demo123'})
        assert code == 200, (code, row)
        return row['token']

    def sql(self, statement):
        with closing(sqlite3.connect(self.root / 'data/devicecontrol.db', timeout=20)) as conn:
            conn.execute('PRAGMA busy_timeout=20000')
            rows = conn.execute(statement).fetchall()
            conn.commit()
            return rows

    def trigger_on(self):
        self.sql("CREATE TRIGGER reject_audit BEFORE INSERT ON audit_events BEGIN SELECT RAISE(ABORT,'probe audit rejection'); END")

    def trigger_off(self):
        self.sql('DROP TRIGGER reject_audit')


def scenario_full_spool_replays_exactly_once():
    s = Server('dc-v25-a-')
    try:
        s.start(); s.login(); s.stop()  # create the database
        now = '2026-10-13T13:00:00Z'
        with s.spool.open('w', encoding='utf-8') as handle:
            for i in range(10000):
                handle.write(json.dumps({'Id': f'probe-{i:05}', 'Time': now, 'Actor': 'admin',
                    'Role': 'Admin', 'Method': 'POST', 'Path': '/api/mode', 'Status': 200,
                    'TraceId': 'probe', 'Outcome': 'Accepted',
                    'CommandId': f'probe-spool-{i:05}', 'StationId': 'A'}, ensure_ascii=False) + '\n')
        s.trigger_on()
        s.start(); token = s.login()  # login audit fails, staged in the spool after the seed
        s.trigger_off()
        code, _, headers = s.call('/api/mode', {'mode': 'Manual'}, token)  # successful write triggers replay
        assert code == 200, code
        rows = s.sql("SELECT COUNT(*) FROM audit_events WHERE id LIKE 'probe-%'")[0][0]
        assert rows == 10000, rows
        assert s.sql("SELECT id FROM audit_events WHERE id='probe-00000'"), 'earliest staged row was lost'
        assert not s.spool.exists(), 'spool must be dropped only after full confirmation'
        mode_cid = headers.get('X-Command-Id')
        assert mode_cid, headers
        code, lookup, _ = s.call(f'/api/commands/{mode_cid}', token=token)
        assert lookup['data']['found'] and lookup['data']['audit'], lookup['data']
        code, diag, _ = s.call('/api/diagnostics/stations', token=token)
        assert code == 200, (code, diag)
        audit = diag['audit']
        assert audit['degraded'] is False and audit['pending'] == 0 and audit['lost'] == 0, audit
        print(f'A PASS: 10000 seeded + failure-window rows replayed exactly once; spool cleared; health recovered')
    finally:
        s.close()


def scenario_rotated_spool_and_restart():
    s = Server('dc-v25-b-')
    try:
        s.start(); token = s.login()
        s.spool.mkdir()  # deterministic primary-slot damage
        s.trigger_on()
        code, _, headers = s.call('/api/auth/logout', {}, token, cid='probe-lost-logout',
                                  origin='http://127.0.0.1:5173')
        assert code == 200, code
        exposed = headers.get('Access-Control-Expose-Headers') or ''
        assert 'X-Audit-Status' in exposed, exposed  # Q04: the header is readable cross-origin
        token = s.login()
        code, lookup, _ = s.call('/api/commands/probe-lost-logout', token=token)
        assert code == 200, (code, lookup)
        data = lookup['data']
        assert data['found'] is True, data
        assert data['pendingAudit'] and data['pendingAudit'][0]['commandId'] == 'probe-lost-logout', data
        assert data['audit'] == [], data
        code, diag, _ = s.call('/api/diagnostics/stations', token=token)
        assert diag['audit']['degraded'] and diag['audit']['pending'] >= 2, diag['audit']
        assert diag['audit']['spool'] == 'ok' and diag['audit']['memoryOnly'] == 0, diag['audit']
        s.stop(); s.trigger_off(); s.start(); token = s.login()
        code, lookup, _ = s.call('/api/commands/probe-lost-logout', token=token)
        data = lookup['data']
        assert data['found'] and data['audit'], data
        assert data['pendingAudit'] == [], data
        code, ready, _ = s.call('/api/ready', token=token)
        assert ready['audit']['pending'] == 0, ready['audit']
        print('B PASS: staging slot survives primary-slot damage; pending rows visible before restart, '
              'replayed and confirmed after; audit status header exposed cross-origin')
    finally:
        s.close()


def scenario_no_durable_channel_then_ack():
    s = Server('dc-v25-c-')
    try:
        s.start(); token = s.login()
        for slot in (s.spool, s.slot1, s.slot2):
            slot.mkdir()
        s.trigger_on()
        code, body, _ = s.call('/api/auth/logout', {}, token, cid='probe-lost-logout-2')
        # The FIRST request after a silent channel loss cannot know it is broken; its
        # audit row is caught by the rescue buffer instead of being dropped silently.
        assert code == 200, (code, body)
        token = s.login()  # login stays available; its audit row is memory-only
        code, body, _ = s.call('/api/mode', {'mode': 'Manual'}, token)
        assert code == 503 and body.get('auditUnavailable') is True, (code, body)
        code, body, _ = s.call('/api/cycle/stop', {}, token)  # survival op is not audit-refused
        assert not (code == 503 and body.get('auditUnavailable')), (code, body)
        code, lookup, _ = s.call('/api/commands/probe-lost-logout-2', token=token)
        data = lookup['data']
        assert data['found'] is True and data['uncertain'] is True and data['pending'] >= 2, data
        assert data['pendingAudit'] and data['pendingAudit'][0]['commandId'] == 'probe-lost-logout-2', data
        code, diag, _ = s.call('/api/diagnostics/stations', token=token)
        assert diag['audit']['memoryOnly'] >= 2 and diag['audit']['degraded'], diag['audit']
        assert s.loss.exists(), 'loss marker must be written while a record has no durable staging'
        # Restart with the incident still unresolved: readiness must NOT recover.
        s.stop(); s.start()
        token = s.login()
        code, ready, _ = s.call('/api/ready', token=token)
        assert code == 503 and ready['audit']['degraded'] and ready['audit']['lost'] >= 2, ready
        code, lookup, _ = s.call('/api/commands/probe-lost-logout-2', token=token)
        data = lookup['data']
        assert data['found'] is False and data['uncertain'] is True and data['lost'] >= 2, data
        assert '丢失范围' in data['guidance'], data['guidance']
        # Repair the channels, then the acknowledgement (a survival operation) re-opens service.
        s.trigger_off()
        for slot in (s.spool, s.slot1, s.slot2):
            slot.rmdir()
        code, body, _ = s.call('/api/mode', {'mode': 'Manual'}, token)
        assert code == 503, (code, body)  # still fail-closed until the loss is acknowledged
        code, ack, _ = s.call('/api/audit/acknowledge-loss', {'note': '探针：确认丢失范围后恢复'}, token)
        assert code == 200 and ack['data']['acknowledged'] is True, (code, ack)
        code, body, _ = s.call('/api/mode', {'mode': 'Manual'}, token)
        assert code == 200, (code, body)
        code, ready, _ = s.call('/api/ready', token=token)
        assert code == 200 and ready['ready'] is True, (code, ready)
        print('C PASS: no durable channel -> traceable changes refused, loss recorded across restart, '
              'readiness 503 until the human acknowledgement, then normal service')
    finally:
        s.close()


scenario_full_spool_replays_exactly_once()
scenario_rotated_spool_and_restart()
scenario_no_durable_channel_then_ack()
print('RESULT: PASS Q01/Q02 HTTP: spool replay exactly once, staged-view visibility, fail-closed loss handling')
