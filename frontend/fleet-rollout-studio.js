/* V1.7 maintenance windows and manual recovery over V1.6 graduated rollout. Browser service is ephemeral; no hardware.
 * Durable .NET service independently enforces the same server-side gates. */
(function(global){
'use strict';
const clone=v=>structuredClone(v);
const safe=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const open=x=>!['Completed','Aborted'].includes(x);
const stamp=()=>new Date().toISOString();
let seq=0;
class OfflineRollouts{
  constructor({offline,templates,upgrades}){Object.assign(this,{offline,templates,upgrades});this.rows=[]}
  list(){return this.rows}
  get(id){const row=this.rows.find(x=>x.id===id);if(!row)throw Error('找不到灰度批次');return row}
  create({templateId,pilotStationId,stationIds,waveSize,windowStartUtc,windowEndUtc},actor){
    const tmpl=this.templates().find(x=>x.id===templateId);
    if(!tmpl||tmpl.status!=='Published')throw Error('必须选择已发布模板');
    const ids=stationIds.map(x=>x.trim().toUpperCase());const pilot=pilotStationId.toUpperCase();
    if(ids.length<2||ids.length>12||ids.some(x=>!this.offline[x])||new Set(ids).size!==ids.length||!ids.includes(pilot))throw Error('请选择 2–12 个互不重复的现有工站，并包含试点');
    if(!Number.isInteger(waveSize)||waveSize<1||waveSize>3)throw Error('每批只允许 1–3 个工站');
    if(this.rows.some(r=>open(r.status)&&r.steps.some(s=>ids.includes(s.stationId))))throw Error('工站已归属其他未关闭批次');
    if(Boolean(windowStartUtc)!==Boolean(windowEndUtc))throw Error('维护窗口必须同时设置开始和结束');
    if(windowStartUtc){
      const start=Date.parse(windowStartUtc),end=Date.parse(windowEndUtc);
      if(!Number.isFinite(start)||!Number.isFinite(end)||end<=start||end-start>7*86400000)throw Error('维护窗口必须为正时长且不超过 7 天');
    }
    const order=[pilot,...ids.filter(x=>x!==pilot)];
    const steps=order.map(id=>({stationId:id,expectedFlowId:this.offline[id].governance.flows.activeId,
      expectedRecipeId:this.offline[id].governance.activeId,status:'Pending'}));
    const row={id:'roll-'+String(++seq).padStart(4,'0'),templateId,templateHash:tmpl.sha256,pilotStationId:pilot,waveSize,actor,createdAt:stamp(),updatedAt:stamp(),status:'PilotPending',steps,reason:null,windowStartUtc:windowStartUtc||null,windowEndUtc:windowEndUtc||null};
    this.rows.unshift(row);return row;
  }
  windowOpen(row){return !row.windowStartUtc||(Date.now()>=Date.parse(row.windowStartUtc)&&Date.now()<Date.parse(row.windowEndUtc))}
  diagnose(id){
    const row=this.get(id),blockers=[],windowOpen=this.windowOpen(row);
    if(!windowOpen)blockers.push('当前不在 UTC 维护窗口内；不能开始部署');
    if(['Paused','Interrupted'].includes(row.status))blockers.push('批次暂停或中断，须人工核查');
    const stations=row.steps.map(s=>{
      const item=this.offline[s.stationId],g=item.governance,state=item.machine.snapshot();const errors=[];
      if(state.busy||state.state!=='Idle'||state.mode!=='Manual')errors.push('不是空闲手动模式');
      if(state.recoveryRequired||state.alarm)errors.push('存在报警或恢复请求');
      const expectedFlow=['Verified','AwaitingEvidence'].includes(s.status)?s.afterFlowId:s.expectedFlowId;
      const expectedRecipe=['Verified','AwaitingEvidence'].includes(s.status)?s.afterRecipeId:s.expectedRecipeId;
      if(g.flows.activeId!==expectedFlow||g.activeId!==expectedRecipe)errors.push('流程/配方版本不一致');
      const unresolved=this.upgrades().filter(x=>x.stationId===s.stationId&&['InProgress','Interrupted','ManualRecoveryRequired'].includes(x.status)).map(x=>x.id+':'+x.status);
      if(unresolved.length)errors.push('存在未解决的单站部署日志');
      return {stationId:s.stationId,stepStatus:s.status,mode:state.mode,machineState:state.state,busy:state.busy,alarm:state.alarm,recoveryRequired:state.recoveryRequired,activeFlowId:g.flows.activeId,activeRecipeId:g.activeId,unresolvedDeployments:unresolved,blockers:errors};
    });
    return {rolloutId:id,status:row.status,windowOpen,windowStartUtc:row.windowStartUtc,windowEndUtc:row.windowEndUtc,blockers,stations};
  }
  reconcile(id,{stationId,note},actor){
    const row=this.get(id),step=row.steps.find(x=>x.stationId===stationId);
    if(!['Paused','Interrupted'].includes(row.status))throw Error('只能在暂停或中断批次执行人工恢复核对');
    if(!note||note.trim().length<8||note.length>500)throw Error('人工核对备注必须为 8～500 字符');
    if(!step||!['Failed','Applying'].includes(step.status))throw Error('工站没有待人工核对的失败步骤');
    const item=this.offline[stationId],g=item.governance,state=item.machine.snapshot();
    if(state.busy||state.state!=='Idle'||state.mode!=='Manual'||state.recoveryRequired||state.alarm)throw Error('工站必须空闲、手动、无报警');
    if(g.flows.activeId!==step.expectedFlowId||g.activeId!==step.expectedRecipeId)throw Error('不在原始版本基线上（内容恢复会发布新的版本 ID）；旧批次不能续跑，请关闭批次后基于当前版本重建');
    if(this.upgrades().some(x=>x.stationId===stationId&&['InProgress','Interrupted','ManualRecoveryRequired'].includes(x.status)))throw Error('单站部署日志尚未解决');
    step.status='Pending';step.error=null;step.recoveryActor=actor;step.recoveryNote=note.trim();step.reconciledAt=stamp();
    row.status='Paused';row.reason='人工核对已记录，确认后可继续';row.updatedAt=stamp();return row;
  }
  async advance(id,actor){
    const row=this.get(id);
    if(!this.windowOpen(row))throw Error('当前不在 UTC 维护窗口内，拒绝开始部署');if(!['PilotPending','WaveReady'].includes(row.status))throw Error('请先完成当前批次验证或解除暂停');
    for(const s of row.steps.filter(x=>x.status==='Verified')){
      const item=this.offline[s.stationId],snap=item.machine.snapshot(),g=item.governance;
      if(g.flows.activeId!==s.afterFlowId||g.activeId!==s.afterRecipeId||snap.recoveryRequired||snap.alarm)
        throw Error(s.stationId+' 已验收工站版本/状态发生漂移，禁止扩批');
    }
    const tmpl=this.templates().find(x=>x.id===row.templateId);if(!tmpl||tmpl.status!=='Published'||tmpl.sha256!==row.templateHash)throw Error('模板已经变化');
    const target=row.steps.filter(s=>s.status==='Pending').slice(0,row.status==='PilotPending'?1:row.waveSize);
    if(!target.length)throw Error('不存在待部署工站');
    row.status='Deploying';row.updatedAt=stamp();
    for(const step of target){
      if(!this.windowOpen(row)){
        row.status='Paused';row.reason='维护窗口已关闭；不再执行后续工站';row.updatedAt=stamp();return row;
      }
      const item=this.offline[step.stationId],g=item.governance;
      if(g.flows.activeId!==step.expectedFlowId||g.activeId!==step.expectedRecipeId){step.status='Failed';step.error='流程或配方版本冲突';row.status='Paused';row.reason=step.error;return row}
      const issues=global.StationUpgradeRules.checks(tmpl,item);
      if(this.upgrades().some(x=>x.stationId===step.stationId&&['InProgress','Interrupted','ManualRecoveryRequired'].includes(x.status)))issues.push('存在未解决的单站部署');
      if(issues.length){step.status='Failed';step.error=issues.join('；');row.status='Paused';row.reason=step.stationId+' '+step.error;return row}
      step.status='Applying';
      const beforeFlow=clone(g.flows.active()),beforeRecipe=clone(g.active());
      try{
        const draft=g.flows.handle('/api/flows/draft',{definition:clone(tmpl.config.sequence),expectedActiveVersionId:beforeFlow.id},{name:actor,role:'Admin'});
        g.flows.handle('/api/flows/'+draft.id+'/validate',{},{name:actor,role:'Admin'});
        const afterFlow=g.flows.handle('/api/flows/'+draft.id+'/publish',{},{name:actor,role:'Admin'});
        let recipe=await g.post('/api/recipes/draft',clone(tmpl.recipe),{name:actor,role:'Admin'});
        await g.post('/api/recipes/'+recipe.data.id+'/validate',{},{name:actor,role:'Admin'});
        recipe=await g.post('/api/recipes/'+recipe.data.id+'/publish',{},{name:actor,role:'Admin'});
        step.status='AwaitingEvidence';step.deploymentId='offline-dep-'+(++seq);step.afterFlowId=afterFlow.id;step.afterRecipeId=recipe.data.id;step.appliedAt=stamp();
      }catch(e){
        const failure=[];
        try{if(g.activeId!==beforeRecipe.id)await g.post('/api/recipes/rollback',{id:beforeRecipe.id},{name:actor,role:'Admin'})}catch(x){failure.push('配方补偿失败：'+x.message)}
        try{if(g.flows.activeId!==beforeFlow.id)g.flows.handle('/api/flows/rollback',{id:beforeFlow.id},{name:actor,role:'Admin'})}catch(x){failure.push('流程补偿失败：'+x.message)}
        step.status='Failed';step.error=e.message+(failure.length?'；'+failure.join('；'):'');row.status='Paused';row.reason=step.stationId+' 部署失败：'+step.error;return row;
      }
    }
    row.status='AwaitingEvidence';row.updatedAt=stamp();return row;
  }
  inspect(id){
    const row=this.get(id),blockers=[],verifiedStations=[];
    const pending=row.steps.filter(x=>x.status==='AwaitingEvidence');
    if(!pending.length)blockers.push('没有待验证工站');
    for(const step of pending){
      const item=this.offline[step.stationId],g=item.governance,s=item.machine.snapshot();
      if(s.state!=='Idle'||s.busy||s.recoveryRequired||s.alarm){blockers.push(step.stationId+' 存在运行状态或报警问题');continue}
      if(g.flows.activeId!==step.afterFlowId||g.activeId!==step.afterRecipeId){blockers.push(step.stationId+' 流程/配方版本漂移');continue}
      const report=global.WorkflowQuality.runSuite(g.flows.active().definition,item.machine.cfg);
      if(!report.pass){blockers.push(step.stationId+' 流程质量门禁失败');continue}
      const runs=s.cycleHistory.filter(x=>Date.parse(x.startedAt)>=Date.parse(step.appliedAt));
      if(runs.some(x=>['Failed','Stopped','Interrupted'].includes(x.outcome))){blockers.push(step.stationId+' 部署后存在异常周期');continue}
      const good=runs.find(x=>x.outcome==='Completed'&&x.flowVersion===step.afterFlowId&&x.recipeId===step.afterRecipeId);
      if(!good){blockers.push(step.stationId+' 缺少升级后的合格仿真周期');continue}
      verifiedStations.push(step.stationId+':'+good.id);
    }
    return {pass:!blockers.length,blockers,verifiedStations};
  }
  verify(id){const row=this.get(id);if(row.status!=='AwaitingEvidence')throw Error('当前状态无法验证');
    const check=this.inspect(id);if(!check.pass)throw Error(check.blockers.join('；'));
    for(const s of row.steps.filter(x=>x.status==='AwaitingEvidence')){s.status='Verified';s.evidenceCycleId=check.verifiedStations.find(x=>x.startsWith(s.stationId+':')).split(':')[1]}
    row.status=row.steps.every(s=>s.status==='Verified')?'Completed':'WaveReady';row.reason=null;row.updatedAt=stamp();return row;
  }
  pause(id,reason){const r=this.get(id);if(!['PilotPending','WaveReady','AwaitingEvidence'].includes(r.status))throw Error('状态不允许暂停');r.status='Paused';r.reason=reason||'管理员暂停';return r}
  resume(id){const r=this.get(id);if(r.status!=='Paused')throw Error('当前不是暂停状态');if(r.steps.some(s=>['Failed','Applying'].includes(s.status)))throw Error('存在失败/中断步骤，需要人工处理后关闭批次');r.status=r.steps.some(s=>s.status==='AwaitingEvidence')?'AwaitingEvidence':r.steps[0].status==='Pending'?'PilotPending':'WaveReady';r.reason=null;return r}
  abort(id,reason){const r=this.get(id);if(!open(r.status))throw Error('批次已关闭');r.status='Aborted';r.reason='人工结束：'+(reason||'');return r}
}
class FleetRolloutStudio{
 constructor({root,backend,token,user,offline,templates,upgradeStudio,notify,onChanged}){
  Object.assign(this,{root,backend,token,user,offline,templates,upgradeStudio,notify,onChanged});
 this.api=global.DeviceControlApiClient.create({root});
  this.mock=new OfflineRollouts({offline,templates,upgrades:()=>upgradeStudio.history});
  this.rows=[];this.selected='';this.busy=false;this.info='';this.recovery=null;
  this.el=document.getElementById('rolloutDashboard');
  this.el.addEventListener('click',e=>{const b=e.target.closest('[data-rollout]');if(b&&!b.disabled)void this.act(b.dataset.rollout)});
  document.getElementById('rolloutHistory').addEventListener('change',e=>{this.selected=e.target.value;this.diagnostics=null;this.recovery=null;this.render()});
  this.render();
 }
 get role(){return ({Operator:1,Engineer:2,Admin:3})[this.user()?.role]||0}
 get current(){return this.rows.find(x=>x.id===this.selected)}
 async call(path,body){const r=await this.api(path,{method:body===undefined?'GET':'POST',headers:{'X-Sim-Token':this.token(),'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),station:this.target?.()||undefined});return (await r.json()).data}
 async refresh(){try{this.rows=this.backend()?await this.call('/api/rollouts'):this.mock.list();if(!this.rows.some(x=>x.id===this.selected))this.selected=this.rows[0]?.id||'';this.render()}catch(e){this.info=e.message;this.render()}}
 render(){
  const select=document.getElementById('rolloutHistory'),old=this.selected;
  select.innerHTML=this.rows.map(x=>`<option value="${safe(x.id)}">${safe(x.id.slice(-8))} · ${safe(x.status)} · ${safe(x.steps.map(s=>s.stationId).join('/'))}</option>`).join('')||'<option value="">暂无灰度批次</option>';
  if(this.rows.some(x=>x.id===old))select.value=old;this.selected=select.value;
  const ids=global.StationWorkspace.stationIds;
  const pilot=document.getElementById('rolloutPilot');const former=pilot.value;
  pilot.innerHTML=ids.map(id=>`<option value="${safe(id)}">工站 ${safe(id)}</option>`).join('');pilot.value=ids.includes(former)?former:ids[0];
  const x=this.current,completed=x?.steps.filter(s=>s.status==='Verified').length||0,total=x?.steps.length||0;
  document.getElementById('rolloutProgress').innerHTML=x?`<div class="rollout-summary"><strong>${safe(x.status)}</strong><span>已验收 ${completed} / ${total} · 试点 ${safe(x.pilotStationId)} · 每批 ${x.waveSize} 站</span></div><div class="rollout-meter"><div style="width:${total?Math.round(completed/total*100):0}%"></div></div><div class="control-hint">${safe(x.reason||'升级后必须有新版本的成功仿真周期及流程质量报告，才能继续扩批。')}</div>`:'尚未创建灰度批次。';
  document.getElementById('rolloutSteps').innerHTML=x?x.steps.map((s,i)=>`<div class="rollout-step"><span class="rollout-ordinal">${i+1}</span><strong>工站 ${safe(s.stationId)}${i===0?' · 试点':''}</strong><span class="rollout-state">${safe(s.status)}</span><small>${safe(s.error||(s.evidenceCycleId?'验收周期 '+s.evidenceCycleId:'流程 '+s.expectedFlowId+' → '+(s.afterFlowId||'待部署')+' · 配方 '+s.expectedRecipeId+' → '+(s.afterRecipeId||'待部署')))}</small></div>`).join(''):'<div class="control-hint">创建后显示各工站部署进度。</div>';
  const recovery=document.getElementById('rolloutRecovery');
  recovery.innerHTML=this.recovery?`<div class="control-hint"><strong>${this.recovery.simulated?'隔离演练 / '+safe(this.recovery.drillScenario):'批次 '+safe(this.recovery.rolloutId)}</strong> · ${this.recovery.requiresManualReview?'需要人工核对':'未发现阻断条件'} · 只读，不自动恢复</div>`+
   this.recovery.stations.map(item=>`<div class="rollout-step"><strong>工站 ${safe(item.stationId)} · ${safe(item.stepStatus)}</strong><span class="rollout-state">${safe(item.category)}</span><small>原始版本匹配 ${item.baselineMatches?'是':'否'} · 目标版本匹配 ${item.targetMatches?'是':'否'} · 备份 ${safe(item.backupStatus||'Unknown')} · ${safe(item.blockers.join('；')||'无检测到的阻断')}</small></div>`).join(''):'<div class="control-hint">选择批次查询只读恢复报告，或运行不影响工站的故障演练。</div>';
  document.getElementById('rolloutMessage').textContent=this.info;
  const wLabel=x?.windowStartUtc?'UTC '+new Date(x.windowStartUtc).toLocaleString('zh-CN',{timeZone:'UTC',hour12:false})+' — '+new Date(x.windowEndUtc).toLocaleString('zh-CN',{timeZone:'UTC',hour12:false}):'未设置 · 随时允许人工部署';
  document.getElementById('rolloutWindowState').textContent=x?'维护窗口：'+wLabel:'可选维护窗口只限制开始升级，不会自动部署';
  const diag=document.getElementById('rolloutDiagnostics');
  diag.innerHTML=this.diagnostics&&x&&this.diagnostics.rolloutId===x.id?`<div class="control-hint">${this.diagnostics.windowOpen?'维护窗口开放':'维护窗口关闭'} · ${safe(this.diagnostics.blockers.join('；')||'批次无全局阻塞')}</div>`+this.diagnostics.stations.map(v=>`<div class="rollout-step"><strong>${safe(v.stationId)} · ${safe(v.machineState)} / ${safe(v.mode)}</strong><span class="rollout-state">${v.blockers.length?'待核查':'正常'}</span><small>${safe(v.blockers.join('；')||'版本与状态检查通过')}${v.unresolvedDeployments.length?' · '+safe(v.unresolvedDeployments.join(',')):''}</small></div>`).join(''):'<div class="control-hint">点击“诊断批次”查看工站版本、模式、报警和升级日志。</div>';
  const reconcileSelect=document.getElementById('rolloutReconcileStation');const oldStation=reconcileSelect.value;
  reconcileSelect.innerHTML=(x?.steps||[]).filter(s=>['Failed','Applying'].includes(s.status)).map(s=>`<option value="${safe(s.stationId)}">${safe(s.stationId)} · ${safe(s.status)}</option>`).join('')||'<option value="">没有待核对步骤</option>';
  if([...reconcileSelect.options].some(o=>o.value===oldStation))reconcileSelect.value=oldStation;
  for(const b of this.el.querySelectorAll('[data-rollout]')){
    const action=b.dataset.rollout;
    const permitted=['refresh','inspect','diagnose','recovery-report','drill'].includes(action)?this.role>=2:this.role>=3;
    const statuses={reconcile:['Paused','Interrupted'],advance:['PilotPending','WaveReady'],verify:['AwaitingEvidence'],pause:['PilotPending','WaveReady','AwaitingEvidence'],resume:['Paused'],abort:['PilotPending','WaveReady','AwaitingEvidence','Paused','Interrupted']};
    b.disabled=this.busy||!this.user()||!permitted||(statuses[action]&&!statuses[action].includes(x?.status))||(['inspect','diagnose','reconcile','recovery-report'].includes(action)&&!x)||(action==='reconcile'&&!x?.steps.some(s=>['Failed','Applying'].includes(s.status)));
  }
 }
 async act(action){if(this.busy||!this.user()||this.role<(['refresh','inspect','diagnose','recovery-report','drill'].includes(action)?2:3))return;
  this.busy=true;this.info='';this.render();
  try{
    const id=this.selected,actor=this.user().name;
    if(action==='refresh'){await this.refresh();return}
    if(action==='create'){
      const tmpl=this.templates().find(x=>x.id===document.getElementById('templateSelect').value);
      if(!tmpl)throw Error('请先选择已发布模板');
      const raw=document.getElementById('rolloutIds').value;
      const stationIds=raw.split(/[,，\s]+/).map(x=>x.trim().toUpperCase()).filter(Boolean);
      const start=document.getElementById('rolloutWindowStart').value,end=document.getElementById('rolloutWindowEnd').value;
      const body={templateId:tmpl.id,pilotStationId:document.getElementById('rolloutPilot').value,stationIds,waveSize:Number(document.getElementById('rolloutWaveSize').value),
        windowStartUtc:start?new Date(start).toISOString():null,windowEndUtc:end?new Date(end).toISOString():null};
      const row=this.backend()?await this.call('/api/rollouts',body):this.mock.create(body,actor);this.selected=row.id;
      this.notify('灰度批次已创建，请先升级试点');
    }else if(action==='diagnose'){
      this.diagnostics=this.backend()?await this.call('/api/rollouts/'+id+'/diagnostics'):this.mock.diagnose(id);
      this.info=this.diagnostics.blockers.length?this.diagnostics.blockers.join('；'):'诊断报告已更新';
    }else if(action==='recovery-report'){
      if(!this.current)throw Error('请先选择灰度批次');
      if(this.backend())this.recovery=await this.call('/api/rollouts/'+id+'/recovery-report');
      else{
        const stations={};for(const step of this.current.steps){const item=this.offline[step.stationId];const state=item.machine.snapshot();
          stations[step.stationId]={flowId:item.governance.flows.activeId,recipeId:item.governance.activeId,
            state:state.state,mode:state.mode,busy:state.busy,recoveryRequired:state.recoveryRequired,alarm:state.alarm};}
        this.recovery=global.RecoveryScenarios.evaluate(this.current,stations,this.upgradeStudio.history);
      }
      this.info='已生成只读一致性报告，不会执行部署或恢复';
    }else if(action==='drill'){
      this.recovery=global.RecoveryScenarios.drill(document.getElementById('recoveryScenario').value);
      this.info='已在隔离数据中模拟异常；原批次和工站保持不变';
    }else if(action==='reconcile'){
      const stationId=document.getElementById('rolloutReconcileStation').value;
      const note=document.getElementById('rolloutReconcileNote').value;
      const body={stationId,note};
      try{
        if(this.backend())await this.call('/api/rollouts/'+id+'/reconcile',body);else this.mock.reconcile(id,body,actor);
        this.info='已记录人工核对，可按当前批次状态继续（不会自动部署）';
        this.diagnostics=null;
      }catch(e){
        // R01 (conservative): a single-station rollback restores CONTENT but publishes
        // NEW version IDs. The old batch can never return to its recorded baseline,
        // so show the替代 steps explicitly instead of leaving a bare 409.
        if(/原始版本基线/.test(e.message||'')){
          this.info='该工站已不在批次原始版本基线上（内容恢复会发布新的版本 ID，内容恢复不等于 ID 恢复）。替代路径：① 终止（Abort）关闭本批次 → ② 在「⑤ 已有工站」升级历史中确认已恢复的版本 → ③ 基于当前版本重建灰度批次。旧批次不会自动续跑，也不放宽基线比较。';
          this.notify('旧批次不能在此工站续跑：请关闭本批次并基于当前版本重建',true);
        } else throw e;
      }
    }else if(action==='inspect'){
      const check=this.backend()?await this.call('/api/rollouts/'+id+'/evidence'):this.mock.inspect(id);
      this.info=check.pass?'质量与周期证据齐全，可以确认验收':check.blockers.join('；');this.notify(this.info,!check.pass);
    }else{
      if(action==='advance'){
        const result=this.backend()?await this.call('/api/rollouts/'+id+'/advance',{}):await this.mock.advance(id,actor);
        this.info=result.reason||'本批次已部署，请完成每站仿真周期验证';
        for(const s of result.steps.filter(x=>x.status==='AwaitingEvidence'))await this.onChanged(s.stationId);
      }else if(action==='verify'){
        const result=this.backend()?await this.call('/api/rollouts/'+id+'/verify',{}):this.mock.verify(id);
        this.info=result.status==='Completed'?'灰度部署全部验收完成':'当前批次已验收，可以升级下一批';
      }else if(action==='pause'){
        const reason=document.getElementById('rolloutReason').value.trim();
        if(this.backend())await this.call('/api/rollouts/'+id+'/pause',{reason});else this.mock.pause(id,reason);
      }else if(action==='resume'){
        if(this.backend())await this.call('/api/rollouts/'+id+'/resume',{});else this.mock.resume(id);
      }else if(action==='abort'){
        const reason=document.getElementById('rolloutReason').value.trim();
        if(this.backend())await this.call('/api/rollouts/'+id+'/abort',{reason});else this.mock.abort(id,reason);
        this.info='批次已关闭。已部署的工站不会自动回滚；请在单站升级历史中处理。';
      }
      this.notify(this.info||'灰度批次状态已更新');
    }
    await this.refresh();
  }catch(e){this.info=global.DeviceControlApiClient.describe(e);this.notify(this.info,true)}finally{this.busy=false;this.render()}
 }
}
global.OfflineRollouts=OfflineRollouts;global.FleetRolloutStudio=FleetRolloutStudio;
})(window);
