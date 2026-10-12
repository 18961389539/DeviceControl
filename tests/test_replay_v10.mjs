// Read-only replay must bind variables by original trace index, not sort order.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const context={window:{},console,setInterval,clearInterval};
vm.runInNewContext(readFileSync(new URL('../frontend/workflow-replay.js',import.meta.url),'utf8'),context);
const timeline=context.window.WorkflowReplay.timeline;
const run={id:'test',trace:[
  {id:'X',endedAt:'2026-01-01T00:00:04Z',status:'Completed'},
  {id:'Y',endedAt:'2026-01-01T00:00:02Z',status:'Completed'}
],snapshots:[
  {ordinal:0,nodeId:'X',inputs:{PartPresent:true},axes:{X:{positionMm:80}},cylinders:{},vision:null},
  {ordinal:1,nodeId:'Y',inputs:{PartPresent:false},axes:{Y:{positionMm:40}},cylinders:{},vision:null}
]};
const items=timeline(run);
assert.equal(items.length,2);
assert.equal(items[0].id,'Y');
assert.equal(items[0].vars.axes.Y.positionMm,40);
assert.equal(items[1].vars.axes.X.positionMm,80);
assert.equal(items[0].vars.inputs.PartPresent,false);
assert.equal(items[1].vars.inputs.PartPresent,true);
assert.equal(timeline({trace:run.trace})[0].vars,null,'never synthesize missing historical evidence');
assert.equal(timeline({steps:[{label:'legacy'}]})[0].vars,null);
console.log('PASS V1.0 replay: snapshot/trace ordinal join, parallel reorder, missing data remains missing');
