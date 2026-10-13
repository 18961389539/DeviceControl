import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';

globalThis.window={};
vm.runInThisContext(await readFile(new URL('../frontend/api-client.js',import.meta.url),'utf8'));
let writes=0,writeCommandId='';
const server=createServer((req,res)=>{
 if(req.url==='/partial'){res.writeHead(200,{'content-type':'application/json'});res.write('{');return}
 if(req.url==='/bad'){res.writeHead(200,{'content-type':'application/json'});res.end('{');return}
 if(req.url==='/write'){writes++;writeCommandId=req.headers['x-command-id']||'';res.writeHead(200,{'content-type':'application/json'});res.write('{');return}
 res.writeHead(200,{'content-type':'application/json'});res.end(JSON.stringify({ok:true}));
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
try{
 const root=`http://127.0.0.1:${server.address().port}`;
 const api=window.DeviceControlApiClient.create({root,readTimeoutMs:120,writeTimeoutMs:120});
 const started=Date.now();await assert.rejects(api('/partial'),/超时/);assert.ok(Date.now()-started<1500,'partial body must respect the deadline');
 await assert.rejects(api('/bad'),/无效 JSON/);
 const success=await api('/ok');assert.deepEqual(await success.json(),{ok:true});
 await assert.rejects(api('/write',{method:'POST',body:'{}'}),/结果未知/);assert.equal(writes,1,'a timed-out write must never be retried');assert.ok(writeCommandId,'writes carry a command correlation ID');
 const index=await readFile(new URL('../frontend/index.html',import.meta.url),'utf8');
 assert.ok(index.indexOf('api-client.js')<index.indexOf('app.js'),'client must load before the app');
 const preview=await readFile(new URL('../tests/fixtures/DeviceControl_V1.9_Preview.html',import.meta.url),'utf8');
 assert.ok(preview.includes('global.DeviceControlApiClient={create,defaults:'),'offline preview builder must inline the shared client');
 for(const file of ['station-console.js','station-template-studio.js','station-upgrade-studio.js','fleet-rollout-studio.js','deployment-sandbox-ui.js']){
  const source=await readFile(new URL('../frontend/'+file,import.meta.url),'utf8');
  assert.ok(source.includes('DeviceControlApiClient.create'),`${file} must use the shared deadline client`);
 }
 console.log('OK   N04 shared API client deadline, response parsing, write unknown outcome, studios and preview bundle');
}finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
