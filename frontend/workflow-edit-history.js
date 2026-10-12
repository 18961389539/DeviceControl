/* V0.7 browser-only workflow editing primitives. Nothing here executes machine commands.
 * Undo history is bounded, local to the workspace, and reset when switching revisions. */
(function(global){
  const clone=x=>JSON.parse(JSON.stringify(x));
  const fingerprint=x=>JSON.stringify(x);
  class EditHistory {
    constructor(initial, limit=80){this.limit=limit;this.reset(initial)}
    reset(initial){this.baseline=clone(initial);this.present=clone(initial);this.past=[];this.future=[]}
    transact(fn){
      const next=clone(this.present);fn(next);
      if(fingerprint(next)===fingerprint(this.present))return false;
      this.past.push(clone(this.present));if(this.past.length>this.limit)this.past.shift();
      this.present=next;this.future=[];return true;
    }
    undo(){if(!this.past.length)return false;this.future.push(clone(this.present));this.present=this.past.pop();return true}
    redo(){if(!this.future.length)return false;this.past.push(clone(this.present));this.present=this.future.pop();return true}
    get changed(){return fingerprint(this.present)!==fingerprint(this.baseline)}
    get canUndo(){return this.past.length>0}
    get canRedo(){return this.future.length>0}
    snapshot(){return clone(this.present)}
  }
  function allIds(nodes){const ids=new Set();const walk=ns=>(ns||[]).forEach(n=>{ids.add(n.id);walk(n.children)});walk(nodes);return ids}
  function cloneFresh(node,existing){
    const used=new Set(existing),copy=clone(node);
    function visit(n){
      const base=(n.id||'node').replace(/[^a-zA-Z0-9_-]/g,'_').slice(0,38)||'node';
      let i=1,id=base+'_copy';while(used.has(id)){id=base+'_copy'+(++i)}
      n.id=id;used.add(id);(n.children||[]).forEach(visit);
    }
    visit(copy);return copy;
  }
  function flatten(nodes){const rows=new Map();function walk(list,parent){(list||[]).forEach((n,index)=>{rows.set(n.id,{node:n,parent,index});walk(n.children,n.id)})}walk(nodes,null);return rows}
  function diffFlows(a,b){
    const left=flatten(a),right=flatten(b),added=[],removed=[],changed=[],moved=[];
    const label=n=>n.label||n.id;
    for(const [id,v] of right){
      const old=left.get(id);
      if(!old){added.push({id,label:label(v.node)});continue}
      const fields=new Set([...Object.keys(old.node),...Object.keys(v.node)].filter(k=>k!=='children'));
      const updates=[...fields].filter(k=>fingerprint(old.node[k])!==fingerprint(v.node[k])).map(k=>({field:k,before:old.node[k],after:v.node[k]}));
      if(updates.length)changed.push({id,label:label(v.node),fields:updates});
      if(v.index!==old.index||v.parent!==old.parent)moved.push({id,label:label(v.node),from:(old.parent||'TOP')+':'+old.index,to:(v.parent||'TOP')+':'+v.index});
    }
    for(const [id,v] of left)if(!right.has(id))removed.push({id,label:label(v.node)});
    return {added,removed,changed,moved,total:added.length+removed.length+changed.length+moved.length};
  }
  global.WorkflowEditTools={EditHistory,allIds,cloneFresh,diffFlows,clone};
})(window);
