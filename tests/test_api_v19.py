"""V1.9 real .NET HTTP contract: temporary SQLite recovery sandbox, RBAC, tamper, no writes.
Must run in CI with .NET 10 SDK. Without it, explicit UNVERIFIED output, not PASS.
"""
import hashlib,json,os,shutil,socket,subprocess,sys,tempfile,time,urllib.request,urllib.error
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api
if not shutil.which('dotnet'):
 print('UNVERIFIED V1.9 HTTP: .NET 10 SDK absent');sys.exit(0)
with tempfile.TemporaryDirectory(prefix='dc-v19-http-') as temp:
 data=Path(temp);(data/'config').mkdir()
 for cfg in ('station.json','station-B.json'):
  shutil.copy(ROOT/'backend/DeviceControl.Api/config'/cfg,data/'config'/cfg)
 with socket.socket() as s:
  s.bind(('127.0.0.1',0));port=s.getsockname()[1]
 base=f'http://127.0.0.1:{port}';env={**os.environ,'SimListenUrl':base,'ASPNETCORE_CONTENTROOT':str(data)}
 project=str(ROOT/'backend/DeviceControl.Api/DeviceControl.Api.csproj')
 def call(path,body=None,token=None):
  payload=None if body is None else json.dumps(body).encode()
  req=urllib.request.Request(base+path, data=payload, method='POST' if body is not None else 'GET',
    headers={**({'Content-Type':'application/json'} if body is not None else {}),
             **({'X-Sim-Token':token} if token else {})})
  try:
   with urllib.request.urlopen(req,timeout=15) as res:return res.status,json.load(res)
  except urllib.error.HTTPError as e:return e.code,json.load(e)
 def login(role):
  code,row=call('/api/auth/login',{'username':role,'password':'demo123'})
  assert code==200,(code,row);return row['token']
 proc=start_api(data, env)
 try:
  for _ in range(180):
   if proc.poll() is not None:raise AssertionError('server exited before healthy')
   try:
    if call('/api/health')[0]==200:break
   except Exception:pass
   time.sleep(.3)
  else:raise AssertionError('HTTP health timed out')
  assert call('/api/health')[1]['version']=='2.0-rc'
  admin=login('admin');engineer=login('engineer');operator=login('operator')
  code,row=call('/api/templates/capture',{'stationId':'A','name':'V19 test'},engineer)
  assert code==200,(code,row);tid=row['data']['id']
  assert call('/api/templates/'+tid+'/validate',{},engineer)[0]==200
  assert call('/api/templates/'+tid+'/publish',{},admin)[0]==200
  code,plan=call('/api/upgrades/preflight',{'templateId':tid,'stationId':'B'},engineer)
  assert code==200 and plan['data']['ready'],(code,plan)
  code,dep=call('/api/upgrades/apply',{'planId':plan['data']['id']},admin)
  assert code==200,(code,dep);rec=dep['data'];rid=rec['id']
  path='/api/upgrades/'+rid+'/sandbox'
  assert call(path,token=operator)[0]==403
  assert call(path)[0]==401
  snap=data/'data/stations/B/devicecontrol.db';back=data/'data/deployments'/rec['backupFile'];journal=data/'data/deployments'/(rid+'.json')
  before=(hashlib.sha256(snap.read_bytes()).hexdigest(),hashlib.sha256(back.read_bytes()).hexdigest(),journal.read_bytes())
  code,rep=call(path,token=engineer)
  assert code==200,(code,rep);report=rep['data']
  assert report['backupValidated'] and report['readOnly'] and not report['writesProduction'] and report['requiresManualReview'],report
  assert {x['code'] for x in report['checks']} >= {'backup-path','backup-size','sha256','sqlite-integrity','baseline'}
  assert not list((data/'data/recovery-sandboxes').glob('*'))
  after=(hashlib.sha256(snap.read_bytes()).hexdigest(),hashlib.sha256(back.read_bytes()).hexdigest(),journal.read_bytes())
  assert before==after,'Sandbox inspection modified live files'
  with back.open('ab') as f:f.write(b'TAMPER')
  code,bad=call(path,token=engineer)
  assert code==200 and not bad['data']['backupValidated'] and any(x['code']=='sha256' and not x['passed'] for x in bad['data']['checks']),(code,bad)
  assert call('/api/upgrades/not-an-id/sandbox',token=engineer)[0] in (400,404,409)
  print('PASS V1.9 .NET HTTP sandbox: RBAC, verified copy, no mutations, tamper rejection, invalid ID')
 finally:
  stop_api(proc)
