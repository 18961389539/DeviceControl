/* V1.8 simulation-only, read-only recovery inspector and deterministic fault drills.
   No controller state or journal is changed by this module. */
(function(global){
'use strict';
const isBlocker=x=>['Applying','Failed'].includes(x);
function evaluate(batch, stations, history=[]){
  if(!batch||!Array.isArray(batch.steps))throw Error('缺少批次与步骤');
  const reports=batch.steps.map(step=>{
    const station=stations[step.stationId];
    if(!station) return {stationId:step.stationId,stepStatus:step.status,category:'ManualReview',baselineMatches:false,
      targetMatches:false,blockers:['无法读取工站状态'],deploymentStatus:null,backupStatus:'Unknown'};
    const flow=station.flowId,recipe=station.recipeId;
    const baselineMatches=flow===step.expectedFlowId&&recipe===step.expectedRecipeId;
    const targetMatches=!!(step.afterFlowId&&step.afterRecipeId&&flow===step.afterFlowId&&recipe===step.afterRecipeId);
    const found=history.find(x=>x.id===step.deploymentId)||history.find(x=>x.stationId===step.stationId&&['Interrupted','InProgress','ManualRecoveryRequired'].includes(x.status));
    const blockers=[];
    if(isBlocker(step.status))blockers.push('执行中断/失败步骤，必须核查');
    if(batch.status==='Interrupted')blockers.push('批次服务中断');
    if(station.busy||station.state!=='Idle'||station.mode!=='Manual'||station.recoveryRequired||station.alarm)blockers.push('工站不满足空闲手动、无报警条件');
    if(!baselineMatches&&!targetMatches)blockers.push('当前版本既非部署前也非已记录目标版本');
    if(found&&['Interrupted','InProgress','ManualRecoveryRequired'].includes(found.status))blockers.push('单站部署日志未完结');
    if(found&&found.backupStatus&&found.backupStatus!=='Verified')blockers.push('备份不可验证：'+found.backupStatus);
    return {stationId:step.stationId,stepStatus:step.status,category:blockers.length?'ManualReview':step.status==='Pending'?'NotStarted':'ReviewEvidence',
      baselineMatches,targetMatches,deploymentId:found?.id||null,deploymentStatus:found?.status||null,
      backupStatus:found?.backupStatus||'Unknown',blockers};
  });
  return {rolloutId:batch.id,status:batch.status,inspectedAt:new Date().toISOString(),readOnly:true,
    requiresManualReview:reports.some(x=>x.category==='ManualReview'),stations:reports,
    disclaimer:'只读分析，不会恢复备份、自动部署、重试或回滚。'};
}
function drill(kind){
 const base={id:'simulation-drill',status:'Interrupted',steps:[{stationId:'SIM-A',status:'Applying',
  expectedFlowId:'flow-old',expectedRecipeId:'recipe-old',afterFlowId:'flow-new',afterRecipeId:'recipe-new',deploymentId:'dep-drill'}]};
 const station={flowId:'flow-old',recipeId:'recipe-old',state:'Idle',mode:'Manual',busy:false,recoveryRequired:false,alarm:null};
 const rec={id:'dep-drill',stationId:'SIM-A',status:'Interrupted',backupStatus:'Verified'};
 if(kind==='split-versions') station.flowId='flow-new';
 else if(kind==='corrupt-backup') rec.backupStatus='Mismatch';
 else if(kind!=='crash-after-intent')throw Error('未知演练类型');
 const report=evaluate(base,{'SIM-A':station},[rec]);
 report.drillScenario=kind;report.simulated=true;
 return report;
}
global.RecoveryScenarios={evaluate,drill};
})(window);
