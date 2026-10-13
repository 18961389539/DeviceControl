/* V1.9 isolated backup inspection presentation. Offline HTML cannot validate actual SQLite. */
(function(global){'use strict';
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
class DeploymentSandboxUI{
 constructor({root,backend,token,user,upgradeStudio}){
  Object.assign(this,{root,backend,token,user,upgradeStudio});
  this.api=global.DeviceControlApiClient.create({root});
  this.output=document.getElementById('sandboxReport');this.button=document.getElementById('sandboxInspectBtn');
  this.button.addEventListener('click',()=>void this.inspect());
 }
 async inspect(){
  if(!this.user()||!['Engineer','Admin'].includes(this.user().role)){
   this.output.textContent='需要工程师及以上权限';return;
  }
  const id=this.upgradeStudio.selectedHistory;
  const dep=this.upgradeStudio.history.find(x=>x.id===id);
  if(!dep){this.output.textContent='请先在「⑤ 已有工站」中选择部署历史。';return;}
  this.button.disabled=true;
  try{
   let report;
   if(this.backend()){
    const res=await this.api('/api/upgrades/'+encodeURIComponent(id)+'/sandbox',
     {headers:{'X-Sim-Token':this.token()}});
    const data=await res.json();
    report=data.data;
   }else{
    // Do NOT forge positive backup/SQLite results in the no-backend demonstration.
    report={deploymentId:id,stationId:dep.stationId,readOnly:true,writesProduction:false,
      requiresManualReview:true,backupValidated:false,
      currentFlowId:'离线仿真',currentRecipeId:'离线仿真',backupFlowId:null,backupRecipeId:null,
      checks:[{code:'file-availability',passed:false,message:'离线 HTML 没有真实 SQLite 数据库/备份文件，SHA-256 与完整性检查均未执行'},
              {code:'journal-baseline',passed:false,message:'仅展示内存部署记录；需要运行 Python 沙箱工具或 .NET 后端完成实测'}]};
   }
   this.output.innerHTML=`<div class="control-hint"><strong>工站 ${esc(report.stationId)} · ${esc(report.deploymentId)}</strong>
      · ${report.backupValidated?'数据库副本检查通过':'未通过/未执行'} · 只读 · 仍须人工核查</div>`+
      report.checks.map(c=>`<div class="rollout-step"><strong>${c.passed?'✓':'✕'} ${esc(c.code)}</strong>
      <span class="rollout-state">${c.passed?'PASS':'BLOCKED'}</span><small>${esc(c.message)}</small></div>`).join('')+
      `<div class="control-hint">生产数据写入：${report.writesProduction?'危险':'无'} · 自动恢复：不允许</div>`;
  }catch(e){this.output.textContent='检查失败：'+e.message;}
  finally{this.button.disabled=false;}
 }
}
global.DeploymentSandboxUI=DeploymentSandboxUI;
})(window);
