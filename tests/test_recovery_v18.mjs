import assert from 'node:assert/strict';
import fs from 'node:fs';import vm from 'node:vm';
const w={};vm.runInNewContext(fs.readFileSync(new URL('../frontend/deployment-recovery.js', import.meta.url),'utf8'),{window:w,Date,Error});
const m=w.RecoveryScenarios;
for(const name of ['crash-after-intent','split-versions','corrupt-backup']){
 const report=m.drill(name);assert.equal(report.readOnly,true);assert.equal(report.requiresManualReview,true);
 assert.equal(report.stations[0].category,'ManualReview');assert.equal(report.stations[0].stepStatus,'Applying');
}
assert.equal(m.drill('corrupt-backup').stations[0].backupStatus,'Mismatch');
assert.equal(m.drill('split-versions').stations[0].baselineMatches,false);
assert.equal(m.drill('crash-after-intent').stations[0].baselineMatches,true);
assert.throws(()=>m.drill('invalid'));
const batch={id:'roll-test',status:'PilotPending',steps:[{stationId:'A',status:'Pending',expectedFlowId:'f0',expectedRecipeId:'r0'}]};
const stations={A:{flowId:'f0',recipeId:'r0',mode:'Manual',state:'Idle',busy:false,recoveryRequired:false,alarm:null}};
const saved=JSON.stringify({batch,stations});
const pass=m.evaluate(batch,stations,[]);assert.equal(pass.requiresManualReview,false);
assert.equal(pass.stations[0].category,'NotStarted');
assert.equal(JSON.stringify({batch,stations}),saved,'read-only analyzer mutated live objects');
const drift=m.evaluate(batch,{A:{...stations.A,flowId:'alien'}},[]);assert.equal(drift.requiresManualReview,true);
const unresolved=m.evaluate(batch,stations,[{id:'dep-x',stationId:'A',status:'Interrupted',backupStatus:'Verified'}]);
assert.equal(unresolved.requiresManualReview,true);
console.log('PASS V1.8 recovery engine: restart intent, split publication, backup mismatch, baseline & immutability');
