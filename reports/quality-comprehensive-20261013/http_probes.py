"""Independent boundary observations; isolated roots only, no production writes.
Exit zero means observations completed, not product qualification.
"""
import json, os, shutil, socket, sqlite3, subprocess, sys, tempfile, time, urllib.request, urllib.error
from contextlib import closing
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'tests'))
from dotnet_harness import start_api, stop_api
OUT = Path(__file__).parent
observations = []

class Server:
    def __init__(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='dc-comprehensive-')
        self.root = Path(self.tmp.name)
        (self.root/'config').mkdir()
        for name in ('station.json','station-B.json'):
            shutil.copy(ROOT/'backend/DeviceControl.Api/config'/name, self.root/'config'/name)
        with socket.socket() as s:
            s.bind(('127.0.0.1',0)); self.base = f'http://127.0.0.1:{s.getsockname()[1]}'
        self.proc = None
    def start(self):
        self.proc = start_api(self.root, {**os.environ,'SimListenUrl':self.base,'ASPNETCORE_CONTENTROOT':str(self.root)})
        assert self.call('/api/health')[0] == 200
    def stop(self):
        if self.proc: stop_api(self.proc); self.proc = None
    def close(self):
        self.stop(); self.tmp.cleanup()
    def call(self,path,body=None,token=None,cid=None,origin=None):
        headers = {'Content-Type':'application/json'}
        if token: headers['X-Sim-Token']=token
        if cid: headers['X-Command-Id']=cid
        if origin: headers['Origin']=origin
        req = urllib.request.Request(self.base+path,data=json.dumps(body).encode() if body is not None else None,headers=headers)
        try:
            with urllib.request.urlopen(req,timeout=90) as r: return r.status,json.load(r),dict(r.headers)
        except urllib.error.HTTPError as r: return r.code,json.load(r),dict(r.headers)
    def login(self):
        code,row,_=self.call('/api/auth/login',{'username':'admin','password':'demo123'})
        assert code==200; return row['token']
    def sql(self,q):
        with closing(sqlite3.connect(self.root/'data/devicecontrol.db',timeout=15)) as c:
            rows=c.execute(q).fetchall();c.commit();return rows

def observe(name,value):
    observations.append({'name':name,**value})
    (OUT/'http-observations.json').write_text(json.dumps(observations,ensure_ascii=False,indent=2),encoding='utf-8')
    print(name, json.dumps(value,ensure_ascii=False),flush=True)

# Simultaneous audit DB rejection and unavailable spool; pending must not be hidden/lost.
s=Server()
try:
    s.start(); token=s.login()
    (s.root/'data/audit-pending.jsonl').mkdir() # deterministic append failure, not disk-full emulation
    s.sql("CREATE TRIGGER reject_audit BEFORE INSERT ON audit_events BEGIN SELECT RAISE(ABORT,'probe audit rejection'); END")
    code,_,headers=s.call('/api/auth/logout',{},token,'probe-lost-logout',origin='http://127.0.0.1:5173')
    token=s.login()
    _,lookup,_=s.call('/api/commands/probe-lost-logout',token=token)
    _,diag,_=s.call('/api/diagnostics/stations',token=token)
    before=diag['audit']
    s.stop(); s.sql('DROP TRIGGER reject_audit'); s.start(); token=s.login()
    _,after,_=s.call('/api/commands/probe-lost-logout',token=token)
    _,ready,_=s.call('/api/ready')
    observe('audit_dual_write_failure',{'writeStatus':code,'lookupBefore':lookup['data'],
        'auditBefore':before,'lookupAfter':after['data'],'readyAfter':ready['ready'],
        'exposedHeaders':headers.get('Access-Control-Expose-Headers'),
        'auditResponseHeader':headers.get('X-Audit-Status')})
finally:s.close()

# Large existing spool + new failed writes cause in-memory eviction; flush deletes whole spool.
s=Server()
try:
    s.start(); s.login(); s.stop()
    now='2026-10-13T13:00:00Z'
    with (s.root/'data/audit-pending.jsonl').open('w',encoding='utf-8') as f:
        for i in range(10000):
            f.write(json.dumps({'Id':f'probe-{i:05}','Time':now,'Actor':'admin','Role':'Admin',
                'Method':'POST','Path':'/api/mode','Status':200,'TraceId':'probe',
                'Outcome':'Accepted','CommandId':f'probe-spool-{i:05}','StationId':'A'})+'\n')
    s.sql("CREATE TRIGGER reject_audit BEFORE INSERT ON audit_events BEGIN SELECT RAISE(ABORT,'probe audit rejection'); END")
    s.start(); token=s.login() # evicts earliest pending row
    s.sql('DROP TRIGGER reject_audit')
    s.call('/api/mode',{'mode':'Manual'},token)
    count=s.sql("SELECT COUNT(*) FROM audit_events WHERE id LIKE 'probe-%'")[0][0]
    observe('audit_spool_eviction',{'seededRows':10000,'replayedRows':count,
        'firstRecordPersisted':bool(s.sql("SELECT id FROM audit_events WHERE id='probe-00000'")),
        'spoolExistsAfterFlush':(s.root/'data/audit-pending.jsonl').exists(),
        'auditAfter':s.call('/api/diagnostics/stations',token=token)[1]['audit']})
finally:s.close()

# Malformed managed filename is outside per-station isolation.
s=Server()
try:
    folder=s.root/'config/managed-stations';folder.mkdir()
    shutil.copy(s.root/'config/station-B.json',folder/'bad name.json')
    results={}
    try:
        s.start();token=s.login()
        for path in ('/api/health','/api/ready','/api/diagnostics/stations','/api/snapshot'):
            code,row,_=s.call(path,token=token);results[path]={'status':code,'body':row if code>=400 else {'ok':True}}
    except RuntimeError as ex:
        dll=ROOT/'backend/DeviceControl.Api/bin/Release/net10.0/DeviceControl.Api.dll'
        r=subprocess.run(['dotnet',str(dll)],cwd=s.root,env={**os.environ,'SimListenUrl':s.base,
            'ASPNETCORE_CONTENTROOT':str(s.root)},capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=15)
        (OUT/'managed-startup-error.txt').write_text(r.stdout+r.stderr,encoding='utf-8')
        results={'startupError':str(ex),'exitCode':r.returncode,'listenerAvailable':False,
            'log':'managed-startup-error.txt'}
    observe('managed_filename_isolation',results)
finally:s.close()

# Pause is outside the long-step interlock monitor; transient failure is not latched.
s=Server()
try:
    s.start();token=s.login()
    definition=[{'id':'delay','type':'Delay','label':'暂停后保持','durationMs':100}]
    code,row,_=s.call('/api/flows/draft',{'definition':definition},token);assert code==200
    fid=row['data']['id']
    assert s.call('/api/flows/'+fid+'/validate',{},token)[0]==200
    assert s.call('/api/flows/'+fid+'/publish',{},token)[0]==200
    for axis in ('X','Y'):
        assert s.call('/api/manual/axes/'+axis+'/enable',{'enabled':True},token)[0]==200
        assert s.call('/api/manual/axes/'+axis+'/home',{},token)[0]==200
    assert s.call('/api/mode',{'mode':'Auto'},token)[0]==200
    assert s.call('/api/flow/debug',{'command':'breakpoint','index':0,'enabled':True},token)[0]==200
    assert s.call('/api/cycle/start',{},token)[0]==200
    for _ in range(100):
        if s.call('/api/flow',token=token)[1]['status']=='Paused':break
        time.sleep(.02)
    s.call('/api/sim/input',{'name':'GuardClosed','value':False},token);time.sleep(.15)
    mid=s.call('/api/snapshot',token=token)[1]
    s.call('/api/sim/input',{'name':'GuardClosed','value':True},token)
    s.call('/api/flow/debug',{'command':'resume'},token)
    for _ in range(100):
        if not s.call('/api/snapshot',token=token)[1]['busy']:break
        time.sleep(.02)
    run=s.call('/api/runs',token=token)[1][0]
    observe('paused_interlock_loss',{'stateDuringLoss':mid['state'],
        'recoveryDuringLoss':mid['recoveryRequired'],'alarmDuringLoss':mid['alarm'],
        'guardDuringLoss':mid['inputs']['GuardClosed'],'outcomeAfterResume':run['outcome']})
finally:s.close()

print('OBSERVATIONS COMPLETE; not a product PASS',flush=True)
