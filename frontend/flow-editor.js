/* V0.6 controlled graph editor. Drawn connectors are directed by top-level order;
 * branch children are restricted to a single level. Changes stay in a workspace
 * until explicitly saved as a new immutable draft. */
class FlowEditor {
  constructor(opts){
    this.opts=opts;this.data=null;this.sequence=[];this.selectedId=null;this.selectedVersion=null;
    this.dragged=null;this.busy=false;this.changed=false;this.clipboard=null;this.history=new window.WorkflowEditTools.EditHistory([]);
    this.stationId='A';this.stationCache={};this.readEpoch=0;
    // F14: warn before a refresh/close silently discards unsaved canvas changes.
    window.addEventListener('beforeunload',e=>{
      if(this.changed){e.preventDefault();e.returnValue='存在未保存的流程修改'}});
    this.$=id=>document.getElementById(id);
    const panel=this.$('panel-workflow');
    panel.addEventListener('click',e=>this.clicked(e));
    panel.addEventListener('change',e=>this.changedInput(e));
    panel.addEventListener('dragstart',e=>{const n=e.target.closest('[data-drag-id]');if(n&&this.canEdit()){this.dragged=n.dataset.dragId;e.dataTransfer.effectAllowed='move';e.dataTransfer.setData('text/plain',this.dragged)}});
    panel.addEventListener('dragover',e=>{if(e.target.closest('[data-drag-id]'))e.preventDefault()});
    panel.addEventListener('drop',e=>{const dst=e.target.closest('[data-drag-id]');if(!dst||!this.dragged)return;e.preventDefault();this.reorder(this.dragged,dst.dataset.dragId);this.dragged=null});
    document.addEventListener('keydown',e=>{
      if(!this.$('panel-workflow').classList.contains('active')||!this.canEdit())return;
      const tag=(e.target?.tagName||'').toLowerCase();
      if(['input','textarea','select'].includes(tag)||e.target?.isContentEditable)return;
      if((e.ctrlKey||e.metaKey)&&!e.altKey){
        const key=e.key.toLowerCase();
        if(key==='z'||key==='y'){e.preventDefault();this.navigateHistory(key==='y'||(key==='z'&&e.shiftKey)?'redo':'undo')}
        else if(key==='c'){if(this.copySelected())e.preventDefault()}
        else if(key==='v'){if(this.pasteSelected())e.preventDefault()}
      }
    });
  }
  switchStation(id){
    if(id===this.stationId)return;
    if(this.busy)throw Error('流程操作执行中，不可切换工站');
    this.readEpoch++;
    this.stationCache[this.stationId]={sequence:this.clone(this.sequence),selectedId:this.selectedId,
      selectedVersion:this.selectedVersion,changed:this.changed,clipboard:this.clipboard?this.clone(this.clipboard):null,
      history:this.history};
    this.stationId=id;
    const saved=this.stationCache[id];
    this.data=null;
    if(saved){this.sequence=this.clone(saved.sequence);this.selectedId=saved.selectedId;
      this.selectedVersion=saved.selectedVersion;this.changed=saved.changed;
      this.clipboard=saved.clipboard?this.clone(saved.clipboard):null;this.history=saved.history;}
    else{this.sequence=[];this.selectedId=null;this.selectedVersion=null;this.changed=false;
      this.clipboard=null;this.history=new window.WorkflowEditTools.EditHistory([]);}
    this.$('studioWorkspace').textContent='工站 '+id+' · 正在加载本工站流程';
  }
  clone(x){return window.FlowStudioModel.clone(x)}
  esc(v){return String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}
  canEdit(){return this.opts.role()>=2&&!this.busy}
  getNode(id){for(const n of this.sequence){if(n.id===id)return n;for(const c of n.children||[])if(c.id===id)return c}return null}
  resetWorkspace(seq){this.history.reset(seq);this.sequence=this.history.snapshot();this.changed=false;this.selectedId=null}
  mutate(fn){if(!this.canEdit())return false;const success=this.history.transact(fn);if(success){this.sequence=this.history.snapshot();this.changed=this.history.changed;this.render()}return success}
  navigateHistory(action){if(!this.canEdit())return;const changed=action==='undo'?this.history.undo():this.history.redo();if(changed){this.sequence=this.history.snapshot();this.changed=this.history.changed;if(!this.getNode(this.selectedId))this.selectedId=null;this.render()}}
  copySelected(){const n=this.sequence.find(n=>n.id===this.selectedId);if(!n||!this.canEdit())return false;this.clipboard=this.clone(n);this.render();return true}
  pasteSelected(){if(!this.clipboard||!this.canEdit())return false;
    let id;const changed=this.mutate(seq=>{const node=window.WorkflowEditTools.cloneFresh(this.clipboard,window.WorkflowEditTools.allIds(seq));id=node.id;const i=seq.findIndex(x=>x.id===this.selectedId);seq.splice(i>=0?i+1:seq.length,0,node)});
    if(changed){this.selectedId=id;this.render()}return changed;
  }
  async open(){
    const epoch=++this.readEpoch;
    try{const data=await this.opts.read();if(epoch!==this.readEpoch)return;
      this.data=data;
      if(!this.selectedVersion||!data.versions.some(v=>v.id===this.selectedVersion)){
        this.resetWorkspace(data.active.definition);this.selectedVersion=data.active.id;
      }
      this.render();
    }catch(e){if(epoch===this.readEpoch)this.opts.notice(e.message,true)}
  }
  async reload(select=null){this.data=await this.opts.read();if(select){this.selectedVersion=select;this.resetWorkspace(this.data.versions.find(v=>v.id===select)?.definition||this.data.active.definition)}this.render()}
  errors(){return window.FlowStudioModel.validateFlow(this.sequence,this.opts.cfg())}
  reorder(a,b){if(a===b||!this.canEdit())return;this.mutate(seq=>{const ai=seq.findIndex(n=>n.id===a),bi=seq.findIndex(n=>n.id===b);if(ai<0||bi<0)return;const [item]=seq.splice(ai,1);seq.splice(bi,0,item)})}
  branchMarkup(n){
    const titles=n.type==='Condition'?['条件为 TRUE','条件为 FALSE']:['分支 A','分支 B','分支 C','分支 D'];
    return `<div class="studio-branch-grid">${(n.children||[]).map((c,j)=>`<button class="studio-branch ${this.selectedId===c.id?'chosen':''}" data-editor-select="${this.esc(c.id)}"><span>${titles[j]}</span><strong>${this.esc(c.label)}</strong><small>${this.esc(c.type)} · ${this.esc(c.device||'')}</small></button>`).join('')}</div>`;
  }
  render(){
    if(!this.data)return;const active=this.data.active,rev=this.data.versions.find(v=>v.id===this.selectedVersion)||active;
    this.$('studioActiveVersion').textContent=active.id;
    this.$('studioWorkspace').textContent='工站 '+this.stationId+' · '+(this.changed?'● 未保存 · ':'')+'来源 '+rev.id+' · '+this.sequence.length+' 个顶层节点';
    this.$('studioVersionList').innerHTML=this.data.versions.map(v=>`<button class="studio-version ${this.selectedVersion===v.id?'selected':''}" data-editor-version="${this.esc(v.id)}"><b>${this.esc(v.id)}</b><span class="pill">${this.esc(v.status)}</span><small>${this.esc(v.author)} · ${this.esc(v.reason)}</small></button>`).join('');
    this.$('studioCanvas').innerHTML=this.sequence.map((n,i)=>`<div class="studio-node-wrap"><div class="studio-node ${this.selectedId===n.id?'chosen':''}" draggable="${this.canEdit()}" data-drag-id="${this.esc(n.id)}"><button data-editor-select="${this.esc(n.id)}" class="studio-node-body"><span class="studio-index">${String(i+1).padStart(2,'0')}</span><span class="studio-node-icon">${{Parallel:'⇉',Condition:'◇',AxisMove:'↔',VisionLocate:'◎',WaitInput:'◉',Delay:'◷',CylinderExtend:'⇥',CylinderRetract:'⇤'}[n.type]||'·'}</span><span><b>${this.esc(n.label)}</b><small>${this.esc(n.type)}${n.device?' · '+this.esc(n.device):''}</small></span></button><span class="studio-grip" title="拖动调整顺序">⠿</span></div>${n.children?.length?this.branchMarkup(n):''}</div>${i<this.sequence.length-1?'<div class="studio-link"><span>↓</span></div>':''}`).join('');
    this.$('studioUndo').disabled=!this.canEdit()||!this.history.canUndo;
    this.$('studioRedo').disabled=!this.canEdit()||!this.history.canRedo;
    this.$('studioCopy').disabled=!this.canEdit()||!this.sequence.some(n=>n.id===this.selectedId);
    this.$('studioPaste').disabled=!this.canEdit()||!this.clipboard;
    this.$('studioDuplicate').disabled=!this.canEdit()||!this.sequence.some(n=>n.id===this.selectedId);
    this.$('studioDiff').innerHTML=this.diffMarkup(active.definition,this.sequence);
    const errs=this.errors();this.$('studioValidation').innerHTML=errs.length?`<b class="studio-invalid">静态检查 · ${errs.length} 项问题</b>${errs.slice(0,7).map(e=>`<small class="studio-error">• ${this.esc(e)}</small>`).join('')}`:'<b class="studio-valid">✓ 静态检查通过</b><small>拓扑、机构引用、参数范围、重试策略均符合受约束流程规则</small>';
    const n=this.getNode(this.selectedId);
    this.$('studioInspector').innerHTML=n?this.inspector(n):'<div class="empty-state">点击画布中的节点，在这里配置动作参数。</div>';
    const admin=this.opts.role()>=3,eng=this.opts.role()>=2,id=rev.id,idle=this.opts.canPublish();
    for(const b of this.$('panel-workflow').querySelectorAll('[data-editor-action]')){
      const a=b.dataset.editorAction;
      if(['undo','redo','copy','paste','duplicate'].includes(a))continue;
      b.disabled=this.busy||(!eng)||((a==='publish'||a==='rollback')&&(!admin||!idle))||(a==='validate'&&rev.status!=='Draft')||(a==='publish'&&rev.status!=='Validated')||(a==='rollback'&&(!['Published','Superseded'].includes(rev.status)||rev.id===active.id))||(a==='save'&&(!this.changed&&rev.id!==active.id));
    }
    for(const b of this.$('panel-workflow').querySelectorAll('[data-editor-add],[data-editor-remove],[data-editor-input]'))b.disabled=this.busy||!eng;
    this.$('studioStateNote').textContent=idle?'空闲，可申请发布或回滚':'执行中或恢复未完成，禁止发布和回滚';
  }
  diffMarkup(base,modified){
    const delta=window.WorkflowEditTools.diffFlows(base,modified);
    const items=[...delta.added.map(x=>['added','新增',x.label]),...delta.removed.map(x=>['removed','删除',x.label]),
      ...delta.changed.map(x=>['modified','修改',x.label+' · '+x.fields.map(y=>y.field).join(' / ')]),
      ...delta.moved.map(x=>['moved','移动',x.label+' · '+x.from+' → '+x.to])];
    return `<div class="diff-summary">相对当前发布版 · 新增 ${delta.added.length} / 删除 ${delta.removed.length} / 属性变更 ${delta.changed.length} / 位置变化 ${delta.moved.length}</div>`+
      (items.length?items.slice(0,35).map(([kind,name,desc])=>`<div class="diff-entry ${kind}"><span>${name}</span><small>${this.esc(desc)}</small></div>`).join(''):'<div class="studio-valid">✓ 与当前发布版本无差异</div>');
  }
  inspector(n){
    const sel=(id,options,value)=>`<select class="select-field" data-editor-input="${id}">${options.map(x=>`<option value="${this.esc(x)}" ${x===value?'selected':''}>${this.esc(x)}</option>`).join('')}</select>`;
    const fld=(name,key,val,type='text')=>`<label class="field-label">${name}<input class="login-input" data-editor-input="${key}" type="${type}" value="${this.esc(val)}"></label>`;
    let html=`<div class="studio-inspector-title"><strong>${this.esc(n.id)}</strong><button class="btn btn-light" data-editor-remove="${this.esc(n.id)}">删除</button></div>${fld('节点名称','label',n.label)}<label class="field-label">节点类型${sel('type',['WaitInput','VisionLocate','AxisMove','CylinderExtend','CylinderRetract','Delay',...(this.sequence.some(x=>x.id===n.id)?['Parallel','Condition']:[])],n.type)}</label>`;
    if(['AxisMove'].includes(n.type))html+=`<label class="field-label">运动轴${sel('device',this.opts.cfg().axes.map(x=>x.id),n.device)}</label>${fld('目标位置 (mm)','positionMm',n.positionMm||0,'number')}`;
    if(['CylinderExtend','CylinderRetract'].includes(n.type))html+=`<label class="field-label">气缸${sel('device',this.opts.cfg().cylinders.map(x=>x.id),n.device)}</label>`;
    if(['WaitInput','Condition'].includes(n.type))html+=`<label class="field-label">输入信号${sel('device',this.opts.cfg().signals.filter(x=>x.direction==='DI').map(x=>x.name),n.device)}</label>`;
    if(n.type==='WaitInput')html+=fld('超时 (ms)','timeoutMs',n.timeoutMs||1500,'number');
    if(n.type==='Delay')html+=fld('延时 (ms)','durationMs',n.durationMs||0,'number');
    if(['WaitInput','VisionLocate','Delay'].includes(n.type))html+=`<label class="field-label">错误策略${sel('onError',['Abort','Retry','WaitForOperator'],n.onError||'Abort')}</label>`;
    if(n.onError==='Retry'&&['WaitInput','VisionLocate','Delay'].includes(n.type))html+=fld('最多重试次数','maxRetries',n.maxRetries||1,'number');
    if(!['Parallel','Condition'].includes(n.type)){
      const keys=(n.sharedResources||[]).join(', ');
      html+=fld('共享资源（逗号分隔）','sharedResources',keys);
      html+=fld('共享资源等待超时 (ms)','resourceWaitTimeoutMs',n.resourceWaitTimeoutMs??2500,'number');
      if(n.type==='VisionLocate')html+='<div class="control-hint">视觉定位节点始终自动申请 CELL-SHARED，兼容旧版流程。</div>';
    }
    if(n.type==='Parallel'&&n.children.length<4)html+=`<button class="btn btn-light" data-editor-add-child="${this.esc(n.id)}">+ 添加并行分支</button>`;
    html+='<div class="control-hint">流程中的机构动作仍由仿真 Runtime 及其普通互锁执行。本界面不能绕过资源锁。</div>';
    return html;
  }
  async clicked(e){
    const el=e.target.closest('button');if(!el||el.disabled)return;
    if(el.dataset.editorSelect){this.selectedId=el.dataset.editorSelect;this.render();return}
    if(el.dataset.editorVersion){const v=this.data.versions.find(x=>x.id===el.dataset.editorVersion);if(!v)return;if(this.changed&&!confirm('切换版本将丢弃未保存的画布修改，继续？'))return;this.selectedVersion=v.id;this.resetWorkspace(v.definition);this.render();return}
    if(el.dataset.editorAdd){this.mutate(seq=>seq.push(window.FlowStudioModel.newNode(el.dataset.editorAdd,this.opts.cfg())));return}
    if(el.dataset.editorAddChild){this.mutate(seq=>{const p=seq.find(x=>x.id===el.dataset.editorAddChild);if(p&&p.type==='Parallel'&&p.children.length<4)p.children.push(window.FlowStudioModel.newNode('Delay',this.opts.cfg()))});return}
    if(el.dataset.editorRemove){const id=el.dataset.editorRemove;this.mutate(seq=>{const i=seq.findIndex(x=>x.id===id);if(i>=0)seq.splice(i,1);else for(const n of seq)if(n.children)n.children=n.children.filter(x=>x.id!==id)});this.selectedId=null;this.render();return}
    const a=el.dataset.editorAction;if(!a)return;
    if(a==='undo'||a==='redo'){this.navigateHistory(a);return}
    if(a==='copy'){this.copySelected();return}
    if(a==='paste'){this.pasteSelected();return}
    if(a==='duplicate'){if(this.copySelected())this.pasteSelected();return}

    try{
      this.busy=true;this.render();let result=null;
      if(a==='save'){const errors=this.errors();if(errors.length)throw Error('保存前检查失败：'+errors[0]);result=await this.opts.send('/api/flows/draft',{definition:this.clone(this.sequence),expectedActiveVersionId:this.data.active.id});}
      else if(a==='validate')result=await this.opts.send('/api/flows/'+encodeURIComponent(this.selectedVersion)+'/validate');
      else if(a==='publish'){
        if(!confirm('确认发布 '+this.selectedVersion+' 吗？将只影响之后启动的仿真周期。'))return;
        result=await this.opts.send('/api/flows/'+encodeURIComponent(this.selectedVersion)+'/publish');
      }else if(a==='rollback'){
        if(!confirm('将创建一个新的发布版本，内容复制自 '+this.selectedVersion+'，确认？'))return;
        result=await this.opts.send('/api/flows/rollback',{id:this.selectedVersion});
      }else if(a==='reset') {
        // F14: never discard unsaved canvas changes without an explicit confirm.
        if(this.changed&&!confirm('重置将丢弃未保存的画布修改，继续？'))return;
        this.resetWorkspace(this.data.active.definition);this.selectedVersion=this.data.active.id;this.opts.notice('已还原工作区');return}
      const v=result?.data||result;
      await this.reload(v?.id||this.selectedVersion);
      await this.opts.refreshed();
      this.opts.notice('操作成功：'+a);
    }catch(err){this.opts.notice(err.message,true)}finally{this.busy=false;this.render()}
  }
  changedInput(e){
    const el=e.target;if(!el.dataset.editorInput)return;
    if(!this.canEdit())return;
    if(!this.getNode(this.selectedId))return;
    const field=el.dataset.editorInput;
    this.mutate(seq=>{
      let n=null;for(const top of seq){if(top.id===this.selectedId)n=top;for(const child of top.children||[])if(child.id===this.selectedId)n=child}
      if(!n)return;
      if(field==='type'){
        const replacement=window.FlowStudioModel.newNode(el.value,this.opts.cfg(),n.id);replacement.label=n.label;
        Object.keys(n).forEach(k=>delete n[k]);Object.assign(n,replacement);
      }else if(field==='sharedResources')n.sharedResources=el.value.split(',').map(x=>x.trim().toUpperCase()).filter(Boolean);
      else n[field]=['positionMm','durationMs','timeoutMs','maxRetries','resourceWaitTimeoutMs'].includes(field)?Number(el.value):el.value;
      if(field==='onError'&&n.onError==='Retry')n.maxRetries=1;
    });
  }
}
window.FlowEditor=FlowEditor;
