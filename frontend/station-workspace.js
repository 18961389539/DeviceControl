/* V1.3 workstation-scoped API routing. Pure helper, usable in Node tests. */
(function(global){
  const stationIds=['A','B'];
  function register(id){if(typeof id!=='string'||!/[A-Za-z]/.test(id[0]||'')||!/^[A-Za-z][A-Za-z0-9_-]{0,15}$/.test(id))throw Error('Invalid station ID');id=id.toUpperCase();if(!stationIds.includes(id))stationIds.push(id);return id}
  function ensureStation(id){if(!stationIds.includes(id))throw Error('Unknown station workspace: '+id);return id}
  function route(id,path){
    ensureStation(id);
    if(typeof path!=='string'||!path.startsWith('/api/')||path.startsWith('/api/stations/')||path.includes('..'))
      throw Error('Not a station-scoped API path');
    if(path==='/api/audit'||path.startsWith('/api/audit?'))return '/api/stations/'+id+'/audit'+path.slice('/api/audit'.length);
    return '/api/stations/'+id+path.slice('/api'.length);
  }
  class StationWorkspace{
    constructor(initial='A'){this.id=ensureStation(initial);this.revision=0}
    select(id){ensureStation(id);if(this.id!==id){this.id=id;this.revision++}return this.revision}
    context(){return {station:this.id,revision:this.revision}}
    isCurrent(value){return value.station===this.id&&value.revision===this.revision}
    route(path){return route(this.id,path)}
  }
  global.StationWorkspace={StationWorkspace,route,ensureStation,stationIds,register};
})(window);
