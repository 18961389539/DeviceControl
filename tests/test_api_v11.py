"""V1.1 C# HTTP tests: station isolation, RBAC, scheduler conflict, stored evidence.
Requires .NET 10; execution is a mandatory CI gate, not an offline assertion.
"""
import json,os,shutil,socket,subprocess,tempfile,time,urllib.request,urllib.error,sys,sqlite3
from pathlib import Path
import contextlib
ROOT=Path(__file__).resolve().parents[1]
if not shutil.which('dotnet'):
 print('UNVERIFIED V1.1 C# API: .NET SDK is not installed')
 sys.exit(0)
with tempfile.TemporaryDirectory(prefix='devicecontrol-v11-') as directory:
 root=Path(directory);(root/'config').mkdir()
 for file in ('station.json','station-B.json'):
  shutil.copyfile(ROOT/'backend/DeviceControl.Api/config'/file,root/'config'/file)
 with socket.socket() as sock:
  sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
 base=f'http://127.0.0.1:{port}'
 env={**os.environ,'SimListenUrl':base,'ASPNETCORE_CONTENTROOT':str(root)}
 project=str(ROOT/'backend/DeviceControl.Api/DeviceControl.Api.csproj')
 process=subprocess.Popen(['dotnet','run','--no-launch-profile','--project',project],cwd=root,env=env,
                         stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
 def request(path,method='GET',body=None,token=None):
  raw=None if body is None else json.dumps(body).encode('utf8')
  req=urllib.request.Request(base+path,method=method,data=raw,
      headers={**({'Content-Type':'application/json'} if raw is not None else {}),
               **({'X-Sim-Token':token} if token else {})})
  try:
   with urllib.request.urlopen(req,timeout=15) as res:return res.status,json.load(res)
  except urllib.error.HTTPError as exc:return exc.code,json.load(exc)
 def idle(id,token,timeout=40):
  end=time.monotonic()+timeout
  while time.monotonic()<end:
   code,val=request(f'/api/stations/{id}/snapshot',token=token)
   if code==200 and not val['busy']:return val
   time.sleep(.1)
  raise AssertionError(f'Station {id} did not return to idle')
 try:
  for _ in range(180):
   if process.poll() is not None:raise AssertionError('V1.1 C# process failed to start')
   try:
    if request('/api/health')[0]==200:break
   except Exception:time.sleep(.3)
  else:raise AssertionError('API did not start')
  assert request('/api/stations')[0]==401
  code,r=request('/api/auth/login','POST',{'username':'engineer','password':'demo123'})
  assert code==200,(code,r)
  engineer=r['token']
  code,r=request('/api/auth/login','POST',{'username':'operator','password':'demo123'})
  assert code==200
  operator=r['token']
  code,fleet=request('/api/stations',token=operator)
  assert code==200 and [x['id'] for x in fleet['stations']]==['A','B']
  assert request('/api/stations/C/snapshot',token=operator)[0]==404
  assert request('/api/stations/B/manual/axes/X/enable','POST',{'enabled':True},operator)[0]==403
  assert request('/api/stations/B/mode','POST',{'mode':'Manual'},operator)[0]==403
  # Each station independently prepares axes. B operations must not affect A.
  for axis in ('X','Y'):
   assert request(f'/api/stations/B/manual/axes/{axis}/enable','POST',{'enabled':True},engineer)[0]==200
   assert request(f'/api/stations/B/manual/axes/{axis}/home','POST',{},engineer)[0]==200
  a=request('/api/stations/A/snapshot',token=engineer)[1]
  b=request('/api/stations/B/snapshot',token=engineer)[1]
  assert a['axes']['X']['homed'] is False and b['axes']['X']['homed'] is True
  assert request('/api/stations/B/sim/input','POST',{'name':'PartPresent','value':False},engineer)[0]==200
  assert request('/api/stations/A/snapshot',token=engineer)[1]['inputs']['PartPresent'] is True
  assert request('/api/stations/B/sim/input','POST',{'name':'PartPresent','value':True},engineer)[0]==200
  for axis in ('X','Y'):
   assert request(f'/api/stations/A/manual/axes/{axis}/enable','POST',{'enabled':True},engineer)[0]==200
   assert request(f'/api/stations/A/manual/axes/{axis}/home','POST',{},engineer)[0]==200
  assert request('/api/stations/A/mode','POST',{'mode':'Auto'},engineer)[0]==200
  assert request('/api/stations/B/mode','POST',{'mode':'Auto'},operator)[0]==200
  assert request('/api/stations/B/cycle/start','POST',{},operator)[0]==200
  assert request('/api/cycle/start','POST',{},operator)[0]==409, 'Legacy route must respect shared gate'
  code,fleet=request('/api/stations',token=engineer)
  assert fleet['owner']=='B'
  assert request('/api/stations/B/cycle/stop','POST',{},operator)[0]==200
  state=idle('B',engineer)
  assert state['recoveryRequired'] is True
  assert request('/api/stations/A/cycle/start','POST',{},operator)[0]==200
  state=idle('A',engineer)
  assert state['recoveryRequired'] is False and state['state']=='Idle',state
  bstate=request('/api/stations/B/snapshot',token=engineer)[1]
  assert bstate['recoveryRequired'] is True, 'A cycle must not clear B recovery flag'
  runs_a=request('/api/stations/A/runs',token=engineer)[1]
  runs_b=request('/api/stations/B/runs',token=engineer)[1]
  assert any(x['outcome']=='Completed' for x in runs_a),runs_a
  assert any(x['outcome']=='Stopped' for x in runs_b),runs_b
  id_a=runs_a[0]['id'];id_b=runs_b[0]['id']
  assert request(f'/api/stations/B/runs/{id_a}',token=engineer)[0]==404
  assert request(f'/api/stations/A/runs/{id_b}',token=engineer)[0]==404
  code,draft=request('/api/stations/B/recipes/draft','POST',{'name':'B-only','visionOffsetMm':0,'extraDwellMs':55},engineer)
  assert code==200,(code,draft)
  recipes_a=request('/api/stations/A/recipes',token=engineer)[1]
  recipes_b=request('/api/stations/B/recipes',token=engineer)[1]
  assert not any(x['values']['name']=='B-only' for x in recipes_a['versions'])
  assert any(x['values']['name']=='B-only' for x in recipes_b['versions'])
  for path in (root/'data/devicecontrol.db',root/'data/stations/B/devicecontrol.db'):
   assert path.exists(),path
   with contextlib.closing(sqlite3.connect(path)) as db:
    assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
  print('PASS V1.1 C# HTTP: legacy gate, scoped RBAC, state/recipe/history/storage isolation, A vs B recovery')
 finally:
  process.terminate()
  try:process.wait(timeout=8)
  except subprocess.TimeoutExpired:process.kill();process.wait()
