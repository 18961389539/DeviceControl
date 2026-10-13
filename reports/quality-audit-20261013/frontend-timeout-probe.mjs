import fs from 'node:fs';
import http from 'node:http';
import vm from 'node:vm';
// Extract the unmodified product helper; stalled response body arrives after headers.
const source=fs.readFileSync('frontend/app.js','utf8');
const helper=source.slice(source.indexOf('  const READ_TIMEOUT_MS'),source.indexOf('  const machineA'));
const server=http.createServer((req,res)=>{
  res.writeHead(200,{'Content-Type':'application/json','Content-Length':'100'});
  res.flushHeaders();res.write('{');
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const root='http://127.0.0.1:'+server.address().port;
const ctx={root,fetch,AbortController,setTimeout,clearTimeout,Error};
vm.createContext(ctx);vm.runInContext(helper+';globalThis.auditFetch=apiFetch;',ctx);
let settled=false;
const response=await ctx.auditFetch('/api/snapshot');
const body=response.json().then(()=>{settled=true;},()=>{settled=true;});
await new Promise(resolve=>setTimeout(resolve,8500));
console.log(JSON.stringify({id:'N04',headersReceived:true,configuredReadTimeoutMs:8000,
  elapsedBodyWaitMs:8500,bodySettled:settled,timeoutCoversBody:settled},null,2));
server.closeAllConnections();server.close();await body;
