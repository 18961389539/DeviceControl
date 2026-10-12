/* Browser-only station simulation. No real IO, motor or camera is controlled. */
class MockRuntime {
  constructor(config=window.STATION_CONFIG){
    this.cfg=JSON.parse(JSON.stringify(config));this.stationId='A';this.sharedCoordinator=null;
    if(this.cfg.driver!=='Simulation')throw Error('只允许 Simulation 驱动');
    this.d={profileId:this.cfg.profileId,mode:'Manual',state:'Idle',step:'等待操作',stepIndex:-1,
      cycles:0,busy:false,recoveryRequired:false,alarm:null,activeAlarm:null,stepHistory:[],cycleHistory:[],axes:{},cylinders:{},
      inputs:{},outputs:{},faultInjection:{},interlocks:[],vision:null,events:[]};
    for(const s of this.cfg.signals){if(s.direction==='DI')this.d.inputs[s.name]=!!s.initial;else this.d.outputs[s.name]=false}
    for(const a of this.cfg.axes){this.d.axes[a.id]={enabled:false,homed:false,moving:false,positionMm:a.homeMm,error:''};this.d.faultInjection[a.id+'Stuck']=false}
    for(const c of this.cfg.cylinders){this.d.cylinders[c.id]={position:'Retracted',extendedSensor:false,retractedSensor:true,busy:false};this.d.faultInjection[c.id+'Stuck']=false}
    this.cycleRecipe={visionOffsetMm:0,extraDwellMs:0};this.cycleRecipeId=null;this.cancel=false;this.cycleId='';this.cycleStarted=0;this.currentSteps=[];
    this.flow=new WorkflowEngine(this,this.cfg.sequence);
    this.updateInterlocks();
    this.log('Info','双轴双气缸仿真站点已加载，未连接物理板卡');
  }
  snapshot(){this.updateInterlocks();return JSON.parse(JSON.stringify(this.d))}
  diagnostic(error,source){
    const message=error?.message||String(error);
    let code='SYS-UNCLASSIFIED',suggestion='查看步骤记录和错误信息，完成人工恢复。';
    if(message.includes('气缸')&&message.includes('超时')){code='ACT-CYL-TIMEOUT';suggestion='检查卡滞模拟和气缸反馈，确认恢复条件。'}
    else if(message.includes('轴')&&(message.includes('故障')||message.includes('超时'))){code='ACT-AXIS-FAULT';suggestion='检查轴模拟故障、当前位置和回零状态。'}
    else if(message.includes('输入')&&message.includes('超时')){code='SEQ-WAIT-TIMEOUT';suggestion='检查输入状态与流程等待超时设置。'}
    else if(message.includes('视觉')){code='VIS-RESULT-NG';suggestion='检查产品到位信号和视觉检测返回值。'}
    else if(message.includes('互锁')||message.includes('联锁')){code='SEQ-INTERLOCK';suggestion='到机构与互锁页检查尚未满足的条件。'}
    else if(message.includes('行程')||message.includes('范围')){code='ACT-LIMIT';suggestion='检查目标位置以及配置的轴行程。'}
    return {code,message,suggestion,source,raisedAt:new Date().toISOString()};
  }
  traceVariables(){
    const v={};
    for(const [k,value] of Object.entries(this.d.inputs))v['DI.'+k]=!!value;
    for(const [k,a] of Object.entries(this.d.axes))v['Axis.'+k]=Number(a.positionMm.toFixed(3));
    for(const [k,c] of Object.entries(this.d.cylinders))v['Cylinder.'+k]=c.position;
    v['Vision.OK']=this.d.vision?.ok??null;
    v['Vision.TargetXmm']=this.d.vision?.targetXmm??null;
    v['Recipe.VisionOffsetMm']=this.cycleRecipe?.visionOffsetMm??0;
    return v;
  }
  recordStep(index,step,outcome,durationMs,error=null){
    const row={index,label:step.label,type:step.type,device:step.device||'',outcome,durationMs,error};
    this.currentSteps.push(row);this.d.stepHistory.unshift(row);this.d.stepHistory=this.d.stepHistory.slice(0,100);
  }
  recordCycle(outcome){
    this.d.cycleHistory.unshift({flowVersion:this.cfg.flowVersion||'unversioned',id:this.cycleId,startedAt:new Date(this.cycleStarted).toISOString(),
      endedAt:new Date().toISOString(),outcome,durationMs:Date.now()-this.cycleStarted,
      steps:this.currentSteps.slice(),trace:this.flow.snapshot().traces,reason:this.d.alarm||(outcome==='Stopped'?'软件请求停止':null),recipeId:this.cycleRecipeId});
    this.d.cycleHistory=this.d.cycleHistory.slice(0,100);
  }
  log(level,message){const t=new Date();this.d.events.unshift({time:t.toLocaleTimeString('zh-CN',{hour12:false})+'.'+String(t.getMilliseconds()).padStart(3,'0'),level,message});this.d.events=this.d.events.slice(0,160)}
  cfgAxis(id){const x=this.cfg.axes.find(a=>a.id===id);if(!x)throw Error('未配置的运动轴：'+id);return x}
  cfgCylinder(id){const c=this.cfg.cylinders.find(a=>a.id===id);if(!c)throw Error('未配置的气缸：'+id);return c}
  updateInterlocks(){this.d.interlocks=this.cfg.interlocks.map(r=>({...r,actual:!!this.d.inputs[r.signal],satisfied:!!this.d.inputs[r.signal]===r.required}))}
  idle(){if(this.d.state!=='Idle'||this.d.busy)throw Error('设备未处于空闲状态')}
  guard(action='',id=''){
    if(!this.d.inputs.GuardClosed)throw Error('普通联锁：防护门状态异常');
    if(!this.d.inputs.AirPressureOk)throw Error('普通联锁：气压不足');
    for(const r of this.cfg.interlocks)if(r.action===action&&(r.device==='*'||r.device===id)&&this.d.inputs[r.signal]!==r.required)throw Error('普通互锁：'+r.message);
  }
  manual(action='',id=''){this.idle();if(this.d.mode!=='Manual')throw Error('仅手动模式允许操作');this.guard(action,id)}
  check(action='',id=''){if(this.cancel)throw Error('STOP_REQUESTED');if(this.flow?.haltGroup)throw Error('GROUP_ABORTED');this.guard(action,id)}
  async sleep(ms){const start=Date.now();while(Date.now()-start<ms){this.check();await new Promise(r=>setTimeout(r,Math.min(35,Math.max(5,ms-(Date.now()-start)))))}this.check()}
  invalidateAxes(){for(const a of Object.values(this.d.axes))a.homed=false}
  async axisMove(id,position,homing=false){
    const cfg=this.cfgAxis(id),a=this.d.axes[id];
    if(!Number.isFinite(position)||position<cfg.minMm||position>cfg.maxMm)throw Error(id+' 位置超出行程');
    if(!a.enabled)throw Error(id+' 尚未使能');
    if(!homing&&!a.homed)throw Error(id+' 尚未回零');
    this.guard('AxisMove',id);a.moving=true;const origin=a.positionMm,start=Date.now();
    const duration=Math.max(200,1000*Math.abs(position-origin)/cfg.speedMmPerSec);
    try{
      while(Date.now()-start<duration||this.d.faultInjection[id+'Stuck']){
        this.check('AxisMove',id);
        if(this.d.faultInjection[id+'Stuck']){if(Date.now()-start>Math.max(2000,duration+1000))throw Error(id+' 轴模拟故障：位置未更新')}
        else a.positionMm=origin+(position-origin)*Math.min(1,(Date.now()-start)/duration);
        await new Promise(r=>setTimeout(r,35));
      }
      this.check('AxisMove',id);a.positionMm=position;if(homing)a.homed=true;
    }catch(e){a.homed=false;throw e}finally{a.moving=false}
  }
  async cylinderAct(id,extend){
    const cfg=this.cfgCylinder(id),c=this.d.cylinders[id],act=extend?'CylinderExtend':'CylinderRetract';
    this.guard(act,id);c.busy=true;
    try{
      if(c.position===(extend?'Extended':'Retracted'))return;
      this.d.outputs[cfg.extendOutput]=false;this.d.outputs[cfg.retractOutput]=false;
      this.d.outputs[extend?cfg.extendOutput:cfg.retractOutput]=true;
      this.d.inputs[cfg.extendedInput]=false;this.d.inputs[cfg.retractedInput]=false;
      c.extendedSensor=false;c.retractedSensor=false;c.position='Unknown';
      const start=Date.now();
      while(Date.now()-start<cfg.timeoutMs){
        this.check(act,id);
        if(!this.d.faultInjection[id+'Stuck']&&Date.now()-start>=420){
          this.d.inputs[cfg.extendedInput]=extend;this.d.inputs[cfg.retractedInput]=!extend;
          c.extendedSensor=extend;c.retractedSensor=!extend;c.position=extend?'Extended':'Retracted';return;
        }
        await new Promise(r=>setTimeout(r,35));
      }
      throw Error(cfg.label+'动作超时：未收到到位反馈');
    }finally{this.d.outputs[cfg.extendOutput]=false;this.d.outputs[cfg.retractOutput]=false;c.busy=false}
  }
  async vision(){await this.sleep(320);const ok=this.d.inputs.PartPresent;this.d.vision={ok,targetXmm:ok?80:0,score:ok?.987:0,message:ok?'模拟视觉定位 OK':'未检测到产品'};if(!ok)throw Error('视觉 NG：未检测到产品')}
  async perform(label,action,id,fn){
    this.manual(action,id);this.d.busy=true;this.d.step=label;this.d.stepIndex=-1;this.log('Info','开始 '+label);
    try{await fn();this.log('Info','完成 '+label)}
    catch(e){this.d.state='Fault';this.d.alarm=e.message;this.d.activeAlarm=this.diagnostic(e,label);this.d.recoveryRequired=true;this.invalidateAxes();this.log('Error',label+' 失败：'+e.message);throw e}
    finally{this.d.busy=false;this.d.step=this.d.state==='Fault'?'故障待处理':'等待操作'}
  }
  async executeStep(s){
    if(s.type==='WaitInput'){
      const start=Date.now();while(!this.d.inputs[s.device]){this.check();if(Date.now()-start>s.timeoutMs)throw Error('等待输入 '+s.device+' 超时');await new Promise(r=>setTimeout(r,35))}return;
    }
    if(s.type==='VisionLocate')return this.vision();
    if(s.type==='AxisMove')return this.axisMove(s.device,s.useVisionX?this.d.vision?.targetXmm+(this.cycleRecipe?.visionOffsetMm||0):s.positionMm);
    if(s.type==='CylinderExtend')return this.cylinderAct(s.device,true);
    if(s.type==='CylinderRetract')return this.cylinderAct(s.device,false);
    if(s.type==='Delay')return this.sleep(s.durationMs+(this.cycleRecipe?.extraDwellMs||0));
    throw Error('未知流程动作 '+s.type);
  }
  async runCycle(){
    try{
      await this.flow.run();
      this.d.cycles++;this.d.state='Idle';this.d.step='单周期完成';this.d.stepIndex=this.cfg.sequence.length;this.log('Info','完成第 '+this.d.cycles+' 个周期');
    }catch(e){
      this.d.recoveryRequired=true;this.invalidateAxes();
      if(this.cancel||['STOP_REQUESTED','GROUP_ABORTED'].includes(e.message)){
        this.d.state='Idle';this.d.step='软件停止完成，需人工恢复';this.log('Warning','流程已取消，需检查机构状态');
      }else{
        this.d.state='Fault';this.d.step='故障待处理';this.d.alarm=e.message;this.d.activeAlarm=this.diagnostic(e,this.d.step);this.log('Error','流程中断：'+e.message);
      }
    }finally{
      this.flow.releaseWait();
      this.recordCycle(this.d.state==='Fault'?'Failed':this.d.recoveryRequired?'Stopped':'Completed');
      this.cancel=false;this.d.busy=false;
    }
  }
  async post(path,body={}){
    if(path==='/api/flow/debug')return this.flow.command(body.command,body);
    if(path==='/api/mode'){
      this.idle();if(!['Manual','Auto'].includes(body.mode))throw Error('未知模式');if(body.mode==='Auto'&&this.d.recoveryRequired)throw Error('请先完成恢复确认');
      this.d.mode=body.mode;this.d.step='等待操作';this.d.stepIndex=-1;this.log('Info','切换模式 '+body.mode);return;
    }
    if(path==='/api/sim/input'){
      if(!this.cfg.signals.find(s=>s.name===body.name&&s.direction==='DI'&&s.injectable))throw Error('该输入不可修改');
      this.d.inputs[body.name]=!!body.value;this.log('Warning','注入外部 DI '+body.name+' = '+body.value);return;
    }
    if(path==='/api/sim/fault'){
      if(!Object.hasOwn(this.d.faultInjection,body.name))throw Error('未知故障注入项');
      this.d.faultInjection[body.name]=!!body.value;this.log('Warning','注入故障 '+body.name+' = '+body.value);return;
    }
    let match=path.match(/^\/api\/manual\/axes\/([^/]+)\/(enable|home|move)$/);
    if(match){
      const [,id,cmd]=match;const a=this.d.axes[id];this.cfgAxis(id);
      if(cmd==='enable')return this.perform(id+(body.enabled?' 使能':' 去使能'),'AxisEnable',id,async()=>{if(a.moving)throw Error('运动中禁止更改使能');a.enabled=!!body.enabled;if(!a.enabled)a.homed=false});
      if(cmd==='home')return this.perform(id+' 回零','AxisMove',id,()=>this.axisMove(id,this.cfgAxis(id).homeMm,true));
      return this.perform(id+' 绝对定位','AxisMove',id,()=>this.axisMove(id,body.positionMm));
    }
    match=path.match(/^\/api\/manual\/cylinders\/([^/]+)\/(extend|retract)$/);
    if(match){const [,id,cmd]=match;this.cfgCylinder(id);return this.perform(id+(cmd==='extend'?' 伸出':' 缩回'),cmd==='extend'?'CylinderExtend':'CylinderRetract',id,()=>this.cylinderAct(id,cmd==='extend'))}
    if(path==='/api/manual/vision/trigger')return this.perform('视觉触发','VisionLocate','Camera',()=>this.vision());
    if(path==='/api/cycle/start'){
      this.idle();if(this.d.mode!=='Auto')throw Error('请先切换自动模式');if(this.d.recoveryRequired)throw Error('先完成故障恢复确认');this.guard();
      if(!this.d.inputs.PartPresent)throw Error('未检测到产品');
      for(const a of this.cfg.axes)if(!this.d.axes[a.id].enabled||!this.d.axes[a.id].homed)throw Error(a.id+' 未使能或未回零');
      for(const c of this.cfg.cylinders)if(this.d.cylinders[c.id].position!=='Retracted')throw Error(c.id+' 未缩回到位');
      this.d.state='Running';this.d.busy=true;this.d.alarm=null;this.d.activeAlarm=null;this.d.vision=null;this.cancel=false;this.cycleId=Date.now().toString(36)+'-'+Math.random().toString(36).slice(2,7);this.cycleStarted=Date.now();this.currentSteps=[];this.d.step='准备开始';this.d.stepIndex=-1;this.log('Info','启动自动单周期');void this.runCycle();return;
    }
    if(path==='/api/cycle/stop'){if(!['Running','Stopping'].includes(this.d.state))throw Error('设备不在自动运行');this.cancel=true;this.flow.releaseWait();this.d.state='Stopping';this.d.step='停止请求处理中';this.log('Warning','请求软件停止，不是急停');return}
    if(path==='/api/fault/reset'){
      if(this.d.busy||this.d.state!=='Fault')throw Error('当前没有可复位故障');this.guard();
      this.d.state='Idle';this.d.mode='Manual';this.d.alarm=null;this.d.activeAlarm=null;this.d.stepIndex=-1;this.d.step='进入人工恢复流程';this.log('Warning','报警复位，仍需机构复归');return;
    }
    if(path==='/api/recovery/confirm'){
      this.idle();if(this.d.mode!=='Manual')throw Error('恢复需在手动模式');this.guard();
      for(const a of this.cfg.axes){const s=this.d.axes[a.id];if(!s.enabled||!s.homed||Math.abs(s.positionMm-a.homeMm)>.01)throw Error(a.id+' 尚未回到零位')}
      for(const c of this.cfg.cylinders)if(this.d.cylinders[c.id].position!=='Retracted')throw Error(c.id+' 尚未缩回');
      this.d.recoveryRequired=false;this.d.step='恢复条件已确认';this.log('Info','仿真恢复确认完成');return;
    }
    throw Error('未知接口：'+path);
  }
}
window.MockRuntime=MockRuntime;
