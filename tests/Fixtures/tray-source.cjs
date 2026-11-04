'use strict';
const readline=require('node:readline');
const http=require('node:http');
const path=require('node:path');
const {pathToFileURL}=require('node:url');
const sampleRate=11025, duration=180, dataSize=sampleRate*duration*2;
const wav=Buffer.alloc(44+dataSize);
wav.write('RIFF',0);wav.writeUInt32LE(36+dataSize,4);wav.write('WAVEfmt ',8);wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(sampleRate,24);wav.writeUInt32LE(sampleRate*2,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(dataSize,40);
const server=http.createServer((req,res)=>{
 const range=/bytes=(\d+)-(\d*)/.exec(req.headers.range||'');
 const start=range?Number(range[1]):0;
 const end=range&&range[2]?Math.min(Number(range[2]),wav.length-1):wav.length-1;
 if(start>=wav.length){res.writeHead(416,{'Content-Range':'bytes */'+wav.length});return res.end();}
 const headers={'Content-Type':'audio/wav','Accept-Ranges':'bytes','Content-Length':end-start+1};
 if(range)headers['Content-Range']=`bytes ${start}-${end}/${wav.length}`;
 res.writeHead(range?206:200,headers);res.end(wav.subarray(start,end+1));
});
const ready=new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const artworkUrl=pathToFileURL(path.join(__dirname,'..','Assets','Luma.png')).href;
const tracks=[{id:12345,title:'托盘测试 · 静音音轨',artist:'Luma Music',album:'Tray',durationMs:180000,artworkUrl},{id:12346,title:'下一首 · 静音音轨',artist:'Luma Music',album:'Tray',durationMs:180000,artworkUrl}];
const input=readline.createInterface({input:process.stdin});
input.on('close',()=>server.close(()=>process.exit(0)));
input.on('line',async line=>{
 const request=JSON.parse(line);await ready;
 const reply=result=>process.stdout.write(JSON.stringify({id:request.id,result})+'\n');
 if(request.method==='discover')return setTimeout(()=>reply({playlists:[{id:456,title:'托盘测试',artworkUrl,trackCount:2}],newSongs:tracks}),4000);
 if(request.method==='authSetSession')return reply({profile:null});
 if(request.method==='authResume')return reply({});
 if(request.method==='stream')return reply({url:`http://127.0.0.1:${server.address().port}/silence.wav`,quality:'exhigh',isPreview:false});
 if(request.method==='lyrics')return reply({lines:[{timeMs:0,durationMs:180000,text:'Luma Music',translation:'小猫陪你听音乐',romanization:'',words:[]}]});
 if(request.method==='playlist')return reply({id:456,title:'托盘测试',subtitle:'Luma Music',artworkUrl,tracks});
 reply({ok:true});
});
