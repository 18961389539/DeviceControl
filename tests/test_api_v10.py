"""V1.0 HTTP integration tests: completed persisted node evidence + unexpected exit.
Requires an installed .NET 10 SDK, local loopback, no hardware. CI MUST require dotnet.
"""
import json,os,shutil,socket,subprocess,tempfile,time,urllib.request,urllib.error,sys
from pathlib import Path
import contextlib
ROOT=Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api
if not shutil.which('dotnet'):
 print('SKIP UNVERIFIED: no .NET SDK; V1.0 API tests did NOT run')
 sys.exit(0)

def free_port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
with tempfile.TemporaryDirectory(prefix='devicecontrol-v10-') as folder:
 root=Path(folder);(root/'config').mkdir()
 shutil.copyfile(ROOT/'backend/DeviceControl.Api/config/station.json',root/'config/station.json')
 port=free_port();base=f'http://127.0.0.1:{port}'
 env={**os.environ,'SimListenUrl':base,'ASPNETCORE_CONTENTROOT':str(root)}
 project=str(ROOT/'backend/DeviceControl.Api/DeviceControl.Api.csproj')
 def req(path,method='GET',body=None,token=None):
  data=None if body is None else json.dumps(body).encode()
  r=urllib.request.Request(base+path,data=data,method=method,
      headers={**({'Content-Type':'application/json'} if data is not None else {}),
               **({'X-Sim-Token':token} if token else {})})
  try:
   with urllib.request.urlopen(r,timeout=5) as response:return response.status,json.load(response)
  except urllib.error.HTTPError as e:return e.code,json.load(e)
 def boot():
  p=start_api(root, env)
  for _ in range(180):
   if p.poll() is not None:raise RuntimeError('V1.0 API failed to launch')
   try:
    if req('/api/health')[0]==200:return p
   except Exception:time.sleep(.5)
  raise RuntimeError('Timeout waiting for V1.0 API')
 def wait_idle(token,timeout=35):
  deadline=time.monotonic()+timeout
  while time.monotonic()<deadline:
   _,s=req('/api/snapshot',token=token)
   if not s['busy'] and s['state'] in ('Idle','Fault'):return s
   time.sleep(.15)
  raise AssertionError('Cycle did not finish')
 process=None
 try:
  process=boot()
  assert req('/api/runs')[0]==401
  assert req('/api/runs/notfound',token='invalid')[0]==401
  s,user=req('/api/auth/login','POST',{'username':'engineer','password':'demo123'})
  assert s==200,(s,user)
  tok=user['token']
  for axis in ('X','Y'):
   status,response=req(f'/api/manual/axes/{axis}/enable','POST',{'enabled':True},tok)
   assert status==200,(axis,status,response)
   status,response=req(f'/api/manual/axes/{axis}/home','POST',{},tok)
   assert status==200,(axis,status,response)
  assert req('/api/mode','POST',{'mode':'Auto'},tok)[0]==200
  assert req('/api/cycle/start','POST',{},tok)[0]==200
  state=wait_idle(tok)
  assert state['state']=='Idle' and not state['recoveryRequired'],state
  status,runs=req('/api/runs',token=tok)
  assert status==200 and runs,(status,runs)
  completed=next(r for r in runs if r['outcome']=='Completed')
  assert len(completed['trace'])==len(completed['snapshots'])>2
  assert [x['ordinal'] for x in completed['snapshots']]==list(range(len(completed['trace'])))
  assert [x['nodeId'] for x in completed['snapshots']]==[x['id'] for x in completed['trace']]
  run_id=completed['id']
  assert req(f'/api/runs/{run_id}',token=tok)[1]['id']==run_id
  saved=req(f'/api/runs/{run_id}/snapshots',token=tok)[1]
  assert len(saved)==len(completed['trace'])
  assert all('inputs' in x and 'axes' in x and 'cylinders' in x for x in saved)
  assert req('/api/runs/nope',token=tok)[0]==404
  # First simulated run committed. Start another, interrupt the process without finalizers.
  assert req('/api/cycle/start','POST',{},tok)[0]==200
  process.kill();process.wait(timeout=8);process=None
  process=boot()
  s,new=req('/api/auth/login','POST',{'username':'engineer','password':'demo123'})
  assert s==200
  tok=new['token']
  s,station=req('/api/snapshot',token=tok)
  assert s==200 and station['recoveryRequired'] is True
  s,runs=req('/api/runs',token=tok)
  interrupted=[r for r in runs if r['outcome']=='Interrupted']
  assert len(interrupted)==1,runs
  # Contract: an Interrupted run has no node trace; its snapshots are UNAVAILABLE (null),
  # never an empty array that would imply "captured nothing". Missing stays missing.
  assert interrupted[0]['trace'] is None and interrupted[0]['snapshots'] is None
  assert req('/api/cycle/start','POST',{},tok)[0]!=200
  import sqlite3
  with contextlib.closing(sqlite3.connect(root/'data/devicecontrol.db')) as db:
   assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
   assert db.execute('SELECT count(*) FROM run_inflight').fetchone()[0]==0
   assert db.execute('SELECT count(*) FROM run_variable_snapshots WHERE run_id=?',(run_id,)).fetchone()[0]==len(saved)
  print('PASS V1.0 HTTP: completed cycle evidence, ordinal consistency, readonly API, kill/restart recovery, login & SQLite')
 finally:
  if process is not None:
   stop_api(process)
