"use strict";
(() => {
  const $ = id => document.getElementById(id);
  const canvas = $("pose-canvas"), ctx = canvas.getContext("2d");
  let sessions = [], selected = "", points = [], cursor = -1, follow = true, auto = true;
  let busy = false, pending = false, generation = 0, yaw = -0.65, elevation = 0.65, zoom = 1, top = false;
  const stamp = s => new Date(s).toLocaleString("zh-CN", {hour12:false});
  const fmt = (n, digits=3) => Number(n).toFixed(digits);
  const elapsed = n => `${Math.floor(n/60)}:${String(Math.floor(n%60)).padStart(2,"0")}`;
  async function api(path) {
    const response = await fetch(path, {cache:"no-store", signal:AbortSignal.timeout(8000)});
    if (!response.ok) throw new Error(response.status === 404 ? "接收服务尚不支持轨迹，请更新并重启接收服务。" : `轨迹读取失败（HTTP ${response.status}）`);
    return response.json();
  }
  function selectSession(id) {
    selected=id;points=[];cursor=-1;generation++;$("pose-scrub").value="0";$("pose-live").checked=true;zoom=1;
    render();
  }
  async function refresh() {
    if (busy) {pending=true;return;}
    busy=true;
    try {
      const list=await api("/api/pose-sessions");sessions=list.sessions;
      if (follow && sessions.length && selected!==sessions[0].sessionId) selectSession(sessions[0].sessionId);
      if (!selected && sessions.length) selectSession(sessions[0].sessionId);
      const signature=sessions.map(s=>s.sessionId).join();
      if ($("pose-session").dataset.signature!==signature) {
        $("pose-session").replaceChildren(...sessions.map(s=>{
          const option=document.createElement("option");option.value=s.sessionId;
          option.textContent=`${stamp(s.startedAt)} · ${s.deviceName} · ${s.clientKind==="editor"?"编辑器":"头显 / 模拟器"} · ${s.sessionId.slice(0,6)}`;return option;
        }));
        $("pose-session").dataset.signature=signature;
      }
      $("pose-session").value=selected;
      const currentGeneration=generation,id=selected;
      // Incremental pages avoid re-downloading the whole trajectory every second.
      if (id) for (let page=0;page<10;page++) {
        const result=await api(`/api/pose-sessions/${id}?after=${cursor}&limit=2000`);
        if (currentGeneration!==generation) {pending=true;return;}
        points.push(...result.samples);cursor=result.nextAfter;
        if (!result.hasMore) break;
      }
      $("pose-error").hidden=true;render();
    } catch (error) {$("pose-error").textContent=error.message.startsWith("轨迹")||error.message.startsWith("接收")?error.message:"无法连接轨迹服务，请确认电脑接收服务正在运行。";$("pose-error").hidden=false;}
    finally {busy=false;if(pending){pending=false;setTimeout(refresh,0);}}
  }
  function currentPoint() {return points[Number($("pose-scrub").value)] || null;}
  function axes(p) {
    const {qx:x,qy:y,qz:z,qw:w}=p;
    return [[1-2*(y*y+z*z),2*(x*y+z*w),2*(x*z-y*w)],
      [2*(x*y-z*w),1-2*(x*x+z*z),2*(y*z+x*w)],
      [2*(x*z+y*w),2*(y*z-x*w),1-2*(x*x+y*y)]];
  }
  function render() {
    const s=sessions.find(item=>item.sessionId===selected);
    const slider=$("pose-scrub");slider.max=String(Math.max(0,points.length-1));slider.disabled=!points.length;
    if ($("pose-live").checked)slider.value=slider.max;
    const p=currentPoint(),last=points[points.length-1];
    $("pose-count").textContent=`${points.length.toLocaleString()} / ${(s?.sampleCount||0).toLocaleString()}`;
    $("pose-progress").textContent=`${points.length?Number(slider.value)+1:0} / ${points.length}`;
    $("pose-export").disabled=!points.length;
    $("pose-ip").textContent=s?.sourceIp||"—";
    $("pose-session-note").textContent=s?`${stamp(s.startedAt)} · ${s.deviceName} · ${s.clientKind==="editor"?"编辑器测试数据":"设备空间追踪"}`:"每次启动独立记录，位置单位为米。";
    $("pose-time").textContent=p?`启动后 ${elapsed(p.elapsed)}`:"—";
    $("pose-point-state").textContent=p?(p.tracked?"有效定位":"未定位 · "+p.trackingState):"尚无采样";
    $("pose-position").textContent=p?.tracked?[p.x,p.y,p.z].map(v=>fmt(v)).join(" / "):"—";
    $("pose-quaternion").textContent=p?.tracked?[p.qx,p.qy,p.qz,p.qw].map(v=>fmt(v,4)).join(" / "):"—";
    if (p?.tracked) {
      const [right,up,forward]=axes(p),deg=180/Math.PI;
      // Intrinsic Y-X-Z: yaw right, pitch up, roll clockwise viewed along forward.
      $("pose-angles").textContent=[Math.atan2(forward[0],forward[2])*deg,Math.asin(Math.max(-1,Math.min(1,forward[1])))*deg,Math.atan2(right[1],up[1])*deg].map(v=>fmt(v,1)).join(" / ");
    } else $("pose-angles").textContent="—";
    const age=last?Math.max(0,(Date.now()-Date.parse(last.sampledAt))/1000):null;
    $("pose-age").textContent=age===null?"—":`${fmt(age,0)} 秒`;
    const receiving=s&&(Date.now()-Date.parse(s.lastReceivedAt))<5000;
    const live=receiving&&last?.tracked;
    $("pose-status-dot").dataset.live=String(live);
    $("pose-status").textContent=!last?"等待设备姿态":!receiving?"暂无新采样 · 历史轨迹已保留":last.tracked?"正在接收 · 目标 1 Hz": "空间追踪暂不可用";
    $("pose-empty").hidden=points.some(point=>point.tracked);
    if(points.length&&!$("pose-empty").hidden){$("pose-empty").querySelector("strong").textContent="等待有效空间定位";}
    else $("pose-empty").querySelector("strong").textContent="等待第一条轨迹";
    draw();
  }
  function draw() {
    const bounds=canvas.getBoundingClientRect();if(!bounds.width||!bounds.height)return;
    const dpr=Math.min(devicePixelRatio||1,2),w=bounds.width,h=bounds.height;
    canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);ctx.setTransform(dpr,0,0,dpr,0,0);
    ctx.fillStyle="#102c32";ctx.fillRect(0,0,w,h);
    const valid=points.filter(p=>p.tracked), min=[0,0,0],max=[0,0,0];
    for(const p of valid) [p.x,p.y,p.z].forEach((v,i)=>{min[i]=Math.min(min[i],v);max[i]=Math.max(max[i],v);});
    const middle=min.map((v,i)=>(v+max[i])/2),span=Math.max(2,...max.map((v,i)=>v-min[i]));
    const scale=Math.min(w-80,h-90)/(span*1.5)*zoom;
    const a=top?0:yaw,b=top?Math.PI/2:elevation;
    const project=(p)=>{const [x,y,z]=p.map((v,i)=>v-middle[i]);return [w/2+(x*Math.cos(a)+z*Math.sin(a))*scale,h/2-(y*Math.cos(b)+(z*Math.cos(a)-x*Math.sin(a))*Math.sin(b))*scale];};
    const line=(from,to,color,width=1)=>{const u=project(from),v=project(to);ctx.beginPath();ctx.moveTo(...u);ctx.lineTo(...v);ctx.strokeStyle=color;ctx.lineWidth=width;ctx.stroke();};
    const step=Math.pow(10,Math.floor(Math.log10(span/4))),radius=Math.ceil(span/step);
    for(let i=-radius;i<=radius;i++) {
      line([middle[0]-span,0,middle[2]+i*step],[middle[0]+span,0,middle[2]+i*step],"#244148");
      line([middle[0]+i*step,0,middle[2]-span],[middle[0]+i*step,0,middle[2]+span],"#244148");
    }
    const axisColors=["#ef8c7d","#b5da7a","#72bdf5"],unit=Math.max(0.25,span*0.14);
    [[unit,0,0],[0,unit,0],[0,0,unit]].forEach((end,i)=>{line([0,0,0],end,axisColors[i],1.5);const q=project(end);ctx.fillStyle=axisColors[i];ctx.font="11px Segoe UI";ctx.fillText("XYZ"[i],q[0]+5,q[1]-5);});
    const circle=(at,r,color)=>{ctx.beginPath();ctx.arc(...project(at),r,0,Math.PI*2);ctx.fillStyle=color;ctx.fill();};
    circle([0,0,0],4,"#e2c780");
    let previous=null;
    for(const p of points) {
      if(p.tracked) {
        if(previous?.tracked&&p.segment===previous.segment&&p.sequence===previous.sequence+1&&p.elapsed-previous.elapsed<=2.5)
          line([previous.x,previous.y,previous.z],[p.x,p.y,p.z],"#64dbb9",2.2);
      }
      previous=p;
    }
    const p=currentPoint();
    if(p?.tracked) {
      const here=[p.x,p.y,p.z];circle(here,5,"#ffffff");
      axes(p).forEach((axis,i)=>{const end=axis.map((v,k)=>here[k]+v*unit);line(here,end,axisColors[i],3);circle(end,2.5,axisColors[i]);});
      const q=project(here);ctx.fillStyle="#ebfff7";ctx.font="12px Segoe UI";ctx.fillText(`t + ${elapsed(p.elapsed)}`,q[0]+12,q[1]-12);
    }
    ctx.fillStyle="#8caaa9";ctx.font="11px Segoe UI";ctx.fillText(`网格 ${fmt(step,step<1?1:0)} m · ${top?"俯视 XZ":"三维视图"}`,18,h-18);
    canvas.dataset.pointCount=String(valid.length);
    canvas.dataset.selectedSequence=p?String(p.sequence):"";
  }
  function switchView(trajectory) {
    $("capture-pane").hidden=trajectory;$("trajectory-pane").hidden=!trajectory;
    $("capture-tab").setAttribute("aria-selected",String(!trajectory));$("trajectory-tab").setAttribute("aria-selected",String(trajectory));
    $("clear-history").hidden=trajectory;history.replaceState(null,"",trajectory?"#trajectory":location.pathname);
    if(trajectory){refresh();requestAnimationFrame(draw);}
  }
  $("capture-tab").addEventListener("click",()=>switchView(false));$("trajectory-tab").addEventListener("click",()=>switchView(true));
  $("pose-session").addEventListener("change",()=>{follow=false;$("pose-follow").setAttribute("aria-pressed","false");selectSession($("pose-session").value);refresh();});
  $("pose-follow").addEventListener("click",()=>{follow=true;$("pose-follow").setAttribute("aria-pressed","true");refresh();});
  $("pose-scrub").addEventListener("input",()=>{$("pose-live").checked=false;render();});$("pose-live").addEventListener("change",render);
  $("pose-top").addEventListener("click",()=>{top=!top;$("pose-top").setAttribute("aria-pressed",String(top));$("pose-top").textContent=top?"返回三维":"俯视 XZ";draw();});
  $("pose-fit").addEventListener("click",()=>{zoom=1;yaw=-0.65;elevation=0.65;draw();});
  let drag=null;
  canvas.addEventListener("pointerdown",e=>{drag=[e.clientX,e.clientY];canvas.setPointerCapture(e.pointerId);});
  canvas.addEventListener("pointermove",e=>{if(!drag||top)return;yaw+=(e.clientX-drag[0])*0.008;elevation=Math.max(0.05,Math.min(1.5,elevation+(e.clientY-drag[1])*0.008));drag=[e.clientX,e.clientY];draw();});
  for(const event of ["pointerup","pointercancel","lostpointercapture"])canvas.addEventListener(event,()=>{drag=null;});
  canvas.addEventListener("wheel",e=>{e.preventDefault();zoom=Math.max(0.25,Math.min(8,zoom*Math.exp(-e.deltaY*0.001)));draw();},{passive:false});
  canvas.addEventListener("keydown",e=>{if(!["ArrowLeft","ArrowRight","ArrowUp","ArrowDown"].includes(e.key))return;e.preventDefault();yaw+=e.key==="ArrowRight"?0.1:e.key==="ArrowLeft"?-0.1:0;elevation=Math.max(0.05,Math.min(1.5,elevation+(e.key==="ArrowUp"?0.1:e.key==="ArrowDown"?-0.1:0)));draw();});
  $("pose-export").addEventListener("click",async()=>{
    const id=selected,session=sessions.find(s=>s.sessionId===id);if(!id)return;
    $("pose-export").disabled=true;
    try {
      const samples=[];let after=-1;
      do {const page=await api(`/api/pose-sessions/${id}?after=${after}&limit=2000`);samples.push(...page.samples);after=page.nextAfter;if(!page.hasMore)break;}while(true);
      const blob=new Blob([JSON.stringify({session,coordinateSystem:"Unity left-handed; meters; X right, Y up, Z initial horizontal forward",samples},null,2)],{type:"application/json"});
      const a=document.createElement("a"),url=URL.createObjectURL(blob);a.href=url;a.download=`trajectory-${id}.json`;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
    } catch(error){$("pose-error").textContent=error.message;$("pose-error").hidden=false;}
    finally{$("pose-export").disabled=!points.length;}
  });
  new ResizeObserver(draw).observe(canvas);
  window.poseMonitor={refresh,setAutomatic(value){auto=value;if(value&&!$("trajectory-pane").hidden)refresh();}};
  setInterval(()=>{if(auto&&!document.hidden&&!$("trajectory-pane").hidden)refresh();},1000);
  document.addEventListener("visibilitychange",()=>{if(auto&&!document.hidden&&!$("trajectory-pane").hidden)refresh();});
  switchView(location.hash==="#trajectory");
})();
