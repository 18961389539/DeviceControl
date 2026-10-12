/* V0.8 offline quality dashboard. No device commands; scenario engine runs in memory. */
(function(global){
  const h=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  class QualityDashboard {
    constructor({editor,config,runs,role,notify}){
      this.editor=editor;this.config=config;this.runs=runs;this.role=role;this.notify=notify;
      this.stationId='A';this.sessions={};this.result=null;this.custom=[];this.version=null;
      this.loadCustom('A');
      const panel=document.getElementById('panel-quality');
      panel.addEventListener('click',e=>{
        const action=e.target.closest('[data-quality-action]')?.dataset.qualityAction;
        if(action==='run'){if(this.role()<2)return this.notify('需要 Engineer 权限',true);this.run()}
        else if(action==='add'){this.add()}
        else if(action==='clear'){this.custom=[];this.save();this.render()}
        else if(action==='export'){this.export()}
        else if(action==='contract'){this.contract()}
        const del=e.target.closest('[data-quality-delete]')?.dataset.qualityDelete;
        if(del){this.custom=this.custom.filter(s=>s.id!==del);this.save();this.render()}
      });
    }
    loadCustom(id){
      try{const key='devicecontrol.v13.scenarios.'+id;
        const raw=localStorage.getItem(key)||(id==='A'?localStorage.getItem('devicecontrol.v08.scenarios'):null)||'[]';
        const saved=JSON.parse(raw);this.custom=Array.isArray(saved)?saved.slice(0,30):[];
      }catch{this.custom=[]}
    }
    switchStation(id){
      if(id===this.stationId)return;
      this.sessions[this.stationId]={result:this.result,version:this.version,custom:this.custom};
      this.stationId=id;
      if(this.sessions[id]){this.result=this.sessions[id].result;this.version=this.sessions[id].version;this.custom=this.sessions[id].custom;}
      else{this.result=null;this.version=null;this.loadCustom(id)}
      document.getElementById('qualityContractStatus').textContent='未在工站 '+id+' 执行本地契约检查';
      document.getElementById('qualityContractCases').innerHTML='';
    }
    contract(){
      const cfg=this.config(),base=JSON.parse(JSON.stringify(cfg.sequence));
      const fixtures=[
        {name:'默认流程',flow:base,expected:true},
        {name:'视觉变量先于赋值',flow:(()=>{const a=JSON.parse(JSON.stringify(base));const at=a.findIndex(n=>n.type==='VisionLocate');a.push(...a.splice(at,1));return a})(),expected:false},
        {name:'同资源并行争用（警告）',flow:(()=>{const a=JSON.parse(JSON.stringify(base));const p=a.find(n=>n.type==='Parallel');p.children[1].device=p.children[0].device;return a})(),expected:true},
        {name:'等待不满足的输入',flow:(()=>{const a=JSON.parse(JSON.stringify(base));a[0].device='ClampExtended';return a})(),expected:false}
      ];
      const result=fixtures.map(f=>{const value=global.WorkflowQuality.runSuite(f.flow,cfg);return {name:f.name,expected:f.expected,actual:value.pass,pass:value.pass===f.expected}});
      const passed=result.filter(x=>x.pass).length;
      document.getElementById('qualityContractStatus').textContent=`浏览器模型契约检查：${passed} / ${result.length} 个符合预期；不等于 C# 端到端测试通过。`;
      document.getElementById('qualityContractCases').innerHTML=result.map(x=>`<div><b>${h(x.pass?'✓':'!')} ${h(x.name)}</b><small>期望 ${x.expected?'通过':'阻止'} · 实际 ${x.actual?'通过':'阻止'}</small></div>`).join('');
      this.notify(passed===result.length?'本地契约检查通过':'本地契约检查存在差异',passed!==result.length);
    }
    save(){try{localStorage.setItem('devicecontrol.v13.scenarios.'+this.stationId,JSON.stringify(this.custom))}catch{}}
    selectedDefinition(){return this.editor.sequence.length?this.editor.sequence:this.config().sequence}
    run(){
      const flow=this.selectedDefinition(),cfg=this.config();
      this.result=global.WorkflowQuality.runSuite(flow,cfg,this.custom);
      this.version=this.editor.data?.active?.id||cfg.flowVersion;
      this.render();this.notify(this.result.pass?'质量门禁通过：所有场景符合预期':'质量门禁未通过：请查看失败场景',!this.result.pass);
    }
    add(){
      if(this.role()<2)return this.notify('需要 Engineer 权限',true);
      const name=document.getElementById('qualityCaseName').value.trim();
      const signal=document.getElementById('qualityCaseSignal').value;
      const flag=document.getElementById('qualityCaseState').value==='true';
      const expected=document.getElementById('qualityCaseExpected').value;
      if(!name||name.length>45||this.custom.length>=30)return this.notify('名称需 1～45 字，最多 30 条',true);
      const inputs=global.WorkflowQuality.scenarios(this.config())[0].inputs;
      if(!(signal in inputs))return this.notify('只能配置已声明的布尔 DI',true);
      this.custom.push({id:'case-'+Date.now().toString(36)+'-'+this.custom.length,name,inputs:{...inputs,[signal]:flag},visionOk:true,expected});
      this.save();this.result=null;this.render();this.notify('已添加仿真场景');
    }
    export(){
      if(!this.result)return this.notify('请先运行质量门禁',true);
      const report={release:'V1.3',stationId:this.stationId,scope:'Browser dry-run, NOT hardware execution',workflowVersion:this.version,
        generatedUtc:new Date().toISOString(),result:this.result,customScenarios:this.custom};
      const blob=new Blob([JSON.stringify(report,null,2)],{type:'application/json'});
      const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='workflow-quality-'+this.stationId+'.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
    }
    render(){
      const cfg=this.config(),inputs=cfg.signals.filter(x=>x.direction==='DI'&&x.injectable);
      const sel=document.getElementById('qualityCaseSignal'),prev=sel.value;
      sel.innerHTML=inputs.map(x=>`<option value="${h(x.name)}">${h(x.name)} · ${h(x.description)}</option>`).join('');
      if(inputs.some(x=>x.name===prev))sel.value=prev;
      document.querySelectorAll('[data-quality-action="run"],[data-quality-action="add"]').forEach(b=>b.disabled=this.role()<2);
      const r=this.result,stats=global.WorkflowQuality.metrics(this.runs());
      document.getElementById('qualityStatus').textContent=r?(r.pass?'门禁通过':'存在失败项'):'待执行';
      document.getElementById('qualityStatus').className='pill '+(r?r.pass?'success':'alert':'neutral');
      document.getElementById('qualityTotal').textContent=r?r.results.length+' 个':'—';
      document.getElementById('qualityPassed').textContent=r?r.results.filter(x=>x.pass).length+' 个':'—';
      document.getElementById('qualityEstimate').textContent=r?(r.estimatedMs/1000).toFixed(2)+' s':'—';
      document.getElementById('qualityScenarios').innerHTML=r?r.results.map(x=>`<div class="quality-case ${x.pass?'pass':'fail'}"><span class="quality-check">${x.pass?'✓':'!'}</span><div><strong>${h(x.name)}</strong><small>预期 ${h(x.expected)} · 实际 ${h(x.actual)} · ${h(x.reason)}</small></div></div>`).join(''):
        '<div class="empty-state">点击“运行质量门禁”执行内存中的确定性场景检查，不会操作仿真设备。</div>';
      document.getElementById('qualityProblems').innerHTML=r?[
        ...r.errors.map(e=>`<div class="quality-issue bad">错误 · ${h(e)}</div>`),
        ...r.warnings.map(e=>`<div class="quality-issue">提示 · ${h(e)}</div>`)].join('')||'<div class="quality-issue good">✓ 未发现静态约束问题</div>':
        '<div class="empty-state">等待执行</div>';
      document.getElementById('qualityCustom').innerHTML=this.custom.map(x=>`<div class="quality-custom"><div><b>${h(x.name)}</b><small>${h(x.expected)} · ${h(Object.keys(x.inputs).find(k=>x.inputs[k]!==global.WorkflowQuality.scenarios(cfg)[0].inputs[k])||'默认')} </small></div><button class="btn btn-light" data-quality-delete="${h(x.id)}">删除</button></div>`).join('')||'<div class="empty-state">尚未添加自定义场景</div>';
      document.getElementById('qualityP50').textContent=stats.p50===null?'—':(stats.p50/1000).toFixed(2)+' s';
      document.getElementById('qualityP95').textContent=stats.p95===null?'—':(stats.p95/1000).toFixed(2)+' s';
      document.getElementById('qualitySuccess').textContent=stats.rate===null?'—':stats.rate+'%';
      document.getElementById('qualityCycleCount').textContent=stats.total+' 条运行记录';
      const max=Math.max(...stats.bottlenecks.map(x=>x.totalMs),1);
      document.getElementById('qualityBottlenecks').innerHTML=stats.bottlenecks.map(x=>`<div class="quality-bar-row"><div><strong>${h(x.label)}</strong><small>${x.count} 次 · 合计 ${x.totalMs} ms</small></div><div class="quality-track"><div style="width:${Math.round(100*x.totalMs/max)}%"></div></div></div>`).join('')||'<div class="empty-state">需要先运行一段模拟生产周期，才能根据实际历史记录生成统计。</div>';
    }
  }
  global.WorkflowQualityUI={QualityDashboard};
})(window);
