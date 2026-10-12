import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const cfg=JSON.parse(fs.readFileSync(new URL('../backend/DeviceControl.Api/config/station.json',import.meta.url)));
const storage=new Map();
const ctx={window:{STATION_CONFIG:structuredClone(cfg)},console,localStorage:{getItem:k=>storage.get(k)||null,setItem:(k,v)=>storage.set(k,v)},setTimeout,clearTimeout,Date,structuredClone};
vm.createContext(ctx);
for(const f of ['shared-resource-coordinator.js','workflow-engine.js','mock-runtime.js','workflow-studio.js','workflow-quality.js','governance-mock.js'])
  vm.runInContext(fs.readFileSync(new URL('../frontend/'+f,import.meta.url),'utf8'),ctx);
const q=ctx.window.WorkflowQuality;
const base=q.runSuite(structuredClone(cfg.sequence),cfg);
assert.equal(base.pass,true,JSON.stringify(base.errors));
assert.equal(base.results.length,4);
assert(base.results.every(x=>x.pass));
assert.equal(q.validateRecipe({visionOffsetMm:2,extraDwellMs:120}).length,0);
assert(q.validateRecipe({visionOffsetMm:99,extraDwellMs:23.2}).length,2);
assert.equal(q.validateValue(q.schema['vision.ok'],0),'需要 Boolean');
assert.equal(q.validateValue(q.schema['vision.targetXmm'],Infinity),'需要有限数值');
const invalid=structuredClone(cfg.sequence);
invalid[2].children[0].useVisionX=true;
const wrongOrder=structuredClone(invalid);wrongOrder.splice(1,1);wrongOrder.push(cfg.sequence[1]);
assert.equal(q.runSuite(wrongOrder,cfg).pass,false,'must find unbound vision target');
const collision=structuredClone(cfg.sequence);collision[2].children[1].device='X';
assert(q.runSuite(collision,cfg).warnings.some(x=>x.includes('竞争相同资源')));
const tampered=q.runSuite(cfg.sequence,cfg,[{name:'期望不匹配',inputs:{...q.scenarios(cfg)[0].inputs},visionOk:true,expected:'Failed'}]);
assert.equal(tampered.pass,false);assert(tampered.results.at(-1).pass===false);
const condition=ctx.window.FlowStudioModel.newNode('Condition',cfg,'condition-test');
condition.device='GuardClosed';
const sequence=structuredClone(cfg.sequence);sequence.push(condition);
const checked=q.runSuite(sequence,cfg);assert.equal(checked.pass,true);
assert(q.runScenario(sequence,cfg,{name:'skip',inputs:{...q.scenarios(cfg)[0].inputs,GuardClosed:false},visionOk:true}).outcome==='Rejected');
const perf=q.metrics([
  {outcome:'Completed',durationMs:100,steps:[{label:'A',durationMs:60},{label:'B',durationMs:30}]},
  {outcome:'Completed',durationMs:300,steps:[{label:'A',durationMs:150},{label:'B',durationMs:20}]},
  {outcome:'Failed',durationMs:200,steps:[{label:'A',durationMs:50}]}
]);
assert.equal(perf.p50,200);assert.equal(perf.p95,290);assert.equal(perf.rate,66.7);
assert.equal(perf.bottlenecks[0].label,'A');assert.equal(q.metrics([]).p95,null);
// optimistic concurrency: stale workspace rejected; validated stale revision cannot publish.
const machine=new ctx.window.MockRuntime(),gov=new ctx.window.GovernanceMock(machine);
const eng=gov.login('engineer','demo123'),admin=gov.login('admin','demo123');
const active=gov.flows.activeId;
assert.throws(()=>gov.flows.handle('/api/flows/draft',{definition:cfg.sequence,expectedActiveVersionId:'stale'},eng),/版本冲突/);
const a=gov.flows.handle('/api/flows/draft',{definition:cfg.sequence,expectedActiveVersionId:active},eng);
const b=gov.flows.handle('/api/flows/draft',{definition:cfg.sequence,expectedActiveVersionId:active},eng);
gov.flows.handle('/api/flows/'+a.id+'/validate',{},eng);
gov.flows.handle('/api/flows/'+b.id+'/validate',{},eng);
gov.flows.handle('/api/flows/'+a.id+'/publish',{},admin);
assert.throws(()=>gov.flows.handle('/api/flows/'+b.id+'/publish',{},admin),/版本冲突/);
assert.throws(()=>gov.flows.handle('/api/flows/draft',{definition:cfg.sequence,expectedActiveVersionId:active},eng),/版本冲突/);
// Browser validation stage refuses structurally valid but dry-run failing sequence.
const failFlow=structuredClone(cfg.sequence);failFlow[2].children[0].useVisionX=true;failFlow.push(cfg.sequence[1]);failFlow.splice(1,1);
assert(ctx.window.FlowStudioModel.validateFlow(failFlow,cfg).length===0);
const d=gov.flows.handle('/api/flows/draft',{definition:failFlow,expectedActiveVersionId:gov.flows.activeId},eng);
assert.throws(()=>gov.flows.handle('/api/flows/'+d.id+'/validate',{},eng),/质量门禁/);
console.log('PASS V0.8: 4 scenario gates, typed recipe schema, vision dependency, resource contention, metrics, optimistic locking, simulation publish guards');
