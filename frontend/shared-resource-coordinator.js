/* V1.2 purely simulated in-process atomic FIFO resource scheduler.
 * Same logical contract as the C# SharedResourceCoordinator, not a safety lock. */
(function(global){
  class SharedResourceCoordinator {
    constructor(){this.held=new Map();this.queue=[];this.events=[];this.nextTicket=0;}
    static keysFor(node){return SharedResourceCoordinator.keys([...(node.sharedResources||[]),...(node.type==='VisionLocate'?['CELL-SHARED']:[])]);}
    static keys(items){
      const keys=[...new Set(items.map(x=>String(x).trim().toUpperCase()))].sort();
      if(keys.length>8||keys.some(x=>!/^[A-Z][A-Z0-9_-]{0,31}$/.test(x)))throw Error('共享资源名称无效');
      return keys;
    }
    event(action,req){this.events.unshift({at:new Date().toISOString(),action,stationId:req.stationId,nodeId:req.nodeId,ticket:req.ticket,resources:req.resources});this.events.length=Math.min(60,this.events.length)}
    drain(){
      while(this.queue.length){
        const q=this.queue[0];if(q.resources.some(key=>this.held.has(key)))break;
        this.queue.shift();q.granted=true;
        for(const key of q.resources)this.held.set(key,q);
        this.event('granted',q);q.finish(null,()=>this.release(q));
      }
    }
    release(req){if(!req.granted)return;req.granted=false;
      for(const key of req.resources)if(this.held.get(key)===req)this.held.delete(key);
      this.event('released',req);this.drain();
    }
    acquire(stationId,nodeId,keys,timeoutMs=2500,canceled=()=>false){
      const resources=SharedResourceCoordinator.keys(keys);
      if(!resources.length)throw Error('空资源请求');
      if(!Number.isInteger(timeoutMs)||timeoutMs<100||timeoutMs>30000)throw Error('资源等待超时参数不合法');
      if(canceled())return Promise.reject(Error('STOP_REQUESTED'));
      return new Promise((resolve,reject)=>{
        const req={ticket:String(++this.nextTicket),stationId,nodeId,resources,granted:false};
        let done=false,timeout,poll;
        const cleanup=()=>{clearTimeout(timeout);clearInterval(poll)};
        req.finish=(error,release)=>{
          if(done){if(release)release();return;}
          done=true;cleanup();error?reject(error):resolve(release);
        };
        const withdraw=error=>{
          if(done)return;
          this.queue=this.queue.filter(item=>item!==req);
          if(req.granted)this.release(req);
          this.event(error.message==='STOP_REQUESTED'?'canceled':'timed-out',req);
          req.finish(error);this.drain();
        };
        this.queue.push(req);this.event('queued',req);
        timeout=setTimeout(()=>withdraw(Error('资源等待超时：'+resources.join(','))),timeoutMs);
        poll=setInterval(()=>{if(canceled())withdraw(Error('STOP_REQUESTED'))},15);
        this.drain();
      });
    }
    snapshot(){return {policy:'AtomicFIFO',mode:'Simulation',safetyRated:false,
      leases:[...this.held].sort(([a],[b])=>a.localeCompare(b)).map(([resource,r])=>({resource,stationId:r.stationId,nodeId:r.nodeId,ticket:r.ticket})),
      queue:this.queue.map((r,i)=>({position:i+1,stationId:r.stationId,nodeId:r.nodeId,ticket:r.ticket,resources:[...r.resources]})),
      events:[...this.events]};}
  }
  global.SharedResourceCoordinator=SharedResourceCoordinator;
})(window);
