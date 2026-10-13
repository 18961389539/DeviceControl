/* One request deadline covers headers, the complete bounded body, and JSON parsing.

   R03: a write whose outcome cannot be confirmed (connection drop, truncated or
   invalid JSON body, oversized response, timeout, 5xx) is surfaced as "result
   unknown" and always carries the locally generated X-Command-Id (plus the server
   trace ID when visible), so the operator reconciles by concrete ID instead of
   guessing or blindly retrying. Business rejections (4xx) remain ordinary errors
   and carry the same correlation IDs. Writes are never retried automatically.

   R07: freshness is tracked per data domain (for example per-station snapshot and
   flow). Only a successful read whose payload passes the caller's contract check
   may mark a domain fresh; every failure (timeout, 503, invalid structure) keeps
   or raises the stale flag, so a 503 JSON can no longer clear it. */
(function(global){'use strict';
 const READ_TIMEOUT_MS=8000,WRITE_TIMEOUT_MS=15000,MAX_RESPONSE_BYTES=8*1024*1024;
 function newCommandId(){
  return global.crypto?.randomUUID?global.crypto.randomUUID():('cmd-'+Date.now().toString(36)+'-'+Math.random().toString(36).slice(2));
 }
 function attach(error,extra){try{Object.assign(error,extra)}catch{}return error}
 function describe(error){
  if(!error)return '命令失败';
  if(error.resultUnknown){
   const ids=[error.commandId?('命令 '+error.commandId):null,error.traceId?('追踪 '+error.traceId):null].filter(Boolean).join(' · ');
   return (error.message||'写操作结果未知：服务端可能已执行')+(ids?'（'+ids+'）':'')+'。请按上述 ID 核对运行记录与审计，不要自动重发';
  }
  return error.message||'命令失败';
 }
 function create({root='',readTimeoutMs=READ_TIMEOUT_MS,writeTimeoutMs=WRITE_TIMEOUT_MS,maxResponseBytes=MAX_RESPONSE_BYTES}={}){
  const maxBytes=maxResponseBytes;
  const domains=new Map();
  const markFresh=key=>{if(key)domains.set(key,true)};
  const markStale=key=>{if(key)domains.set(key,false)};
  async function request(path,options={}){
   const method=(options.method||'GET').toUpperCase(),write=method!=='GET';
   const controller=new AbortController(),timeout=write?writeTimeoutMs:readTimeoutMs;
   const timer=setTimeout(()=>controller.abort(),timeout);
   const commandId=write?newCommandId():null;
   const headers=new Headers(options.headers||{});
   if(write&&!headers.has('X-Command-Id'))headers.set('X-Command-Id',commandId);
   const context={action:path,station:options.station||null,at:new Date().toISOString(),commandId};
   // First writer wins: a server-echoed command/trace ID set by an earlier pass
   // must never be overwritten by the locally generated fallbacks.
   const fail=(error,extra)=>{
    const source={...context,...(extra||{})};
    for(const key in source)if(error[key]===undefined)error[key]=source[key];
    return error;
   };
   try{
    const response=await fetch(root+path,{...options,headers,signal:controller.signal});
    const declared=Number(response.headers.get('content-length')||0);
    if(declared>maxBytes)
     throw fail(Error(write?'服务器响应过大，已拒绝读取；写操作结果未知，服务端可能已执行':'服务器响应过大，已拒绝读取'),{resultUnknown:write,responseStatus:response.status});
    const reader=response.body?.getReader();let chunks=[],length=0;
    if(reader){
     while(true){const {done,value}=await reader.read();if(done)break;length+=value.byteLength;
      if(length>maxBytes){await reader.cancel();
       throw fail(Error(write?'服务器响应超过 8 MiB，已拒绝读取；写操作结果未知，服务端可能已执行':'服务器响应超过 8 MiB，已拒绝读取'),{resultUnknown:write,responseStatus:response.status})}
      chunks.push(value);
     }
    }
    const bytes=new Uint8Array(length);let offset=0;for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.byteLength}
    const text=new TextDecoder().decode(bytes);let value=null;
    try{value=text?JSON.parse(text):null}
    catch(parseError){
     // R03: a broken body cannot prove the write was rejected - it may be committed.
     throw fail(Error(write?'写操作结果未知：响应不是有效 JSON，服务端可能已执行':('服务器返回了无效 JSON：'+parseError.message)),
       {resultUnknown:write,responseStatus:response.status,parseError:parseError.message});
    }
    const traceId=response.headers.get('X-Trace-Id')||(value&&typeof value==='object'?value.traceId:null)||null;
    const echoedId=response.headers.get('X-Command-Id')||(value&&typeof value==='object'?value.commandId:null)||commandId;
    if(!response.ok){
     const serverMessage=value&&typeof value==='object'?(value.error||value.message):null;
     throw fail(Error(serverMessage||('请求失败 '+response.status)),
      {status:response.status,responseStatus:response.status,
       resultUnknown:write&&response.status>=500,commandId:echoedId,traceId});
    }
    if(!write&&options.freshness){
     if(!options.expect||options.expect(value))markFresh(options.freshness);
     else markStale(options.freshness);
    }
    return {ok:true,status:response.status,statusText:response.statusText,headers:response.headers,
      json:async()=>value,text:async()=>text,data:value,commandId:echoedId,traceId};
   }catch(error){
    if(error?.name==='AbortError'){
     if(write)throw fail(Error('写操作超时：结果未知，服务端可能已执行'),{resultUnknown:true,timeout:true,commandId});
     if(options.freshness)markStale(options.freshness);
     throw fail(Error('数据获取超时，界面显示的可能不是最新状态'),{timeout:true});
    }
    if(write&&error?.resultUnknown===undefined){
     // No confirmable server response at all: the command may already be committed.
     attach(error,{resultUnknown:true,network:true,commandId});
     if(!error.message||/fetch|network/i.test(error.message))
      error.message='写操作结果未知：与本地服务的连接中断，命令可能已被执行';
     throw fail(error);
    }
    if(!write&&options.freshness)markStale(options.freshness);
    throw fail(error);
   }finally{clearTimeout(timer)}
  }
  request.isDataStale=()=>[...domains.values()].some(fresh=>!fresh);
  request.staleDomains=()=>[...domains].filter(pair=>!pair[1]).map(pair=>pair[0]);
  return request;
 }
 global.DeviceControlApiClient={create,defaults:{readTimeoutMs:READ_TIMEOUT_MS,writeTimeoutMs:WRITE_TIMEOUT_MS,maxResponseBytes:MAX_RESPONSE_BYTES},describe};
})(window);
