"""V1.4 .NET 10 integration contract; executes only if SDK is available.

Checks RBAC, version lifecycle, immutable deployment, data isolation and restart recovery.
Never report as passed if .NET SDK is missing.
"""
import json, os, shutil, socket, subprocess, sys, tempfile, time, urllib.error, urllib.request
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
if not shutil.which('dotnet'):
 print('UNVERIFIED V1.4 HTTP: .NET 10 SDK absent')
 sys.exit(0)
with tempfile.TemporaryDirectory(prefix='devicecontrol-v14-') as d:
 root=Path(d);(root/'config').mkdir()
 for name in ('station.json','station-B.json'):
  shutil.copy(ROOT/'backend/DeviceControl.Api/config'/name,root/'config'/name)
 with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
 base=f'http://127.0.0.1:{port}'
 env={**os.environ,'SimListenUrl':base,'ASPNETCORE_CONTENTROOT':str(root)}
 project=str(ROOT/'backend/DeviceControl.Api/DeviceControl.Api.csproj')
 def request(path,body=None,token=None):
  data=None if body is None else json.dumps(body).encode()
  req=urllib.request.Request(base+path,method='GET' if data is None else 'POST',data=data,
      headers={**({'X-Sim-Token':token} if token else {}),**({'Content-Type':'application/json'} if data else {})})
  try:
   with urllib.request.urlopen(req,timeout=15) as r:return r.status,json.load(r)
  except urllib.error.HTTPError as e:return e.code,json.load(e)
 def boot():
  proc=subprocess.Popen(['dotnet','run','--no-launch-profile','--project',project],cwd=root,env=env,
                         stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT)
  for _ in range(150):
   if proc.poll() is not None:raise AssertionError('V1.4 .NET process exited')
   try:
    if request('/api/health')[0]==200:return proc
   except Exception:pass
   time.sleep(.35)
  raise AssertionError('V1.4 API startup timeout')
 def stop(proc):
  proc.terminate()
  try:proc.wait(timeout=10)
  except subprocess.TimeoutExpired:proc.kill();proc.wait()
 def login(role):
  code,val=request('/api/auth/login',{'username':role,'password':'demo123'});assert code==200,(code,val)
  return val['token']
 proc=boot()
 try:
  assert request('/api/health')[1]['version'] in ('1.4','1.5','1.6','1.7','1.8','1.9','2.0-rc')
  engineer,admin,operator=login('engineer'),login('admin'),login('operator')
  assert request('/api/templates')[0]==401
  assert request('/api/templates',token=operator)[0]==200
  assert request('/api/templates/capture',{'stationId':'A','name':'test'},operator)[0]==403
  code,value=request('/api/templates/capture',{'stationId':'A','name':'3C fixture'},engineer)
  assert code==200,(code,value)
  rev=value['data']['id']
  assert request(f'/api/templates/{rev}/publish',{},admin)[0]==409
  assert request(f'/api/templates/{rev}/validate',{},engineer)[0]==200
  assert request(f'/api/templates/{rev}/publish',{},engineer)[0]==403
  assert request(f'/api/templates/{rev}/publish',{},admin)[0]==200
  assert request(f'/api/templates/{rev}/validate',{},engineer)[0]==409
  assert request(f'/api/templates/{rev}/diff/B',token=engineer)[0]==200
  assert request(f'/api/templates/{rev}/deploy',{'stationIds':['C','D']},engineer)[0]==403
  status,created=request(f'/api/templates/{rev}/deploy',{'stationIds':['C','D']},admin)
  assert status==200 and created['data']['ok'] is True and created['data']['created']==['C','D'],(status,created)
  fleet=request('/api/stations',token=engineer)[1]['stations']
  assert [x['id'] for x in fleet]==['A','B','C','D']
  for station in ('C','D'):
   assert request(f'/api/stations/{station}/flows',token=operator)[0]==200
   assert request(f'/api/stations/{station}/recipes',token=operator)[0]==200
   assert (root/'data'/'stations'/station/'devicecontrol.db').exists()
   assert (root/'config'/'managed-stations'/f'{station}.json').exists()
  assert request('/api/stations/C/sim/input',{'name':'PartPresent','value':False},engineer)[0]==200
  assert request('/api/stations/C/snapshot',token=operator)[1]['inputs']['PartPresent'] is False
  assert request('/api/stations/D/snapshot',token=operator)[1]['inputs']['PartPresent'] is True
  duplicate=request(f'/api/templates/{rev}/deploy',{'stationIds':['C']},admin)
  assert duplicate[0]==409
  path_attack=request(f'/api/templates/{rev}/deploy',{'stationIds':['../bad']},admin)
  assert path_attack[0]==409
  assert request(f'/api/templates/{rev}/deploy',{'stationIds':['E','E']},admin)[0]==409
  assert request('/api/stations/C/runs',token=operator)[0]==200
 finally:stop(proc)
 # A second startup reconstructs C/D from stored software profiles.
 proc=boot()
 try:
  admin=login('admin')
  fleet=request('/api/stations',token=admin)[1]['stations']
  assert [x['id'] for x in fleet]==['A','B','C','D']
  assert request('/api/stations/C/recipes',token=admin)[0]==200
  assert len(request('/api/templates',token=admin)[1])==1
  print('PASS V1.4 .NET HTTP: RBAC, publication gate, batch creation, isolation, duplicate/path rejection and restart persistence')
 finally:stop(proc)
