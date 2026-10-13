/* R03/R07 client contract: unconfirmable writes become "result unknown" with a
   concrete command ID; fresh/stale flags follow the per-domain contract rule.
   Controlled HTTP server only - never touches a real device or production data. */
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';

globalThis.window={};
vm.runInThisContext(await readFile(new URL('../frontend/api-client.js',import.meta.url),'utf8'));
let writes=0,dropCommandId='';
const server=createServer((req,res)=>{
 const send=(code,body,headers={})=>{res.writeHead(code,{'content-type':'application/json',...headers});res.end(body)};
 if(req.url==='/drop'){writes++;dropCommandId=req.headers['x-command-id']||'';req.socket.destroy();return}
 if(req.url==='/badjson'){send(200,'{"broken"');return}
 if(req.url==='/oversize'){send(200,JSON.stringify({pad:'x'.repeat(512)}));return}
 if(req.url==='/timeout'){return} /* never respond */
 if(req.url==='/server-500'){send(500,JSON.stringify({ok:false,error:'请求处理失败；请使用命令和追踪 ID 核对审计记录',commandId:'server-cmd-1',traceId:'trace-1'}));return}
 if(req.url==='/server-503'){send(503,JSON.stringify({ready:false}));return}
 if(req.url==='/server-409'){send(409,JSON.stringify({ok:false,error:'版本冲突：请刷新后重试'}));return}
 if(req.url==='/snap'){send(200,JSON.stringify({state:'Idle',axes:{X:{}},inputs:{PartPresent:true}}));return}
 if(req.url==='/wrong-contract'){send(200,JSON.stringify({unexpected:true}));return}
 send(200,JSON.stringify({ok:true}));
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
try{
 const root=`http://127.0.0.1:${server.address().port}`;
 const api=window.DeviceControlApiClient.create({root,readTimeoutMs:150,writeTimeoutMs:150,maxResponseBytes:256});
 const failed=promise=>promise.then(()=>({}),e=>e);

 // --- R03: write with the connection destroyed after the request was sent. ---
 const drop=await failed(api('/drop',{method:'POST',body:'{}'}));
 assert.equal(drop.resultUnknown,true,'dropped write must be result unknown');
 assert.ok(drop.commandId&&drop.commandId===dropCommandId,'error must carry the sent command ID');
 assert.match(window.DeviceControlApiClient.describe(drop),/命令 /);
 assert.match(window.DeviceControlApiClient.describe(drop),/不要自动重发/);
 assert.equal(writes,1,'a dropped write must never be retried');
 // --- R03: 200 with a broken body cannot prove the write was rejected. ---
 const bad=await failed(api('/badjson',{method:'POST',body:'{}'}));
 assert.equal(bad.resultUnknown,true,'broken write response must be result unknown');
 assert.ok(bad.commandId,'bad-JSON write must carry command ID');
 // --- R03: oversized write response. ---
 const big=await failed(api('/oversize',{method:'POST',body:'{}'}));
 assert.equal(big.resultUnknown,true,'oversized write response must be result unknown');
 // --- R03: timeout write (slow body) keeps the unknown classification. ---
 const slow=await failed(api('/timeout',{method:'POST',body:'{}'}));
 assert.equal(slow.resultUnknown,true);assert.equal(slow.timeout,true);assert.ok(slow.commandId);
 // --- R03: 5xx is unconfirmable and carries the server body's IDs. ---
 const five=await failed(api('/server-500',{method:'POST',body:'{}'}));
 assert.equal(five.status,500);assert.equal(five.resultUnknown,true);
 assert.equal(five.commandId,'server-cmd-1','server echoed command ID must win');
 assert.equal(five.traceId,'trace-1');
 // --- R03: 4xx business rejection stays an ordinary error with the same IDs. ---
 const conflict=await failed(api('/server-409',{method:'POST',body:'{}'}));
 assert.equal(conflict.status,409);assert.equal(conflict.resultUnknown,false);
 assert.match(conflict.message,/版本冲突/);assert.ok(conflict.commandId);
 assert.ok(!/不要自动重发/.test(window.DeviceControlApiClient.describe(conflict)),'business rejection must not read as unknown');
 // --- R07: freshness follows the per-domain contract rule. ---
 const snapOptions={freshness:'snap:A',expect:v=>!!v&&v.state==='Idle'&&!!v.axes&&!!v.inputs};
 await api('/snap',snapOptions);
 assert.equal(api.isDataStale(),false,'valid read marks the domain fresh');
 await failed(api('/timeout',snapOptions));
 assert.equal(api.isDataStale(),true,'timeout marks the domain stale');
 const notFound=await failed(api('/server-503',snapOptions));
 assert.equal(notFound.status,503);
 assert.equal(api.isDataStale(),true,'a 503 JSON must NOT clear the stale flag');
 await api('/snap',snapOptions);
 assert.equal(api.isDataStale(),false,'a valid read restores freshness');
 await api('/wrong-contract',snapOptions);
 assert.equal(api.isDataStale(),true,'a contract-invalid 200 must keep the domain stale');
 await api('/snap',{freshness:'flow:B',expect:v=>!!v&&v.state==='Idle'});
 assert.equal(api.isDataStale(),true,'other domains cannot mask a stale one');
 await api('/snap',snapOptions);
 assert.equal(api.isDataStale(),false,'all tracked domains fresh again');
 console.log('OK   R03/R07 shared client: unknown writes carry IDs, freshness obeys the contract rule');
}finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
