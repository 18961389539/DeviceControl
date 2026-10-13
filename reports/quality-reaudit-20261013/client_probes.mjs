import fs from 'node:fs';
import http from 'node:http';
import vm from 'node:vm';
let committed=0;const observations=[];
const server=http.createServer((req,res)=>{
  if(req.method==='POST')committed++;
  if(req.url==='/drop'){req.socket.destroy();return}
  if(req.url==='/bad'){res.writeHead(200,{'Content-Type':'application/json'});res.end('{invalid');return}
  if(req.url==='/slow'){res.writeHead(200,{'Content-Type':'application/json'});res.write('{');return}
  res.writeHead(503,{'Content-Type':'application/json'});res.end('{"error":"unavailable"}');
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const window={};const context={window,fetch,Headers,AbortController,setTimeout,clearTimeout,TextDecoder,Uint8Array,Error};
vm.createContext(context);vm.runInContext(fs.readFileSync('frontend/api-client.js','utf8'),context);
const client=window.DeviceControlApiClient.create({root:'http://127.0.0.1:'+server.address().port,readTimeoutMs:100,writeTimeoutMs:120});
try{
  for(const path of ['/drop','/bad','/slow']){
    const before=committed;let error;
    try{await client(path,{method:'POST',body:'{}'})}catch(e){error=e}
    observations.push({id:'R03',scenario:path,committedAtServer:committed-before,error:error?.message,
      explicitlyUnknown:error?.message.includes('结果未知')||false,commandIdReturnedToCaller:error?.commandId||null});
  }
  try{await client('/slow')}catch{}
  const staleAfterTimeout=client.isDataStale();
  await client('/unavailable');
  observations.push({id:'R07',staleAfterTimeout,staleAfterHttp503:client.isDataStale()});
}finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve))}
fs.writeFileSync('reports/quality-reaudit-20261013/client-observations.json',JSON.stringify(observations,null,2));
console.log(JSON.stringify(observations,null,2));
