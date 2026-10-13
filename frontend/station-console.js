/* V1.1 isolated A/B station control, same API semantics online/offline.
 * Online failure locks this panel; there is no mid-session silent simulator fallback. */
(function(global){
  const safe=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
  class StationConsole {
    constructor({root,backend,token,user,offline,coordinator,notify,openStation}){
      this.root=root;this.backend=backend;this.token=token;this.user=user;this.offline=offline;this.coordinator=coordinator;this.notify=notify;this.openStation=openStation;
      this.api=global.DeviceControlApiClient.create({root});
      this.overviews=[];this.snaps={};this.owner=null;this.resources={leases:[],queue:[],events:[]};this.log=[];
      this.locked=false;this.busy=false;this.polling=false;
      this.panel=document.getElementById('panel-stations');
      this.panel.addEventListener('click',e=>{const open=e.target.closest('button[data-fleet-open]');if(open&&!open.disabled){void this.openStation(open.dataset.fleetOpen);return}const btn=e.target.closest('button[data-fleet-action]');if(btn&&!btn.disabled)
        void this.action(btn.dataset.stationId,btn.dataset.fleetAction);
      });
      document.getElementById('fleetRefresh').addEventListener('click',()=>void this.refresh());
      setInterval(()=>{if(this.panel.classList.contains('active')&&this.user()&&!this.busy)void this.refresh()},180);
      this.render();
    }
    record(message,error=false){this.log.unshift({time:new Date().toLocaleTimeString(),message,error});this.log.length=Math.min(35,this.log.length);this.renderLog();this.notify(message,error)}
    renderLog(){const dest=document.getElementById('fleetLog');dest.innerHTML=this.log.length?this.log.map(x=>`<div class="fleet-log-entry ${x.error?'error':''}">${safe(x.time)} · ${safe(x.message)}</div>`).join(''):'尚无操作记录'}
    can(action,s){const role=({Operator:1,Engineer:2,Admin:3})[this.user()?.role]||0;
      if(this.locked||this.busy||!role)return false;
      const ready=s.state==='Idle'&&!s.busy;
      if(action==='prepare')return role>=2&&ready&&s.mode==='Manual';
      if(action==='auto')return role>=1&&ready&&s.mode==='Manual'&&!s.recoveryRequired;
      if(action==='manual')return role>=2&&ready&&s.mode==='Auto';
      if(action==='start')return role>=1&&ready&&s.mode==='Auto'&&!s.recoveryRequired;
      if(action==='stop')return role>=1&&['Running','Stopping'].includes(s.state);
      if(action==='reset')return role>=2&&s.state==='Fault'&&!s.busy;
      if(action==='confirm')return role>=2&&ready&&s.mode==='Manual'&&s.recoveryRequired;
      return false;
    }
    render(){
      const rows=this.overviews||[];
      document.getElementById('fleetCount').textContent=String(rows.length);
      document.getElementById('fleetRunning').textContent=String(rows.filter(x=>x.busy).length);
      document.getElementById('fleetFaults').textContent=String(rows.filter(x=>x.state==='Fault'||x.recoveryRequired).length);
      document.getElementById('fleetOwner').textContent=this.owner?'工站 '+this.owner:'空闲';
      const labels={prepare:'准备轴 / 回零',auto:'切换自动',manual:'切换手动',start:'运行周期',stop:'停止周期',reset:'报警复位',confirm:'确认恢复'};
      const box=document.getElementById('fleetStations');
      box.innerHTML=rows.length?rows.map(s=>{
        const snap=this.snaps[s.id]||s;
        const actionButtons=Object.entries(labels).map(([k,name])=>`<button class="btn ${k==='start'?'btn-primary':k==='stop'?'btn-danger':'btn-light'}" data-station-id="${safe(s.id)}" data-fleet-action="${k}" ${this.can(k,s)?'':'disabled'}>${safe(name)}</button>`).join('');
        const flow=s.flowVersion||'—',recipe=s.recipeId||'—',last=(snap.cycleHistory||[])[0];
        return `<article class="fleet-station ${s.state==='Fault'||s.recoveryRequired?'problem':s.busy?'running':''}" data-fleet-station="${safe(s.id)}">
        <div class="fleet-station-head"><div><div class="eyebrow">STATION ${safe(s.id)} · SIMULATION</div><h2>工站 ${safe(s.id)}</h2><small>${safe(s.profileId)}</small></div>
        <span class="pill ${s.state==='Fault'||s.recoveryRequired?'alert':s.busy?'neutral':''}">${safe(s.state==='Fault'?'故障':s.recoveryRequired?'待恢复':s.busy?'执行中':'待机')} · ${safe(s.mode)}</span></div>
        <div class="fleet-station-metrics"><div><small>完成周期</small><b>${Number(s.cycles)||0}</b></div><div><small>状态</small><b>${safe(s.state)}</b></div><div><small>运行结果</small><b>${safe(last?.outcome||'—')}</b></div></div>
        <div class="fleet-step"><span>当前步骤</span><b>${safe(snap.step||'等待操作')}</b></div>
        <div class="fleet-buttons">${actionButtons}</div><button class="btn fleet-workspace-action" data-fleet-open="${safe(s.id)}" ${this.user()?'':'disabled'}>↗ 打开工站 ${safe(s.id)} 工作区</button>
        <div class="fleet-info"><span>流程 <code>${safe(flow)}</code></span><span>配方 <code>${safe(recipe)}</code></span><span class="${s.alarm?'alarm':''}">报警：${safe(s.alarm||'无')}</span></div></article>`;
      }).join(''):'<div class="card">尚未取得工站状态。请登录或刷新。</div>';
      const resources=this.resources||{leases:[],queue:[]};
      document.getElementById('fleetQueueCount').textContent='等待 '+resources.queue.length;
      const held=resources.leases.map(x=>`<div class="fleet-resource-line"><b>${safe(x.resource)}</b><span>工站 ${safe(x.stationId)} · ${safe(x.nodeId)}</span></div>`).join('');
      const queue=resources.queue.map(x=>`<div class="fleet-resource-line"><b>#${Number(x.position)||'?' } · 工站 ${safe(x.stationId)}</b><span>${safe(x.nodeId)} · ${safe((x.resources||[]).join(', '))}</span></div>`).join('');
      document.getElementById('fleetResourceRows').innerHTML=`<div class="fleet-resource-lane"><h3>已分配 LEASES · ${resources.leases.length}</h3>${held||'<div class="fleet-resource-empty">当前无占用，可并发运行非共享节点</div>'}</div><div class="fleet-resource-lane"><h3>FIFO 等待 QUEUE · ${resources.queue.length}</h3>${queue||'<div class="fleet-resource-empty">暂无排队请求</div>'}</div>`;
      document.getElementById('fleetRefresh').disabled=this.busy||!this.user()||this.locked;
    }
    async request(path,body){
      const write=body!==undefined;
      const response=await this.api(path,{method:write?'POST':'GET',headers:{'Content-Type':'application/json','X-Sim-Token':this.token()},body:write?JSON.stringify(body):undefined});
      return await response.json();
    }
    async refresh(){
      if(!this.user()||this.polling||this.locked)return;
      this.polling=true;
      try{
        if(this.backend()){
          const [result,resources]=await Promise.all([this.request('/api/stations'),this.request('/api/stations/resources')]);
          this.resources=resources;
          const snaps=await Promise.all(result.stations.map(s=>this.request('/api/stations/'+encodeURIComponent(s.id)+'/snapshot')));
          this.overviews=result.stations;this.owner=result.owner;
          result.stations.forEach((s,i)=>{this.snaps[s.id]=snaps[i]});
        }else{
          this.snaps=Object.fromEntries(Object.entries(this.offline).map(([id,item])=>[id,item.machine.snapshot()]));
          this.overviews=Object.entries(this.snaps).map(([id,s])=>({id,profileId:s.profileId,mode:s.mode,
            state:s.state,cycles:s.cycles,busy:s.busy,recoveryRequired:s.recoveryRequired,alarm:s.alarm,
            flowVersion:this.offline[id].machine.cfg.flowVersion||'offline-flow',recipeId:this.offline[id].governance.activeId}));
          this.resources=this.coordinator.snapshot();
          this.owner=this.resources.leases.find(x=>x.resource==='CELL-SHARED')?.stationId||null;
        }
        this.render();
      }catch(e){
        if(this.backend()) {this.locked=true;this.record('多工站服务连接丢失，禁止发起新操作：'+e.message,true)}
        else this.record('离线读取失败：'+e.message,true);
      }finally{this.polling=false}
    }
    async post(id,path,body={}){
      if(this.backend())return this.request('/api/stations/'+id+path,body);
      return this.offline[id].governance.post(path,body,this.user());
    }
    async action(id,action){
      if(!this.user()||this.locked||this.busy)return;
      const current=this.overviews.find(s=>s.id===id);
      if(!current||!this.can(action,current))return;
      this.busy=true;this.render();
      try{
        if(action==='prepare'){
          // Explicit normal manual actions; this is NOT an interlock bypass.
          const cfg=this.backend()?await this.request('/api/stations/'+id+'/config'):this.offline[id].machine.cfg;
          for(const a of cfg.axes){await this.post(id,`/api/manual/axes/${encodeURIComponent(a.id)}/enable`,{enabled:true});await this.post(id,`/api/manual/axes/${encodeURIComponent(a.id)}/home`)}
        }else if(action==='auto'||action==='manual')await this.post(id,'/api/mode',{mode:action==='auto'?'Auto':'Manual'});
        else if(action==='start'){
          // V1.2 allows concurrent cycles; only shared workflow nodes queue.
          await this.post(id,'/api/cycle/start');
        }else if(action==='stop')await this.post(id,'/api/cycle/stop');
        else if(action==='reset')await this.post(id,'/api/fault/reset');
        else if(action==='confirm')await this.post(id,'/api/recovery/confirm');
        this.record('工站 '+id+'：'+({prepare:'轴已完成准备',auto:'切换自动',manual:'切换手动',start:'已提交启动请求',stop:'已提交软件停止',reset:'报警复位',confirm:'恢复确认'}[action]||action));
      }catch(e){this.record('工站 '+id+' 操作失败：'+global.DeviceControlApiClient.describe(e),true)}
      finally{this.busy=false;await this.refresh();this.render()}
    }
  }
  global.StationConsole=StationConsole;
})(window);
