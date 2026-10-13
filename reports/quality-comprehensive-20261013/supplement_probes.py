"""Shared-resource, short latency sample, artifact provenance observations."""
import concurrent.futures, hashlib, json, math, time
from pathlib import Path
# Reuse only the definitions, never execute the earlier scenarios on import.
source=Path(__file__).with_name('http_probes.py').read_text(encoding='utf-8')
exec(compile(source.split('# Simultaneous audit DB')[0],str(Path(__file__).with_name('http_probes.py')),'exec'))
observations=[]
def save(name,value):
    observations.append({'name':name,**value})
    Path(__file__).with_name('supplement-observations.json').write_text(json.dumps(observations,ensure_ascii=False,indent=2),encoding='utf-8')
    print(name,json.dumps(value,ensure_ascii=False),flush=True)

s=Server()
try:
    cfgpath=s.root/'config/station-B.json'
    cfg=json.loads(cfgpath.read_text(encoding='utf-8'))
    cfg['sequence']=[{'id':'vision-wait','type':'VisionLocate','label':'视觉人工处理',
        'onError':'WaitForOperator'}]
    cfgpath.write_text(json.dumps(cfg),encoding='utf-8')
    s.start();token=s.login()
    for axis in ('X','Y'):
        assert s.call('/api/stations/B/manual/axes/'+axis+'/enable',{'enabled':True},token)[0]==200
        assert s.call('/api/stations/B/manual/axes/'+axis+'/home',{},token)[0]==200
    assert s.call('/api/stations/B/mode',{'mode':'Auto'},token)[0]==200
    assert s.call('/api/stations/B/cycle/start',{},token)[0]==200
    time.sleep(.08)
    assert s.call('/api/stations/B/sim/input',{'name':'PartPresent','value':False},token)[0]==200
    for _ in range(100):
        bflow=s.call('/api/stations/B/flow',token=token)[1]
        if bflow['status']=='Paused':break
        time.sleep(.02)
    assert bflow['status']=='Paused',bflow
    held=s.call('/api/stations/resources',token=token)[1]
    started=time.perf_counter()
    code,body,_=s.call('/api/manual/vision/trigger',{},token)
    elapsed=time.perf_counter()-started
    # Route name is part of the source contract; reject a failed probe setup.
    assert code!=404,(code,body)
    snap=s.call('/api/snapshot',token=token)[1]
    save('operator_pause_resource_hold',{'bFlowStatus':bflow['status'],'bWaiting':bflow['waiting'],
        'resourceSnapshot':held,'aVisionStatus':code,'aVisionBody':body,'aWaitSeconds':round(elapsed,3),
        'aState':snap['state'],'aRecoveryRequired':snap['recoveryRequired']})
    s.call('/api/stations/B/cycle/stop',{},token)
    save('operator_stop_resource_release',{'resourcesAfterStop':s.call('/api/stations/resources',token=token)[1]})

    def measure(i):
        started=time.perf_counter();code,_,_=s.call('/api/stations/'+('A' if i%2==0 else 'B')+'/snapshot',token=token)
        return (time.perf_counter()-started)*1000,code
    started=time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool: samples=list(pool.map(measure,range(160)))
    elapsed=time.perf_counter()-started;lat=sorted(x[0] for x in samples)
    save('short_snapshot_latency_sample',{'requests':160,'clients':8,'stations':2,
        'p95Ms':round(lat[math.ceil(.95*len(lat))-1],2),'p99Ms':round(lat[math.ceil(.99*len(lat))-1],2),
        'maxMs':round(max(lat),2),'requestsPerSecond':round(len(lat)/elapsed,2),
        'statusCounts':{str(c):sum(x[1]==c for x in samples) for c in set(x[1] for x in samples)},
        'scope':'Settled simulations, short snapshot HTTP only; not production throughput or soak acceptance'})
finally:s.close()

lines=(ROOT/'MANIFEST_SHA256.txt').read_text(encoding='utf-8').splitlines()
missing=[];mismatched=[];matched=0
for line in lines:
    parts=line.split(None,1)
    if len(parts)!=2:continue
    expected,name=parts;name=name.lstrip('*');path=ROOT/name
    if not path.is_file():missing.append(name)
    elif hashlib.sha256(path.read_bytes()).hexdigest().lower()!=expected.lower():mismatched.append(name)
    else:matched+=1
save('manifest_provenance',{'matched':matched,'missing':missing,'mismatched':mismatched,
    'releaseStatus':json.loads((ROOT/'DeviceControl_V2.0_ReleaseStatus.json').read_text(encoding='utf-8'))})
print('OBSERVATIONS COMPLETE; not a product PASS')
