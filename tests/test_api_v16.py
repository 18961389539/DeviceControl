"""V1.6 HTTP rollout smoke; intentionally requires actual .NET 10 API runtime.
Checks no-cycle quality gate, operator rights, crash-safe journal and restart.
Full completed-cycle behavior is additionally covered by offline Chromium.
"""
import json,os,shutil,socket,subprocess,sys,tempfile,time,urllib.request,urllib.error
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api
if not shutil.which('dotnet'):
 print('UNVERIFIED V1.6 HTTP: .NET 10 SDK absent')
 sys.exit(0)
with tempfile.TemporaryDirectory(prefix='dc-v16-') as folder:
 root=Path(folder);(root/'config').mkdir()
 for name in ['station.json','station-B.json']:
  shutil.copy(ROOT/'backend/DeviceControl.Api/config'/name,root/'config'/name)
 with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
 base=f'http://127.0.0.1:{port}'
 env={**os.environ,'SimListenUrl':base,'ASPNETCORE_CONTENTROOT':str(root)}
 project=str(ROOT/'backend/DeviceControl.Api/DeviceControl.Api.csproj')
 def request(path,body=None,token=None):
  data=None if body is None else json.dumps(body).encode()
  req=urllib.request.Request(base+path,data=data,method='GET' if body is None else 'POST',
    headers={**({'Content-Type':'application/json'} if body is not None else {}),**({'X-Sim-Token':token} if token else {})})
  try:
   with urllib.request.urlopen(req,timeout=12) as r:return r.status,json.load(r)
  except urllib.error.HTTPError as e:return e.code,json.load(e)
 def boot():
  proc=start_api(root, env)
  for _ in range(160):
   if proc.poll() is not None:raise RuntimeError('V1.6 .NET startup failed')
   try:
    if request('/api/health')[0]==200:return proc
   except Exception:pass
   time.sleep(.3)
  raise RuntimeError('V1.6 .NET startup timeout')
 def stop(p):
  stop_api(p)
 def login(who):
  status,r=request('/api/auth/login',{'username':who,'password':'demo123'})
  assert status==200,(status,r)
  return r['token']
 p=boot()
 try:
  assert request('/api/health')[1]['version'] in ('1.7','1.8','1.9','2.0-rc')
  admin,engineer,operator=login('admin'),login('engineer'),login('operator')
  status,r=request('/api/templates/capture',{'stationId':'A','name':'V16 fixture'},engineer)
  assert status==200,(status,r);tmpl=r['data']['id']
  assert request('/api/templates/'+tmpl+'/validate',{},engineer)[0]==200
  assert request('/api/templates/'+tmpl+'/publish',{},admin)[0]==200
  assert request('/api/rollouts',token=operator)[0]==403
  assert request('/api/rollouts',token=engineer)[0]==200
  body={'templateId':tmpl,'pilotStationId':'A','stationIds':['A','B'],'waveSize':2}
  assert request('/api/rollouts',body,engineer)[0]==403
  status,r=request('/api/rollouts',body,admin)
  assert status==200,(status,r);roll=r['data']['id'];assert r['data']['status']=='PilotPending'
  assert request('/api/rollouts',body,admin)[0]==409
  assert request('/api/rollouts/'+roll+'/advance',{},engineer)[0]==403
  status,r=request('/api/rollouts/'+roll+'/advance',{},admin)
  assert status==200,(status,r);assert r['data']['status']=='AwaitingEvidence',r
  assert request('/api/rollouts/'+roll+'/verify',{},admin)[0]==409 # no new completed cycle
  status,r=request('/api/rollouts/'+roll+'/evidence',token=engineer)
  assert status==200 and not r['data']['pass'],(status,r)
  assert request('/api/rollouts/'+roll+'/advance',{},admin)[0]==409 # pilot evidence required
  assert request('/api/rollouts/'+roll+'/pause',{'reason':'check'},admin)[1]['data']['status']=='Paused'
  assert request('/api/rollouts/'+roll+'/resume',{},admin)[1]['data']['status']=='AwaitingEvidence'
  assert request('/api/rollouts/'+roll+'/abort',{'reason':'no cycle evidence'},admin)[1]['data']['status']=='Aborted'
  assert request('/api/rollouts/'+roll+'/advance',{},admin)[0]==409
  assert (root/'data'/'rollouts'/(roll+'.json')).exists()
 finally:stop(p)
 p=boot()
 try:
  admin=login('admin')
  code,r=request('/api/rollouts',token=admin)
  assert code==200 and len(r['data'])==1 and r['data'][0]['status']=='Aborted',(code,r)
  print('PASS V1.6 .NET HTTP: rollout RBAC, pilot evidence gate, pause/resume/abort, durable restart')
 finally:stop(p)
