"use strict";
const $ = id => document.getElementById(id);
const stateNames = {legacy:"历史记录",processing:"处理中",responded:"已发送响应",disconnected:"发送断连",failed:"处理失败",interrupted:"服务中断"};
let captures = new Map(), selectedId = null, current = null, nextBefore = null;
let automatic = true, following = true, query = "", generation = 0, busy = false, pendingRefresh = false, selectedAttemptId = null, searchTimer, toastTimer;
let clearing = false, historyRevision = null, photoCount = 0;
const stamp = new Intl.DateTimeFormat("zh-CN", {month:"2-digit",day:"2-digit",hour:"2-digit",minute:"2-digit",second:"2-digit",hour12:false});
const clock = new Intl.DateTimeFormat("zh-CN", {hour:"2-digit",minute:"2-digit",second:"2-digit",hour12:false});
function time(value) { return value ? stamp.format(new Date(value)) : "—"; }
function size(bytes) { return bytes >= 1048576 ? (bytes/1048576).toFixed(2)+" MB" : (bytes/1024).toFixed(1)+" KB"; }
function duration(ms) { return Number.isFinite(ms) ? ms.toLocaleString("zh-CN")+" ms" : "—"; }
function node(tag, className, value) { const n = document.createElement(tag); if(className)n.className=className; if(value!==undefined)n.textContent=value; return n; }
function badge(value) { const b=node("span","badge",stateNames[value]||value); b.dataset.state=value; return b; }
function online(ok, message) {
  $("connection").dataset.state = ok ? "online" : "offline";
  $("connection").querySelector("span").textContent = ok ? "接收服务在线" : "接收服务未连接";
  $("error-banner").hidden = ok;
  $("error-banner").textContent = message || "无法连接接收服务。请运行 StartServer.cmd；页面会保留当前内容并自动重试。";
}
async function api(path) {
  const r = await fetch(path, {cache:"no-store",signal:AbortSignal.timeout(6000)});
  if(!r.ok)throw new Error(r.status===403 ? "请在接收电脑打开 http://127.0.0.1:8765。" : `读取失败（HTTP ${r.status}），请检查接收服务。`);
  return r.json();
}
function syncFollowing() {
  $("follow-latest").setAttribute("aria-pressed",String(following));
  $("follow-latest").textContent = following ? "正在跟随最新" : "跟随最新";
}
function renderList() {
  const entries = [...captures.values()].sort((a,b)=>b.sequence-a.sequence);
  const signature = JSON.stringify(entries.map(c=>[c.captureId,c.state,c.attemptCount,c.sourceIp,c.completedAt]))+selectedId;
  if($("capture-list").dataset.signature !== signature) {
    const fragment = document.createDocumentFragment();
    for(const c of entries) {
      const row=node("button","capture-row"+(selectedId===c.captureId?" active":""));
      row.type="button"; row.dataset.captureId=c.captureId;
      row.setAttribute("aria-label",`${c.serial}，${time(c.receivedAt)}，${stateNames[c.state]}`);
      row.setAttribute("aria-current",String(selectedId===c.captureId));
      const top=node("div","row-top");top.append(node("span","row-serial",c.serial),badge(c.state));
      const bottom=node("div","row-bottom");bottom.append(node("span","",time(c.receivedAt)),node("span","mono",c.sourceIp||"来源未记录"));
      row.append(top,bottom);row.addEventListener("click",()=>select(c.captureId));fragment.append(row);
    }
    $("capture-list").replaceChildren(fragment);$("capture-list").dataset.signature=signature;
  }
  $("list-empty").hidden=entries.length>0;
  $("list-empty").textContent=query ? "没有匹配的序列号或请求 ID" : "还没有上传记录";
  $("list-count").textContent=`已显示 ${entries.length} 条`;
  $("load-more").hidden=nextBefore===null;
}
async function select(id) {
  if(clearing)return;
  following=false;syncFollowing();selectedId=id;renderList();
  const viewGeneration=generation;
  try {
    const detail=await api(`/api/captures/${id}`);
    if(selectedId===id && generation===viewGeneration)renderDetail(detail);
  } catch(error) { if(generation===viewGeneration)online(false,error.message); }
}
function renderOverlay() {
  const result=current?.response;
  const valid=result && Number.isFinite(result.centerX) && Number.isFinite(result.centerY) && result.centerX>=0 && result.centerX<=1 && result.centerY>=0 && result.centerY<=1;
  $("image-overlay").hidden=!valid || !$("overlay-toggle").checked || !$("image-error").hidden;
  if(valid) {
    $("image-overlay").style.left=`${result.centerX*100}%`;
    $("image-overlay").style.top=`${result.centerY*100}%`;
    $("overlay-label").textContent=result.serial||"";
    $("center-caption").textContent=`标签中心 (${result.centerX.toFixed(3)}, ${result.centerY.toFixed(3)})`;
  } else $("center-caption").textContent=current?.state==="legacy" ? "历史记录未保存返回坐标" : "等待标签结果";
}
function renderDetail(c) {
  if(current?.captureId!==c.captureId)selectedAttemptId=null;
  current=c;$("empty-state").hidden=true;$("capture-view").hidden=false;
  $("capture-title").textContent=c.serial;$("capture-state").textContent=stateNames[c.state]||c.state;$("capture-state").dataset.state=c.state;
  if($("capture-image").getAttribute("src")!==c.imageUrl) {
    $("image-error").hidden=true;$("capture-image").hidden=false;
    $("image-dimensions").textContent="JPEG 原图";
    $("capture-image").alt=`${c.serial} 设备上传的原始画面`;
    $("capture-image").src=c.imageUrl;
  }
  $("download-image").href=c.imageUrl+"?download=1";
  $("download-image").download=c.serial+".jpg";
  $("result-serial").textContent=c.response?.serial || c.serial;
  $("result-hint").textContent=c.state==="legacy"?"数据库序列号":c.state==="processing"?"等待返回":"服务端结果";
  $("result-center").textContent=c.response && Number.isFinite(c.response.centerX) && Number.isFinite(c.response.centerY) ? `(${c.response.centerX.toFixed(3)}, ${c.response.centerY.toFixed(3)})` : "未记录";
  $("result-duration").textContent=duration(c.processingMs);
  const notes={legacy:"原图和序列号已保留，当时响应的耗时及发送情况未知。",processing:"图片已收到，正在处理或等待返回。",responded:"响应已写入连接。设备端是否已显示，尚无回执。",disconnected:"写出响应时连接已断开，可对照请求 ID 排查。",failed:"处理过程失败，请查看下方请求记录。",interrupted:"服务曾中断，此次请求没有记录到完整发送结果。"};
  $("delivery-note").textContent=notes[c.state]||"";
  $("received-at").textContent=time(c.receivedAt);$("completed-at").textContent=time(c.completedAt);
  $("source-ip").textContent=c.sourceIp||"未记录";$("image-size").textContent=size(c.imageBytes);
  $("request-id").textContent=c.captureId;$("image-hash").textContent=c.imageSha256;
  const chosen=c.attempts.find(a=>a.id===selectedAttemptId)?.response || c.response;
  $("copy-json").disabled=!chosen;
  $("response-json").textContent=chosen ? JSON.stringify(chosen,null,2) : c.state==="legacy" ? "这条历史记录没有保存响应 JSON。" : "等待服务端生成响应…";
  $("attempt-count").textContent=c.attemptCount>50 ? `${c.attemptCount} 次 · 显示最近 50 次` : `${c.attemptCount} 次请求`;
  $("legacy-note").hidden=c.attemptCount!==0;$("attempt-table").hidden=c.attemptCount===0;
  const fragment=document.createDocumentFragment();
  for(const attempt of c.attempts) {
    const tr=node("tr");const stateCell=node("td");stateCell.append(badge(attempt.state));
    if(attempt.error) { stateCell.title=attempt.error;const detail=node("small","attempt-error",attempt.error);stateCell.append(detail); }
    tr.append(node("td","",time(attempt.startedAt)),stateCell,node("td","mono",attempt.sourceIp),node("td","mono",attempt.httpStatus??"—"),node("td","mono",duration(attempt.processingMs)),node("td","",attempt.cached?"幂等重试":"首次上传"));
    if(attempt.response) {tr.title="点击此行查看该次请求的完整响应";tr.tabIndex=0;tr.setAttribute("role","button");
      const show=()=>{selectedAttemptId=attempt.id;$("response-json").textContent=JSON.stringify(attempt.response,null,2);$("response-json").scrollIntoView({block:"nearest"});};
      tr.addEventListener("click",show);tr.addEventListener("keydown",e=>{if(e.key==="Enter"||e.key===" "){e.preventDefault();show();}});}
    fragment.append(tr);
  }
  $("attempt-body").replaceChildren(fragment);renderOverlay();
}
async function refresh(more=false) {
  if(clearing)return;
  if(busy){pendingRefresh=true;return;}
  busy=true;const viewGeneration=generation;
  // Background polling must not toggle the visible button's disabled/opacity state.
  if(more)$("load-more").disabled=true;
  try {
    const params=new URLSearchParams({limit:"50",q:query});
    if(more && nextBefore!==null)params.set("before",String(nextBefore));
    const page=await api("/api/captures?"+params);
    if(viewGeneration!==generation)return;
    if(historyRevision!==null && historyRevision!==page.historyRevision) {
      resetHistoryView();
      if(more)pendingRefresh=true;
    }
    historyRevision=page.historyRevision;
    for(const c of page.captures)captures.set(c.captureId,c);
    if(more || captures.size<=50)nextBefore=page.nextBefore;
    for(const [key,value] of Object.entries(page.stats))$("stat-"+key).textContent=value.toLocaleString("zh-CN");
    photoCount=page.stats.photos;$("clear-history").disabled=photoCount===0;
    const newest=[...captures.values()].sort((a,b)=>b.sequence-a.sequence)[0];
    if(newest && (following || !selectedId))selectedId=newest.captureId;
    renderList();
    if(!newest) {$("capture-view").hidden=true;$("empty-state").hidden=false;}
    if(selectedId) {
      const id=selectedId;const detail=await api(`/api/captures/${id}`);
      if(viewGeneration===generation && selectedId===id)renderDetail(detail);
    }
    if(viewGeneration===generation){online(true);$("updated-at").textContent="最近更新 "+clock.format(new Date());}
  } catch(error) { if(viewGeneration===generation)online(false,error.message.includes("HTTP") || error.message.includes("127.0.0.1") ? error.message : undefined); }
  finally {busy=false;if(more)$("load-more").disabled=false;if(pendingRefresh){pendingRefresh=false;setTimeout(()=>refresh(),0);}}
}
function resetHistoryView() {
  captures=new Map();selectedId=null;current=null;selectedAttemptId=null;nextBefore=null;following=true;
  $("capture-view").hidden=true;$("empty-state").hidden=false;$("capture-image").removeAttribute("src");
  $("image-overlay").hidden=true;$("response-json").textContent="等待响应";$("attempt-body").replaceChildren();
  syncFollowing();renderList();
}
async function clearHistory() {
  if(clearing)return;
  clearing=true;generation++;pendingRefresh=false;clearTimeout(searchTimer);
  $("confirm-clear").disabled=true;$("cancel-clear").disabled=true;$("clear-history").disabled=true;
  $("confirm-clear").textContent="正在清除…";$("clear-error").hidden=true;
  try {
    const response=await fetch("/api/captures",{method:"DELETE",headers:{"X-Demo-Action":"clear-history"},signal:AbortSignal.timeout(30000)});
    if(!response.ok)throw new Error(response.status===409 ? "有图片正在上传或处理，请稍后再试。" : "清除失败，请检查服务和图片文件是否被占用后重试。");
    const result=await response.json();
    historyRevision=result.historyRevision;query="";$("search").value="";resetHistoryView();photoCount=0;
    for(const key of ["photos","responded","processing","failed"])$("stat-"+key).textContent="0";
    $("clear-dialog").close();
    toast(`已清除 ${result.deletedCaptures} 条记录及对应原图、响应`);
    if(result.cleanupPending)toast("记录已清除；部分原图暂被占用，将在服务下次启动时清理。",7000);
  } catch(error) {
    $("clear-error").textContent=error.name==="TimeoutError" || error.name==="TypeError" ? "未收到清除结果，请刷新记录确认后再试。" : error.message;
    $("clear-error").hidden=false;
  } finally {
    clearing=false;$("confirm-clear").disabled=false;$("cancel-clear").disabled=false;
    $("confirm-clear").textContent="确认清除";$("clear-history").disabled=photoCount===0;refresh();
  }
}
async function copy(value) {
  try {await navigator.clipboard.writeText(value);toast("已复制到剪贴板");} catch {toast("复制失败，请手动选中内容复制");}
}
function toast(message,ms=2200) {$("toast").textContent=message;$("toast").hidden=false;clearTimeout(toastTimer);toastTimer=setTimeout(()=>$("toast").hidden=true,ms);}
$("capture-image").addEventListener("load",()=>{$("image-dimensions").textContent=`${$("capture-image").naturalWidth} × ${$("capture-image").naturalHeight} · JPEG`;renderOverlay();});
$("capture-image").addEventListener("error",()=>{$("image-error").hidden=false;$("capture-image").hidden=true;renderOverlay();});
$("overlay-toggle").addEventListener("change",renderOverlay);
$("refresh").addEventListener("click",()=>{refresh();window.poseMonitor?.refresh();});
$("clear-history").addEventListener("click",()=>{$("clear-error").hidden=true;$("clear-dialog").showModal();});
$("cancel-clear").addEventListener("click",()=>$("clear-dialog").close());
$("confirm-clear").addEventListener("click",clearHistory);
$("clear-dialog").addEventListener("cancel",event=>{if(clearing)event.preventDefault();});
$("load-more").addEventListener("click",()=>refresh(true));
$("follow-latest").addEventListener("click",()=>{following=true;syncFollowing();refresh();});
$("auto-toggle").addEventListener("click",()=>{automatic=!automatic;$("auto-toggle").setAttribute("aria-pressed",String(automatic));$("auto-toggle").textContent="自动刷新 · "+(automatic?"开":"关");window.poseMonitor?.setAutomatic(automatic);if(automatic)refresh();});
$("search").addEventListener("input",()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{query=$("search").value.trim();generation++;captures=new Map();selectedId=null;current=null;selectedAttemptId=null;nextBefore=null;following=true;$("capture-view").hidden=true;syncFollowing();renderList();refresh();},250);});
$("copy-id").addEventListener("click",()=>{if(current)copy(current.captureId);});
$("copy-json").addEventListener("click",()=>copy($("response-json").textContent));
syncFollowing();refresh();
setInterval(()=>{if(automatic && !document.hidden)refresh();},1000);
document.addEventListener("visibilitychange",()=>{if(!document.hidden && automatic)refresh();});
