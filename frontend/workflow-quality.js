/* V0.8 deterministic, side-effect-free workflow quality gate.
 * Scenario execution is a model, NOT the control runtime: no API / device calls.
 * All scenario inputs are typed and cannot write internal cylinder feedback. */
(function (global) {
  'use strict';
  const copy = v => JSON.parse(JSON.stringify(v));
  const schema = Object.freeze({
    'recipe.visionOffsetMm': {type:'number',min:-10,max:10,unit:'mm'},
    'recipe.extraDwellMs': {type:'integer',min:0,max:1000,unit:'ms'},
    'vision.ok': {type:'boolean'},
    'vision.targetXmm': {type:'number',min:0,max:200,unit:'mm'}
  });
  function validateValue(spec,value){
    if(spec.type==='boolean') return typeof value==='boolean' ? null : '需要 Boolean';
    if(typeof value!=='number'||!Number.isFinite(value))return '需要有限数值';
    if(spec.type==='integer'&&!Number.isInteger(value))return '需要整数';
    if(value<spec.min||value>spec.max)return `超出范围 ${spec.min} ～ ${spec.max}`;
    return null;
  }
  function validateRecipe(values){
    const errors=[];
    for(const [field,spec] of Object.entries(schema)) if(field.startsWith('recipe.')){
      const error=validateValue(spec,values?.[field.slice(7)]);
      if(error) errors.push(`${field}: ${error}`);
    }
    return errors;
  }
  function inputsFor(cfg){return Object.fromEntries((cfg.signals||[]).filter(x=>x.direction==='DI').map(s=>[s.name,!!s.initial]));}
  function scenarios(cfg){
    const normal=inputsFor(cfg);
    return [
      {id:'normal',name:'正常路径',inputs:normal,visionOk:true,expected:'Completed'},
      {id:'missing',name:'产品缺失',inputs:{...normal,PartPresent:false},visionOk:true,expected:'Rejected'},
      {id:'air',name:'气压异常',inputs:{...normal,AirPressureOk:false},visionOk:true,expected:'Rejected'},
      {id:'camera',name:'视觉 NG',inputs:normal,visionOk:false,expected:'Failed',onlyIfVision:true}
    ];
  }
  function estimateFlow(flow,cfg){
    const axes=new Map(cfg.axes.map(a=>[a.id,a]));
    function duration(n){
      if(n.type==='Delay')return n.durationMs||0;
      if(n.type==='AxisMove'){const a=axes.get(n.device);return a?Math.ceil(Math.abs((n.useVisionX?80:n.positionMm)-a.homeMm)/a.speedMmPerSec*1000):0}
      if(n.type==='Parallel')return Math.max(0,...(n.children||[]).map(duration));
      if(n.type==='Condition')return Math.max(0,...(n.children||[]).map(duration));
      return {VisionLocate:320,CylinderExtend:140,CylinderRetract:140,WaitInput:10}[n.type]||0;
    }
    return flow.reduce((v,n)=>v+duration(n),0);
  }
  function runScenario(nodes,cfg,scenario){
    const steps=[], axes=Object.fromEntries(cfg.axes.map(x=>[x.id,x.homeMm]));
    const cylinders=Object.fromEntries(cfg.cylinders.map(x=>[x.id,'Retracted']));
    const inputs={...inputsFor(cfg),...scenario.inputs};
    const target=Number.isFinite(scenario.visionTargetXmm)?scenario.visionTargetXmm:80;
    let vision=null,reason=null;
    const fail=(why,node)=>{reason=why;if(node)steps.push({id:node.id,label:node.label,outcome:'Failed',reason:why});throw Error(why)};
    // Mirrors prerequisites of V0.7 StartSingleCycle: deliberately never mutates runtime.
    if(!inputs.GuardClosed||!inputs.AirPressureOk||!inputs.PartPresent)
      return {name:scenario.name,outcome:'Rejected',reason:'启动前置条件未满足',steps,axes,cylinders};
    const interlocks=cfg.interlocks||[];
    const execute=node=>{
      if(node.type==='Condition'){
        const branch=inputs[node.device]?0:1;
        steps.push({id:node.id,label:node.label,outcome:'Branch '+(branch===0?'TRUE':'FALSE')});
        return execute(node.children[branch]);
      }
      if(node.type==='Parallel'){
        // Ignore wall-time scheduling: exact concurrency is not predictable from a dry-run.
        // Resource collisions are checked as static warnings, not simulated as racing writes.
        for(const child of node.children)execute(child);
        steps.push({id:node.id,label:node.label,outcome:'Completed'});
        return;
      }
      if(node.type==='WaitInput'&&!inputs[node.device])fail('输入条件不满足: '+node.device,node);
      if(node.type==='VisionLocate'){
        if(!scenario.visionOk)fail('视觉检测 NG',node);
        vision={ok:true,targetXmm:target};
      }
      if(node.type==='AxisMove'){
        for(const x of interlocks.filter(x=>x.action==='AxisMove'&&(x.device==='*'||x.device===node.device)))
          if(inputs[x.signal]!==!!x.required)fail('动作互锁未满足: '+x.message,node);
        if(node.useVisionX&&!vision)fail('视觉变量未赋值，禁止引用',node);
        const axis=cfg.axes.find(x=>x.id===node.device);
        const position=node.useVisionX?vision.targetXmm:node.positionMm;
        if(!axis||!Number.isFinite(position)||position<axis.minMm||position>axis.maxMm)fail('轴目标不在类型/量程范围',node);
        axes[node.device]=position;
      }
      if(node.type==='CylinderExtend'||node.type==='CylinderRetract'){
        for(const x of interlocks.filter(x=>x.action===node.type&&(x.device==='*'||x.device===node.device)))
          if(inputs[x.signal]!==!!x.required)fail('动作互锁未满足: '+x.message,node);
        const cyl=cfg.cylinders.find(x=>x.id===node.device);if(!cyl)fail('气缸不存在',node);
        const ext=node.type==='CylinderExtend';cylinders[node.device]=ext?'Extended':'Retracted';
        inputs[cyl.extendedInput]=ext;inputs[cyl.retractedInput]=!ext;
      }
      steps.push({id:node.id,label:node.label,outcome:'Completed'});
    };
    try{for(const node of nodes)execute(node)}catch(e){return {name:scenario.name,outcome:'Failed',reason:reason||e.message,steps,axes,cylinders}}
    return {name:scenario.name,outcome:'Completed',reason:'全部模型步骤完成',steps,axes,cylinders};
  }
  function staticWarnings(nodes,cfg){
    const warnings=[];
    const resource=n=>n.type==='AxisMove'?'axis:'+n.device:
      /Cylinder/.test(n.type)?'cylinder:'+n.device:null;
    let precedingVision=false;
    for(const n of nodes){
      if(n.type==='AxisMove'&&n.useVisionX&&!precedingVision)warnings.push(`${n.label} 在未经过必经视觉节点时引用了视觉目标`);
      if(n.type==='VisionLocate')precedingVision=true;
      if(n.type==='Parallel'){
        const owners=new Set();for(const child of n.children||[]){const r=resource(child);
          if(r&&owners.has(r))warnings.push(`${n.label} 的并行分支竞争相同资源 ${r}，运行时会串行化`);
          if(r)owners.add(r);
          if(child.type==='AxisMove'&&child.useVisionX&&!precedingVision)warnings.push(`${child.label} 使用未确认存在的视觉目标`);
        }
      }
    }
    return warnings;
  }
  function runSuite(nodes,cfg,custom=[]){
    const errors=global.FlowStudioModel.validateFlow(nodes,cfg);
    const cases=[...scenarios(cfg),...custom];
    const results=cases.filter(s=>!s.onlyIfVision||nodes.some(n=>n.type==='VisionLocate')).map(s=>{
      const actual=runScenario(nodes,cfg,s);
      return {id:s.id,name:s.name,expected:s.expected,actual:actual.outcome,pass:actual.outcome===s.expected,reason:actual.reason,steps:actual.steps};
    });
    const warnings=staticWarnings(nodes,cfg);
    const totalPass=errors.length===0&&results.every(x=>x.pass)&&warnings.filter(w=>w.includes('未经过必经')||w.includes('未确认存在')).length===0;
    return {pass:totalPass,errors,warnings,results,estimatedMs:estimateFlow(nodes,cfg),testedAt:new Date().toISOString()};
  }
  function percentile(values,p){
    if(!values.length)return null;
    const a=[...values].sort((x,y)=>x-y),idx=(a.length-1)*p,lo=Math.floor(idx),hi=Math.ceil(idx);
    return +(a[lo]+(a[hi]-a[lo])*(idx-lo)).toFixed(1);
  }
  function metrics(runs){
    const all=runs||[],completed=all.filter(r=>r.outcome==='Completed');
    const times=completed.map(r=>Number(r.durationMs)).filter(Number.isFinite);
    const sums=new Map();
    for(const r of all){
      const traces=Array.isArray(r.trace)&&r.trace.length?r.trace.filter(x=>x.type!=='Parallel'&&x.type!=='Condition'):r.steps||[];
      for(const t of traces){const ms=Number(t.durationMs)||0,key=t.label||t.id||'unnamed';
        const item=sums.get(key)||{label:key,count:0,totalMs:0,maxMs:0};item.count++;item.totalMs+=ms;item.maxMs=Math.max(item.maxMs,ms);sums.set(key,item);
      }
    }
    return {total:all.length,completed:completed.length,rate:all.length?+(100*completed.length/all.length).toFixed(1):null,p50:percentile(times,.5),p95:percentile(times,.95),
      bottlenecks:[...sums.values()].sort((a,b)=>b.totalMs-a.totalMs).slice(0,6)};
  }
  global.WorkflowQuality={schema,validateValue,validateRecipe,scenarios,runScenario,runSuite,metrics,percentile};
})(typeof window!=='undefined'?window:globalThis);
