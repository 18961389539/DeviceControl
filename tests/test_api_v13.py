"""V1.3 C# HTTP integration gate. Needs .NET 10 SDK; never asserts a pass without executing."""
import copy,json,os,shutil,socket,subprocess,tempfile,time,urllib.request,urllib.error,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
from dotnet_harness import start_api, stop_api
if not shutil.which('dotnet'):
 print('UNVERIFIED V1.3 C# HTTP integration: .NET 10 SDK absent')
 sys.exit(0)
with tempfile.TemporaryDirectory(prefix='devicecontrol-v13-') as directory:
 root=Path(directory);(root/'config').mkdir()
 for fname in ('station.json','station-B.json'):
  shutil.copyfile(ROOT/'backend/DeviceControl.Api/config'/fname,root/'config'/fname)
 with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
 base=f'http://127.0.0.1:{port}'
 env={**os.environ,'SimListenUrl':base,'ASPNETCORE_CONTENTROOT':str(root)}
 project=str(ROOT/'backend/DeviceControl.Api/DeviceControl.Api.csproj')
 proc=start_api(root, env)
 def request(path,method='GET',body=None,token=None):
  raw=None if body is None else json.dumps(body).encode()
  req=urllib.request.Request(base+path,method=method,data=raw,
   headers={**({'Content-Type':'application/json'} if raw is not None else {}),**({'X-Sim-Token':token} if token else {})})
  try:
   with urllib.request.urlopen(req,timeout=15) as res:return res.status,json.load(res)
  except urllib.error.HTTPError as exc:return exc.code,json.load(exc)
 try:
  for _ in range(160):
   if proc.poll() is not None:raise AssertionError('V1.3 .NET process exited unexpectedly')
   try:
    if request('/api/health')[0]==200:break
   except Exception:time.sleep(.3)
  else:raise AssertionError('API startup timed out')
  assert request('/api/health')[1]['version'] in ('1.3','1.4','1.5','1.6','1.7','1.8','1.9','2.0-rc'), 'V1.3 compatibility API must remain accessible'
  def login(username):
   code,data=request('/api/auth/login','POST',{'username':username,'password':'demo123'});assert code==200;return data['token']
  engineer=login('engineer');admin=login('admin');operator=login('operator')
  assert request('/api/stations/B/audit',token=operator)[0]==403
  assert request('/api/stations/C/audit',token=engineer)[0]==404
  for sid in ['A','B']:
   for endpoint in ['config','snapshot','flow','flows','recipes','runs','recovery/checklist','audit']:
    code,_=request(f'/api/stations/{sid}/{endpoint}',token=engineer);assert code==200,(sid,endpoint,code)
  a_flows=request('/api/stations/A/flows',token=engineer)[1]
  b_flows=request('/api/stations/B/flows',token=engineer)[1]
  draft=copy.deepcopy(b_flows['active']['definition']);draft[0]['label']='Only-B-Flow'
  code,created=request('/api/stations/B/flows/draft','POST',{'definition':draft,'expectedActiveVersionId':b_flows['active']['id']},engineer)
  assert code==200,(code,created)
  version=created['id'] if 'id' in created else created['data']['id']
  assert request('/api/stations/B/flows/'+version+'/validate','POST',{},engineer)[0]==200
  assert request('/api/stations/B/flows/'+version+'/publish','POST',{},operator)[0]==403
  assert request('/api/stations/B/flows/'+version+'/publish','POST',{},admin)[0]==200
  b_flows=request('/api/stations/B/flows',token=engineer)[1]
  a_flows_after=request('/api/stations/A/flows',token=engineer)[1]
  assert b_flows['active']['definition'][0]['label']=='Only-B-Flow'
  assert a_flows_after['active']['definition'][0]['label']!= 'Only-B-Flow'
  assert a_flows_after['active']['id']==a_flows['active']['id']
  code,_=request('/api/stations/A/sim/input','POST',{'name':'PartPresent','value':False},engineer)
  assert code==200
  code,_=request('/api/stations/B/sim/input','POST',{'name':'PartPresent','value':False},engineer)
  assert code==200
  a_audit=request('/api/stations/A/audit?page=1&pageSize=80',token=engineer)[1]['items']
  b_audit=request('/api/stations/B/audit?page=1&pageSize=80',token=engineer)[1]['items']
  assert a_audit and b_audit
  assert any('/api/stations/A/sim/input' in x['path'] for x in a_audit)
  assert any('/api/stations/B/sim/input' in x['path'] for x in b_audit)
  assert all(not x['path'].startswith('/api/stations/B/') for x in a_audit)
  assert all(not x['path'].startswith('/api/stations/A/') for x in b_audit)
  print('PASS V1.3 C# HTTP: A/B isolated flow publish, audit filtering, read APIs and RBAC')
 finally:
  stop_api(proc)
