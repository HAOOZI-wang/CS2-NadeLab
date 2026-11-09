'use strict';
const $=id=>document.getElementById(id), canvas=$('plot'), ctx=canvas.getContext('2d');
let report=null, selected=0, view='top',pick='target',transform=null, imported=false;
const fields=['sx','sy','sz','tx','ty','tz','radius'];
const num=id=>Number($(id).value), vec=(x,y,z)=>({x,y,z});
const normalise=v=>Array.isArray(v)?v.map(normalise):v&&typeof v==='object'?Object.fromEntries(Object.entries(v).map(([k,val])=>[k.toLowerCase(),normalise(val)])):v;
const matches=()=>report?.result?.matches||[];
const start=()=>report?.start?.feet||vec(num('sx'),num('sy'),num('sz'));
const target=()=>report?.target?.position||vec(num('tx'),num('ty'),num('tz'));
const radius=()=>report?.target?.radius||num('radius');
const labelMode=m=>typeof m==='string'?({'Left':'左键','Both':'双键','Right':'右键'}[m]||m):['左键','双键','右键'][m];
function status(text,error=false){$('status').textContent=text;$('status').classList.toggle('error',error)}
function clear(){report=null;imported=false;$('export').disabled=true;$('scene').textContent='合成测试场';$('sceneNote').textContent='用于检查求解流程，不是 Mirage 地图';renderResults();draw()}
function setReport(raw){
 const r=normalise(raw);
 if(r.actual&&(r.predicted||r.observationonly)){r.result={matches:r.predicted?[r.predicted]:[],physicsid:r.physics?.id,calibrated:r.physics?.calibrated};r.actualpath=(r.samples||[]).map(s=>s.position)}
 if(!r.result||!Array.isArray(r.result.matches)||!r.start?.feet||!r.target?.position)throw new Error('这不是 NadeLab 搜索或实投记录。');
 // Imported JSON is untrusted: validate finite geometry before drawing.
 const valid=p=>p&&['x','y','z'].every(k=>typeof p[k]==='number'&&Number.isFinite(p[k])&&Math.abs(p[k])<1e7);
 if(!valid(r.start.feet)||!valid(r.target.position)||!Number.isFinite(r.target.radius)||r.target.radius<=0||r.result.matches.length>20)throw new Error('记录中的坐标或目标半径无效。');
 for(const c of r.result.matches)if(!Array.isArray(c.trajectory?.points)||c.trajectory.points.length>10000||!c.trajectory.points.every(valid)||!valid(c.trajectory.endpoint))throw new Error('轨迹数据无效或过大。');
 if(r.actual&&(!valid(r.actual)||!Array.isArray(r.actualpath)||r.actualpath.length>10000||!r.actualpath.every(valid)))throw new Error('实投轨迹数据无效。');
 report=r;selected=0;
 $('export').disabled=false;$('scene').textContent=r.map==='de_mirage'?'Mirage · 训练服结果':'合成测试场';
 $('sceneNote').textContent=r.map==='de_mirage'?'来自地图原生碰撞查询；候选仍需实投验证':'用于检查求解流程，不是 Mirage 地图';
 $('physics').textContent='模型：'+(r.result.physicsid||'未知')+(r.result.calibrated?' · 配置标记已校准':' · 待实测校准');
 $('actualLegend').hidden=!r.actualpath;
 renderResults();draw();
 if(r.actual)status(r.observationonly?`自由实投：目标误差 ${r.targeterror?.toFixed(1)} u，${r.targethit?'命中目标，已记录可达证据':'未命中目标'}；不作为模型验证。`:`实投目标误差 ${r.targeterror?.toFixed(1)} u；模型误差 ${r.modelerror?.toFixed(1)} u；${r.matched?'动作匹配':'动作不匹配'}，${r.success?'实投验证命中':'未通过验证'}。`);
 else status(`已计算 ${r.result.evaluated??'—'} 个候选。${r.result.truncated?'已达搜索预算。':''} ${matches().length?'以下为模拟命中，选择查看轨迹。':'当前范围内未找到，不能据此断言无解。'}`);
}
function renderResults(){
 const ms=matches();$('resultTitle').textContent=report?`${ms.length} 个候选投法`:'等待一次搜索';$('results').replaceChildren();
 if(!ms.length){const d=document.createElement('div');d.className='empty';d.innerHTML='<span>⌁</span><p>这里还没有可行候选</p><small>尝试改变起点、目标半径或搜索范围</small>';$('results').append(d);return}
 ms.forEach((c,i)=>{
  const b=document.createElement('button');b.className='candidate'+(selected===i?' selected':'');b.setAttribute('aria-pressed',String(selected===i));
  const n=document.createElement('span');n.className='number';n.textContent=String(i+1).padStart(2,'0');b.append(n);
  const a=document.createElement('div'),title=document.createElement('strong'),angle=document.createElement('small');title.textContent=c.aim.instruction||labelMode(c.aim.mode);angle.textContent=`P ${c.aim.pitch.toFixed(2)}° / Y ${c.aim.yaw.toFixed(2)}°`;a.append(title,angle);b.append(a);
  const e=document.createElement('div');e.className='measure';const ev=document.createElement('b');ev.textContent=c.error.toFixed(1)+' u';const et=document.createElement('small');et.textContent='预测目标误差';e.append(ev,et);b.append(e);
  const rob=document.createElement('div');rob.className='robust';const rv=document.createElement('b');rv.textContent=`${c.robusthits||0}/${c.robusttrials||0}`;const rt=document.createElement('small');rt.textContent='模拟扰动命中';rob.append(rv,rt);b.append(rob);
  b.onclick=()=>{selected=i;renderResults();draw()};$('results').append(b);
 });
}
function draw(){
 const rect=canvas.getBoundingClientRect(),dpr=window.devicePixelRatio||1,w=rect.width,h=rect.height;
 canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
 const project=p=>[p.x,view==='top'?p.y:p.z],s=start(),t=target(),r=radius();
 let all=[s,t,...matches().flatMap(c=>c.trajectory.points),...(report?.actualpath||[])].map(project);
 const bounds=all.reduce((b,p)=>[Math.min(b[0],p[0]),Math.max(b[1],p[0]),Math.min(b[2],p[1]),Math.max(b[3],p[1])],[Infinity,-Infinity,Infinity,-Infinity]);
 let minX=bounds[0]-100,maxX=bounds[1]+100,minY=bounds[2]-100,maxY=bounds[3]+100;
 if(maxX-minX<600){let mid=(maxX+minX)/2;minX=mid-300;maxX=mid+300}if(maxY-minY<300){let mid=(maxY+minY)/2;minY=mid-150;maxY=mid+150}
 const scale=Math.min((w-100)/(maxX-minX),(h-100)/(maxY-minY)),cx=(minX+maxX)/2,cy=(minY+maxY)/2;
 const to=p=>[w/2+(p[0]-cx)*scale,h/2-(p[1]-cy)*scale];transform={scale,cx,cy,w,h};
 ctx.strokeStyle='#1c2b32';ctx.lineWidth=1;ctx.font='9px Consolas';ctx.fillStyle='#52666f';
 const grid=100;
 for(let x=Math.ceil(minX/grid)*grid;x<=maxX;x+=grid){let px=to([x,0])[0];ctx.beginPath();ctx.moveTo(px,15);ctx.lineTo(px,h-40);ctx.stroke();ctx.fillText(x.toFixed(0),px+3,h-43)}
 for(let y=Math.ceil(minY/grid)*grid;y<=maxY;y+=grid){let py=to([0,y])[1];ctx.beginPath();ctx.moveTo(20,py);ctx.lineTo(w-20,py);ctx.stroke();ctx.fillText(y.toFixed(0),24,py-4)}
 if(!imported&&$('box').checked){let a=to([300,view==='top'?-90:0]),b=to([360,view==='top'?90:160]);ctx.fillStyle='#334048';ctx.fillRect(a[0],b[1],b[0]-a[0],a[1]-b[1]);ctx.fillStyle='#9aa8ae';ctx.fillText('测试箱体',a[0]+5,b[1]+13)}
 if(view==='side'){let p=to([0,0]);ctx.strokeStyle='#6a777d';ctx.beginPath();ctx.moveTo(20,p[1]);ctx.lineTo(w-20,p[1]);ctx.stroke()}
 const tp=to(project(t));ctx.beginPath();ctx.arc(tp[0],tp[1],Math.max(3,r*scale),0,Math.PI*2);ctx.fillStyle='#dcb97b12';ctx.fill();ctx.strokeStyle='#dcb97b';ctx.setLineDash([4,4]);ctx.stroke();ctx.setLineDash([]);
 const path=(pts,color,width)=>{if(!pts?.length)return;ctx.strokeStyle=color;ctx.lineWidth=width;ctx.beginPath();pts.forEach((p,i)=>{let a=to(project(p));i?ctx.lineTo(...a):ctx.moveTo(...a)});ctx.stroke()};
 matches().forEach((c,i)=>{if(i!==selected)path(c.trajectory.points,'#365647',1)});
 if(matches()[selected])path(matches()[selected].trajectory.points,'#94edb6',2);
 if(report?.actualpath)path(report.actualpath,'#73baff',2);
 const dot=(p,color,label)=>{let a=to(project(p));ctx.beginPath();ctx.arc(a[0],a[1],4,0,Math.PI*2);ctx.fillStyle=color;ctx.fill();ctx.font='11px Segoe UI';ctx.fillText(label,a[0]+10,a[1]-10)};
 dot(s,'#e9edf0','起点');dot(t,'#dcb97b','目标');
 if(matches()[selected])dot(matches()[selected].trajectory.endpoint,'#94edb6','预测引爆');
 if(report?.actual)dot(report.actual,'#73baff','实投引爆');
 ctx.fillStyle='#627780';ctx.font='10px Segoe UI';ctx.fillText(view==='top'?'X / Y · 俯视':'X / Z · 侧视',w-100,24);
}
fields.forEach(id=>$(id).addEventListener('change',clear));$('box').onchange=clear;
function setPick(p){pick=p;$('pickStart').classList.toggle('active',p==='start');$('pickTarget').classList.toggle('active',p==='target');$('viewNote').textContent='点击图面设置'+(p==='start'?'起点':'目标')}
$('pickStart').onclick=()=>setPick('start');$('pickTarget').onclick=()=>setPick('target');
$('top').onclick=()=>{view='top';$('top').classList.add('active');$('side').classList.remove('active');draw()};
$('side').onclick=()=>{view='side';$('side').classList.add('active');$('top').classList.remove('active');draw()};
canvas.onclick=e=>{if(view!=='top'||imported||!transform)return;const rect=canvas.getBoundingClientRect(),p=transform,x=Math.round(p.cx+(e.clientX-rect.left-p.w/2)/p.scale),y=Math.round(p.cy-(e.clientY-rect.top-p.h/2)/p.scale);$(pick==='start'?'sx':'tx').value=x;$(pick==='start'?'sy':'ty').value=y;clear()};
$('solve').onclick=async()=>{
 $('solve').disabled=true;status('正在搜索角度和力度，并对候选进行小幅扰动测试…');
 try{let req={start:vec(num('sx'),num('sy'),num('sz')),target:vec(num('tx'),num('ty'),num('tz')),radius:num('radius'),wide:$('wide').checked,boxes:$('box').checked?[{min:vec(300,-90,0),max:vec(360,90,160)}]:[]};
 const response=await fetch('/api/solve',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(req)}),data=await response.json();if(!response.ok)throw new Error(data.error||'搜索失败');imported=false;setReport(data)}catch(e){status(e.message,true)}finally{$('solve').disabled=false}
};
$('file').onchange=async e=>{try{const f=e.target.files[0];if(!f)return;if(f.size>20*1024*1024)throw new Error('文件超过20MB，请截取需要的记录。');const txt=await f.text();let data;try{data=JSON.parse(txt)}catch{const lines=txt.split(/\r?\n/).filter(x=>x.trim());data=JSON.parse(lines.at(-1))}setReport(data);imported=true;draw();}catch(e){status(e.message,true)}finally{$('file').value=''}};
$('refresh').onclick=async()=>{try{const res=await fetch('/api/report'),data=await res.json();if(!res.ok)throw new Error(data.error);if(data.connected===false){status(data.message);return}setReport(data);imported=true;draw()}catch(e){status(e.message,true)}};
$('export').onclick=()=>{if(!report)return;const url=URL.createObjectURL(new Blob([JSON.stringify(report,null,2)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download='nadelab-'+(report.map||'report')+'.json';a.click();URL.revokeObjectURL(url)};
new ResizeObserver(draw).observe(canvas);draw();
