/* Browser-only governance for V0.4 offline demo. Not an authentication/security implementation. */
class GovernanceMock {
  constructor(machine) {
    this.machine=machine;this.audit=[];this.versions=[];this.flows=new window.FlowStudioModel.WorkflowRevisions(machine);
    const initial={name:'默认工艺',visionOffsetMm:0,extraDwellMs:0};
    this.versions.push(this.createVersion(initial,'system','Published',null,'初始化基准版本'));
    this.activeId=this.versions[0].id;
  }
  createVersion(values,author,status,sourceId,reason){
    return {id:'v'+(this.versions.length+1).toString().padStart(3,'0'),sourceId,status,
      createdAt:new Date().toISOString(),author,values:{...values},hash:this.hash(JSON.stringify(values)),reason};
  }
  hash(s){let n=2166136261;for(let i=0;i<s.length;i++){n^=s.charCodeAt(i);n=Math.imul(n,16777619)}return(n>>>0).toString(16).padStart(8,'0')}
  login(username,password){
    const roles={operator:'Operator',engineer:'Engineer',admin:'Admin'};
    if(!Object.hasOwn(roles,username)||password!=='demo123')throw Error('演示账号或密码错误');
    this.record({name:username,role:roles[username]},'/api/auth/login',200);
    return {name:username,role:roles[username]};
  }
  rank(role){return ({Operator:1,Engineer:2,Admin:3})[role]||0}
  required(path,body={}){
    if(path==='/api/cycle/start'||path==='/api/cycle/stop'||path==='/api/auth/logout')return 1;
    if(path==='/api/mode')return body.mode==='Manual'?2:1;
    if(path==='/api/flow/debug')return 2;
    if(path==='/api/flows/draft'||path.match(/^\/api\/flows\/[^/]+\/validate$/))return 2;
    if(path.startsWith('/api/flows/'))return 3;
    if(path.startsWith('/api/manual/')||path.startsWith('/api/sim/')||path.startsWith('/api/fault/')||path.startsWith('/api/recovery/'))return 2;
    if(path==='/api/recipes/draft'||path.endsWith('/validate'))return 2;
    if(path.endsWith('/publish')||path==='/api/recipes/rollback')return 3;
    return 3;
  }
  record(user,path,status){
    this.audit.unshift({id:Math.random().toString(36).slice(2,14),time:new Date().toISOString(),
      actor:user?.name||'anonymous',role:user?.role||'None',method:'POST',path,status,
      traceId:Math.random().toString(36).slice(2),outcome:status<400?'Accepted':'Rejected'});
    this.audit=this.audit.slice(0,400);
  }
  checkPublish(){const s=this.machine.snapshot();if(s.state!=='Idle'||s.busy)throw Error('设备正在执行，禁止热切换配方');if(s.recoveryRequired)throw Error('恢复未完成，禁止发布配方')}
  check(values){if(!values.name?.trim()||values.name.length>50||!Number.isFinite(values.visionOffsetMm)||Math.abs(values.visionOffsetMm)>10||!Number.isInteger(values.extraDwellMs)||values.extraDwellMs<0||values.extraDwellMs>1000)throw Error('配方校验失败：名称 1~50 字，视觉偏移 -10~10 mm，附加延时 0~1000 ms')}
  active(){return this.versions.find(v=>v.id===this.activeId)}
  list(){return {active:structuredClone(this.active()),versions:structuredClone([...this.versions].reverse())}}
  auditPage(page=1,pageSize=40){return {total:this.audit.length,page,pageSize,items:structuredClone(this.audit.slice((page-1)*pageSize,page*pageSize))}}
  checklist(){const s=this.machine.snapshot();return {required:s.recoveryRequired,alarm:s.activeAlarm,checks:[
    {key:'idle',label:'执行服务空闲',ok:!s.busy&&s.state==='Idle'},
    {key:'manual',label:'已切换手动模式',ok:s.mode==='Manual'},
    {key:'input',label:'普通仿真工艺条件正常',ok:s.inputs.GuardClosed&&s.inputs.AirPressureOk},
    ...Object.entries(s.axes).map(([id,a])=>({key:'axis-'+id,label:id+' 轴使能且回零位',ok:a.enabled&&a.homed&&Math.abs(a.positionMm-(this.machine.cfg.axes.find(x=>x.id===id).homeMm))<=0.01})),
    ...Object.entries(s.cylinders).map(([id,c])=>({key:'cylinder-'+id,label:id+' 气缸已缩回',ok:c.position==='Retracted'}))]};
  }
  async post(path,body,user){
    let status=200;
    try {
      if(!user){status=401;throw Error('请登录演示账号')}
      if(this.rank(user.role)<this.required(path,body)){status=403;throw Error('当前角色无权执行此操作')}
      if(path.startsWith('/api/flows/'))return {ok:true,data:this.flows.handle(path,body,user)};
      if(path==='/api/recipes/draft'){
        const version=this.createVersion({name:body.name,visionOffsetMm:body.visionOffsetMm,extraDwellMs:body.extraDwellMs},user.name,'Draft',this.activeId,'待验证');this.versions.push(version);return {ok:true,data:version};
      }
      if(path==='/api/recipes/rollback'){
        this.checkPublish();const source=this.versions.find(v=>v.id===body.id);if(!source||!['Published','Superseded'].includes(source.status))throw Error('只能回滚到曾经发布过的版本');
        this.active().status='Superseded';const version=this.createVersion(source.values,user.name,'Published',source.id,'回滚发布自 '+source.id);this.versions.push(version);this.activeId=version.id;return {ok:true,data:version};
      }
      const match=path.match(/^\/api\/recipes\/([^/]+)\/(validate|publish)$/);
      if(match){
        const version=this.versions.find(v=>v.id===match[1]);if(!version)throw Error('找不到配方版本');
        if(match[2]==='validate'){
          if(version.status!=='Draft')throw Error('仅草稿允许验证');this.check(version.values);version.status='Validated';version.reason='静态校验通过';return {ok:true,data:version};
        }
        this.checkPublish();if(version.status!=='Validated')throw Error('只允许发布已验证版本');this.check(version.values);
        this.active().status='Superseded';version.status='Published';version.reason='经管理员发布';this.activeId=version.id;return {ok:true,data:version};
      }
      if(path==='/api/cycle/start'){this.machine.cycleRecipe=structuredClone(this.active().values);this.machine.cycleRecipeId=this.activeId;}
      await this.machine.post(path,body);return {ok:true};
    }catch(e){status=status===200?409:status;throw e}
    finally{this.record(user,path,status)}
  }
}
window.GovernanceMock=GovernanceMock;
