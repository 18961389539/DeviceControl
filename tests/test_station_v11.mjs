// Runtime isolation smoke executed in Node. This is NOT a C# endpoint test.
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const root=new URL('../frontend/',import.meta.url);
const win={};win.window=win;
const context=vm.createContext({window:win,console,structuredClone,setTimeout,clearTimeout,Date,Math,performance});
for(const fn of ['station-data.js','shared-resource-coordinator.js','workflow-engine.js','mock-runtime.js','workflow-studio.js','governance-mock.js'])
  vm.runInContext(readFileSync(new URL(fn,root),'utf8'),context,{filename:fn});
const a=new win.MockRuntime(win.STATION_CONFIG);
const cfgB=structuredClone(win.STATION_CONFIG);cfgB.profileId='DEMO-STATION-B';
const b=new win.MockRuntime(cfgB);
const engineer={name:'engineer',role:'Engineer'};
const aGov=new win.GovernanceMock(a),bGov=new win.GovernanceMock(b);
assert.equal(a.snapshot().inputs.PartPresent,true);
assert.equal(b.snapshot().inputs.PartPresent,true);
await aGov.post('/api/sim/input',{name:'PartPresent',value:false},engineer);
assert.equal(a.snapshot().inputs.PartPresent,false,'A input injected');
assert.equal(b.snapshot().inputs.PartPresent,true,'B input remains isolated');
await bGov.post('/api/manual/axes/X/enable',{enabled:true},engineer);
await bGov.post('/api/manual/axes/X/home',{},engineer);
assert.equal(b.snapshot().axes.X.homed,true);
assert.equal(a.snapshot().axes.X.homed,false,'A axis remains unhomed');
await aGov.post('/api/recipes/draft',{name:'A-only',visionOffsetMm:0,extraDwellMs:20},engineer);
assert.equal(aGov.versions.length,bGov.versions.length+1,'A recipe draft is not written to B');
await aGov.post('/api/sim/input',{name:'PartPresent',value:true},engineer);
assert.equal(a.snapshot().inputs.PartPresent,true);
console.log('PASS V1.1: two MockRuntime instances isolate DI, axes, recipe versions');
