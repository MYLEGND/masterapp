import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root=resolve(fileURLToPath(new URL('./dist/',import.meta.url)));
const port=Number(process.env.PORT||8080);
const types={'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.js':'text/javascript; charset=utf-8','.svg':'image/svg+xml','.xml':'application/xml; charset=utf-8','.txt':'text/plain; charset=utf-8','.png':'image/png','.jpg':'image/jpeg','.jpeg':'image/jpeg','.webp':'image/webp'};
const headers={
 'X-Content-Type-Options':'nosniff',
 'X-Frame-Options':'SAMEORIGIN',
 'Referrer-Policy':'strict-origin-when-cross-origin',
 'Permissions-Policy':'camera=(), microphone=(), geolocation=()',
 'Content-Security-Policy':"default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'self'; form-action 'self' mailto:; base-uri 'self'"
};

function safePath(urlPath){
 const decoded=decodeURIComponent(urlPath.split('?')[0]);
 const cleaned=normalize(decoded).replace(/^(\.\.[/\\])+/, '').replace(/^[/\\]+/,'');
 const resolved=resolve(join(root,cleaned));
 return resolved.startsWith(root)?resolved:null;
}

async function candidate(pathname){
 const base=safePath(pathname);
 if(!base)return null;
 try{const s=await stat(base);if(s.isFile())return base;if(s.isDirectory())return join(base,'index.html');}catch{}
 if(!extname(base)){
  try{await stat(`${base}.html`);return `${base}.html`;}catch{}
  try{await stat(join(base,'index.html'));return join(base,'index.html');}catch{}
 }
 return null;
}

const server=http.createServer(async(req,res)=>{
 try{
  const file=await candidate(req.url||'/');
  const chosen=file||join(root,'404.html');
  const body=await readFile(chosen);
  const ext=extname(chosen).toLowerCase();
  const isHtml=ext==='.html';
  res.writeHead(file?200:404,{
   ...headers,
   'Content-Type':types[ext]||'application/octet-stream',
   'Cache-Control':isHtml?'no-cache':'public, max-age=604800',
   'Content-Length':body.length
  });
  if(req.method==='HEAD')return res.end();
  res.end(body);
 }catch(error){
  console.error(error);
  res.writeHead(500,{...headers,'Content-Type':'text/plain; charset=utf-8'});
  res.end('LEGEND® website host error.');
 }
});
server.listen(port,'0.0.0.0',()=>console.log(`LEGEND® website listening on ${port}`));
