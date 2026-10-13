"""R08/Q04 real-Chromium cross-port contract.

- Correlation headers (X-Command-Id / X-Trace-Id) must be readable by the real
  cross-port web frontend, and the shared client must keep the concrete IDs on
  failures.
- Q04: the audit-degradation header (X-Audit-Status) must be readable by the
  browser as well; the main status bar can only show "审计降级" when the header
  actually crosses the origin boundary. The degraded state is produced
  deterministically with a SQLite trigger, and recovery is observed after it is
  removed.

Uses a temporary frontend HTTP origin on 127.0.0.1:5173 (the allow-listed origin)
and an isolated API instance. Without Playwright, the .NET SDK or a free port
this prints RESULT: BLOCKED and exits 77 - it is never counted as PASS.
"""
import asyncio, functools, os, shutil, socket, sqlite3, sys, tempfile, threading, time, urllib.request
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api


def blocked(reason):
    print('RESULT: BLOCKED R08/Q04 Chromium CORS: ' + reason)
    sys.exit(77)


try:
    from playwright.async_api import async_playwright
except ImportError:
    blocked('playwright unavailable')
if not shutil.which('dotnet'):
    blocked('.NET 10 SDK absent')


class QuietHandler(SimpleHTTPRequestHandler):
    def log_message(self, *args): pass


def wait_healthy(base):
    for _ in range(120):
        try:
            with urllib.request.urlopen(base + '/api/health', timeout=2) as res:
                if res.status == 200: return
        except Exception: pass
        time.sleep(0.3)
    raise AssertionError('HTTP health timed out')


class Db:
    def __init__(self, path):
        self.path = path

    def run(self, statement):
        conn = sqlite3.connect(str(self.path), timeout=10)
        conn.execute('PRAGMA busy_timeout=10000')
        conn.execute(statement)
        conn.commit()
        conn.close()


async def run():
    if sys.platform == 'win32':
        asyncio.set_event_loop_policy(asyncio.WindowsProactorEventLoopPolicy())
    with tempfile.TemporaryDirectory(prefix='dc-r08-') as temp:
        data = Path(temp); (data / 'config').mkdir()
        for cfg in ('station.json', 'station-B.json'):
            shutil.copy(ROOT / 'backend/DeviceControl.Api/config' / cfg, data / 'config' / cfg)
        with socket.socket() as s:
            s.bind(('127.0.0.1', 0)); api_port = s.getsockname()[1]
        base = f'http://127.0.0.1:{api_port}'
        env = {**os.environ, 'SimListenUrl': base, 'ASPNETCORE_CONTENTROOT': str(data)}
        try:
            httpd = ThreadingHTTPServer(('127.0.0.1', 5173),
                functools.partial(QuietHandler, directory=str(ROOT / 'frontend')))
        except OSError:
            blocked('127.0.0.1:5173 already in use locally')
        thread = threading.Thread(target=httpd.serve_forever, daemon=True); thread.start()
        proc = start_api(data, env)
        try:
            wait_healthy(base)
            db = Db(data / 'data' / 'devicecontrol.db')
            console = {
                'health': '''async (root) => {
                    const api = window.DeviceControlApiClient.create({root});
                    const health = await api('/api/health');
                    return {traceHeader: health.headers.get('X-Trace-Id'),
                            commandHeader: health.headers.get('X-Command-Id')}; }''',
                'basic': '''async (root) => {
                    const api = window.DeviceControlApiClient.create({root});
                    const out = {};
                    const failed = await api('/api/auth/login', {method: 'POST',
                        headers: {'Content-Type': 'application/json'},
                        body: JSON.stringify({username: 'admin', password: 'wrong'})}).then(() => null, e => e);
                    out.loginStatus = failed?.status;
                    out.loginUnknown = failed?.resultUnknown;
                    out.loginCommandId = failed?.commandId;
                    const login = await api('/api/auth/login', {method: 'POST',
                        headers: {'Content-Type': 'application/json'},
                        body: JSON.stringify({username: 'engineer', password: 'demo123'})}).then(r => r.json());
                    const write = await api('/api/sim/input', {method: 'POST',
                        headers: {'Content-Type': 'application/json', 'X-Sim-Token': login.token},
                        body: JSON.stringify({name: 'GuardClosed', value: true})});
                    out.writeCommandId = write.commandId;
                    out.writeTraceId = write.traceId;
                    return out; }''',
                'degraded': '''async (root) => {
                    const api = window.DeviceControlApiClient.create({root});
                    const out = {statuses: []};
                    const login = await api('/api/auth/login', {method: 'POST',
                        headers: {'Content-Type': 'application/json'},
                        body: JSON.stringify({username: 'engineer', password: 'demo123'})}).then(r => r.json());
                    for (let i = 0; i < 3; i++) {
                        const write = await api('/api/sim/input', {method: 'POST',
                            headers: {'Content-Type': 'application/json', 'X-Sim-Token': login.token},
                            body: JSON.stringify({name: 'GuardClosed', value: true})});
                        out.statuses.push(write.headers.get('X-Audit-Status'));
                    }
                    return out; }''',
                'recovered': '''async (root) => {
                    const api = window.DeviceControlApiClient.create({root});
                    const login = await api('/api/auth/login', {method: 'POST',
                        headers: {'Content-Type': 'application/json'},
                        body: JSON.stringify({username: 'engineer', password: 'demo123'})}).then(r => r.json());
                    let last = null;
                    for (let i = 0; i < 3; i++) {
                        const write = await api('/api/sim/input', {method: 'POST',
                            headers: {'Content-Type': 'application/json', 'X-Sim-Token': login.token},
                            body: JSON.stringify({name: 'GuardClosed', value: true})});
                        last = write.headers.get('X-Audit-Status');
                    }
                    return {lastStatus: last}; }'''
            }
            async with async_playwright() as pw:
                kw = {'headless': True, 'args': ['--no-sandbox']}
                if shutil.which('chromium'): kw['executable_path'] = shutil.which('chromium')
                browser = await pw.chromium.launch(**kw)
                page = await browser.new_page()
                errors = []
                page.on('pageerror', lambda e: errors.append(str(e)))
                await page.goto('http://127.0.0.1:5173/index.html', wait_until='domcontentloaded')
                health = await page.evaluate(console['health'], base)
                basic = await page.evaluate(console['basic'], base)
                # Q04: deterministic audit degradation, then observe the header in the browser.
                db.run("CREATE TRIGGER reject_audit BEFORE INSERT ON audit_events BEGIN SELECT RAISE(ABORT,'browser audit rejection'); END")
                degraded = await page.evaluate(console['degraded'], base)
                expose = ''
                try:
                    probe_req = urllib.request.Request(base + '/api/health', method='GET',
                        headers={'Origin': 'http://127.0.0.1:5173'})
                    with urllib.request.urlopen(probe_req, timeout=10) as res:
                        expose = res.headers.get('Access-Control-Expose-Headers') or ''
                except Exception:
                    pass
                db.run('DROP TRIGGER reject_audit')
                recovered = await page.evaluate(console['recovered'], base)
                await browser.close()
            assert health['traceHeader'], health
            assert health['commandHeader'], health
            assert basic['loginStatus'] == 401 and basic['loginUnknown'] is False, basic
            assert basic['loginCommandId'], basic
            assert basic['writeCommandId'] and basic['writeTraceId'], basic
            # Q04: the browser must READ the audit status; null means the header was not exposed.
            assert all(x == 'degraded' for x in degraded['statuses']), degraded
            assert recovered['lastStatus'] == 'ok', recovered
            assert 'X-Audit-Status' in expose, expose
            assert 'X-Command-Id' in expose and 'X-Trace-Id' in expose, expose
            app_source = (ROOT / 'frontend/app.js').read_text(encoding='utf-8')
            assert '审计不可用' in app_source and '审计降级' in app_source, 'UI must show degraded/unavailable states'
            assert not errors, errors
            print('PASS R08/Q04 Chromium CORS: correlation AND audit-status headers readable cross-port; '
                  'degraded state visible in the browser, ok again after recovery')
        finally:
            stop_api(proc)
            httpd.shutdown()


if __name__ == '__main__':
    asyncio.run(run())
    print('RESULT: PASS R08/Q04 Chromium CORS cross-port header contract')
