import datetime, hashlib, json, subprocess
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
OUT=Path(__file__).parent
def command(args):
    r=subprocess.run(args,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace')
    return {'exitCode':r.returncode,'output':r.stdout.strip()}
files=[]
for folder in ('backend','frontend','tests','tools','.github','docs'):
    files.extend(p for p in (ROOT/folder).rglob('*') if p.is_file() and not any(x in p.parts for x in ('bin','obj','__pycache__')))
files.extend(ROOT/n for n in ('README.md','MANIFEST_SHA256.txt','DeviceControl_V2.0_ReleaseStatus.json'))
state={'recordedAt':datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).isoformat(),
    'scope':'Current working tree, including pre-existing uncommitted corrections; product sources unchanged during audit',
    'head':command(['git','rev-parse','HEAD']), 'status':command(['git','status','--short']),
    'dotnet':command(['dotnet','--version']),'node':command(['node','--version']),
    'sha256':{p.relative_to(ROOT).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(files)}}
(OUT/'source-state.json').write_text(json.dumps(state,ensure_ascii=False,indent=2),encoding='utf-8')
obs=json.loads((OUT/'supplement-observations.json').read_text(encoding='utf-8'))[-1]
print(json.dumps({'manifestMatched':obs['matched'],'manifestMissing':len(obs['missing']),
    'manifestMismatched':len(obs['mismatched']),'sourceFingerprints':len(state['sha256']),
    'dotnet':state['dotnet'],'node':state['node']},ensure_ascii=False))
