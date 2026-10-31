"use strict";
(() => {
  const el = id => document.getElementById(id);
  const key = "hololens-system-live";
  let session = null, generation = 0;
  const validIp = value => /^(?:\d{1,3}\.){3}\d{1,3}$/.test(value) && value.split(".").every(p => String(Number(p)) === p && Number(p) <= 255) && !/^(0|127|22[4-9]|23\d|24\d|25[0-5])\./.test(value);

  function settings() {
    const address = el("live-address").value.trim();
    if (!validIp(address)) throw new Error("请输入头显的 IPv4 地址，例如 192.168.100.61；不要填写网址、端口或账号密码。");
    const quality = el("live-quality").value;
    if (!["low", "med", "high"].includes(quality)) throw new Error("请选择有效的画质档位。");
    return {address, quality, holograms: el("live-holograms").checked};
  }
  function save(value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch (_) { /* Preferences are optional. */ } }
  function failure(error) { el("live-error").textContent = error.message; el("live-error").hidden = false; }
  function display(state, title, detail = "") {
    el("live-player").dataset.state = state;
    el("live-player-state").textContent = title;
    el("live-placeholder").hidden = state === "playing";
    el("live-placeholder-title").textContent = title;
    el("live-placeholder-detail").textContent = detail;
    el("live-play").hidden = true;
    el("live-fullscreen").disabled = !session && !document.fullscreenElement;
  }
  function sync() {
    el("live-stop").disabled = !session;
    el("live-start").textContent = session ? "重新开始直播" : "开始直播";
  }
  function unload(current) {
    if (!current) return;
    clearTimeout(current.mediaTimer);
    const video = current.video;
    current.video = null;
    if (video) {
      // pause() alone keeps the HTTP stream/camera open. load() with no src
      // aborts the media resource before the browser acknowledges the handoff.
      video.pause(); video.removeAttribute("src"); video.load(); video.remove();
    }
    el("live-resolution").textContent = "";
  }
  function stop(message = "直播已停止。可以继续在 App 中合拢拍照。") {
    generation++;
    const previous = session; session = null;
    unload(previous);
    if (previous) {
      previous.abort?.abort();
      fetch("/api/live-preview", {method:"POST",headers:{"Content-Type":"application/json","X-Demo-Action":"live-preview"},
        body:JSON.stringify({previewId:previous.id,action:"stop"}),keepalive:true}).catch(()=>{});
    }
    sync(); display("idle", "直播未开启", "点击“开始直播”，画面将在这里显示。");
    el("live-status").textContent = message;
  }
  function mediaFailure(current, video, message) {
    if (session !== current || current.video !== video) return;
    stop("未能继续播放，已断开视频连接。检查后可重新开始。");
    display("error", "暂时无法播放", message);
    failure(new Error(message));
  }
  function startMedia(current, resuming) {
    const video = document.createElement("video");
    video.id = "live-video";
    video.muted = true; video.defaultMuted = true;
    video.autoplay = true; video.playsInline = true;
    video.preload = "none"; video.disablePictureInPicture = true;
    video.setAttribute("disableRemotePlayback", "");
    // Deliberately omit crossorigin: Portal does not provide CORS headers.
    // Ordinary media embedding can use the browser's existing HTTP auth cache.
    current.video = video;
    const active = () => session === current && current.video === video;
    const loading = () => {
      if (!active()) return;
      display("loading", resuming ? "正在恢复直播" : "正在连接", "等待头显传来画面…");
      clearTimeout(current.mediaTimer);
      current.mediaTimer = setTimeout(() => mediaFailure(current, video,
        "等待画面超时。请确认头显保持唤醒、地址正确，并已在同一浏览器登录设备门户。"), 15000);
    };
    video.addEventListener("playing", () => {
      if (!active()) return;
      clearTimeout(current.mediaTimer);
      display("playing", "直播中");
      el("live-resolution").textContent = video.videoWidth + " × " + video.videoHeight;
      el("live-status").textContent = resuming ? "拍照结束，直播画面已自动恢复。" : "已收到直播画面。在 App 内合拢一次即可拍照，直播会自动暂停和恢复。";
    });
    video.addEventListener("waiting", loading);
    video.addEventListener("error", () => mediaFailure(current, video,
      "无法读取视频。请先点“设备门户登录”，完成登录及证书确认后回到本页重试，并关闭其他直播。"));
    video.addEventListener("ended", () => mediaFailure(current, video, "头显结束了视频流，请保持设备唤醒后重新开始。"));
    el("live-video-slot").append(video);
    loading();
    el("live-status").textContent = resuming ? "拍照结束，正在重新连接直播…" : "正在连接头显，等待视频画面…";
    // A fresh URL prevents the media cache from replaying an earlier connection
    // when a new element resumes the same live endpoint after a photograph.
    video.src = current.url + "&streamSession=" + crypto.randomUUID();
    play(current, video);
  }
  function play(current, video) {
    video.play().catch(error => {
      if (session !== current || current.video !== video || error.name === "AbortError") return;
      if (error.name === "NotAllowedError") {
        clearTimeout(current.mediaTimer);
        display("blocked", "浏览器暂停了自动播放", "点击下面的按钮继续播放。");
        el("live-play").hidden = false;
      } else mediaFailure(current, video, "播放失败。请检查设备门户登录、证书和头显连接后重新开始。");
    });
  }
  async function coordinate(current, version) {
    let action = "register", observed = "", pausedLeaseId = "";
    try {
      while (session === current && generation === version) {
        current.abort = new AbortController();
        const abort = current.abort;
        const timer = setTimeout(() => abort.abort(), 6000);
        let response, result;
        try {
          response = await fetch("/api/live-preview", {method:"POST",headers:{"Content-Type":"application/json","X-Demo-Action":"live-preview"},signal:abort.signal,
            body:JSON.stringify({previewId:current.id,deviceAddress:current.address,action,observed,pausedLeaseId,waitSeconds:action === "poll" ? 2 : 0})});
          result = await response.json();
        } finally {clearTimeout(timer);}
        if (session !== current || generation !== version) return;
        if (!response.ok || result.previewId !== current.id || !["play","pause"].includes(result.action)) throw new Error("直播协调服务未连接或已重启，请重新开始直播。");
        action = "poll";
        if (result.action === "pause") {
          if (pausedLeaseId !== result.leaseId) {
            unload(current);
            pausedLeaseId = result.leaseId;
            display("capturing", "正在拍照", "直播已自动暂停，拍照结束后会恢复。");
            el("live-status").textContent = "头显正在拍照，直播已自动暂停。无需操作网页。";
          }
          observed = result.leaseId;
        } else {
          if (!current.video) startMedia(current, Boolean(pausedLeaseId));
          pausedLeaseId = ""; observed = "play";
        }
      }
    } catch (error) {
      if (session !== current || generation !== version) return;
      stop("协调连接中断，已停止直播以释放相机。请重新开始直播。");
      failure(error);
    }
  }
  el("live-login").addEventListener("click", () => {
    try {
      el("live-error").hidden = true;
      const value = settings(); save(value);
      const child = window.open("about:blank", "_blank");
      if (!child) throw new Error("浏览器阻止了设备门户登录页。请允许此登录页打开后重试。");
      child.opener = null; child.location.replace(`https://${value.address}/`);
      el("live-status").textContent = "请在设备门户完成登录，然后回到本页开始直播。";
    } catch (error) { failure(error); }
  });
  el("live-start").addEventListener("click", () => {
    try {
      el("live-error").hidden = true;
      const value = settings(); save(value);
      const query = new URLSearchParams({holo:String(value.holograms),pv:"true",mic:"false",loopback:"false"});
      stop();
      session = {id:crypto.randomUUID().replaceAll("-",""),address:value.address,video:null,
        url:`https://${value.address}/api/holographic/stream/live_${value.quality}.mp4?${query}`};
      sync(); display("loading", "正在连接", "正在准备直播…");
      el("live-status").textContent = "正在连接自动拍照协调服务…";
      coordinate(session, generation);
    } catch (error) { failure(error); sync(); }
  });
  el("live-stop").addEventListener("click", () => stop());
  el("live-play").addEventListener("click", () => { if (session?.video) play(session, session.video); });
  el("live-fullscreen").addEventListener("click", async () => {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else await el("live-player").requestFullscreen();
    } catch (_) { failure(new Error("此浏览器暂不支持全屏播放，可以继续在本页观看。")); }
  });
  document.addEventListener("fullscreenchange", () => {
    el("live-fullscreen").textContent = document.fullscreenElement ? "退出全屏" : "全屏";
    el("live-fullscreen").disabled = !session && !document.fullscreenElement;
  });
  for (const name of ["live-address", "live-quality", "live-holograms"]) el(name).addEventListener("change", () => {
    el("live-error").hidden = true;
    if (session) el("live-status").textContent = "选项已修改。点击“重新开始直播”后应用。";
  });
  try {
    const value = JSON.parse(localStorage.getItem(key) || "{}");
    if (typeof value.address === "string" && validIp(value.address)) el("live-address").value = value.address;
    if (["low","med","high"].includes(value.quality)) el("live-quality").value = value.quality;
    if (typeof value.holograms === "boolean") el("live-holograms").checked = value.holograms;
  } catch (_) { /* Ignore corrupted preferences. */ }
  const address = new URLSearchParams(location.search).get("device");
  if (address && validIp(address)) el("live-address").value = address;
  function tick() {
    const now = new Date();
    el("live-clock").textContent = now.toLocaleTimeString("zh-CN", {hour12:false}) + "." + Math.floor(now.getMilliseconds() / 100);
  }
  tick(); setInterval(tick, 100);
  window.addEventListener("pagehide", () => stop());
})();
