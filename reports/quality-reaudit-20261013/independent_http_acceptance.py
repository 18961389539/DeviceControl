"""Root review: real HTTP against an isolated Release DLL, assertions unchanged by product fixes."""
import contextlib, hashlib, json, os, shutil, socket, sqlite3, subprocess, sys, tempfile, time, urllib.request, urllib.error
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
OUT=Path(__file__).resolve().parent
checks=[]

def checked(name, condition, evidence):
    checks.append({'name':name,'passed':bool(condition),'evidence':evidence})
    name_suffix=('-unknown' if '--unknown' in sys.argv else '')+('-combined' if '--combined' in sys.argv else '')
    (OUT/('independent-http-results'+name_suffix+'.json')).write_text(json.dumps(checks,ensure_ascii=False,indent=2),encoding='utf-8')
    assert condition,(name,evidence)

with tempfile.TemporaryDirectory(prefix='dc-root-acceptance-') as tmp:
    data=Path(tmp);(data/'config').mkdir()
    for file in ('station.json','station-B.json'):
        shutil.copy(ROOT/'backend/DeviceControl.Api/config'/file,data/'config'/file)
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
    base=f'http://127.0.0.1:{port}'
    log=(OUT/'independent-http-server.log').open('w',encoding='utf-8')
    proc=None;token=None;last_headers={}
    def call(path,body=None):
        req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),
            headers={'Content-Type':'application/json',**({'X-Sim-Token':token} if token else {})})
        try:
            with urllib.request.urlopen(req,timeout=12) as response:
                last_headers.clear();last_headers.update({key.lower():value for key,value in response.headers.items()})
                text=response.read().decode();return response.status,json.loads(text) if text else None
        except urllib.error.HTTPError as error:
            last_headers.clear();last_headers.update({key.lower():value for key,value in error.headers.items()})
            text=error.read().decode(errors='replace')
            try:payload=json.loads(text)
            except ValueError:payload=text
            return error.code,payload
    def stop():
        global proc
        if proc is not None:
            proc.terminate()
            try:proc.wait(timeout=10)
            except subprocess.TimeoutExpired:proc.kill();proc.wait()
            proc=None
    def boot():
        global proc,token
        token=None
        proc=subprocess.Popen(['dotnet',str(ROOT/'backend/DeviceControl.Api/bin/Release/net10.0/DeviceControl.Api.dll')],
            cwd=data,env={**os.environ,'SimListenUrl':base,'ASPNETCORE_CONTENTROOT':str(data)},stdout=log,stderr=subprocess.STDOUT)
        for _ in range(100):
            if proc.poll() is not None:raise AssertionError('server exited during isolated startup')
            try:
                if call('/api/health')[0]==200:break
            except OSError:pass
            time.sleep(.1)
        else:raise AssertionError('isolated startup timed out')
        code,row=call('/api/auth/login',{'username':'admin','password':'demo123'})
        assert code==200,(code,row)
        token=row['token']
    try:
        boot()
        code,row=call('/api/templates/capture',{'stationId':'A','name':'root recovery acceptance'})
        assert code==200,(code,row);tid=row['data']['id']
        assert call('/api/templates/'+tid+'/validate',{})[0]==200
        assert call('/api/templates/'+tid+'/publish',{})[0]==200
        code,row=call('/api/upgrades/preflight',{'templateId':tid,'stationId':'B'})
        assert code==200 and row['data']['ready'],(code,row)
        code,row=call('/api/upgrades/apply',{'planId':row['data']['id']})
        assert code==200,(code,row);record=row['data'];dep=record['id']
        stop()
        path=data/'data/deployments'/(dep+'.json')
        persisted=json.loads(path.read_text(encoding='utf-8'));persisted['Status']='InProgress'
        if '--unknown' in sys.argv:
            persisted['AfterFlowId']=None;persisted['AfterRecipeId']=None
        path.write_text(json.dumps(persisted,ensure_ascii=False),encoding='utf-8')
        if '--combined' in sys.argv:
            with contextlib.closing(sqlite3.connect(data/'data/stations/B/devicecontrol.db')) as db:
                db.execute("INSERT INTO recovery_state(singleton,required,reason,action,raised_utc) VALUES(1,1,'root recovery combination','root-test','2026-10-13T00:00:00Z') ON CONFLICT(singleton) DO UPDATE SET required=1,reason=excluded.reason")
                db.commit()
        boot()
        code,row=call('/api/stations/B/mode',{'mode':'Auto'})
        checked('N01 interrupted deployment blocks Auto',code==409,{'status':code,'body':row})
        rollback={'deploymentId':dep}
        if '--unknown' in sys.argv:
            code,row=call('/api/upgrades/rollback',rollback)
            checked('N01 unknown after-version requires human observation',code==409,{'status':code,'body':row})
            wrong={**rollback,'expectedCurrentFlowId':'wrong','expectedCurrentRecipeId':'wrong','recoveryNote':'root inspected software state'}
            code,row=call('/api/upgrades/rollback',wrong)
            checked('N01 incorrect human version observation is refused',code==409,{'status':code,'body':row})
            rollback.update(expectedCurrentFlowId=record['afterFlowId'],expectedCurrentRecipeId=record['afterRecipeId'],recoveryNote='root inspected versions and backup in isolated recovery acceptance')
        code,row=call('/api/upgrades/rollback',rollback)
        checked('N01 real changed-version deployment can be rolled back',code==200,{'status':code,'body':row})
        after=json.loads(path.read_text(encoding='utf-8'))
        checked('N01 rollback persists resolved status',after['Status']=='RolledBack',{'status':after['Status']})
        if '--unknown' in sys.argv:
            checked('N01 unknown result recovery retains human version evidence',after.get('RecoveryObservedBy')=='admin' and after.get('RecoveryObservedFlowId')==record['afterFlowId'] and after.get('RecoveryObservedRecipeId')==record['afterRecipeId'] and bool(after.get('RecoveryNote')),
                {key:value for key,value in after.items() if key.startswith('Recovery')})
        before_recipe=record['beforeRecipe']['values']
        active_recipe=call('/api/stations/B/recipes')[1]['active']['values']
        checked('N01 rollback restores recipe content',active_recipe==before_recipe,{'before':before_recipe,'after':active_recipe})
        if '--combined' in sys.argv:
            snapshot=call('/api/stations/B/snapshot')[1]
            checked('N01 configuration recovery preserves independent runtime recovery gate',snapshot['recoveryRequired'],snapshot['recoveryRequired'])
            config=call('/api/stations/B/config')[1]
            for axis in config['axes']:
                assert call('/api/stations/B/manual/axes/'+axis['id']+'/enable',{'enabled':True})[0]==200
                assert call('/api/stations/B/manual/axes/'+axis['id']+'/home',{})[0]==200
            code,row=call('/api/stations/B/recovery/confirm',{})
            checked('N01 independent human recovery can complete after deployment rollback',code==200,{'status':code,'body':row})
            stop();boot()
            snapshot=call('/api/stations/B/snapshot')[1]
            checked('N01 confirmed recovery stays cleared after restart',not snapshot['recoveryRequired'],snapshot['recoveryRequired'])
        if '--p1' in sys.argv:
            print('PASS independent root P1 HTTP acceptance:',len(checks),'checks')
            sys.exit(0)
        code,row=call('/api/recipes/draft',{'name':'root audit failure','visionOffsetMm':0,'extraDwellMs':0})
        assert code==200,(code,row);rid=row['data']['id']
        assert call('/api/recipes/'+rid+'/validate',{})[0]==200
        with contextlib.closing(sqlite3.connect(data/'data/devicecontrol.db')) as db:
            db.executescript("CREATE TRIGGER root_fail_recipe BEFORE INSERT ON recipe_revisions BEGIN SELECT RAISE(ABORT,'root acceptance refusal'); END;")
        code,row=call('/api/recipes/'+rid+'/publish',{})
        with contextlib.closing(sqlite3.connect(data/'data/devicecontrol.db')) as db:
            audit=db.execute('SELECT http_status,payload_json FROM audit_events WHERE path=?',('/api/recipes/'+rid+'/publish',)).fetchone()
            db.executescript('DROP TRIGGER root_fail_recipe;')
        audit_payload=json.loads(audit[1])
        checked('N07 actual failure audit matches HTTP',code==500 and audit[0]==500 and audit_payload['Outcome']!='Accepted',
            {'http':code,'auditStatus':audit[0],'auditOutcome':audit_payload['Outcome']})
        checked('N09 HTTP failure correlates command trace and persistent exception detail',
            audit_payload.get('CommandId')==last_headers.get('x-command-id') and bool(audit_payload.get('CommandId')) and
            audit_payload.get('TraceId')==last_headers.get('x-trace-id') and bool(audit_payload.get('ErrorDetail')),
            {'commandId':audit_payload.get('CommandId'),'traceId':audit_payload.get('TraceId'),'responseCommandId':last_headers.get('x-command-id'),
             'responseTraceId':last_headers.get('x-trace-id'),'exceptionDetailPresent':bool(audit_payload.get('ErrorDetail'))})
        code,row=call('/api/manual/axes/X/enable',{'enabled':True})
        assert code==200,(code,row)
        command_id=last_headers.get('x-command-id');trace_id=last_headers.get('x-trace-id')
        event_path=data/'data/run-events.jsonl';matching=[]
        for _ in range(50):
            if event_path.exists():
                lines=event_path.read_text(encoding='utf-8').splitlines()
                events=[]
                for line in lines:
                    try:events.append(json.loads(line))
                    except json.JSONDecodeError:pass  # an append may currently be in progress
                matching=[event for event in events if event.get('CommandId')==command_id and event.get('TraceId')==trace_id]
                if any('完成' in event.get('Message','') for event in matching):break
            time.sleep(.02)
        checked('N09 actual manual operation log correlates with HTTP command and trace',
            bool(command_id) and bool(trace_id) and any('完成' in event.get('Message','') for event in matching),
            {'commandId':command_id,'traceId':trace_id,'matchingEvents':matching})
        stop()
        (data/'data/rollouts').mkdir(exist_ok=True)
        (data/'data/rollouts'/('roll-'+'d'*32+'.json')).write_text('{',encoding='utf-8')
        boot()
        code,row=call('/api/diagnostics/stations')
        checked('N06 corrupt rollout preserves diagnostic API',code==200,{'status':code,'body':row})
        checked('N06 diagnostic identifies corrupt rollout','roll-'+'d'*32 in json.dumps(row),row)
        code,row=call('/api/ready')
        checked('N09 readiness rejects damaged rollout state',code==503,{'status':code,'body':row})
        # Use the real A database to verify long-history query after restart.
        with contextlib.closing(sqlite3.connect(data/'data/devicecontrol.db')) as db:
            for i in range(2100):
                payload={'Id':f'root-history-{i:04}','Time':'2026-01-01T00:00:00Z','Actor':'root-review','Role':'Admin',
                    'Method':'POST','Path':'/api/mode','Status':200,'TraceId':f'old-{i}','Outcome':'Accepted'}
                db.execute('INSERT INTO audit_events VALUES(?,?,?,?,?,?,?,?,?)',
                    (payload['Id'],payload['Time'],payload['Actor'],payload['Role'],payload['Method'],payload['Path'],200,payload['TraceId'],json.dumps(payload)))
            db.commit()
        stop();boot()
        code,row=call('/api/audit?page=22&pageSize=100')
        checked('N09 database audit pagination retains old history',code==200 and row['total']>2100 and bool(row['items']),
            {'status':code,'total':row.get('total'),'count':len(row.get('items',[]))})
        stop()
        catalog=data/'data/templates/catalog.json'
        catalog.write_text('{',encoding='utf-8')
        digest=hashlib.sha256(catalog.read_bytes()).hexdigest()
        boot()
        code,row=call('/api/diagnostics/stations')
        checked('N06 corrupt template preserves diagnostics',code==200 and 'catalog.json' in json.dumps(row),{'status':code,'body':row})
        code,row=call('/api/templates/capture',{'stationId':'A','name':'must reject damaged catalog'})
        checked('N06 damaged template blocks mutation and preserves evidence',code==409 and hashlib.sha256(catalog.read_bytes()).hexdigest()==digest,{'status':code,'body':row})
    finally:
        stop();log.close()
print('PASS independent root HTTP acceptance:',len(checks),'checks')
