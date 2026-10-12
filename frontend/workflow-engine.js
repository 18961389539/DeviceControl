/* V0.5 SIMULATION-ONLY workflow scheduler. Deterministic stage graph and trace APIs.
 * Resource locks are business-process locks, NOT a safety system. */
class WorkflowEngine {
  constructor(runtime, sequence) {
    this.m=runtime;
    this.sequence=sequence;
    this.breakpoints=new Set();
    this.resourceOwners=new Map();
    this.status='Idle';this.active=[];this.traces=[];this.pauseRequested=false;this.stepMode=false;
    this.waiting=null;this.haltGroup=false;this.lastError=null;
    this.version=runtime.cfg.flowVersion||'unversioned';
    this.nodes=this.flatten(sequence);
    this.validate(sequence);
  }
  flatten(nodes){return nodes.flatMap((node,i)=>[{id:node.id,label:node.label,type:node.type,resources:this.resources(node),children:(node.children||[]).map(x=>x.id)},...(node.children?this.flatten(node.children):[])]);}
  resources(node){
    if(node.type==='Parallel'||node.type==='Condition')return [...new Set((node.children||[]).flatMap(c=>this.resources(c)))].sort();
    if(node.type==='AxisMove')return ['Axis:'+node.device];
    if(node.type==='CylinderExtend'||node.type==='CylinderRetract')return ['Cylinder:'+node.device];
    if(node.type==='VisionLocate')return ['Camera:Main'];
    return [];
  }
  validate(nodes){
    const ids=new Set(),seen=new Set(),safe=new Set(['WaitInput','VisionLocate','Delay']);
    const visit=(nodes,level=0)=>{
      if(level>1)throw Error('V0.5 最多支持一层并行分支');
      for(const n of nodes){
        if(!n.id||ids.has(n.id))throw Error('流程节点 ID 缺失或重复');ids.add(n.id);
        if(!n.label||!n.type)throw Error('流程节点缺少名称或类型');
        const policy=n.onError||'Abort';
        if(!['Abort','Retry','WaitForOperator'].includes(policy))throw Error(n.id+' 不支持的错误策略');
        if(policy!=='Abort'&&!safe.has(n.type))throw Error(n.id+' 非幂等动作禁止自动重试或操作员重试');
        if(policy==='Retry'&&(!Number.isInteger(n.maxRetries)||n.maxRetries<1||n.maxRetries>2))throw Error(n.id+' 重试上限必须为 1~2');
        if(n.sharedResources?.length){
          if(n.type==='Parallel'||n.type==='Condition')throw Error('分支容器不得直接占用共享资源');
          window.SharedResourceCoordinator.keys(n.sharedResources);
        }
        if(n.resourceWaitTimeoutMs!==undefined&&(!Number.isInteger(n.resourceWaitTimeoutMs)||n.resourceWaitTimeoutMs<100||n.resourceWaitTimeoutMs>30000))throw Error('资源等待超时参数不合法');
        if(n.type==='Parallel'||n.type==='Condition'){
          if(level||!n.children||(n.type==='Parallel'?(n.children.length<2||n.children.length>4):n.children.length!==2))throw Error('分支结构不合法');
          visit(n.children,level+1);
        }else if(!['WaitInput','VisionLocate','AxisMove','CylinderExtend','CylinderRetract','Delay'].includes(n.type))throw Error('未知流程节点 '+n.type);
      }
    };visit(nodes);
  }
  snapshot(){return {version:this.version,status:this.status,breakpoints:[...this.breakpoints].sort((a,b)=>a-b),active:this.active.map(x=>({...x})),traces:this.traces.slice(-180).map(x=>({...x})),resources:Object.fromEntries(this.resourceOwners),waiting:this.waiting?{kind:this.waiting.kind,index:this.waiting.index,message:this.waiting.message}:null,nodes:this.nodes};}
  command(cmd,body={}){
    if(cmd==='breakpoint'){
      if(!Number.isInteger(body.index)||body.index<0||body.index>=this.sequence.length)throw Error('断点序号无效');
      if(body.enabled)this.breakpoints.add(body.index);else this.breakpoints.delete(body.index);
      return this.snapshot();
    }
    if(cmd==='pause'){
      if(this.status!=='Running')throw Error('流程当前不可暂停');
      this.pauseRequested=true;return this.snapshot();
    }
    if(cmd==='resume'||cmd==='step'){
      if(this.status!=='Paused'||!this.waiting||this.waiting.kind!=='breakpoint')throw Error('流程当前不在可继续的断点');
      this.stepMode=cmd==='step';this.pauseRequested=false;
      const waiter=this.waiting;this.waiting=null;this.status='Running';waiter.resolve('continue');return this.snapshot();
    }
    if(cmd==='retry'||cmd==='abort'){
      if(this.status!=='Paused'||!this.waiting||this.waiting.kind!=='error')throw Error('当前没有等待人工处理的异常');
      const waiter=this.waiting;this.waiting=null;this.status='Running';waiter.resolve(cmd);return this.snapshot();
    }
    throw Error('未知流程调试指令 '+cmd);
  }
  releaseWait(){if(this.waiting){const w=this.waiting;this.waiting=null;w.resolve('abort');}}
  async atBoundary(index,node){
    if(!this.pauseRequested&&!this.breakpoints.has(index)&&!this.stepMode)return;
    this.stepMode=false;this.pauseRequested=false;
    this.status='Paused';this.m.d.step='断点暂停 · '+node.label;
    const outcome=await new Promise(resolve=>{this.waiting={kind:'breakpoint',index,message:node.label,resolve}});
    if(outcome==='abort'||this.m.cancel)throw Error('STOP_REQUESTED');
  }
  async operatorRetry(index,node,error){
    this.status='Paused';this.lastError=error.message;
    this.m.d.step='异常待人工处理 · '+node.label;
    const decision=await new Promise(resolve=>{this.waiting={kind:'error',index,message:error.message,resolve}});
    if(decision!=='retry'||this.m.cancel)throw Error('STOP_REQUESTED');
  }
  async withResources(node,fn){
    const resources=this.resources(node);
    const shared=window.SharedResourceCoordinator.keysFor(node);
    let releaseShared=null;
    const acquired=[];
    try{
      if(shared.length&&this.m.sharedCoordinator){
        releaseShared=await this.m.sharedCoordinator.acquire(this.m.stationId||'A',node.id,shared,
          node.resourceWaitTimeoutMs??2500,()=>this.m.cancel||this.haltGroup);
      }
      for(const r of resources){
        while(this.resourceOwners.has(r)){
          this.m.check();if(this.haltGroup)throw Error('GROUP_ABORTED');
          await new Promise(resolve=>setTimeout(resolve,15));
        }
        this.resourceOwners.set(r,node.id);acquired.push(r);
      }
      return await fn();
    }finally{
      for(const r of acquired)if(this.resourceOwners.get(r)===node.id)this.resourceOwners.delete(r);
      if(releaseShared)releaseShared();
    }
  }
  async executeNode(node,index,parentId=null){
    const started=Date.now(),trace={id:node.id,parentId,label:node.label,type:node.type,resources:this.resources(node),sharedResources:window.SharedResourceCoordinator.keysFor(node),index,attempt:0,status:'Running',startedAt:new Date().toISOString(),durationMs:0,variablesBefore:this.m.traceVariables?.()||null};
    this.active.push(trace);this.traces.push(trace);if(this.traces.length>180)this.traces.shift();
    let outcome='Completed',error=null;
    try{
      if(node.type==='Condition'){
        const branch=this.m.d.inputs[node.device]?node.children[0]:node.children[1];
        await this.executeNode(branch,index,node.id);
      }else if(node.type==='Parallel'){
        this.haltGroup=false;
        const settled=await Promise.allSettled(node.children.map(child=>this.executeNode(child,index,node.id).catch(err=>{this.haltGroup=true;throw err})));
        this.haltGroup=false;
        const failed=settled.find(x=>x.status==='rejected');if(failed)throw failed.reason;
      }else{
        let attempt=0;
        while(true){
          this.m.check();if(this.haltGroup)throw Error('GROUP_ABORTED');
          attempt++;trace.attempt=attempt;
          try{await this.withResources(node,()=>this.m.executeStep(node));break}
          catch(e){
            if(this.m.cancel||e.message==='STOP_REQUESTED'||e.message==='GROUP_ABORTED')throw e;
            if(node.onError==='Retry'&&!e.message.includes('资源等待超时')&&attempt<=node.maxRetries){this.m.log('Warning',node.label+' 第 '+attempt+' 次失败，执行安全重试');continue}
            if(node.onError==='WaitForOperator'&&!e.message.includes('资源等待超时')) {await this.operatorRetry(index,node,e);continue}
            throw e;
          }
        }
      }
    }catch(e){error=e;outcome=this.m.cancel||e.message==='STOP_REQUESTED'||e.message==='GROUP_ABORTED'?'Canceled':'Failed';throw e}
    finally{
      trace.status=outcome;trace.durationMs=Date.now()-started;trace.endedAt=new Date().toISOString();trace.variablesAfter=this.m.traceVariables?.()||null;
      if(error)trace.error=error.message;
      this.active=this.active.filter(x=>x!==trace);
      this.m.recordStep(index,node,outcome,trace.durationMs,error?.message||null);
    }
  }
  async run(){
    this.status='Running';this.lastError=null;this.active=[];this.resourceOwners.clear();this.traces=[];this.pauseRequested=false;this.stepMode=false;
    try{
      for(const [index,node] of this.sequence.entries()){
        this.m.check('Sequence','Station');
        await this.atBoundary(index,node);this.m.check();
        this.m.d.stepIndex=index;this.m.d.step=node.label;this.m.log('Info','流程步骤: '+node.label);
        await this.executeNode(node,index);
      }
    }finally{this.status='Idle';this.releaseWait();this.resourceOwners.clear();this.active=[];this.haltGroup=false}
  }
}
window.WorkflowEngine=WorkflowEngine;
