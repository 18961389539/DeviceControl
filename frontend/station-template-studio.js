/* V1.4 governed station profile snapshot library. Offline state is memory-only.
 * Published templates can create NEW simulation stations. No hot replacement. */
(function(global){
  const clone=x=>structuredClone(x);
  const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
  const canonical=id=>{if(typeof id!=='string'||!/^[A-Za-z][A-Za-z0-9_-]{0,15}$/.test(id))throw Error('工站 ID 必须由英文字母开头，长度 1–16');return id.toUpperCase()};
  const flatten=(value,path='',result={})=>{
    if(Array.isArray(value)){value.forEach((v,i)=>flatten(v,path+'['+i+']',result))}
    else if(value&&typeof value==='object')for(const [key,val] of Object.entries(value))flatten(val,path?(path+'.'+key):key,result);
    else result[path]=String(value);
    return result;
  };
  const compare=(template,station)=>{
    const desired=clone(template.config);desired.profileId=station.machine.cfg.profileId;desired.flowVersion=station.machine.cfg.flowVersion;
    const a=flatten({config:desired,recipe:template.recipe}),b=flatten({config:station.machine.cfg,recipe:station.governance.active().values});
    return [...new Set([...Object.keys(a),...Object.keys(b)])].sort().filter(path=>a[path]!==b[path])
      .slice(0,150).map(path=>({path,templateValue:a[path]??null,stationValue:b[path]??null}));
  };
  class StationTemplateStudio {
    constructor({root,backend,token,user,offline,coordinator,notify,onDeployed}){
      Object.assign(this,{root,backend,token,user,offline,coordinator,notify,onDeployed});
      this.api=global.DeviceControlApiClient.create({root});
      this.templates=[];this.selected='';this.diff=[];this.error='';this.busy=false;
      this.panel=document.getElementById('panel-templates');
      this.panel.addEventListener('click',e=>{const b=e.target.closest('button[data-template-action]');if(b&&!b.disabled)void this.act(b.dataset.templateAction)});
      document.getElementById('templateSelect').addEventListener('change',e=>{this.selected=e.target.value;this.diff=[];this.render()});
      document.getElementById('templateCompareStation').addEventListener('change',()=>{this.diff=[];this.render()});
      this.render();
    }
    get selectedRevision(){return this.templates.find(v=>v.id===this.selected)}
    get role(){return ({Operator:1,Engineer:2,Admin:3})[this.user()?.role]||0}
    async call(path,body){
      const res=await this.api(path,{method:body===undefined?'GET':'POST',headers:{'X-Sim-Token':this.token(),'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body)});
      return await res.json();
    }
    stationOptions(){return Object.keys(this.offline).sort();}
    async refresh(){
      if(!this.user()||this.busy)return;
      if(this.backend()){
        try{this.templates=await this.call('/api/templates');const stations=await this.call('/api/stations');
          this.stationIds=stations.stations.map(s=>s.id);
        }catch(e){this.error=e.message;this.render();return}
      }else this.stationIds=this.stationOptions();
      if(!this.templates.some(v=>v.id===this.selected))this.selected=this.templates[0]?.id||'';
      this.render();
    }
    render(){
      const selected=this.selectedRevision,ids=this.stationIds||this.stationOptions(),source=document.getElementById('templateSource'),compareTo=document.getElementById('templateCompareStation');
      for(const select of [source,compareTo]){
        const value=select.value;
        select.innerHTML=ids.map(id=>`<option value="${esc(id)}">工站 ${esc(id)}</option>`).join('');
        if(ids.includes(value))select.value=value;
      }
      const select=document.getElementById('templateSelect');select.innerHTML=this.templates.map(r=>`<option value="${esc(r.id)}">${esc(r.name)} · ${esc(r.status)} · ${esc(r.id)}</option>`).join('')||'<option value="">暂无模板</option>';
      select.value=this.selected;
      document.getElementById('templateSummary').innerHTML=selected?`<b>${esc(selected.name)}</b><span>来源 ${esc(selected.sourceStation)} · ${esc(selected.status)}</span><small>版本 ${esc(selected.id)} · SHA256 ${esc((selected.sha256||'').slice(0,18))}…</small>`:'从已发布的工站流程与配方捕获一个草稿模板。';
      const enabled=this.user()&&!this.busy;
      for(const button of this.panel.querySelectorAll('button[data-template-action]')){
        const action=button.dataset.templateAction;
        const role=action==='capture'||action==='validate'||action==='compare'?2:3;
        button.disabled=!enabled||this.role<role||(action!=='capture'&&!selected)||(action==='validate'&&selected?.status!=='Draft')||(action==='publish'&&selected?.status!=='Validated')||(action==='deploy'&&selected?.status!=='Published');
      }
      document.getElementById('templateDiffCount').textContent=this.diff.length+' 项差异';
      document.getElementById('templateDiffRows').innerHTML=this.diff.length?this.diff.map(r=>`<tr><td><code>${esc(r.path)}</code></td><td>${esc(r.stationValue??'（不存在）')}</td><td>${esc(r.templateValue??'（不存在）')}</td></tr>`).join(''):'<tr><td colspan="3" class="subtle">请选择模板与工站并点击“比较配置”</td></tr>';
      document.getElementById('templateError').textContent=this.error;
    }
    async act(action){
      if(!this.user()||this.busy)return;
      const min=action==='capture'||action==='validate'||action==='compare'?2:3;
      if(this.role<min){this.notify('当前角色无权执行模板操作',true);return}
      this.busy=true;this.error='';this.render();
      try {
        const revision=this.selectedRevision;
        if(action==='capture'){
          const stationId=document.getElementById('templateSource').value,name=document.getElementById('templateName').value.trim();
          if(!name||name.length>60)throw Error('模板名称为 1–60 字符');
          if(this.backend()){
            const response=await this.call('/api/templates/capture',{stationId,name});this.selected=response.data.id;
          }else{
            const station=this.offline[stationId],config=clone(station.machine.cfg);
            config.sequence=clone(station.governance.flows.active().definition);config.flowVersion='template-candidate';
            const recipe=clone(station.governance.active().values);
            const id='tmpl-'+String(Date.now())+'-'+(this.templates.length+1);
            const row={id,name,status:'Draft',sourceStation:stationId,author:this.user().name,createdAt:new Date().toISOString(),sha256:'offline-only',config,recipe};
            this.templates.unshift(row);this.selected=id;
          }
        } else if(action==='validate'||action==='publish'){
          if(!revision)throw Error('请选择模板');
          if(this.backend())await this.call('/api/templates/'+encodeURIComponent(revision.id)+'/'+action,{});
          else{
            if(action==='validate'&&revision.status==='Draft'){
              const errors=window.FlowStudioModel.validateFlow(revision.config.sequence,revision.config);
              if(errors.length)throw Error(errors.join('；'));
              this.offline[revision.sourceStation].governance.check(revision.recipe);
              revision.status='Validated';
            }else if(action==='publish'&&revision.status==='Validated')revision.status='Published';
            else throw Error('模板状态不允许 '+action);
          }
        } else if(action==='compare'){
          const target=document.getElementById('templateCompareStation').value;
          if(!revision)throw Error('未选择模板');
          const result=this.backend()?await this.call('/api/templates/'+encodeURIComponent(revision.id)+'/diff/'+encodeURIComponent(target)):
            {data:{changes:compare(revision,this.offline[target])}};
          this.diff=result.data.changes||[];
        } else if(action==='deploy'){
          if(!revision||revision.status!=='Published')throw Error('模板尚未发布');
          const raw=document.getElementById('templateNewIds').value;
          const ids=raw.split(/[,，\s]+/).map(x=>x.trim()).filter(Boolean).map(canonical);
          if(!ids.length||ids.length>5||new Set(ids).size!==ids.length)throw Error('每次允许 1–5 个不重复的新工站 ID');
          if(this.backend()){
            const response=await this.call('/api/templates/'+encodeURIComponent(revision.id)+'/deploy',{stationIds:ids});
            if(!response.data.ok){await this.onDeployed(response.data.created||[]);throw Error('部分部署失败：'+response.data.error+'；已创建 '+response.data.created.join(','))}
          }else{
            if(Object.keys(this.offline).length+ids.length>12||ids.some(id=>this.offline[id]))throw Error('工站 ID 已存在或超过容量 12');
            const staged=ids.map(id=>{
              const config=clone(revision.config);config.profileId='DEMO-STATION-'+id;config.flowVersion='template-'+id;
              const machine=new MockRuntime(config);machine.stationId=id;machine.sharedCoordinator=this.coordinator;
              const governance=new GovernanceMock(machine);
              governance.versions[0].values=clone(revision.recipe);
              governance.machine.cycleRecipe=clone(revision.recipe);
              return [id,{machine,governance}];
            });
            staged.forEach(([id,value])=>this.offline[id]=value);
          }
          await this.onDeployed(ids);
          document.getElementById('templateNewIds').value='';
          this.notify('模板已部署到新工站 '+ids.join(', ')+'（Simulation）');
        }
        if(action!=='compare')this.diff=[];
        this.notify('模板操作完成：'+action);
      }catch(e){this.error=global.DeviceControlApiClient.describe(e);this.notify(this.error,true)}
      finally{this.busy=false;if(this.backend()&&this.user()&&action!=='compare')await this.refresh();else this.render()}
    }
  }
  global.StationTemplateStudio=StationTemplateStudio;
  global.StationTemplateDiff={canonical,flatten,compare};
})(window);
