"""Full simulated API smoke test (requires .NET 10 SDK).
Runs a localhost-only API in a temporary content root. No hardware. No real credentials.
Run: python tests/test_api_v09.py
"""
import json,os,shutil,socket,subprocess,tempfile,time,urllib.request,urllib.error,sys
from pathlib import Path
import contextlib
ROOT=Path(__file__).resolve().parents[1]
if not shutil.which('dotnet'):
 print('SKIP (unverified): .NET SDK unavailable; API integration tests were not executed')
 sys.exit(0)

with tempfile.TemporaryDirectory(prefix='devicecontrol-v09-api-') as folder:
 root=Path(folder); (root/'config').mkdir()
 shutil.copyfile(ROOT/'backend/DeviceControl.Api/config/station.json',root/'config/station.json')
 with socket.socket() as s:
  s.bind(('127.0.0.1',0));port=s.getsockname()[1]
 url=f'http://127.0.0.1:{port}'
 env={**os.environ,'SimListenUrl':url,'ASPNETCORE_CONTENTROOT':str(root)}
 project=ROOT/'backend/DeviceControl.Api/DeviceControl.Api.csproj'
 proc=subprocess.Popen(['dotnet','run','--project',str(project),'--no-launch-profile'],cwd=root,
    env=env,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True)
 def request(path,method='GET',body=None,token=None):
  data=None if body is None else json.dumps(body).encode()
  req=urllib.request.Request(url+path,data=data,method=method,
      headers={**({'Content-Type':'application/json'} if data is not None else {}),
               **({'X-Sim-Token':token} if token else {})})
  try:
   with urllib.request.urlopen(req,timeout=4) as res:return res.status,json.load(res)
  except urllib.error.HTTPError as e:return e.code,json.load(e)
 try:
  for _ in range(90):
   if proc.poll() is not None:raise AssertionError('C# API failed to start')
   try:
    if request('/api/health')[0]==200:break
   except Exception:time.sleep(.35)
  else:raise AssertionError('Timed out starting C# API')
  status,session=request('/api/auth/login','POST',{'username':'engineer','password':'demo123'})
  assert status==200,(status,session)
  engineer=session['token']
  status,session=request('/api/auth/login','POST',{'username':'admin','password':'demo123'})
  assert status==200,(status,session)
  admin=session['token']
  status,listing=request('/api/flows',token=engineer)
  active=listing['active']['id']
  cfg=json.loads((ROOT/'backend/DeviceControl.Api/config/station.json').read_text(encoding='utf-8'))
  bad=json.loads(json.dumps(cfg['sequence']))
  bad[0]['device']='ClampExtended'   # structurally valid, normal scenario never completes
  s,d=request('/api/flows/draft','POST',{'definition':bad,'expectedActiveVersionId':active},engineer)
  assert s==200,(s,d)
  bad_id=d['data']['id']
  s,error=request(f'/api/flows/{bad_id}/validate','POST',{},engineer)
  assert s==409,(s,error)
  s,report=request(f'/api/flows/{bad_id}/quality',token=engineer)
  assert s==200 and report['data'][0]['pass'] is False,(s,report)
  s,d=request('/api/flows/draft','POST',{'definition':cfg['sequence'],'expectedActiveVersionId':active},engineer)
  assert s==200,(s,d)
  fresh=d['data']['id']
  s,result=request(f'/api/flows/{fresh}/validate','POST',{},engineer)
  assert s==200,(s,result)
  s,report=request(f'/api/flows/{fresh}/quality',token=engineer)
  assert s==200 and report['data'][0]['pass'] is True
  s,error=request(f'/api/flows/{fresh}/publish','POST',{},engineer)
  assert s==403,(s,error)
  s,pub=request(f'/api/flows/{fresh}/publish','POST',{},admin)
  assert s==200,(s,pub)
  s,listing=request('/api/flows',token=admin)
  assert listing['active']['id']==fresh
  s,stale=request('/api/flows/draft','POST',{'definition':cfg['sequence'],'expectedActiveVersionId':active},engineer)
  assert s==409,(s,stale)
  import sqlite3
  db=root/'data/devicecontrol.db'
  with contextlib.closing(sqlite3.connect(db)) as conn:
   assert conn.execute('SELECT count(*) FROM workflow_quality_reports WHERE flow_id=? AND passed=1',(fresh,)).fetchone()[0]>=2
   assert conn.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
  print('PASS V0.9 C# HTTP API: publish gate, failed evidence, role denial, optimistic locking, SQLite')
 finally:
  proc.terminate()
  try:proc.communicate(timeout=8)
  except subprocess.TimeoutExpired:proc.kill();proc.communicate()
