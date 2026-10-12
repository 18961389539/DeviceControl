(() => {
  const root='http://127.0.0.1:5088';
  // F09: unified fetch with application-level timeouts. Reads mark staleness instead of
  // silently keeping old state; writes report "result unknown" and are never auto-resent.
  const READ_TIMEOUT_MS=8000,WRITE_TIMEOUT_MS=15000;
  let dataStale=false;
  async function apiFetch(path,opts={}){
    const write=(opts.method||'GET')!=='GET';
    const controller=new AbortController();
    const timer=setTimeout(()=>controller.abort(),write?WRITE_TIMEOUT_MS:READ_TIMEOUT_MS);
    try{
      const res=await fetch(root+path,{...opts,signal:controller.signal});
      if(!write)dataStale=false;
      return res;
    }catch(e){
      if(e&&e.name==='AbortError'){
        if(write)throw Error('写操作超时：结果未知。请核对运行记录与审计，设备动作不会自动重发');
        dataStale=true;throw Error('数据获取超时，界面显示的可能不是最新状态');
      }
      throw e;
    }finally{clearTimeout(timer)}
  }
  const machineA=new MockRuntime(window.STATION_CONFIG);
  const machineB=new MockRuntime(window.STATION_CONFIG_B);
  machineA.stationId='A';machineB.stationId='B';
  const governanceA=new GovernanceMock(machineA),governanceB=new GovernanceMock(machineB);
  const coordinator=new window.SharedResourceCoordinator();
  machineA.sharedCoordinator=coordinator;machineB.sharedCoordinator=coordinator;
  const offline={A:{machine:machineA,governance:governanceA},B:{machine:machineB,governance:governanceB}};
  const workspace=new window.StationWorkspace.StationWorkspace('A');
  let mock=machineA,governance=governanceA;
  let cfg=window.STATION_CONFIG, snapshot=mock.snapshot(),flow=mock.flow.snapshot();
  let switching=false;
  const apiPath=(path,id=workspace.id)=>window.StationWorkspace.route(id,path);
  let usingBackend=false, disconnected=false, currentTab='dashboard', inFlight=0, backendProbe=null;
  let editor=null,replay=null,quality=null,stationConsole=null,templateStudio=null,upgradeStudio=null,rolloutStudio=null,sandboxStudio=null;
  let selectedAxis='X',selectedCylinder='Clamp',toastTimer=null,polling=false;
  let historyFilter='All',selectedCycleId=null;
  let user=null,token=null,governanceData=null,auditData=null,recoveryData=null,selectedRecipeId=null,auditPage=1;
  const $=id=>document.getElementById(id);
  const h=t=>String(t??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const names={PartPresent:'产品到位',GuardClosed:'防护门状态（普通 DI）',AirPressureOk:'气压正常',ClampExtended:'夹紧伸出到位',ClampRetracted:'夹紧缩回到位',StopperExtended:'阻挡伸出到位',StopperRetracted:'阻挡缩回到位',ClampExtend:'夹紧伸出阀',ClampRetract:'夹紧缩回阀',StopperExtend:'阻挡伸出阀',StopperRetract:'阻挡缩回阀',XStuck:'X 轴卡滞',YStuck:'Y 轴卡滞',ClampStuck:'夹紧气缸卡滞',StopperStuck:'阻挡气缸卡滞'};
  const stateText={Idle:'待机',Running:'运行中',Stopping:'停止中',Fault:'故障',Manual:'手动',Auto:'自动',Retracted:'缩回到位',Extended:'伸出到位',Unknown:'位置未知'};
  const titles={templates:'配置模板与部署',stations:'多工站调度',dashboard:'生产总览',manual:'手动调试',io:'IO 监控',config:'机构与互锁',sequence:'流程调试',analytics:'运行分析',events:'报警与日志',recovery:'故障恢复向导',governance:'配方版本与审计',workflow:'流程设计器',replay:'历史回放',quality:'流程质量门禁'};
  const txt=(id,val)=>{const e=$(id);if(e)e.textContent=val};
  const status=(id,ok)=>{const e=$(id);if(e){e.classList.toggle('ok',!!ok);e.classList.toggle('error',!ok)}};
  function toast(message,error=false){const n=$('toast');n.textContent=message;n.className='toast'+(error?' error':'');clearTimeout(toastTimer);toastTimer=setTimeout(()=>n.classList.add('hidden'),4200)}
  async function selectStation(id){
    if(id===workspace.id)return;
    if(switching||inFlight>0||editor?.busy){toast('当前操作正在进行，暂时不可切换工站',true);$('workspaceStation').value=workspace.id;return}
    if(!user){toast('请先登录',true);$('workspaceStation').value=workspace.id;return}
    // Important: while swapping context, disable writes and reject stale poll responses.
    switching=true;replay?.stop();
    const old=workspace.id;
    try{
      const target=window.StationWorkspace.ensureStation(id);
      let targetConfig=offline[target]?.machine.cfg || window.STATION_CONFIG;
      if(usingBackend){const res=await apiFetch(apiPath('/api/config',target),{headers:{'X-Sim-Token':token}});
        if(!res.ok)throw Error('读取工站 '+target+' 配置失败');targetConfig=await res.json();}
      // Avoid changing context until the target has passed validation and can be loaded.
      if(!offline[target]){const shadow=new MockRuntime(targetConfig);shadow.stationId=target;shadow.sharedCoordinator=coordinator;offline[target]={machine:shadow,governance:new GovernanceMock(shadow)}}
      editor?.switchStation(target);
      quality?.switchStation(target);
      workspace.select(target);
      mock=offline[target].machine;governance=offline[target].governance;
      cfg=targetConfig;snapshot=mock.snapshot();flow=mock.flow.snapshot();
      selectedAxis=cfg.axes[0].id;selectedCylinder=cfg.cylinders[0].id;
      selectedRecipeId=null;selectedCycleId=null;recoveryData=null;governanceData=null;auditData=null;auditPage=1;
      replay?.switchStation();
      populateSelectors();
      $('workspaceStation').value=target;
      txt('workspaceTag','工作区 '+target);txt('workspaceBreadcrumb','STATION '+target);
      txt('diagramStationLabel','STATION '+target);
      switching=false;
      await refresh();
      if(currentTab==='governance'||currentTab==='recovery')await loadGovernance();
      if(currentTab==='workflow')await editor.open();
      if(currentTab==='quality')quality.render();
      replay?.render();render();
      toast('已切换至工站 '+target+'：流程、配方、历史和诊断数据独立');
    }catch(e){switching=false;$('workspaceStation').value=old;toast('工作区切换失败：'+e.message,true)}
  }
  async function registerRemoteStations(){
    if(!usingBackend||!user)return;
    const r=await apiFetch('/api/stations',{headers:{'X-Sim-Token':token}});
    if(!r.ok)throw Error('读取工站注册表失败');
    const items=(await r.json()).stations||[];
    items.forEach(s=>window.StationWorkspace.register(s.id));
    updateStationOptions();
  }
  function updateStationOptions(){
    const selector=$('workspaceStation'),before=workspace.id;
    selector.innerHTML=window.StationWorkspace.stationIds.map(id=>`<option value="${h(id)}">工站 ${h(id)}</option>`).join('');
    selector.value=before;
  }
  function activate(tab){if(!titles[tab])return;if(tab!=='replay')replay?.stop();currentTab=tab;txt('pageTitle',titles[tab]);document.querySelectorAll('[data-tab]').forEach(e=>e.classList.toggle('active',e.dataset.tab===tab));document.querySelectorAll('.panel').forEach(e=>e.classList.toggle('active',e.id==='panel-'+tab));render();if(tab==='governance'||tab==='recovery')loadGovernance();if(tab==='workflow')editor.open();if(tab==='replay')replay.render();if(tab==='quality')quality?.render();if(tab==='stations')void stationConsole?.refresh();if(tab==='templates'){void templateStudio?.refresh();void upgradeStudio?.refresh();void rolloutStudio?.refresh()}}
  async function detectBackend(){
    if(location.protocol==='file:'){render();return}
    try{
      const controller=new AbortController();const timeout=setTimeout(()=>controller.abort(),650);
      const res=await fetch(root+'/api/health',{signal:controller.signal});clearTimeout(timeout);
      if(!res.ok)throw Error('服务不可用');
      const health=await res.json();if(health.mode!=='Simulation')throw Error('本界面仅可连接仿真服务');
      usingBackend=true;
      // Do not auto-authorize or silently reuse a previous session. Explicit login is required.
      if(user){const r=await apiFetch(apiPath('/api/config'),{headers:{'X-Sim-Token':token}});if(!r.ok)throw Error('无法读取站点配置');cfg=await r.json();populateSelectors();await refresh();}
    }catch(e){if(usingBackend){disconnected=true;toast('后端连接异常，控制已锁定：'+e.message,true)}else toast('未检测到 C# 本地仿真服务，已启用浏览器离线演示')}
    render();
  }
  async function request(path,body={}){
    if(switching)throw Error('正在切换工站，请完成切换后操作');
    if(disconnected)throw Error('本地服务断开，控制已锁定；请检查服务并重新加载页面');
    if(!user)throw Error('请先登录');
    const target=workspace.id;
    if(!usingBackend)return offline[target].governance.post(path,body,user);
    const resp=await apiFetch(apiPath(path,target),{method:'POST',headers:{'Content-Type':'application/json','X-Sim-Token':token},body:JSON.stringify(body)});
    let value;try{value=await resp.json()}catch{throw Error('后端未返回有效 JSON')}
    if(!resp.ok)throw Error(value.error||'请求失败 '+resp.status);return value;
  }
  async function refresh(){
    if(polling||disconnected||!user||switching)return;
    polling=true;const context=workspace.context();
    try{
      if(usingBackend){
        const heads={'X-Sim-Token':token};
        const [res,fres]=await Promise.all([apiFetch(apiPath('/api/snapshot',context.station),{headers:heads}),apiFetch(apiPath('/api/flow',context.station),{headers:heads})]);
        if(!res.ok||!fres.ok)throw Error('HTTP '+(res.ok?fres.status:res.status));
        const [nextSnapshot,nextFlow]=await Promise.all([res.json(),fres.json()]);
        if(workspace.isCurrent(context)){snapshot=nextSnapshot;flow=nextFlow}
      }else if(workspace.isCurrent(context)) {snapshot=offline[context.station].machine.snapshot();flow=offline[context.station].machine.flow.snapshot()}
    }catch(e){if(workspace.isCurrent(context)){disconnected=true;toast('本地仿真 API 已断开，所有控制已锁定，不会自动切换到浏览器模拟器',true)}}
    finally{polling=false;render()}
  }
  async function act(path,body={}){
    if(switching)return;
    const context=workspace.context();inFlight++;render();
    try{await request(path,body);await refresh();if(!workspace.isCurrent(context))return;if(currentTab==='governance'||currentTab==='recovery')await loadGovernance();toast('指令已执行（仿真）')}
    catch(e){toast(e.message||'命令失败',true);await refresh()}
    finally{inFlight--;render()}
  }
  function populateSelectors(){
    $('axisSelect').innerHTML=cfg.axes.map(a=>`<option value="${h(a.id)}">${h(a.label)}（${h(a.id)}）</option>`).join('');
    $('cylinderSelect').innerHTML=cfg.cylinders.map(c=>`<option value="${h(c.id)}">${h(c.label)}（${h(c.id)}）</option>`).join('');
    if(!cfg.axes.some(a=>a.id===selectedAxis))selectedAxis=cfg.axes[0].id;
    if(!cfg.cylinders.some(c=>c.id===selectedCylinder))selectedCylinder=cfg.cylinders[0].id;
    $('axisSelect').value=selectedAxis;$('cylinderSelect').value=selectedCylinder;
  }
  const rank=()=>({Operator:1,Engineer:2,Admin:3})[user?.role]||0;
  async function login(username,password){
    // Do not race a local API health probe and accidentally create an offline
    // session that cannot authorize the actual backend.
    if(backendProbe)await backendProbe;
    if(disconnected)throw Error('服务断开，请刷新页面后重试');
    if(usingBackend){
      const res=await apiFetch('/api/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username,password})});
      const value=await res.json();if(!res.ok)throw Error(value.error||'登录失败');token=value.token;user=value.user;
      const cfgRes=await apiFetch(apiPath('/api/config'),{headers:{'X-Sim-Token':token}});
      if(!cfgRes.ok)throw Error('登录后读取配置失败');cfg=await cfgRes.json();
    }else user=governance.login(username,password);
    $('loginOverlay').classList.add('hidden');await registerRemoteStations();populateSelectors();void stationConsole?.refresh();await refresh();await loadGovernance();render();
  }
  async function logout(){
    if(usingBackend&&token){try{await apiFetch('/api/auth/logout',{method:'POST',headers:{'X-Sim-Token':token}})}catch{}}
    user=null;token=null;governanceData=null;auditData=null;recoveryData=null;
    $('loginOverlay').classList.remove('hidden');stationConsole?.render();render();
  }
  async function securedGet(path){
    if(!user)return null;
    if(disconnected)throw Error('服务断开，控制已锁定');
    const target=workspace.id;
    if(!usingBackend){
      const gov=offline[target].governance;
      if(path==='/api/recipes')return gov.list();
      if(path==='/api/flows')return gov.flows.list();
      if(path.startsWith('/api/audit'))return gov.auditPage(auditPage,40);
      if(path==='/api/recovery/checklist')return gov.checklist();
    }
    const res=await apiFetch(apiPath(path,target),{headers:{'X-Sim-Token':token}});
    if(!res.ok){const v=await res.json().catch(()=>({}));throw Error(v.error||'API '+res.status)}return res.json();
  }
  async function loadGovernance(){
    if(!user||switching)return;
    const context=workspace.context();
    try{
      if(currentTab==='governance'){
        const [nextGov,nextAudit]=await Promise.all([securedGet('/api/recipes'),rank()>=2?securedGet('/api/audit?page='+auditPage+'&pageSize=40'):Promise.resolve(null)]);
        if(workspace.isCurrent(context)){governanceData=nextGov;auditData=nextAudit;}
      }
      if(currentTab==='recovery'){
        const data=await securedGet('/api/recovery/checklist');if(workspace.isCurrent(context))recoveryData=data;
      }
      if(workspace.isCurrent(context))render();
    }catch(e){if(workspace.isCurrent(context))toast('治理数据加载失败：'+e.message,true)}
  }
  function renderRecovery(d){
    const data=usingBackend?recoveryData:offline[workspace.id].governance.checklist();if(!data)return;
    const checks=data.checks;const all=checks.every(x=>x.ok);
    txt('recoveryStatus',d.recoveryRequired?all?'条件已满足':'需要处理':'无需恢复');
    $('recoveryStatus').className='pill'+(d.recoveryRequired&&!all?' alert':'');
    txt('guideAlarm',d.activeAlarm?d.activeAlarm.code+' · '+d.activeAlarm.message:d.recoveryRequired?'流程停止或故障已复位，仍需完成机构确认':'当前无活动故障');
    $('recoveryChecklist').innerHTML=checks.map(x=>`<div class="check-row ${x.ok?'pass':'fail'}"><span class="check-icon">${x.ok?'✓':'✕'}</span>${h(x.label)}<span style="margin-left:auto;font-size:10px">${x.ok?'通过':'未满足'}</span></div>`).join('');
    txt('recoveryAdvice',!d.recoveryRequired?'当前无需故障恢复。':d.state==='Fault'?'先排除仿真故障并在设备总览复位报警。':all?'条件齐全，工程师可点击确认恢复。':'请逐项完成未满足的恢复条件。');
  }
  function formatRecipe(v){return `<div class="keyvalue"><span>名称</span><b>${h(v.name)}</b></div><div class="keyvalue"><span>视觉 X 偏移</span><b>${Number(v.visionOffsetMm).toFixed(2)} mm</b></div><div class="keyvalue"><span>额外延时</span><b>${v.extraDwellMs} ms</b></div>`}
  function renderGovernance(d){
    if(!governanceData)return;
    const a=governanceData.active,versions=governanceData.versions;
    if(!selectedRecipeId||!versions.some(v=>v.id===selectedRecipeId))selectedRecipeId=a.id;
    txt('activeRecipeTag',a.id);$('activeRecipeInfo').innerHTML=formatRecipe(a.values)+`<small>创建者 ${h(a.author)} · Hash ${h(a.hash)}</small>`;
    $('recipeVersions').innerHTML=versions.map(v=>`<button class="recipe-item ${v.id===selectedRecipeId?'selected':''}" data-recipe-id="${h(v.id)}"><strong>${h(v.id)} · ${h(v.values.name)}</strong><span class="pill">${h({Draft:'草稿',Validated:'已验证',Published:'已发布',Superseded:'历史版本'}[v.status]||v.status)}</span><small>${h(v.author)} · ${h(new Date(v.createdAt).toLocaleString('zh-CN'))}</small><small>${h(v.reason)}</small></button>`).join('');
    const sel=versions.find(v=>v.id===selectedRecipeId)||a;
    const fields=[['名称',a.values.name,sel.values.name],['视觉偏移(mm)',a.values.visionOffsetMm,sel.values.visionOffsetMm],['延时(ms)',a.values.extraDwellMs,sel.values.extraDwellMs]];
    $('recipeDiff').innerHTML=fields.map(([n,oldV,newV])=>`<div class="keyvalue"><span>${h(n)}</span><b>${h(String(oldV))} → <span style="color:${oldV===newV?'#778ea9':'#3367d8'}">${h(String(newV))}</span></b></div>`).join('');
    document.querySelectorAll('[data-governance]').forEach(b=>{
      let allowed=rank()>=2;
      if(b.dataset.governance==='publish'||b.dataset.governance==='rollback')allowed=rank()>=3&&!d.busy&&!d.recoveryRequired&&d.state==='Idle';
      if(b.dataset.governance==='validate')allowed=allowed&&sel.status==='Draft';
      if(b.dataset.governance==='publish')allowed=allowed&&sel.status==='Validated';
      if(b.dataset.governance==='rollback')allowed=allowed&&['Published','Superseded'].includes(sel.status)&&sel.id!==a.id;
      if(b.dataset.governance==='audit-next')allowed=rank()>=2&&auditPage*40<(auditData?.total||0);
      if(b.dataset.governance==='audit-prev')allowed=rank()>=2&&auditPage>1;
      b.disabled=!allowed||inFlight>0;
    });
    const rows=auditData?.items||[];
    $('auditRows').innerHTML=rows.length?rows.map(x=>`<tr><td>${h(new Date(x.time).toLocaleString('zh-CN'))}</td><td><b>${h(x.actor)}</b><br>${h(x.role)}</td><td>${h(x.method)} ${h(x.path)}</td><td class="${x.status>=400?'denied':''}">${x.status} · ${h(x.outcome)}</td><td><code>${h(x.traceId)}</code></td></tr>`).join(''):'<tr><td colspan="5">该角色没有审计查看权限，或尚无记录。</td></tr>';
    txt('auditMeta',auditData?'工站 '+workspace.id+' · 共 '+auditData.total+' 条 · 第 '+auditPage+' 页':'审计记录需要工程师权限');
  }
  function exportAudit(){
    if(rank()<2||!auditData)throw Error('无审计导出权限');
    const rows=[['时间','操作人','角色','方法','路径','结果','TraceId'],...auditData.items.map(x=>[x.time,x.actor,x.role,x.method,x.path,x.status,x.traceId])];
    const csv='\ufeff'+rows.map(x=>x.map(v=>'"'+String(v??'').replaceAll('"','""')+'"').join(',')).join('\r\n');
    const url=URL.createObjectURL(new Blob([csv],{type:'text/csv;charset=utf-8'}));
    const a=document.createElement('a');a.href=url;a.download='device-control-'+workspace.id+'-audit-page-'+auditPage+'.csv';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
  }
  function render(){
    const d=snapshot;if(!d)return;
    $('workspaceStation').disabled=!user||switching||inFlight>0||editor?.busy||disconnected;
    txt('workspaceTag','工作区 '+workspace.id);txt('workspaceBreadcrumb','STATION '+workspace.id);
    txt('userLabel',user?user.name+' · '+user.role:'未登录');$('logoutButton').disabled=!user;
    txt('connectionLabel',disconnected?'已断开 · 锁定':usingBackend?'C# 本地模拟后端':'浏览器模拟驱动');
    txt('sourceMode',disconnected?'连接丢失':dataStale?'数据可能过期':usingBackend?'本地 API · Simulation':'离线 Simulation');
    txt('clock',new Date().toLocaleTimeString('zh-CN',{hour12:false}));
    txt('stateValue',stateText[d.state]||d.state);
    $('stateDot').className='state-dot '+(disconnected||d.state==='Fault'?'error':d.state==='Running'||d.state==='Stopping'?'running':'ok');
    txt('stateSubtitle',disconnected?'控制锁定':d.recoveryRequired?'需要恢复确认':'普通互锁有效');
    txt('modeValue',stateText[d.mode]||d.mode);txt('cyclesValue',String(d.cycles));
    const x=d.axes.X||Object.values(d.axes)[0],clamp=d.cylinders.Clamp||Object.values(d.cylinders)[0];
    txt('axisValue',x.positionMm.toFixed(3));txt('axisSub',x.homed?'X 轴已回零':'X 轴未回零');
    txt('stepValue',d.step);txt('flowStatus',stateText[d.state]||d.state);
    txt('recoveryTag',d.recoveryRequired?'需要恢复确认':d.state==='Fault'?'报警':'状态正常');
    $('recoveryTag').className='pill'+(d.recoveryRequired||d.state==='Fault'?' alert':'');
    const xCfg=cfg.axes.find(a=>a.id==='X')||cfg.axes[0];
    $('axisCarriage').style.left=Math.min(100,Math.max(0,(x.positionMm-xCfg.minMm)/(xCfg.maxMm-xCfg.minMm)*100))+'%';
    $('piston').classList.toggle('extended',clamp.position==='Extended');
    $('productSymbol').classList.toggle('absent',!d.inputs.PartPresent);
    status('guardLed',d.inputs.GuardClosed);status('airLed',d.inputs.AirPressureOk);status('partLed',d.inputs.PartPresent);
    txt('visionValue',d.vision?'视觉：'+(d.vision.ok?'OK · X='+d.vision.targetXmm.toFixed(3)+' mm':'NG'):'视觉：尚未触发');
    $('stepProgress').style.width=(d.stepIndex>=cfg.sequence.length?100:d.stepIndex<0?0:Math.round((d.stepIndex+1)/cfg.sequence.length*100))+'%';
    txt('actionHint',disconnected?'连接已断开，重新启动服务后刷新页面':d.alarm?'故障：'+d.alarm:d.recoveryRequired?'需要手动恢复所有轴与气缸后才能切换自动':d.mode==='Manual'?'先使能并回零 X/Y，再切换自动模式':'自动模式可以启动一个单周期');
    $('modeManual').classList.toggle('selected',d.mode==='Manual');$('modeAuto').classList.toggle('selected',d.mode==='Auto');
    renderManual(d);
    const manual=d.mode==='Manual'&&d.state==='Idle'&&!d.busy&&d.inputs.GuardClosed&&d.inputs.AirPressureOk;
    document.querySelectorAll('[data-action]').forEach(b=>{
      const a=b.dataset.action;let ok=true;
      if(['servo-on','servo-off','axis-home','axis-move','cylinder-extend','cylinder-retract','vision-trigger'].includes(a))ok=manual;
      if(a==='cycle-start')ok=d.mode==='Auto'&&d.state==='Idle'&&!d.busy&&!d.recoveryRequired;
      if(a==='cycle-stop')ok=d.state==='Running'||d.state==='Stopping';
      if(a==='fault-reset')ok=d.state==='Fault'&&!d.busy;
      if(a==='recovery-confirm')ok=d.mode==='Manual'&&d.state==='Idle'&&!d.busy&&d.recoveryRequired;
      if(['servo-on','servo-off','axis-home','axis-move','cylinder-extend','cylinder-retract','vision-trigger','fault-reset','recovery-confirm'].includes(a))ok=ok&&rank()>=2;
      b.disabled=!ok||!user||inFlight>0||switching||disconnected;
    });
    document.querySelectorAll('[data-mode]').forEach(b=>b.disabled=!user||rank()<(b.dataset.mode==='Manual'?2:1)||d.state!=='Idle'||d.busy||inFlight>0||disconnected);
    if(currentTab==='io')renderIO(d);
    if(currentTab==='config')renderConfig(d);
    if(currentTab==='sequence')renderSteps(d);
    if(currentTab==='replay')replay?.render();
    if(currentTab==='events')renderEvents(d);
    if(currentTab==='analytics')renderAnalytics(d);
    if(currentTab==='recovery')renderRecovery(d);
    if(currentTab==='governance')renderGovernance(d);
  }
  function renderManual(d){
    const a=d.axes[selectedAxis]||Object.values(d.axes)[0];const ac=cfg.axes.find(x=>x.id===selectedAxis)||cfg.axes[0];
    const c=d.cylinders[selectedCylinder]||Object.values(d.cylinders)[0];
    txt('axisReadout',a.positionMm.toFixed(3));txt('homedReadout',a.homed?'已回零':'未回零');
    txt('axisLimits',ac.minMm+'–'+ac.maxMm+' mm / '+ac.speedMmPerSec+' mm/s');
    txt('axisBadge',a.enabled?'已使能':'未使能');txt('cylinderBadge',stateText[c.position]||c.position);
    txt('cylinderReadout',stateText[c.position]||c.position);txt('extendReadout',c.extendedSensor?'ON':'OFF');txt('retractReadout',c.retractedSensor?'ON':'OFF');
    txt('mechanismHint',selectedCylinder==='Clamp'?'Clamp 伸出需先使 Stopper 伸出。轴运动前必须缩回两只气缸。':'Stopper 可独立伸出；伸出期间 X/Y 轴均禁止移动。');
    $('mechanismOverview').innerHTML=[...cfg.axes.map(x=>{const s=d.axes[x.id];return `<div class="mini-item"><b>${h(x.label)}</b><small>Axis ${h(x.id)} / ${s.homed?'已回零':'未回零'}</small><strong>${s.positionMm.toFixed(3)} mm</strong></div>`}),...cfg.cylinders.map(x=>{const s=d.cylinders[x.id];return `<div class="mini-item"><b>${h(x.label)}</b><small>Cylinder ${h(x.id)}</small><strong>${h(stateText[s.position]||s.position)}</strong></div>`})].join('');
  }
  function signalRow(s,value){
    const writable=s.injectable&&s.direction==='DI';
    return `<div class="signal-row"><span class="signal-index">${h(s.channel)}</span><div class="signal-text"><b>${h(s.description||names[s.name]||s.name)}</b><small>${h(s.name)}</small></div><div class="signal-control"><span class="signal-indicator"><span class="led ${value?'on':''}"></span>${value?'ON':'OFF'}</span>${writable?`<button class="switch ${value?'on':''}" data-signal-name="${h(s.name)}" data-signal-kind="input" data-next="${!value}" aria-label="切换${h(s.name)}"></button>`:''}</div></div>`;
  }
  function renderIO(d){
    $('inputRows').innerHTML=cfg.signals.filter(s=>s.direction==='DI').map(s=>signalRow(s,d.inputs[s.name])).join('');
    $('outputRows').innerHTML=cfg.signals.filter(s=>s.direction==='DO').map(s=>signalRow(s,d.outputs[s.name])).join('');
    $('faultRows').innerHTML=Object.entries(d.faultInjection).map(([n,v],i)=>`<div class="signal-row"><span class="signal-index">F${i+1}</span><div class="signal-text"><b>${h(names[n]||n)}</b><small>${h(n)}</small></div><div class="signal-control"><span class="signal-indicator"><span class="led ${v?'on':''}"></span>${v?'ON':'OFF'}</span><button class="switch ${v?'on':''}" data-signal-name="${h(n)}" data-signal-kind="fault" data-next="${!v}" aria-label="模拟${h(n)}"></button></div></div>`).join('');
    document.querySelectorAll('[data-signal-kind]').forEach(x=>x.disabled=!user||rank()<2||disconnected||inFlight>0);
  }
  function renderConfig(d){
    txt('profileDescription',cfg.profileId+' · '+cfg.driver+' · '+cfg.axes.length+' 轴 / '+cfg.cylinders.length+' 气缸');
    $('configMechanisms').innerHTML=[...cfg.axes.map(a=>`<div class="mini-item"><b>${h(a.label)}</b><small>轴 ID ${h(a.id)} · 范围 ${a.minMm}–${a.maxMm} mm</small><strong>${a.speedMmPerSec} mm/s</strong></div>`),...cfg.cylinders.map(c=>`<div class="mini-item"><b>${h(c.label)}</b><small>${h(c.extendOutput)} / ${h(c.retractOutput)}</small><strong>${c.timeoutMs} ms</strong></div>`)].join('');
    $('mappingRows').innerHTML=cfg.signals.map(s=>`<div class="mapping-row"><span class="map-chip">${h(s.channel)}</span><div><b>${h(s.name)}</b><small>${h(s.description)}</small></div><span class="map-chip">${h(s.direction)}</span></div>`).join('');
    $('interlockRows').innerHTML=d.interlocks.map(x=>`<div class="interlock-card"><b>${h(x.message)}</b><small>动作: ${h(x.action)} · ${h(x.device)} / DI ${h(x.signal)} = ${x.required?'ON':'OFF'}</small><small class="${x.satisfied?'pass':'block'}">${x.satisfied?'● 已满足':'● 不满足'} · 实际 ${x.actual?'ON':'OFF'}</small></div>`).join('');
  }
  function renderSteps(d){
    const f=flow||{version:cfg.flowVersion,status:'Idle',breakpoints:[],active:[],traces:[],resources:{},waiting:null};
    const active=new Set((f.active||[]).map(v=>typeof v==='string'?v:v.id));
    const traces=f.traces||[];
    const last=id=>[...traces].reverse().find(t=>t.id===id);
    txt('sequenceStatus',stateText[d.state]||d.state);
    txt('flowVersion',f.version||cfg.flowVersion||'unversioned');
    txt('flowDebugStatus',f.status==='Paused'?'已暂停':f.status==='Running'?'执行中':'待机');
    $('flowDebugStatus').className='pill'+(f.status==='Paused'?' alert':'');
    txt('flowDetail',`${cfg.sequence.length} 个顶层步骤 · ${cfg.sequence.filter(n=>n.type==='Parallel').length} 个并行组 · ${traces.length} 条执行轨迹`);
    $('sequenceSteps').innerHTML=cfg.sequence.map((node,i)=>{
      const lastRoot=last(node.id),on=(f.breakpoints||[]).includes(i),isActive=active.has(node.id),isPaused=f.status==='Paused'&&f.waiting?.index===i;
      const cl=isActive?'active':isPaused?'paused':lastRoot?.status==='Failed'?'failed':lastRoot?.status==='Completed'?'finished':'';
      const label=isActive?'● 执行中':isPaused?'● 断点暂停':lastRoot?.status==='Completed'?'✓ 已完成':lastRoot?.status==='Failed'?'✕ 失败':'待执行';
      const children=node.children?.length?`<div class="flow-children">${node.children.map(n=>{const t=last(n.id);return `<div class="flow-child"><b>${active.has(n.id)?'◉':'↳'}</b><div style="flex:1"><strong>${h(n.label)}</strong><small>${h(n.id)} · ${h(n.type)}</small></div><span class="flow-status-text">${active.has(n.id)?'运行中':t?.status==='Completed'?'完成 · '+t.durationMs+' ms':t?.status==='Failed'?'失败':'待执行'}</span></div>`}).join('')}</div>`:'';
      return `<div class="flow-node ${cl}"><div class="flow-node-main"><button class="break-dot ${on?'on':''}" data-debug="breakpoint" data-index="${i}" data-enabled="${!on}" title="${on?'移除':'设置'}断点" ${rank()<2?'disabled':''}>●</button><span class="flow-index">${String(i+1).padStart(2,'0')}</span><div class="flow-title"><strong>${h(node.label)}</strong><small>${h(node.id)} · ${h(node.type)} ${node.onError&&node.onError!=='Abort'?'· '+h(node.onError):''}</small></div><span class="flow-status-text">${label}</span></div>${children}</div>`;
    }).join('');
    const varRows=[['流程状态',f.status],['设备模式',d.mode],['顶层步骤',d.stepIndex>=0?String(d.stepIndex+1):'—'],['视觉坐标',d.vision?.ok?d.vision.targetXmm.toFixed(3)+' mm':'—'],['暂停/错误原因',f.waiting?.message||'—']];
    $('flowVariables').innerHTML=varRows.map(([k,v])=>`<div class="flow-var"><span>${h(k)}</span><b>${h(v)}</b></div>`).join('');
    const resources=Object.entries(f.resources||{});
    $('flowResources').innerHTML=resources.length?resources.map(([k,v])=>`<span class="resource-chip">🔒 ${h(k)} : ${h(v)}</span>`).join(''):'<small class="subtle">资源空闲，无机构锁占用</small>';
    $('flowTraces').innerHTML=traces.length?[...traces].reverse().slice(0,35).map(t=>`<div class="trace-row"><div class="trace-top"><b>${h(t.label)}</b><span class="trace-${h((t.status||'').toLowerCase())}">${h(t.status)}</span></div><small>${h(t.parentId?'↳ '+t.parentId+' · ':'')}${h(t.id)} · 第 ${t.attempt||0} 次尝试 · ${t.durationMs} ms</small>${t.error?`<div class="trace-failed">${h(t.error)}</div>`:''}</div>`).join(''):'<p class="empty-state">暂无执行轨迹。启动自动单周期后实时显示。</p>';
    document.querySelectorAll('[data-debug]').forEach(b=>{
      const cmd=b.dataset.debug;
      const paused=f.status==='Paused',running=f.status==='Running',wait=f.waiting?.kind;
      b.disabled=!user||rank()<2||disconnected||inFlight>0||(cmd==='breakpoint'?false:cmd==='pause'?!running:cmd==='resume'||cmd==='step'?!paused||wait!=='breakpoint':cmd==='retry'||cmd==='abort'?!paused||wait!=='error':true);
    });
  }
  function renderEvents(d){
    txt('alarmStatus',d.alarm?'有活动报警':'无活动报警');$('alarmStatus').className='pill'+(d.alarm?' alert':'');
    txt('alarmDetails',d.alarm||'当前没有活动报警');$('alarmDetails').className=d.alarm?'alarm-active':'alarm-empty';
    const a=d.activeAlarm,box=$('alarmDiagnosis');box.classList.toggle('hidden',!a);
    if(a)box.innerHTML=`<b>${h(a.code)}</b><span>步骤：${h(a.source)} · ${h(new Date(a.raisedAt).toLocaleString('zh-CN'))}</span><p>${h(a.suggestion)}</p>`;
    $('eventsRows').innerHTML=d.events.map(ev=>`<div class="event-row"><time>${h(ev.time)}</time><span class="loglevel ${h(ev.level)}">${h(ev.level)}</span><span>${h(ev.message)}</span></div>`).join('');
  }
  function renderAnalytics(d){
    const all=d.cycleHistory||[];
    const complete=all.filter(c=>c.outcome==='Completed'),failed=all.filter(c=>c.outcome!=='Completed');
    txt('histTotal',all.length);txt('histSuccess',all.length?(complete.length/all.length*100).toFixed(1)+'%':'—');
    txt('histMean',complete.length?(complete.reduce((n,c)=>n+c.durationMs,0)/complete.length/1000).toFixed(2):'—');
    txt('histFailures',failed.length);
    const shown=all.filter(c=>historyFilter==='All'||c.outcome===historyFilter);
    if(!shown.some(c=>c.id===selectedCycleId))selectedCycleId=shown[0]?.id||null;
    $('historyRows').innerHTML=shown.length?shown.map(c=>`<button class="history-row ${selectedCycleId===c.id?'selected':''}" data-cycle-id="${h(c.id)}"><span><b>${h(c.id)}</b><small>${h(new Date(c.startedAt).toLocaleString('zh-CN'))}</small></span><span class="outcome ${h(c.outcome)}">${h({Completed:'成功',Failed:'失败',Stopped:'已停止'}[c.outcome]||c.outcome)}</span><strong>${(c.durationMs/1000).toFixed(2)} s</strong></button>`).join(''):'<p class="empty-state">暂无符合条件的周期记录。可以在生产总览运行一次仿真流程。</p>';
    const selected=shown.find(c=>c.id===selectedCycleId);
    txt('selectedRunLabel',selected?'周期 '+selected.id+' · '+(selected.durationMs/1000).toFixed(2)+' 秒 · 配方 '+(selected.recipeId||'未记录')+' · 流程 '+(selected.flowVersion||'旧版本'):'暂无记录');
    txt('selectedRunStatus',selected?{Completed:'成功',Failed:'失败',Stopped:'已停止'}[selected.outcome]: '—');
    const steps=selected?.steps||[];const peak=Math.max(1,...steps.map(x=>x.durationMs));
    $('stepTimingRows').innerHTML=steps.length?steps.map((st,i)=>`<div class="timing-row"><div class="timing-top"><b>${String(st.index+1).padStart(2,'0')} ${h(st.label)}</b><span class="outcome ${h(st.outcome)}">${h({Completed:'完成',Failed:'失败',Canceled:'取消'}[st.outcome]||st.outcome)}</span><b>${st.durationMs} ms</b></div><div class="timing-rail"><div style="width:${(st.durationMs/peak*100).toFixed(1)}%"></div></div>${st.error?`<small class="error-line">${h(st.error)}</small>`:''}</div>`).join(''):'<p class="empty-state">没有步骤记录。</p>';
  }
  function exportHistory(){
    const rows=[['周期编号','开始时间','结束时间','执行结果','耗时毫秒','步骤数','配方版本','流程版本','原因'],...(snapshot.cycleHistory||[]).map(c=>[c.id,c.startedAt,c.endedAt,c.outcome,c.durationMs,c.steps.length,c.recipeId||'',c.flowVersion||'',c.reason||''])];
    const csv='\ufeff'+rows.map(row=>row.map(v=>'"'+String(v??'').replaceAll('"','""')+'"').join(',')).join('\r\n');
    const blob=new Blob([csv],{type:'text/csv;charset=utf-8'});const url=URL.createObjectURL(blob);
    const a=document.createElement('a');a.href=url;a.download='device-control-'+workspace.id+'-cycle-history.csv';a.click();
    setTimeout(()=>URL.revokeObjectURL(url),1000);toast('已导出当前历史记录 CSV');
  }
  document.addEventListener('click',async e=>{
    const b=e.target.closest('button');if(!b||b.disabled)return;
    if(switching)return;
    if(b.dataset.tab){activate(b.dataset.tab);return}
    if(b.dataset.debug){const body={command:b.dataset.debug};if(b.dataset.debug==='breakpoint'){body.index=Number(b.dataset.index);body.enabled=b.dataset.enabled==='true'}await act('/api/flow/debug',body);return}
    if(b.dataset.replayRun){replay.choose(b.dataset.replayRun);return}
    if(b.dataset.replayIndex){replay.seek(Number(b.dataset.replayIndex));return}
    if(b.dataset.replayAction){const a=b.dataset.replayAction;if(a==='prev')replay.step(-1);else if(a==='next')replay.step(1);else replay.play();return}
    if(b.dataset.cycleId){selectedCycleId=b.dataset.cycleId;render();return}
    if(b.dataset.recipeId){selectedRecipeId=b.dataset.recipeId;render();return}
    if(b.dataset.governance){
      const action=b.dataset.governance;
      if(action==='audit-refresh'){await loadGovernance();return}
      if(action==='audit-prev'||action==='audit-next'){auditPage+=action==='audit-prev'?-1:1;await loadGovernance();return}
      if(action==='audit-export'){try{exportAudit();toast('本页审计记录已导出')}catch(e){toast(e.message,true)}return}
      const id=selectedRecipeId;
      if(action==='draft'){
        const body={name:$('recipeName').value.trim(),visionOffsetMm:Number($('recipeOffset').value),extraDwellMs:Number($('recipeDwell').value)};
        if(!body.name||!Number.isFinite(body.visionOffsetMm)||!Number.isInteger(body.extraDwellMs)){toast('请输入合法的草稿参数',true);return}
        await act('/api/recipes/draft',body);if(governanceData?.versions?.[0])selectedRecipeId=governanceData.versions[0].id;
      }
      if(action==='validate')await act('/api/recipes/'+encodeURIComponent(id)+'/validate');
      if(action==='publish')await act('/api/recipes/'+encodeURIComponent(id)+'/publish');
      if(action==='rollback'){
        if(!window.confirm('确定回滚到 '+id+' 吗？系统会创建新的已发布版本。'))return;
        await act('/api/recipes/rollback',{id});
      }
      await loadGovernance();return;
    }
    if(b.dataset.action==='export-history'){exportHistory();return}
    if(b.id==='helpToggle'){$('help').classList.toggle('hidden');return}
    if(b.dataset.mode){await act('/api/mode',{mode:b.dataset.mode});return}
    if(b.dataset.signalKind){await act('/api/sim/'+(b.dataset.signalKind==='fault'?'fault':'input'),{name:b.dataset.signalName,value:b.dataset.next==='true'});return}
    const a=b.dataset.action;if(!a)return;
    const axis='/api/manual/axes/'+encodeURIComponent(selectedAxis),cy='/api/manual/cylinders/'+encodeURIComponent(selectedCylinder);
    const routes={'servo-on':[axis+'/enable',{enabled:true}],'servo-off':[axis+'/enable',{enabled:false}],
      'axis-home':[axis+'/home'],'axis-move':[axis+'/move',{positionMm:Number($('moveTarget').value)}],
      'cylinder-extend':[cy+'/extend'],'cylinder-retract':[cy+'/retract'],'vision-trigger':['/api/manual/vision/trigger'],
      'cycle-start':['/api/cycle/start'],'cycle-stop':['/api/cycle/stop'],
      'fault-reset':['/api/fault/reset'],'recovery-confirm':['/api/recovery/confirm']};
    if(a==='axis-move'&&($('moveTarget').value.trim()===''||!Number.isFinite(Number($('moveTarget').value)))){toast('请输入合法数值',true);return}
    if(routes[a])await act(...routes[a]);
  });
  $('workspaceStation').addEventListener('change',e=>void selectStation(e.target.value));
  $('loginForm').addEventListener('submit',async e=>{
    e.preventDefault();$('loginError').textContent='';
    try{await login($('loginUser').value,$('loginPassword').value)}catch(err){$('loginError').textContent=err.message}
  });
  $('logoutButton').addEventListener('click',logout);
  $('axisSelect').addEventListener('change',e=>{selectedAxis=e.target.value;const a=cfg.axes.find(x=>x.id===selectedAxis);$('moveTarget').value=String(a.homeMm);render()});
  $('historyFilter').addEventListener('change',e=>{historyFilter=e.target.value;selectedCycleId=null;render()});
  $('cylinderSelect').addEventListener('change',e=>{selectedCylinder=e.target.value;render()});
  replay=new window.WorkflowReplay.ReplayViewer({runs:()=>snapshot?.cycleHistory||[]});
  $('replayRange').addEventListener('input',e=>replay.seek(Number(e.target.value)));
  editor=new FlowEditor({
    role:rank,
    cfg:()=>cfg,
    canPublish:()=>snapshot?.state==='Idle'&&!snapshot?.busy&&!snapshot?.recoveryRequired,
    notice:toast,
    read:()=>securedGet('/api/flows'),
    send:async(path,body)=>request(path,body),
    refreshed:async()=>{
      if(usingBackend){const res=await apiFetch(apiPath('/api/config'),{headers:{'X-Sim-Token':token}});if(!res.ok)throw Error('更新站点配置失败');cfg=await res.json()}
      else cfg=offline[workspace.id].machine.cfg;
      await refresh();
    }
  });
  quality=new window.WorkflowQualityUI.QualityDashboard({editor,config:()=>cfg,runs:()=>snapshot?.cycleHistory||[],role:rank,notify:toast});
  stationConsole=new window.StationConsole({root,backend:()=>usingBackend,token:()=>token,user:()=>user,
    offline,coordinator,notify:toast,openStation:async id=>{await selectStation(id);if(workspace.id===id)activate('dashboard')}});
  templateStudio=new window.StationTemplateStudio({root,backend:()=>usingBackend,token:()=>token,user:()=>user,
    offline,coordinator,notify:toast,onDeployed:async ids=>{
      ids.forEach(id=>window.StationWorkspace.register(id));updateStationOptions();
      await stationConsole.refresh();
    }});
  upgradeStudio=new window.StationUpgradeStudio({root,backend:()=>usingBackend,token:()=>token,user:()=>user,
    offline,templates:()=>templateStudio.templates,notify:toast,onChanged:async id=>{
      if(id===workspace.id){
        if(usingBackend){const r=await apiFetch(apiPath('/api/config'),{headers:{'X-Sim-Token':token}});if(r.ok)cfg=await r.json();}
        await refresh();if(currentTab==='workflow')await editor.open();
      }
      await stationConsole.refresh();
    }});
  rolloutStudio=new window.FleetRolloutStudio({root,backend:()=>usingBackend,token:()=>token,user:()=>user,
    offline,templates:()=>templateStudio.templates,upgradeStudio,notify:toast,onChanged:async id=>{
      if(id===workspace.id){await refresh();if(currentTab==='workflow')await editor.open()}
      await stationConsole.refresh();
    }});
  sandboxStudio=new window.DeploymentSandboxUI({root,backend:()=>usingBackend,token:()=>token,user:()=>user,upgradeStudio});
  populateSelectors();updateStationOptions();render();setInterval(refresh,300);backendProbe=detectBackend();
})();
