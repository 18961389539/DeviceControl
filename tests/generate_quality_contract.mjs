import fs from 'node:fs';
import assert from 'node:assert/strict';
import vm from 'node:vm';
const cfg=JSON.parse(fs.readFileSync(new URL('../backend/DeviceControl.Api/config/station.json',import.meta.url)));
const ctx={window:{STATION_CONFIG:structuredClone(cfg)},localStorage:{getItem:()=>null,setItem:()=>{}},structuredClone,console};
vm.createContext(ctx);
for(const name of ['workflow-studio.js','workflow-quality.js'])
  vm.runInContext(fs.readFileSync(new URL('../frontend/'+name,import.meta.url),'utf8'),ctx);
const q=ctx.window.WorkflowQuality;
const clone=x=>structuredClone(x),cases=[];
function add(id,definition){const result=q.runSuite(definition,cfg);cases.push({id,definition,expectedPass:result.pass,expectedCases:result.results.map(c=>({id:c.id,outcome:c.actual,pass:c.pass})),expectedWarnings:result.warnings.length});}
add('normal',clone(cfg.sequence));
let reordered=clone(cfg.sequence);const at=reordered.findIndex(n=>n.type==='VisionLocate');reordered.push(...reordered.splice(at,1));add('unbound-vision',reordered);
let collision=clone(cfg.sequence),par=collision.find(n=>n.type==='Parallel');par.children[1].device=par.children[0].device;add('same-resource-warning',collision);
let unsatisfied=clone(cfg.sequence);unsatisfied[0].device='ClampExtended';add('unsatisfied-wait',unsatisfied);
let condition=clone(cfg.sequence),node=ctx.window.FlowStudioModel.newNode('Condition',cfg,'condition-contract');node.children[0].id='contract-true';node.children[1].id='contract-false';condition.push(node);add('conditional-path',condition);
const fixture={contractVersion:1,source:'frontend/workflow-quality.js',cases};
const target=new URL('./fixtures/quality_contract.json',import.meta.url);
if(process.argv.includes('--write')){
  fs.writeFileSync(target,JSON.stringify(fixture,null,2)+'\n');
  console.log('Wrote',cases.length,'cross-language baseline fixtures');
}else{
  const baseline=JSON.parse(fs.readFileSync(target,'utf8'));
  assert.equal(JSON.stringify(fixture),JSON.stringify(baseline),'Browser quality-model contract changed: review changes and run --write only after approval');
  console.log('PASS V0.9 browser quality contract baseline:',cases.length,'definitions and scenarios');
}
