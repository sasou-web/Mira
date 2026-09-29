// Isolated integration fixture. No production Jellyfin calls and no real credentials.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
const root=path.resolve('.artifacts');
const video=path.join(root,'lecture-test.mp4');
const reports=[];
const tick=10_000_000;
const media={Id:'mock-film',Name:'Essai de lecture mpv',Type:'Movie',ProductionYear:2026,RunTimeTicks:18*tick,Overview:'Vidéo de test locale. Ce serveur de test est indépendant de ta bibliothèque Jellyfin.',Genres:['Test'],ImageTags:{},UserData:{PlaybackPositionTicks:2*tick,Played:false,IsFavorite:false}};
function write(res,status,body){res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(body));}
const server=http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://localhost');
 if(!req.headers.authorization?.includes('mira-test-token'))return write(res,401,{error:'Missing test authorization'});
 let raw='';for await(const chunk of req)raw+=chunk;
 if(url.pathname.startsWith('/Sessions/Playing')){
   const report=JSON.parse(raw);reports.push({path:url.pathname,...report,receivedAt:new Date().toISOString()});
   media.UserData.PlaybackPositionTicks=report.PositionTicks;
   fs.writeFileSync(path.join(root,'mock-reports.json'),JSON.stringify(reports,null,2));res.writeHead(204);res.end();return;
 }
 if(url.pathname==='/Items')return write(res,200,{Items:[media],TotalRecordCount:1});
 if(url.pathname==='/UserItems/Resume')return write(res,200,{Items:[media],TotalRecordCount:1});
 if(url.pathname==='/Shows/NextUp')return write(res,200,{Items:[],TotalRecordCount:0});
 if(url.pathname==='/UserViews')return write(res,200,{Items:[{Id:'library',Name:'Bibliothèque de test',CollectionType:'movies'}]});
 if(url.pathname==='/Items/mock-film/PlaybackInfo')return write(res,200,{PlaySessionId:'test-session-'+Date.now(),MediaSources:[{Id:'source',Protocol:'Http',Container:'mp4',MediaStreams:[]}]});
 if(url.pathname==='/Videos/mock-film/stream'){
   const size=fs.statSync(video).size;const range=req.headers.range;
   if(range){const match=/bytes=(\d+)-(\d*)/.exec(range);const start=Number(match[1]);const end=match[2]?Math.min(Number(match[2]),size-1):size-1;res.writeHead(206,{'Content-Type':'video/mp4','Accept-Ranges':'bytes','Content-Range':`bytes ${start}-${end}/${size}`,'Content-Length':end-start+1});fs.createReadStream(video,{start,end}).pipe(res);}
   else{res.writeHead(200,{'Content-Type':'video/mp4','Accept-Ranges':'bytes','Content-Length':size});fs.createReadStream(video).pipe(res);}return;
 }
 return write(res,404,{});
});
server.listen(18096,'127.0.0.1',()=>console.log('Mock Jellyfin ready on 127.0.0.1:18096'));
