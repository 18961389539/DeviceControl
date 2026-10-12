// Deterministic browser-simulator resource arbitration tests, including flow integration.
import {readFileSync} from 'node:fs';
import assert from 'node:assert/strict';
import vm from 'node:vm';
const window={};window.window=window;
const ctx=vm.createContext({window, console, setInterval, clearInterval, setTimeout, clearTimeout, Date, Math, performance});
for(const f of ['shared-resource-coordinator.js','workflow-engine.js'])
  vm.runInContext(readFileSync(new URL('../frontend/'+f,import.meta.url),'utf8'),ctx,{filename:f});
const Scheduler=window.SharedResourceCoordinator;
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
{
 const s=new Scheduler(),events=[];
 const a=await s.acquire('A','n1',['CELL-SHARED'],500);
 const b=s.acquire('B','n2',['CELL-SHARED'],500).then(rel=>{events.push('B');return rel});
 const c=s.acquire('C','n3',['CELL-SHARED'],500).then(rel=>{events.push('C');return rel});
 assert.equal(s.snapshot().queue.length,2);
 assert.equal(s.snapshot().leases.length,1);
 a();const rb=await b;assert.deepEqual(events,['B']);rb();const rc=await c;
 assert.deepEqual(events,['B','C']);rc();assert.equal(s.snapshot().leases.length,0);
}
{
 const s=new Scheduler();
 const holder=await s.acquire('A','n1',['S1','S2'],500);
 const timedOut=s.acquire('B','n2',['S2'],100).then(()=>{throw Error('timeout expected')},e=>e.message);
 assert.match(await timedOut,/资源等待超时/);
 assert.equal(s.snapshot().queue.length,0);holder();assert.equal(s.snapshot().leases.length,0);
}
{
 const s=new Scheduler();
 const holder=await s.acquire('A','n1',['S1'],500);
 let stopped=false;
 const pending=s.acquire('B','n2',['S1'],400,()=>stopped).then(()=>{throw Error('cancel expected')},e=>e.message);
 stopped=true;assert.match(await pending,/STOP_REQUESTED/);
 holder();assert.equal(s.snapshot().queue.length,0);
}
{
 // Atomic multi-resource request cannot hold half of the set and deadlock.
 const s=new Scheduler();
 const la=await s.acquire('A','one',['A'],500);
 const lb=await s.acquire('B','two',['B'],500);
 const waiting=s.acquire('C','both',['B','A'],500);
 assert.equal(s.snapshot().leases.length,2);
 assert.equal(s.snapshot().queue.length,1);
 la();assert.equal(s.snapshot().leases.length,1);
 lb();const lc=await waiting;
 assert.equal(s.snapshot().leases.length,2);lc();assert.equal(s.snapshot().leases.length,0);
}
{
 const s=new Scheduler();let concurrent=0,max=0;const ends=[];
 const create=(station,id)=>{
  const fake={cfg:{flowVersion:'v12'},stationId:station,sharedCoordinator:s,cancel:false,d:{stepIndex:-1,step:''},
   check(){if(this.cancel)throw Error('STOP_REQUESTED')},traceVariables(){return {}},log(){},recordStep(){},
   async executeStep(){concurrent++;max=Math.max(max,concurrent);await sleep(90);concurrent--;ends.push(station);}};
  const flow=new window.WorkflowEngine(fake,[{id,type:'VisionLocate',label:'视觉',onError:'Abort',children:[],sharedResources:['CELL-SHARED'],resourceWaitTimeoutMs:1000}]);
  return flow;
 };
 await Promise.all([create('A','node-A').run(),create('B','node-B').run()]);
 assert.equal(max,1,'shared camera stages must never overlap');
 assert.deepEqual(ends,['A','B']);
 assert.equal(s.snapshot().leases.length,0);
}
console.log('PASS V1.2 FIFO, timeout, cancellation, atomic multi-key lease, cross-station flow isolation');
