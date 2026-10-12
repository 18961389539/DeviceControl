/* V0.7 read-only historical run replay. A replay never sends hardware or runtime commands.
 * Data is sourced strictly from persisted cycle trace/steps and NEVER guessed from live state. */
(function(global){
  const clone=v=>JSON.parse(JSON.stringify(v));
  function timeline(run){
    if(!run)return [];
    if(Array.isArray(run.trace)&&run.trace.length){
      // Join persisted snapshots by immutable trace ordinal. Never synthesize past values.
      const evidence = new Map((run.snapshots||[]).map(s=>[s.ordinal,s]));
      return [...run.trace].map((t,i)=>({...t,recordIndex:i})).sort((a,b)=>(a.endedAt||'').localeCompare(b.endedAt||'')||a.recordIndex-b.recordIndex).map((t,i)=>({order:i,id:t.id,label:t.label||t.id,parentId:t.parentId||null,
        kind:t.type||'Unknown',status:t.status||'Unknown',durationMs:Number(t.durationMs)||0,
        resources:t.resources||[],attempt:t.attempt||0,error:t.error||null,endedAt:t.endedAt||null,
        vars:t.variablesAfter?clone(t.variablesAfter):(evidence.has(t.recordIndex)?{
          inputs:evidence.get(t.recordIndex).inputs,axes:evidence.get(t.recordIndex).axes,
          cylinders:evidence.get(t.recordIndex).cylinders,vision:evidence.get(t.recordIndex).vision,
          recipeId:evidence.get(t.recordIndex).recipeId,flowVersion:evidence.get(t.recordIndex).flowVersion
        }:null),fromRecordedTrace:true}));
    }
    return (run.steps||[]).map((t,i)=>({order:i,id:String(t.index??i)+'-'+i,label:t.label||'步骤 '+i,parentId:null,
      kind:t.type||'Unknown',status:t.outcome||'Unknown',durationMs:Number(t.durationMs)||0,
      resources:[],attempt:0,error:t.error||null,endedAt:null,vars:null,fromRecordedTrace:false}));
  }
  class ReplayViewer{
    constructor(opts){this.opts=opts;this.runId=null;this.index=0;this.playing=false;this.timer=null;this.speed=750}
    switchStation(){this.stop();this.runId=null;this.index=0;this.lastSignature=null}
    stop(){this.playing=false;if(this.timer){clearInterval(this.timer);this.timer=null}}
    choose(id){this.stop();this.runId=id;this.index=0;this.render()}
    runs(){return this.opts.runs()||[]}
    selected(){return this.runs().find(r=>r.id===this.runId)||null}
    items(){return timeline(this.selected())}
    step(delta){this.stop();const items=this.items();this.index=Math.max(0,Math.min(items.length-1,this.index+delta));this.render()}
    seek(i){this.stop();const items=this.items();this.index=Math.max(0,Math.min(items.length-1,Number(i)||0));this.render()}
    play(){const items=this.items();if(!items.length)return;
      if(this.playing){this.stop();this.render();return}
      if(this.index>=items.length-1)this.index=0;
      this.playing=true;this.timer=setInterval(()=>{if(this.index>=this.items().length-1){this.stop()}else this.index++;this.render()},this.speed);this.render();
    }
    render(){
      const $=id=>document.getElementById(id),escape=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
      const runs=this.runs();if(!runs.some(r=>r.id===this.runId)){this.stop();this.runId=runs[0]?.id||null;this.index=0}
      const run=this.selected(),items=timeline(run);
      const signature=[this.runId,this.index,this.playing,runs.length,runs[0]?.id||'',this.selected()?.snapshots?.length||0].join('|');
      if(signature===this.lastSignature)return;
      this.lastSignature=signature;
      const runsScroll=$('replayRuns').scrollTop,timelineScroll=$('replayTimeline').scrollTop;
      $('replayRuns').innerHTML=runs.map(r=>`<button data-replay-run="${escape(r.id)}" class="replay-run ${r.id===this.runId?'selected':''}"><b>${escape(r.id)}</b><span>${escape(r.outcome)} · ${escape(r.flowVersion||'无版本')}</span></button>`).join('')||'<div class="empty-state">没有已完成周期。先执行一次模拟单周期。</div>';
      $('replayMeta').textContent=run?`结果 ${run.outcome} · 流程 ${run.flowVersion||'未知'} · 配方 ${run.recipeId||'未知'} · ${items.length} 条记录 · ${items.filter(x=>x.vars).length} 条变量证据`:'等待选择历史周期';
      const idx=Math.min(this.index,Math.max(0,items.length-1));this.index=idx;
      $('replayTimeline').innerHTML=items.map((x,i)=>`<button data-replay-index="${i}" class="replay-event ${i===idx?'selected':''}"><span class="replay-order">${String(i+1).padStart(2,'0')}</span><div><b>${escape(x.label)}</b><small>${escape(x.kind)} · ${escape(x.status)} · ${x.durationMs} ms</small></div></button>`).join('')||'<div class="empty-state">该周期没有可用步骤记录。</div>';
      $('replayRuns').scrollTop=runsScroll;$('replayTimeline').scrollTop=timelineScroll;
      const cur=items[idx];
      $('replayStepLabel').textContent=cur?cur.label:'—';
      $('replayStepStatus').textContent=cur?cur.status:'无记录';
      $('replayIndex').textContent=items.length?`${idx+1} / ${items.length}`:'0 / 0';
      $('replayRange').max=Math.max(items.length-1,0);$('replayRange').value=idx;$('replayRange').disabled=!items.length;
      for(const [id,condition] of [['replayPrev',idx<=0],['replayNext',idx>=items.length-1],['replayPlay',!items.length]])$(id).disabled=condition;
      $('replayPlay').textContent=this.playing?'Ⅱ 暂停回放':'▶ 自动回放';
      if(!cur){$('replayWatch').innerHTML='<div class="empty-state">尚无历史变量快照</div>';return}
      const data=cur.vars;
      $('replayWatch').innerHTML=data?
        `<div class="replay-watch-grid">${Object.entries(data).map(([k,v])=>`<div class="replay-watch-row"><b>${escape(k)}</b><code>${escape(typeof v==='object'?JSON.stringify(v):String(v))}</code></div>`).join('')}</div>`:
        '<div class="empty-state">此周期未记录变量快照，仅显示已存的步骤信息；不会借用当前仿真状态填充。</div>';
      $('replayError').textContent=cur.error?'异常：'+cur.error:cur.parentId?'分支节点 · '+cur.parentId:cur.fromRecordedTrace?'历史快照 · 只读回放':'仅有聚合步骤记录';
    }
  }
  global.WorkflowReplay={timeline,ReplayViewer};
})(window);
