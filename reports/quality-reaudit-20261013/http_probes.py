"""Read-only product review using isolated data roots and fresh Release DLL processes.
Observations intentionally include defects; process success is not product approval.
"""
import contextlib,json,os,shutil,socket,sqlite3,subprocess,tempfile,time,urllib.request,urllib.error,threading
from http.server import ThreadingHTTPServer,BaseHTTPRequestHandler
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
OUT=Path(__file__).resolve().parent
observations=[]

class Server:
    def __init__(self,root):
        self.root=root;self.token=None;self.proc=None
        with socket.socket() as s:s.bind(('127.0.0.1',0));self.port=s.getsockname()[1]
        self.base=f'http://127.0.0.1:{self.port}'
        self.log=(OUT/(root.name+'.server.log')).open('w',encoding='utf-8')
    def call(self,path,body=None):
        req=urllib.request.Request(self.base+path,data=None if body is None else json.dumps(body).encode(),
            headers={'Content-Type':'application/json',**({'X-Sim-Token':self.token} if self.token else {})})
        try:
            with urllib.request.urlopen(req,timeout=10) as response:return response.status,json.load(response)
        except urllib.error.HTTPError as error:
            content=error.read().decode(errors='replace')
            try:content=json.loads(content)
            except ValueError:pass
            return error.code,content
    def start(self):
        self.token=None
        self.proc=subprocess.Popen(['dotnet',str(ROOT/'backend/DeviceControl.Api/bin/Release/net10.0/DeviceControl.Api.dll')],
            cwd=self.root,env={**os.environ,'SimListenUrl':self.base,'ASPNETCORE_CONTENTROOT':str(self.root)},stdout=self.log,stderr=subprocess.STDOUT)
        for _ in range(150):
            if self.proc.poll() is not None:return False
            try:
                if self.call('/api/health')[0]==200:
                    code,row=self.call('/api/auth/login',{'username':'admin','password':'demo123'})
                    assert code==200,(code,row);self.token=row['token'];return True
            except OSError:pass
            time.sleep(.05)
        raise RuntimeError('isolated startup deadline exceeded')
    def stop(self):
        if self.proc is not None:
            self.proc.terminate()
            try:self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:self.proc.kill();self.proc.wait()
            self.proc=None
    def close(self):self.stop();self.log.close()

def setup(root):
    (root/'config').mkdir(parents=True)
    for name in ('station.json','station-B.json'):
        shutil.copy(ROOT/'backend/DeviceControl.Api/config'/name,root/'config'/name)
def observe(row):
    observations.append(row);(OUT/'http-observations.json').write_text(json.dumps(observations,ensure_ascii=False,indent=2),encoding='utf-8')
def data(server,path,body=None):
    code,row=server.call(path,body);assert code==200,(path,code,row)
    return row.get('data',row)

with tempfile.TemporaryDirectory(prefix='dc-reaudit-') as temp:
    root=Path(temp)/'cross-module';setup(root);server=Server(root)
    try:
        assert server.start()
        template=data(server,'/api/templates/capture',{'stationId':'A','name':'reaudit template'})['id']
        data(server,'/api/templates/'+template+'/validate',{})
        data(server,'/api/templates/'+template+'/publish',{})
        rollout=data(server,'/api/rollouts',{'templateId':template,'pilotStationId':'B','stationIds':['B','A'],'waveSize':1})
        rid=rollout['id'];baseline=rollout['steps'][0]
        advanced=data(server,'/api/rollouts/'+rid+'/advance',{})
        dep=advanced['steps'][0]['deploymentId']
        record=next(x for x in data(server,'/api/upgrades/history') if x['id']==dep)
        server.stop()
        rollout_path=root/'data/rollouts'/(rid+'.json')
        persisted=json.loads(rollout_path.read_text(encoding='utf-8'))
        persisted['Status']='Deploying';persisted['Steps'][0]['Status']='Applying'
        rollout_path.write_text(json.dumps(persisted),encoding='utf-8')
        dep_path=root/'data/deployments'/(dep+'.json')
        persisted=json.loads(dep_path.read_text(encoding='utf-8'));persisted['Status']='InProgress'
        dep_path.write_text(json.dumps(persisted),encoding='utf-8')
        assert server.start()
        restored=data(server,'/api/upgrades/rollback',{'deploymentId':dep})
        active_flow=data(server,'/api/stations/B/flows')['active']
        active_recipe=data(server,'/api/stations/B/recipes')['active']
        code,row=server.call('/api/rollouts/'+rid+'/reconcile',{'stationId':'B','note':'已完成单站回滚并核对原始流程配方内容'})
        observe({'id':'R01','rollbackStatus':restored['status'],'flowContentRestored':active_flow['definition']==record['beforeFlow']['definition'],
            'recipeContentRestored':active_recipe['values']==record['beforeRecipe']['values'],'originalFlowId':baseline['expectedFlowId'],
            'currentFlowId':active_flow['id'],'originalRecipeId':baseline['expectedRecipeId'],'currentRecipeId':active_recipe['id'],
            'reconcileHttpStatus':code,'reconcileResponse':row})
        abort_code,_=server.call('/api/rollouts/'+rid+'/abort',{'reason':'单站恢复已完成，关闭旧基线批次后重建'})
        new_code,new_batch=server.call('/api/rollouts',{'templateId':template,'pilotStationId':'B','stationIds':['B','A'],'waveSize':1})
        observe({'id':'R01-alternative','abortHttpStatus':abort_code,'newBatchHttpStatus':new_code,
            'newBatchUsesRestoredFlowId':new_code==200 and new_batch['data']['steps'][0]['expectedFlowId']==active_flow['id']})
        # A valid single Delay flow has no following node to recheck ordinary conditions.
        draft=data(server,'/api/flows/draft',{'definition':[{'id':'terminal-dwell','type':'Delay','label':'末尾工艺等待','durationMs':500}]})
        data(server,'/api/flows/'+draft['id']+'/validate',{})
        data(server,'/api/flows/'+draft['id']+'/publish',{})
        cfg=data(server,'/api/config')
        for axis in cfg['axes']:
            data(server,'/api/manual/axes/'+axis['id']+'/enable',{'enabled':True})
            data(server,'/api/manual/axes/'+axis['id']+'/home',{})
        data(server,'/api/mode',{'mode':'Auto'})
        data(server,'/api/cycle/start',{})
        time.sleep(.08)
        data(server,'/api/sim/input',{'name':'GuardClosed','value':False})
        for _ in range(100):
            snapshot=data(server,'/api/snapshot')
            if not snapshot['busy']:break
            time.sleep(.02)
        observe({'id':'R02','guardClosed':snapshot['inputs']['GuardClosed'],'cycleOutcome':snapshot['cycleHistory'][0]['outcome'],
            'state':snapshot['state'],'recoveryRequired':snapshot['recoveryRequired'],'completedCounter':snapshot['cycles']})
        data(server,'/api/sim/input',{'name':'GuardClosed','value':True})
        # A completed mutation remains accepted when its mandatory audit insert is refused.
        db_path=root/'data/devicecontrol.db'
        with contextlib.closing(sqlite3.connect(db_path)) as db:
            db.executescript("CREATE TRIGGER reaudit_reject_audit BEFORE INSERT ON audit_events BEGIN SELECT RAISE(ABORT,'reaudit audit write refusal'); END;")
            before=db.execute('SELECT COUNT(*) FROM audit_events').fetchone()[0]
        code,response=server.call('/api/mode',{'mode':'Manual'})
        ready_code,ready=server.call('/api/ready')
        diagnostic=data(server,'/api/diagnostics/stations')
        with contextlib.closing(sqlite3.connect(db_path)) as db:
            after=db.execute('SELECT COUNT(*) FROM audit_events').fetchone()[0]
            db.executescript('DROP TRIGGER reaudit_reject_audit;')
        observe({'id':'R04','mutationHttpStatus':code,'actualMode':data(server,'/api/snapshot')['mode'],
            'auditRowsAdded':after-before,'readyHttpStatus':ready_code,'ready':ready.get('ready'),
            'diagnostics':diagnostic})
        # Target B is encoded in business records but global HTTP audit is attributed to A.
        a_audit=data(server,'/api/stations/A/audit?pageSize=100')['items']
        b_audit=data(server,'/api/stations/B/audit?pageSize=100')['items']
        observe({'id':'R05','targetStation':'B','globalApplyEntriesInA':sum(x['path']=='/api/upgrades/apply' for x in a_audit),
            'globalRollbackEntriesInA':sum(x['path']=='/api/upgrades/rollback' for x in a_audit),
            'targetBHasRollbackAudit':any(x['path']=='/api/upgrades/rollback' for x in b_audit),
            'targetBHasRolloutAdvanceAudit':any(x['path'].endswith('/advance') for x in b_audit)})
        # Actual Chromium CORS semantics differ from Node fetch/header inspection.
        from playwright.sync_api import sync_playwright
        class FrontPage(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200);self.send_header('Content-Type','text/html');self.end_headers()
                self.wfile.write(b'<html><body>isolated client probe</body></html>')
            def log_message(self,*args):pass
        front=ThreadingHTTPServer(('127.0.0.1',5173),FrontPage)
        front_thread=threading.Thread(target=front.serve_forever,daemon=True);front_thread.start()
        with sync_playwright() as pw:
            browser=pw.chromium.launch(headless=True)
            page=browser.new_page()
            browser_messages=[]
            page.on('console',lambda message:browser_messages.append(message.text))
            page.goto('http://127.0.0.1:5173/reaudit-client')
            page.add_script_tag(content=(ROOT/'frontend/api-client.js').read_text(encoding='utf-8'))
            try:
                result=page.evaluate('''async ({root,token}) => {
                const api=window.DeviceControlApiClient.create({root});
                const response=await api('/api/mode',{method:'POST',headers:{'Content-Type':'application/json','X-Sim-Token':token},body:JSON.stringify({mode:'Manual'})});
                return {httpStatus:response.status,traceHeader:response.headers.get('X-Trace-Id'),commandHeader:response.headers.get('X-Command-Id'),body:await response.json()};
                }''',{'root':server.base,'token':server.token})
                observe({'id':'R08','frontendOrigin':'http://127.0.0.1:5173',**result})
            except Exception as exc:
                observe({'id':'browser-probe-diagnostic','error':str(exc),'console':browser_messages})
            browser.close()
        front.shutdown();front.server_close();front_thread.join(timeout=3)
    finally:server.close()
    root=Path(temp)/'corrupt-fixed-B';setup(root)
    (root/'config/station-B.json').write_text('{',encoding='utf-8')
    server=Server(root)
    try:
        healthy=server.start()
        observe({'id':'R06','onlyStationBCorrupted':True,'healthAccessible':healthy,'processExitCode':server.proc.poll()})
    finally:server.close()
print(json.dumps(observations,ensure_ascii=False,indent=2))
