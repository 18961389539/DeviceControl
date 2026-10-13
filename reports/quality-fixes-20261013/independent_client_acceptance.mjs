import fs from 'node:fs';
import http from 'node:http';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const checks=[];let writes=0;
const server=http.createServer((req,res)=>{
  if(req.method==='POST')writes++;
  if(req.url==='/invalid'){res.writeHead(200,{'Content-Type':'application/json'});res.end('broken-json');return}
  if(req.url==='/valid'){res.writeHead(200,{'Content-Type':'application/json'});res.end('{"ok":true}');return}
  res.writeHead(200,{'Content-Type':'application/json','Content-Length':'100'});res.flushHeaders();res.write('{');
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const window={};const ctx={window,fetch,Headers,AbortController,setTimeout,clearTimeout,TextDecoder,Uint8Array,Error};
vm.createContext(ctx);vm.runInContext(fs.readFileSync('frontend/api-client.js','utf8'),ctx);
const client=window.DeviceControlApiClient.create({root:'http://127.0.0.1:'+server.address().port,readTimeoutMs:150,writeTimeoutMs:200});
try{
  let error;let started=Date.now();
  try{await client('/partial')}catch(e){error=e.message}
  assert.match(error,/超时/);assert.ok(Date.now()-started<1500);
  checks.push({name:'N04 full response body deadline',passed:true,configuredTimeoutMs:150,elapsedMs:Date.now()-started,error});
  error=undefined;
  try{await client('/partial',{method:'POST',body:'{}'})}catch(e){error=e.message}
  assert.match(error,/结果未知/);assert.equal(writes,1);
  checks.push({name:'N04 write timeout stays unknown and is not resent',passed:true,writes,error});
  error=undefined;
  try{await client('/invalid')}catch(e){error=e.message}
  assert.match(error,/JSON/);
  checks.push({name:'N04 malformed complete JSON rejected',passed:true,error});
  const response=await client('/valid');assert.equal((await response.json()).ok,true);
  checks.push({name:'N04 complete response succeeds',passed:true});
}finally{server.closeAllConnections();server.close()}
fs.writeFileSync('reports/quality-fixes-20261013/independent-client-results.json',JSON.stringify(checks,null,2));
console.log('PASS independent root client acceptance:',checks.length,'checks');
