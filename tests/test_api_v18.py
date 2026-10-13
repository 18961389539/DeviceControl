"""V1.8 actual .NET 10 HTTP restart test. Cannot be replaced by static inspection.
Creates a real successful deployment and real SQLite backup, then simulates power loss
by changing only persisted journal states while the service is down.
"""
import json,os,shutil,socket,subprocess,sys,tempfile,time,urllib.request,urllib.error
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api
if not shutil.which('dotnet'):
 print('UNVERIFIED V1.8 HTTP: .NET 10 SDK absent');sys.exit(0)
with tempfile.TemporaryDirectory(prefix='dc-v18-') as tmp:
 root=Path(tmp);(root/'config').mkdir()
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
   with urllib.request.urlopen(req,timeout=12) as res:return res.status,json.load(res)
  except urllib.error.HTTPError as e:return e.code,json.load(e)
 def boot():
  proc=start_api(root, env)
  for _ in range(160):
   if proc.poll() is not None:raise RuntimeError('V1.8 API process failed to start')
   try:
    if request('/api/health')[0]==200:return proc
   except Exception:pass
   time.sleep(.3)
  raise RuntimeError('V1.8 API startup timeout')
 def stop(proc):
  stop_api(proc)
 def login(name):
  code,row=request('/api/auth/login',{'username':name,'password':'demo123'})
  assert code==200,(code,row);return row['token']
 service=boot()
 try:
  assert request('/api/health')[1]['version'] in ('1.8','1.9','2.0-rc')
  admin=login('admin');engineer=login('engineer');operator=login('operator')
  code,template=request('/api/templates/capture',{'stationId':'A','name':'V18 restart fixture'},engineer)
  assert code==200,(code,template);template_id=template['data']['id']
  assert request('/api/templates/'+template_id+'/validate',{},engineer)[0]==200
  assert request('/api/templates/'+template_id+'/publish',{},admin)[0]==200
  code,plan=request('/api/upgrades/preflight',{'templateId':template_id,'stationId':'A'},engineer)
  assert code==200 and plan['data']['ready'],(code,plan)
  code,deploy=request('/api/upgrades/apply',{'planId':plan['data']['id']},admin)
  assert code==200,(code,deploy);deployment_id=deploy['data']['id']
  code,backup=request('/api/upgrades/'+deployment_id+'/integrity',token=engineer)
  assert code==200 and backup['data']['status']=='Verified',(code,backup)
  assert request('/api/upgrades/'+deployment_id+'/integrity',token=operator)[0]==403
  code,roll=request('/api/rollouts',{'templateId':template_id,'pilotStationId':'B','stationIds':['A','B'],'waveSize':1},admin)
  assert code==200,(code,roll);roll_id=roll['data']['id']
  assert request('/api/rollouts/'+roll_id+'/recovery-report',token=operator)[0]==403
 finally:stop(service)
 # Simulate an interrupted write, not a second HTTP action. Test has no live service now.
 journal=root/'data/rollouts'/(roll_id+'.json');row=json.loads(journal.read_text(encoding='utf-8'))
 row['Status']='Deploying';row['Steps'][0]['Status']='Applying'
 journal.write_text(json.dumps(row))
 depfile=root/'data/deployments'/(deployment_id+'.json');dep=json.loads(depfile.read_text(encoding='utf-8'))
 dep['Status']='InProgress';depfile.write_text(json.dumps(dep))
 service=boot()
 try:
  engineer=login('engineer');admin=login('admin')
  code,roll=request('/api/rollouts/'+roll_id,token=engineer)
  assert code==200 and roll['data']['status']=='Interrupted',(code,roll)
  assert json.loads(journal.read_text(encoding='utf-8'))['Status']=='Interrupted','restart marker not persisted'
  assert json.loads(depfile.read_text(encoding='utf-8'))['Status']=='Interrupted','deployment marker not persisted'
  code,rep=request('/api/rollouts/'+roll_id+'/recovery-report',token=engineer)
  assert code==200 and rep['data']['readOnly'] and rep['data']['requiresManualReview'],(code,rep)
  assert request('/api/rollouts/'+roll_id+'/advance',{},admin)[0]==409
  assert request('/api/upgrades/preflight',{'templateId':template_id,'stationId':'A'},engineer)[1]['data']['ready']==False
  backuppath=root/'data/deployments'/dep['BackupFile']
  with backuppath.open('ab') as f:f.write(b'corruption-in-test')
  code,bad=request('/api/upgrades/'+deployment_id+'/integrity',token=engineer)
  assert code==200 and bad['data']['status']=='Mismatch',(code,bad)
  print('PASS V1.8 .NET HTTP: durable startup interruption, no auto retry, recovery report RBAC, SHA mismatch')
 finally:stop(service)
