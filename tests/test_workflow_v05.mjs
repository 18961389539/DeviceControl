import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const cfg=JSON.parse(fs.readFileSync(new URL('../backend/DeviceControl.Api/config/station.json',import.meta.url),'utf8'));
const ctx={window:{STATION_CONFIG:cfg},setTimeout,clearTimeout,console,Date,Error,Promise,structuredClone};
vm.createContext(ctx);
for (const name of ['shared-resource-coordinator.js','workflow-engine.js','mock-runtime.js','workflow-studio.js','governance-mock.js']) vm.runInContext(fs.readFileSync(new URL('../frontend/'+name,import.meta.url),'utf8'),ctx);
const delay=ms=>new Promise(r=>setTimeout(r,ms));
const until=async(fn,ms=16000)=>{const start=Date.now();while(!fn()){if(Date.now()-start>ms)throw Error('Timed out waiting');await delay(15)}};
async function ready(m){for(const id of ['X','Y']){await m.post(`/api/manual/axes/${id}/enable`,{enabled:true});await m.post(`/api/manual/axes/${id}/home`)}await m.post('/api/mode',{mode:'Auto'})}
// 1. Parallel X/Y actions overlap but distinct resources are held independently.
const m=new ctx.window.MockRuntime();
assert.equal(m.flow.sequence.length,9);
assert.equal(m.flow.nodes.length,13);
assert.throws(()=>new ctx.window.WorkflowEngine(m,[{id:'bad',type:'AxisMove',label:'unsafe',device:'X',onError:'Retry',maxRetries:1}]),/非幂等/);
await ready(m);
const flowVersion=cfg.flowVersion;
await m.post('/api/cycle/start');
await until(()=>m.flow.snapshot().active.some(x=>x.id==='x-place'));
const live=m.flow.snapshot();
assert(live.active.some(x=>x.id==='y-place'),'Y must run concurrently with X');
assert.equal(live.resources['Axis:X'],'x-place');
assert.equal(live.resources['Axis:Y'],'y-place');
await until(()=>!m.snapshot().busy);
assert.equal(m.snapshot().cycleHistory[0].outcome,'Completed');
assert.equal(m.snapshot().cycleHistory[0].flowVersion,flowVersion);
const t=m.flow.snapshot().traces;
const children=t.filter(x=>x.parentId==='xy-approach');
assert.equal(children.length,2);
assert(children.every(x=>x.status==='Completed'));
const parent=t.find(x=>x.id==='xy-approach');
assert(parent.durationMs < children.reduce((sum,x)=>sum+x.durationMs,0),'parallel group should cost less than sum of child durations');
// 2. Breakpoint, step, operator role check, and continue.
const gov=new ctx.window.GovernanceMock(m);
const admin=gov.login('admin','demo123'),operator=gov.login('operator','demo123');
await assert.rejects(gov.post('/api/flow/debug',{command:'breakpoint',index:2,enabled:true},operator),/无权/);
await gov.post('/api/flow/debug',{command:'breakpoint',index:2,enabled:true},admin);
await m.post('/api/cycle/start');
await until(()=>m.flow.snapshot().status==='Paused');
assert.equal(m.flow.snapshot().waiting.index,2);
assert.equal(m.snapshot().stepIndex,1,'execution must pause before parallel group');
await m.post('/api/flow/debug',{command:'step'});
await until(()=>m.flow.snapshot().status==='Paused'&&m.flow.snapshot().waiting?.index===3);
assert(t.length>0);
await m.post('/api/flow/debug',{command:'resume'});
await until(()=>!m.snapshot().busy);
assert.equal(m.snapshot().cycleHistory[0].outcome,'Completed');
await m.post('/api/flow/debug',{command:'breakpoint',index:2,enabled:false});
// 3. Cooperative stop from a paused boundary unblocks the engine and requires recovery.
await m.post('/api/flow/debug',{command:'breakpoint',index:0,enabled:true});
await m.post('/api/cycle/start');
await until(()=>m.flow.snapshot().status==='Paused');
await m.post('/api/cycle/stop');
await until(()=>!m.snapshot().busy);
assert.equal(m.snapshot().cycleHistory[0].outcome,'Stopped');
assert.equal(m.snapshot().recoveryRequired,true);
assert.equal(m.flow.snapshot().status,'Idle');
await m.post('/api/flow/debug',{command:'breakpoint',index:0,enabled:false});
// 4. The mutex prevents concurrent access to the same resource, even if both branches request it.
let simultaneously=0,maxSim=0;
const fake={cfg:{flowVersion:'test-lock'},check(){},cancel:false,d:{step:''},recordStep(){},log(){},async executeStep(){simultaneously++;maxSim=Math.max(maxSim,simultaneously);await delay(60);simultaneously--}};
const resourceFlow=new ctx.window.WorkflowEngine(fake,[{id:'p',label:'pair',type:'Parallel',children:[
 {id:'a',label:'one',type:'AxisMove',device:'X'}, {id:'b',label:'two',type:'AxisMove',device:'X'}]}]);
await resourceFlow.run();
assert.equal(maxSim,1);
assert.equal(Object.keys(resourceFlow.snapshot().resources).length,0);
// 5. Non-actuating nodes can use bounded automatic retry.
let calls=0;
const retryFake={...fake,async executeStep(){calls++;if(calls===1)throw Error('transient')}};
const retryFlow=new ctx.window.WorkflowEngine(retryFake,[{id:'r',label:'retry',type:'VisionLocate',onError:'Retry',maxRetries:1}]);
await retryFlow.run();assert.equal(calls,2);
assert.equal(retryFlow.snapshot().traces[0].attempt,2);
// 6. WaitForOperator resumes only on explicit retry.
let waits=0;
const waitFake={...fake,async executeStep(){waits++;if(waits===1)throw Error('intermittent image')}};
const waitFlow=new ctx.window.WorkflowEngine(waitFake,[{id:'w',label:'manual retry',type:'VisionLocate',onError:'WaitForOperator'}]);
const pending=waitFlow.run();
await until(()=>waitFlow.snapshot().waiting?.kind==='error');
assert.equal(waitFlow.snapshot().status,'Paused');
waitFlow.command('retry');await pending;
assert.equal(waits,2);
assert.equal(waitFlow.snapshot().status,'Idle');
console.log('PASS V0.5 flow: parallel overlap, locks, breakpoint, step, stop, bounded retry, manual retry, RBAC');
