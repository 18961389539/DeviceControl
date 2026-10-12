/* V0.6 simulation workflow revision store and validator. Browser storage is demo-only.
 * No actual machine I/O is available through this module. */
(function(global){
  const clone = x => JSON.parse(JSON.stringify(x));
  const ALLOWED = new Set(['WaitInput','VisionLocate','AxisMove','CylinderExtend','CylinderRetract','Delay','Parallel','Condition']);
  const RETRYABLE = new Set(['WaitInput','VisionLocate','Delay']);
  const key='devicecontrol.v06.workflows';
  function validateFlow(nodes, cfg){
    const errors=[], ids=new Set(), inputs=new Set(cfg.signals.filter(s=>s.direction==='DI').map(s=>s.name));
    const axes=new Map(cfg.axes.map(a=>[a.id,a])),cylinders=new Set(cfg.cylinders.map(c=>c.id));
    if(!Array.isArray(nodes)||nodes.length<1||nodes.length>60)return ['顶层节点数量必须为 1～60'];
    function visit(n,depth){
      if(!n||typeof n!=='object'){errors.push('节点不是对象');return}
      if(!n.id||!/^[a-zA-Z0-9_-]{1,50}$/.test(n.id)||ids.has(n.id))errors.push('节点 ID 不合法或重复: '+n.id);
      ids.add(n.id);
      if(!String(n.label||'').trim()||n.label.length>90)errors.push(n.id+' 标签为空或过长');
      if(!ALLOWED.has(n.type))errors.push(n.id+' 类型不受支持');
      if(!['Abort','Retry','WaitForOperator'].includes(n.onError||'Abort'))errors.push(n.id+' 错误策略无效');
      if((n.onError||'Abort')!=='Abort'&&!RETRYABLE.has(n.type))errors.push(n.id+' 执行动作不允许重试');
      if(n.onError==='Retry'&&(!Number.isInteger(n.maxRetries)||n.maxRetries<1||n.maxRetries>2))errors.push(n.id+' 重试次数仅支持 1～2');
      if(n.resourceWaitTimeoutMs!==undefined&&(!Number.isInteger(n.resourceWaitTimeoutMs)||n.resourceWaitTimeoutMs<100||n.resourceWaitTimeoutMs>30000))errors.push(n.id+' 共享资源等待超时必须为 100～30000 ms');
      const resources=n.sharedResources||[];
      if(!Array.isArray(resources)||resources.length>8||resources.some(k=>typeof k!=='string'||!/^[A-Z][A-Z0-9_-]{0,31}$/.test(k.toUpperCase()))||new Set(resources.map(k=>k.toUpperCase())).size!==resources.length)errors.push(n.id+' 共享资源列表不合法或重复');
      if((n.type==='Parallel'||n.type==='Condition')&&resources.length)errors.push(n.id+' 分支容器不可直接锁共享资源');
      if(n.type==='Parallel'||n.type==='Condition'){
        if(depth!==0)errors.push(n.id+' 暂不支持嵌套分支');
        const size=n.children?.length||0;
        if(n.type==='Parallel'&&(size<2||size>4))errors.push(n.id+' 并行子节点必须为 2～4');
        if(n.type==='Condition'&&size!==2)errors.push(n.id+' 条件分支必须有真/假两个子节点');
        if(n.type==='Condition'&&!inputs.has(n.device))errors.push(n.id+' 条件输入不存在: '+n.device);
        (n.children||[]).forEach(c=>visit(c,depth+1));
      }else{
        if(n.children?.length)errors.push(n.id+' 普通节点不能包含子节点');
        if(n.type==='WaitInput'&&(!inputs.has(n.device)||!Number.isFinite(n.timeoutMs)||n.timeoutMs<100||n.timeoutMs>120000))errors.push(n.id+' 等待输入/超时配置无效');
        if(n.type==='AxisMove'&&(!axes.has(n.device)||(!n.useVisionX&&(!Number.isFinite(n.positionMm)||n.positionMm<axes.get(n.device).minMm||n.positionMm>axes.get(n.device).maxMm))))errors.push(n.id+' 轴/定位目标无效');
        if(['CylinderExtend','CylinderRetract'].includes(n.type)&&!cylinders.has(n.device))errors.push(n.id+' 气缸名称无效');
        if(n.type==='Delay'&&(!Number.isFinite(n.durationMs)||n.durationMs<0||n.durationMs>30000))errors.push(n.id+' 延时必须为 0～30000 ms');
      }
    }
    nodes.forEach(n=>visit(n,0));
    if(ids.size>140)errors.push('节点总数不能超过 140');
    return errors;
  }
  function newNode(type,config,id){
    let n={id:id||'n_'+Math.random().toString(36).slice(2,9),label:{WaitInput:'等待输入',VisionLocate:'视觉定位',AxisMove:'轴定位',CylinderExtend:'气缸伸出',CylinderRetract:'气缸缩回',Delay:'延时',Parallel:'并行动作',Condition:'条件分支'}[type]||type,type,onError:'Abort',device:'',children:[],sharedResources:[],resourceWaitTimeoutMs:2500};
    if(type==='WaitInput'){n.device='PartPresent';n.timeoutMs=1500}
    if(type==='AxisMove'){n.device=config.axes[0].id;n.positionMm=config.axes[0].homeMm}
    if(type==='CylinderExtend'||type==='CylinderRetract')n.device=config.cylinders[0].id;
    if(type==='Delay')n.durationMs=200;
    if(type==='Condition'){n.device='PartPresent';n.children=[newNode('Delay',config),newNode('Delay',config)];n.children[0].label='条件满足';n.children[1].label='条件不满足';}
    if(type==='Parallel'){n.children=[newNode('Delay',config),newNode('Delay',config)];n.children[0].label='并行任务 A';n.children[1].label='并行任务 B';}
    return n;
  }
  class WorkflowRevisions {
    constructor(machine){
      this.machine=machine;this.cfg=machine.cfg;this.versions=[];this.activeId=null;
      this.storageKey=machine.stationId==='A'?key:key+'.'+machine.stationId;
      try {
        const saved=JSON.parse(localStorage.getItem(this.storageKey)||'null');
        if(saved&&Array.isArray(saved.versions)&&saved.versions.length&&saved.activeId&&saved.versions.some(v=>v.id===saved.activeId&&v.status==='Published')){
          this.versions=saved.versions;this.activeId=saved.activeId;
        }
      }catch{}
      if(!this.versions.length){
        const initial=this.make(clone(machine.cfg.sequence),'system','Published',null,'初始固定流程');
        this.versions=[initial];this.activeId=initial.id;
      }
      this.apply(this.active());
    }
    active(){return this.versions.find(v=>v.id===this.activeId)}
    list(){return {active:clone(this.active()),versions:clone([...this.versions].reverse())}}
    make(definition,author,status,sourceId,reason){
      return {id:'flow-'+String(this.versions.length+1).padStart(4,'0'),status,author,createdAt:new Date().toISOString(),sourceId,reason,definition:clone(definition),hash:this.fingerprint(definition)};
    }
    fingerprint(obj){let n=2166136261;for(const c of JSON.stringify(obj)){n^=c.charCodeAt(0);n=Math.imul(n,16777619)}return(n>>>0).toString(16).padStart(8,'0')+'-demo'}
    save(){try{localStorage.setItem(this.storageKey,JSON.stringify({activeId:this.activeId,versions:this.versions}))}catch{}}
    ensureIdle(){const m=this.machine.d;if(m.state!=='Idle'||m.busy||m.recoveryRequired)throw Error('运行中或恢复未完成，禁止流程发布/回滚')}
    apply(v){
      this.machine.cfg.sequence=clone(v.definition);this.machine.cfg.flowVersion=v.id;
      this.machine.flow=new WorkflowEngine(this.machine,this.machine.cfg.sequence);
      // app.js keeps the same STATION_CONFIG reference; update it in place.
      if(global.STATION_CONFIG){global.STATION_CONFIG.sequence=clone(v.definition);global.STATION_CONFIG.flowVersion=v.id}
    }
    handle(path,body,user){
      const rank={Operator:1,Engineer:2,Admin:3}[user?.role]||0;
      const need=path==='/api/flows/draft'||path.endsWith('/validate')?2:3;
      if(rank<need)throw Error('当前角色无权操作流程版本');
      if(path==='/api/flows/draft'){
        if(body.expectedActiveVersionId && body.expectedActiveVersionId!==this.activeId)throw Error('版本冲突：当前已发布流程发生变化，请刷新并合并改动');
        const errors=validateFlow(body.definition,this.cfg);if(errors.length)throw Error('流程静态检查失败: '+errors[0]);
        const v=this.make(body.definition,user.name,'Draft',this.activeId,'草稿已保存');this.versions.push(v);this.save();return clone(v);
      }
      if(path==='/api/flows/rollback'){
        this.ensureIdle();const from=this.versions.find(v=>v.id===body.id&&['Published','Superseded'].includes(v.status));
        if(!from)throw Error('只能回滚到已发布过的历史版本');
        const errors=validateFlow(from.definition,this.cfg);if(errors.length)throw Error(errors.join('; '));
        this.active().status='Superseded';const v=this.make(from.definition,user.name,'Published',from.id,'回滚发布');this.versions.push(v);this.activeId=v.id;this.apply(v);this.save();return clone(v);
      }
      const m=path.match(/^\/api\/flows\/([^/]+)\/(validate|publish)$/);if(!m)throw Error('未知流程版本操作');
      const v=this.versions.find(x=>x.id===m[1]);if(!v)throw Error('流程版本不存在');
      const errors=validateFlow(v.definition,this.cfg);if(errors.length)throw Error('验证失败: '+errors.join('；'));
      if(m[2]==='validate'){
        if(global.WorkflowQuality){const gate=global.WorkflowQuality.runSuite(v.definition,this.cfg);if(!gate.pass)throw Error('质量门禁未通过：'+(gate.errors[0]||gate.results.find(x=>!x.pass)?.name||gate.warnings[0]))}
        if(v.status!=='Draft')throw Error('只有草稿才能验证');v.status='Validated';v.reason='静态验证通过';
      }else{
        this.ensureIdle();if(v.status!=='Validated')throw Error('只允许发布已验证版本');
        if(v.sourceId!==this.activeId)throw Error('版本冲突：草稿基于过期版本，拒绝发布');
        if(global.WorkflowQuality&&!global.WorkflowQuality.runSuite(v.definition,this.cfg).pass)throw Error('质量门禁未通过，禁止发布');
        this.active().status='Superseded';v.status='Published';v.reason='管理员发布';this.activeId=v.id;this.apply(v);
      }
      this.save();return clone(v);
    }
  }
  global.FlowStudioModel={clone,validateFlow,newNode,WorkflowRevisions};
})(window);
