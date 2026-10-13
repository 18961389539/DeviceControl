"""Real cross-port Chromium observation of audit status visibility."""
import asyncio, functools, threading
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from playwright.async_api import async_playwright
source=Path(__file__).with_name('http_probes.py').read_text(encoding='utf-8')
exec(compile(source.split('# Simultaneous audit DB')[0],str(Path(__file__).with_name('http_probes.py')),'exec'))

class Quiet(SimpleHTTPRequestHandler):
    def log_message(self,*args):pass

async def main():
    # Binding failure is an explicit failure, never a successful skipped acceptance.
    httpd=ThreadingHTTPServer(('127.0.0.1',5173),functools.partial(Quiet,directory=str(ROOT/'frontend')))
    worker=threading.Thread(target=httpd.serve_forever,daemon=True);worker.start()
    s=Server()
    try:
        s.start();token=s.login()
        s.sql("CREATE TRIGGER reject_audit BEFORE INSERT ON audit_events BEGIN SELECT RAISE(ABORT,'browser audit rejection'); END")
        assert s.call('/api/mode',{'mode':'Manual'},token)[0]==200
        native=s.call('/api/snapshot',token=token,origin='http://127.0.0.1:5173')
        async with async_playwright() as pw:
            browser=await pw.chromium.launch(headless=True)
            page=await browser.new_page()
            await page.goto('http://127.0.0.1:5173/index.html')
            result=await page.evaluate('''async ({base,token})=>{
                const r=await fetch(base+'/api/snapshot',{headers:{'X-Sim-Token':token}});
                return {status:r.status,auditStatus:r.headers.get('X-Audit-Status'),
                    commandId:r.headers.get('X-Command-Id'),traceId:r.headers.get('X-Trace-Id')};
            }''',{'base':s.base,'token':token})
            await browser.close()
        result.update({'nativeAuditStatus':native[2].get('X-Audit-Status'),
            'exposeHeaders':native[2].get('Access-Control-Expose-Headers'),
            'scope':'Real Chromium response-header visibility; not complete main UI usability acceptance'})
        (OUT/'browser-observations.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps(result,ensure_ascii=False))
    finally:
        s.close();httpd.shutdown();httpd.server_close();worker.join(timeout=3)

asyncio.run(main())
