"""Fresh isolated API, no device action and no production data access."""
import contextlib,json,os,shutil,socket,sqlite3,subprocess,tempfile,time,urllib.request,urllib.error
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='dc-audit-status-') as tmp:
    data=Path(tmp);(data/'config').mkdir()
    for name in ('station.json','station-B.json'):
        shutil.copy(ROOT/'backend/DeviceControl.Api/config'/name,data/'config'/name)
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
    base=f'http://127.0.0.1:{port}'
    with (ROOT/'reports/quality-audit-20261013/http-audit-server.log').open('w',encoding='utf-8') as log:
        proc=subprocess.Popen(['dotnet',str(ROOT/'backend/DeviceControl.Api/bin/Release/net10.0/DeviceControl.Api.dll')],
            cwd=data,env={**os.environ,'SimListenUrl':base,'ASPNETCORE_CONTENTROOT':str(data)},stdout=log,stderr=subprocess.STDOUT)
        token=None
        def call(path,body=None):
            req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),
                headers={'Content-Type':'application/json',**({'X-Sim-Token':token} if token else {})})
            try:
                with urllib.request.urlopen(req,timeout=10) as r:return r.status,json.load(r)
            except urllib.error.HTTPError as e:
                return e.code,e.read().decode(errors='replace')
        try:
            for _ in range(80):
                try:
                    if call('/api/health')[0]==200:break
                except OSError:pass
                time.sleep(.15)
            else:raise RuntimeError('isolated server startup timeout')
            token=call('/api/auth/login',{'username':'admin','password':'demo123'})[1]['token']
            revision=call('/api/recipes/draft',{'name':'audit-failure','visionOffsetMm':0,'extraDwellMs':0})[1]['data']['id']
            assert call('/api/recipes/'+revision+'/validate',{})[0]==200
            db=data/'data/devicecontrol.db'
            with contextlib.closing(sqlite3.connect(db)) as c:
                c.executescript("CREATE TRIGGER audit_fail_recipe BEFORE INSERT ON recipe_revisions BEGIN SELECT RAISE(ABORT,'audit write failure'); END;")
            actual_status,_=call('/api/recipes/'+revision+'/publish',{})
            with contextlib.closing(sqlite3.connect(db)) as c:
                row=c.execute("SELECT http_status,payload_json FROM audit_events WHERE path=?",('/api/recipes/'+revision+'/publish',)).fetchone()
            print(json.dumps({'id':'N07','actualHttpStatus':actual_status,'storedAuditStatus':row[0],
                'storedAuditOutcome':json.loads(row[1])['Outcome']},indent=2))
        finally:
            proc.terminate()
            try:proc.wait(timeout=10)
            except subprocess.TimeoutExpired:proc.kill();proc.wait()
