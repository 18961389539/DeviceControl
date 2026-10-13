/* V1.5 software-only upgrade console. Browser mode is ephemeral and never changes hardware. */
(function(global){
 const copy=x=>structuredClone(x);
 const esc=x=>String(x??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
 const structure=c=>JSON.stringify({driver:c.driver,signals:c.signals,axes:c.axes,cylinders:c.cylinders,interlocks:c.interlocks});
 const checks=(template,station)=>{
   const issues=[];
   if(!template||template.status!=='Published')issues.push('必须选择已发布的模板');
   if(!station){issues.push('目标工站不存在');return issues}
   const s=station.machine.snapshot();
   if(s.state!=='Idle'||s.busy)issues.push('目标工站必须空闲');
   if(s.mode!=='Manual')issues.push('目标工站必须为手动模式');
   if(s.recoveryRequired||s.alarm)issues.push('工站存在故障或待恢复状态');
   if(template&&structure(template.config)!==structure(station.machine.cfg))issues.push('机构/信号/互锁定义不同，不能在线升级');
   return issues;
 };
 class StationUpgradeStudio {
   constructor({root,backend,token,user,offline,templates,notify,onChanged}){
     Object.assign(this,{root,backend,token,user,offline,templates,notify,onChanged});
   this.api=global.DeviceControlApiClient.create({root});
     this.plan=null;this.history=[];this.busy=false;this.error='';this.selectedHistory='';
     this.panel=document.getElementById('panel-templates');
     this.panel.addEventListener('click',e=>{const b=e.target.closest('button[data-upgrade]');if(b&&!b.disabled)void this.act(b.dataset.upgrade)});
     for(const id of ['upgradeStation','upgradeHistory'])document.getElementById(id).addEventListener('change',()=>{if(id==='upgradeStation'){this.plan=null;this.error=''}else this.selectedHistory=document.getElementById(id).value;this.render()});
     document.getElementById('templateSelect').addEventListener('change',()=>{this.plan=null;this.render()});
     this.render();
   }
   get role(){return ({Operator:1,Engineer:2,Admin:3})[this.user()?.role]||0}
   get selectedTemplate(){return this.templates().find(v=>v.id===document.getElementById('templateSelect').value)}
   get target(){return document.getElementById('upgradeStation').value}
   async call(path,body){const r=await this.api(path,{method:body===undefined?'GET':'POST',headers:{'X-Sim-Token':this.token(),'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),station:this.target});return (await r.json()).data}
   async refresh(){
     if(this.backend()&&this.user()&&!this.busy){try{this.history=await this.call('/api/upgrades/history');this.error=''}catch(e){this.error=e.message}}
     this.render();
   }
   render(){
     const ids=Object.keys(this.offline).sort();const selector=document.getElementById('upgradeStation');const before=selector.value;
     // In API mode new stations may exist only on the backend, so use the registered IDs.
     const stationIds=global.StationWorkspace?.stationIds||ids;
     selector.innerHTML=stationIds.map(id=>`<option value="${esc(id)}">工站 ${esc(id)}</option>`).join('');
     if(stationIds.includes(before))selector.value=before;
     const h=document.getElementById('upgradeHistory');const selected=this.selectedHistory;
     h.innerHTML=this.history.slice(0,80).map(row=>`<option value="${esc(row.id)}">${esc(row.stationId)} · ${esc(row.status)} · ${esc(row.id.slice(-8))}</option>`).join('')||'<option value="">暂无部署记录</option>';
     if(this.history.some(x=>x.id===selected))h.value=selected;this.selectedHistory=h.value;
     const p=this.plan;
     document.getElementById('upgradePreflight').innerHTML=p?`<strong>${p.ready?'✓ 预检通过':'✕ 预检拒绝'}</strong><span>工站 ${esc(p.stationId)} · ${esc(p.templateId)}</span><small>${esc((p.blockers||[]).join('；')||'流程与配方可以发布；机构配置保持不变。')}</small>`:'<span>选择目标工站并检查运行状态、版本及机构兼容性。</span>';
     const row=this.history.find(x=>x.id===this.selectedHistory);
     document.getElementById('upgradeEvidence').textContent=row?`状态：${row.status} · 目标：${row.stationId} · 来源：${row.templateId} · 原流程 ${row.beforeFlow?.id||'—'} / 配方 ${row.beforeRecipe?.id||'—'}${row.error?' · '+row.error:''}`:'尚无部署历史。';
     document.getElementById('upgradeError').textContent=this.error;
     for(const b of this.panel.querySelectorAll('[data-upgrade]')){
       const action=b.dataset.upgrade, required=action==='preflight'?2:3;
       b.disabled=!this.user()||this.busy||this.role<required||
         (action==='apply'&&(!p?.ready||p.templateId!==this.selectedTemplate?.id||p.stationId!==this.target))||
         (action==='rollback'&&(!row||!['Succeeded','InProgress','Interrupted','ManualRecoveryRequired'].includes(row.status)));
     }
   }
   async act(action){
     if(this.busy||this.role<(action==='preflight'?2:3))return;
     this.busy=true;this.error='';this.render();
     try{
       const stationId=this.target,template=this.selectedTemplate;
       if(action==='refresh'){await this.refresh();return;}
       if(action==='preflight'){
         if(!template)throw Error('请选择模板');
         if(this.backend())this.plan=await this.call('/api/upgrades/preflight',{templateId:template.id,stationId});
         else{
           const station=this.offline[stationId],blockers=checks(template,station);
           if(this.history.some(h=>h.stationId===stationId&&['InProgress','Interrupted','ManualRecoveryRequired'].includes(h.status)))blockers.push('存在未解决的部署');
           this.plan={id:'plan-'+Date.now(),templateId:template.id,stationId,ready:blockers.length===0,blockers,
             expectedFlowId:station?.governance.flows.activeId,expectedRecipeId:station?.governance.activeId,templateHash:template.sha256,createdAt:Date.now()};
         }
         this.notify(this.plan.ready?'升级预检通过，可由管理员确认发布':'预检不通过：'+this.plan.blockers.join('；'),!this.plan.ready);
       }else if(action==='apply'){
         const p=this.plan;if(!p?.ready||!template||template.id!==p.templateId||stationId!==p.stationId)throw Error('请先对当前模板及目标工站重新预检');
         if(this.backend()){
           const record=await this.call('/api/upgrades/apply',{planId:p.id});this.notify('部署完成，ID：'+record.id);
         }else{
           if(Date.now()-p.createdAt>600000)throw Error('预检已过期');
           const target=this.offline[stationId],g=target?.governance;
           if(checks(template,target).length||g.flows.activeId!==p.expectedFlowId||g.activeId!==p.expectedRecipeId)throw Error('工站状态或版本改变，请重新预检');
           const beforeFlow=copy(g.flows.active()),beforeRecipe=copy(g.active());const id='dep-'+String(Date.now())+Math.random().toString(36).slice(2,7);
           const row={id,stationId,templateId:template.id,actor:this.user().name,status:'InProgress',startedAt:new Date().toISOString(),beforeFlow,beforeRecipe,afterFlowId:null,afterRecipeId:null};
           this.history.unshift(row);
           try{
             const flow=g.flows.handle('/api/flows/draft',{definition:copy(template.config.sequence),expectedActiveVersionId:beforeFlow.id},this.user());
             g.flows.handle('/api/flows/'+flow.id+'/validate',{},this.user());
             const activeFlow=g.flows.handle('/api/flows/'+flow.id+'/publish',{},this.user());row.afterFlowId=activeFlow.id;
             let next=await g.post('/api/recipes/draft',copy(template.recipe),this.user());
             await g.post('/api/recipes/'+next.data.id+'/validate',{},this.user());
             next=await g.post('/api/recipes/'+next.data.id+'/publish',{},this.user());row.afterRecipeId=next.data.id;
             row.status='Succeeded';this.notify('工站 '+stationId+' 流程与配方已升级（离线仿真）');
           }catch(e){
             const recovery=[];
             try{if(g.activeId!==beforeRecipe.id)await g.post('/api/recipes/rollback',{id:beforeRecipe.id},this.user())}catch(x){recovery.push(x.message)}
             try{if(g.flows.activeId!==beforeFlow.id)g.flows.handle('/api/flows/rollback',{id:beforeFlow.id},this.user())}catch(x){recovery.push(x.message)}
             row.status=recovery.length?'ManualRecoveryRequired':'FailedCompensated';row.error=e.message;throw e;
           }finally{row.finishedAt=new Date().toISOString()}
         }
         this.plan=null;await this.onChanged(stationId);await this.refresh();
       }else if(action==='rollback'){
         const row=this.history.find(r=>r.id===this.selectedHistory);if(!row)throw Error('请选择部署记录');
         if(this.backend()){
           let body={deploymentId:row.id},changed;
           try{changed=await this.call('/api/upgrades/rollback',body)}catch(e){
             if(e.status!==409||!e.message.includes('结果未知'))throw e;
             const station=row.stationId;
             const flow=prompt(`部署 ${row.id} 的提交结果未知。先查 GET /api/stations/${station}/flows 与 /api/stations/${station}/recipes 的当前活动版本，并完成现场核对。输入确认看到的流程版本 ID：`);
             if(!flow)throw Error('已取消受控恢复');
             const recipe=prompt('输入确认看到的配方活动版本 ID：');if(!recipe)throw Error('已取消受控恢复');
             const note=prompt('简述版本核对依据（8–500 字符）；管理员将作为恢复证据持久记录：');
             if(!note)throw Error('已取消受控恢复');
             body={deploymentId:row.id,expectedCurrentFlowId:flow.trim(),expectedCurrentRecipeId:recipe.trim(),recoveryNote:note.trim()};
             changed=await this.call('/api/upgrades/rollback',body);
           }
           this.notify('已回滚：'+changed.id);
         }else{
           const target=this.offline[row.stationId],g=target?.governance;
           if(!target||checks({status:'Published',config:target.machine.cfg},target).length)throw Error('工站不处于可回滚的空闲手动状态');
           if(row.status==='Succeeded'&&(g.flows.activeId!==row.afterFlowId||g.activeId!==row.afterRecipeId))throw Error('已有后续发布，不允许覆盖');
           if(g.activeId!==row.beforeRecipe.id)await g.post('/api/recipes/rollback',{id:row.beforeRecipe.id},this.user());
           if(g.flows.activeId!==row.beforeFlow.id)g.flows.handle('/api/flows/rollback',{id:row.beforeFlow.id},this.user());
           row.status='RolledBack';row.finishedAt=new Date().toISOString();this.notify('工站 '+row.stationId+' 已回滚到部署前版本');
         }
         this.plan=null;await this.onChanged(row.stationId);await this.refresh();
       }
     }catch(e){this.error=global.DeviceControlApiClient.describe(e);this.notify(this.error,true)}finally{this.busy=false;this.render()}
   }
 }
 global.StationUpgradeStudio=StationUpgradeStudio;
 global.StationUpgradeRules={structure,checks};
})(window);
