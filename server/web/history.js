"use strict";
(() => {
  const el=id=>document.getElementById(id);
  let snapshot={devices:[],works:[],computerCache:{}}, busy=false, automatic=true, sending=false, cleaning=false, cacheDeviceId="";
  const statusNames={queued:"等待设备完成",completed:"设备已完成",partial:"部分完成 / 等待定位",failed:"设备执行失败",interrupted:"执行被中断",expired:"清理指令已过期"};
  const bytes=n=>n>=1048576?(n/1048576).toFixed(1)+" MB":Math.ceil((n||0)/1024)+" KB";
  const message={device_offline_refresh_before_clearing:"设备离线，请打开 App 并刷新后再清理。",uploads_in_progress:"数据正在上传，请稍后重试。",unknown_device:"目标设备不存在，请刷新后重试。"};
  const id=()=>crypto.randomUUID().replaceAll("-","");
  async function request(path,body,action) {
    const response=await fetch(path,{method:body?"POST":"GET",cache:"no-store",headers:body?{"Content-Type":"application/json","X-Demo-Action":action}:undefined,body:body?JSON.stringify(body):undefined,signal:AbortSignal.timeout(15000)});
    const result=await response.json();
    if(!response.ok)throw new Error(message[result.error]||`操作失败：${result.error||response.status}`);
    return result;
  }
  function options(select,values,preferred) {
    const prior=select.value;
    const signature=JSON.stringify(values);
    if(select.dataset.signature!==signature) {
      select.replaceChildren(...values.map(([value,label])=>{const option=document.createElement("option");option.value=value;option.textContent=label;return option;}));
      select.dataset.signature=signature;
      select.value=values.some(v=>v[0]===prior)?prior:values.some(v=>v[0]===preferred)?preferred:(values[0]?.[0]||"");
    }
  }
  const device=()=>snapshot.devices.find(d=>d.deviceId===el("work-device").value);
  function render() {
    options(el("work-device"),snapshot.devices.length?snapshot.devices.map(d=>[d.deviceId,`${d.deviceName}${d.clientKind==="editor"?"（编辑器）":""} · ${d.online?"在线":"离线"}`]):[["","等待新版 App 连接"]]);
    const d=device();
    options(el("work-select"),snapshot.works.length?snapshot.works.map(w=>[w.workId,`${w.title} · ${w.recordCount} 条`]):[["","暂无工作"]],d?.workId);
    const work=snapshot.works.find(w=>w.workId===el("work-select").value);
    el("work-connection").textContent=d?`${d.online?"设备在线":"设备离线"} · ${d.sourceIp}`:"等待新版 App 连接";
    el("work-summary").textContent=work?`已归档 ${work.recordCount} 条标签，其中 ${work.restorableCount} 条具有空间备份（${bytes(work.anchorBytes)}）。`+ (work.restorableCount<work.recordCount?"没有空间备份的记录无法还原原位置。":"") :"新版 App 会自动把标签与空间锚点归档到电脑。旧版照片没有空间备份，无法恢复原位置。";
    el("resume-work").disabled=sending||!d||!work||work.restorableCount===0;
    el("new-work").disabled=sending||!d;
    el("work-device-status").textContent=d?`${d.status||"已连接"}；待归档 ${d.cache.pendingArchives}，待上传照片 ${d.cache.pendingPhotos}。`:"请打开新版 App，并保持前台运行。";
    el("work-command-status").textContent=d?.command?`${statusNames[d.command.status]||d.command.status}${d.command.result?.detail?"："+d.command.result.detail:""}`:"";
  }
  async function refresh() {
    if(busy)return;busy=true;
    try {snapshot=await request("/api/work-history");render();}
    catch(error){el("work-connection").textContent="工作历史服务未连接";}
    finally {busy=false;}
  }
  async function command(kind) {
    if(sending||!device())return;
    sending=true;el("work-error").hidden=true;render();
    try {
      await request("/api/device-commands",{commandId:id(),deviceId:device().deviceId,kind,workId:el("work-select").value,title:el("new-work-title").value.trim()},"device-command");
      el("work-command-status").textContent="已排队，等待设备回执";
      if(kind==="new_work")el("new-work-title").value="";
      await refresh();
    }catch(error){el("work-error").textContent=error.message;el("work-error").hidden=false;}
    finally{sending=false;render();}
  }
  function syncCacheSelection() {
    const checked=[...el("cache-dialog").querySelectorAll("input[data-cache-scope]:checked")];
    const loss=checked.some(input=>input.dataset.loss==="true");
    el("cache-loss-label").hidden=!loss;
    el("confirm-cache").disabled=cleaning||!checked.length||(loss&&!el("cache-confirm-loss").checked);
  }
  async function showCache() {
    await refresh();
    const d=device(), pc=snapshot.computerCache||{};
    cacheDeviceId=d?.deviceId||"";
    for(const input of el("cache-dialog").querySelectorAll("input")) {input.checked=false;input.disabled=false;}
    for(const input of el("cache-dialog").querySelectorAll('[data-cache-scope="device"]')) input.disabled=!d?.online;
    el("cache-computer-images").textContent=`${pc.images?.count||0} 张 / ${bytes(pc.images?.bytes)}`;
    el("cache-computer-poses").textContent=`${pc.poses?.count||0} 点 / 数据库 ${bytes(pc.poses?.bytes)}`;
    el("cache-device-title").textContent=d?`设备端 · ${d.deviceName}`:"设备端 · 未连接";
    el("cache-device-downloads").textContent=`${d?.cache.downloadedAnchors||0} 个 / ${bytes(d?.cache.downloadBytes)}`;
    el("cache-device-labels").textContent=`${d?.cache.visibleLabels||0} 个`;
    el("cache-device-photos").textContent=`${d?.cache.pendingPhotos||0} 张 / ${bytes(d?.cache.photoBytes)}`;
    el("cache-device-poses").textContent=`${d?.cache.pendingPoses||0} 点 / ${bytes(d?.cache.poseBytes)}`;
    el("cache-device-note").textContent=d?.online?"头显清理需要设备回执；指令 30 秒后过期。身份配置和未归档的空间锚点不会作为普通缓存删除。":"设备离线时不发送清理指令；请打开 App 后重新打开此窗口。";
    el("cache-error").hidden=true;syncCacheSelection();el("cache-dialog").showModal();
  }
  async function clearSelected() {
    if(el("confirm-cache").disabled)return;
    cleaning=true;el("cancel-cache").disabled=true;el("cache-error").hidden=true;syncCacheSelection();
    const checked=[...el("cache-dialog").querySelectorAll("input[data-cache-scope]:checked")];
    const pc=checked.filter(i=>i.dataset.cacheScope==="computer").map(i=>i.value);
    const remote=checked.filter(i=>i.dataset.cacheScope==="device").map(i=>i.value);
    let completed="";
    try {
      if(pc.length){
        const result=await request("/api/cache/clear",{categories:pc},"cache-clear");
        completed=`电脑已清理${result.images?`原图 ${result.images.count} 张`:""}${result.poses?` 轨迹 ${result.poses.count} 点`:""}。`;
        if(result.images?.failed)completed+=`${result.images.failed} 个文件被占用，未删除。`;
      }
      if(remote.length){await request("/api/device-commands",{commandId:id(),deviceId:cacheDeviceId,kind:"clear_cache",categories:remote},"device-command");completed+="头显清理已排队，请查看设备回执。";}
      el("cache-dialog").close();
      window.toast?.(completed,6500);el("work-command-status").textContent=completed;
      el("refresh").click();
    }catch(error){el("cache-error").textContent=completed+error.message;el("cache-error").hidden=false;}
    finally{cleaning=false;el("cancel-cache").disabled=false;syncCacheSelection();await refresh();}
  }
  el("work-device").addEventListener("change",render);el("work-select").addEventListener("change",render);
  el("resume-work").addEventListener("click",()=>command("resume"));el("new-work").addEventListener("click",()=>command("new_work"));
  el("manage-cache").addEventListener("click",showCache);el("cancel-cache").addEventListener("click",()=>el("cache-dialog").close());
  el("cache-dialog").addEventListener("change",syncCacheSelection);el("cache-dialog").addEventListener("cancel",event=>{if(cleaning)event.preventDefault();});
  el("confirm-cache").addEventListener("click",clearSelected);
  window.historyMonitor={refresh,setAutomatic(value){automatic=value;if(value)refresh();}};
  refresh();setInterval(()=>{if(automatic&&!document.hidden)refresh();},2000);
})();
