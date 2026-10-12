"""V1.5 end-to-end API checks (requires .NET SDK): role gates, plan token,
idle/manual requirements, versioned flow/recipe deployment, rollback and restart.
Skip is reported explicitly as UNVERIFIED, never PASS.
"""
import json,os,sqlite3,shutil,socket,subprocess,sys,tempfile,time,urllib.request,urllib.error
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
if not shutil.which('dotnet'):
 print('UNVERIFIED V1.5 HTTP: .NET 10 SDK absent')
 sys.exit(0)
with tempfile.TemporaryDirectory(prefix='dc-v15-') as directory:
 root=Path(directory);(root/'config').mkdir()
 for name in ['station.json','station-B.json']:
  shutil.copy(ROOT/'backend/DeviceControl.Api/config'/name,root/'config'/name)
 with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
 url=f'http://127.0.0.1:{port}'
 env={**os.environ,'SimListenUrl':url,'ASPNETCORE_CONTENTROOT':str(root)}
 project=str(ROOT/'backend/DeviceControl.Api/DeviceControl.Api.csproj')
 def call(path,payload=None,token=None):
  data=None if payload is None else json.dumps(payload).encode()
  req=urllib.request.Request(url+path,method='POST' if data is not None else 'GET',data=data,
      headers={**({'X-Sim-Token':token} if token else {}),**({'Content-Type':'application/json'} if data is not None else {})})
  try:
   with urllib.request.urlopen(req,timeout=10) as r:return r.status,json.load(r)
  except urllib.error.HTTPError as e:return e.code,json.load(e)
 def boot():
  p=subprocess.Popen(['dotnet','run','--no-launch-profile','--project',project],cwd=root,env=env,stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
  for _ in range(160):
   if p.poll() is not None:raise AssertionError('V1.5 API startup failed')
   try:
    if call('/api/health')[0]==200:return p
   except Exception:pass
   time.sleep(.3)
  raise AssertionError('V1.5 startup timeout')
 def halt(p):
  p.terminate()
  try:p.wait(timeout=10)
  except subprocess.TimeoutExpired:p.kill();p.wait()
 def login(name):
  code,resp=call('/api/auth/login',{'username':name,'password':'demo123'})
  assert code==200,(code,resp)
  return resp['token']
 proc=boot()
 try:
  assert call('/api/health')[1]['version']in ('1.5','1.6','1.7','1.8','1.9','2.0-rc')
  admin=login('admin');engineer=login('engineer');operator=login('operator')
  code,r=call('/api/templates/capture',{'stationId':'A','name':'V15 template'},engineer)
  assert code==200,(code,r);tmpl=r['data']['id']
  assert call(f'/api/templates/{tmpl}/validate',{},engineer)[0]==200
  assert call(f'/api/templates/{tmpl}/publish',{},admin)[0]==200
  assert call('/api/upgrades/preflight',{'templateId':tmpl,'stationId':'B'},operator)[0]==403
  assert call('/api/upgrades/history',token=operator)[0]==403
  assert call('/api/upgrades/apply',{'planId':'bad'},engineer)[0]==403
  assert call('/api/stations/B/mode',{'mode':'Auto'},engineer)[0]==200
  code,r=call('/api/upgrades/preflight',{'templateId':tmpl,'stationId':'B'},engineer)
  assert code==200 and not r['data']['ready'] and any('手动模式' in v for v in r['data']['blockers']),(code,r)
  assert call('/api/upgrades/apply',{'planId':r['data']['id']},admin)[0]==409
  assert call('/api/stations/B/mode',{'mode':'Manual'},engineer)[0]==200
  code,r=call('/api/upgrades/preflight',{'templateId':tmpl,'stationId':'B'},engineer)
  assert code==200 and r['data']['ready'],(code,r);plan=r['data']['id']
  code,older=call('/api/upgrades/preflight',{'templateId':tmpl,'stationId':'B'},engineer)
  assert code==200 and older['data']['ready'];stale_plan=older['data']['id']
  old_flow=call('/api/stations/B/flows',token=admin)[1]['active']['id']
  old_recipe=call('/api/stations/B/recipes',token=admin)[1]['active']['id']
  assert call('/api/upgrades/apply',{'planId':plan},engineer)[0]==403
  code,r=call('/api/upgrades/apply',{'planId':plan},admin)
  assert code==200 and r['data']['status']=='Succeeded',(code,r)
  record=r['data'];deployment=record['id']
  assert record['beforeFlow']['id']==old_flow and record['beforeRecipe']['id']==old_recipe
  assert call('/api/stations/B/flows',token=admin)[1]['active']['id']!=old_flow
  assert call('/api/stations/B/recipes',token=admin)[1]['active']['id']!=old_recipe
  assert call('/api/upgrades/apply',{'planId':plan},admin)[0]==409
  assert call('/api/upgrades/apply',{'planId':stale_plan},admin)[0]==409
  assert call('/api/upgrades/rollback',{'deploymentId':deployment},engineer)[0]==403
  code,r=call('/api/upgrades/rollback',{'deploymentId':deployment},admin)
  assert code==200 and r['data']['status']=='RolledBack',(code,r)
  assert call('/api/upgrades/rollback',{'deploymentId':deployment},admin)[0]==409
  assert call('/api/stations/A/flows',token=admin)[1]['active'] is not None
  history=call('/api/upgrades/history?stationId=B',token=admin)[1]['data']
  assert len(history)==1 and history[0]['status']=='RolledBack'
  assert (root/'data'/'deployments'/(deployment+'.json')).exists()
  backup=root/'data'/'deployments'/'backups'/'B'/(deployment+'.db')
  assert backup.exists() and backup.stat().st_size>0
  with sqlite3.connect(backup) as db:assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
 finally:halt(proc)
 # Durable deployment evidence must survive a new process.
 proc=boot()
 try:
  admin=login('admin')
  history=call('/api/upgrades/history?stationId=B',token=admin)[1]['data']
  assert len(history)==1 and history[0]['status']=='RolledBack'
  print('PASS V1.5 .NET HTTP: RBAC, mode gate, version publication, replay resistance, rollback, durable history')
 finally:halt(proc)
