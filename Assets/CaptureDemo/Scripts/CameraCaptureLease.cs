using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace HoloLensCaptureDemo
{
    public sealed class CameraCaptureLease : IDisposable
    {
        [Serializable] private sealed class Body { public string leaseId, action; }
        [Serializable] private sealed class Reply { public string leaseId; public bool ready, released; public int previewCount, settleMs; }
        private readonly DemoConfig config;
        private readonly string id = Guid.NewGuid().ToString("N");
        private UnityWebRequest active;
        private bool attempted;
        public bool MayCapture { get; private set; }
        public string Error { get; private set; }
        public CameraCaptureLease(DemoConfig settings) { config = settings; }

        public IEnumerator Acquire(Action<string> status)
        {
            var deadline = Time.realtimeSinceStartup + 6;
            bool coordinated = false;
            while (Time.realtimeSinceStartup < deadline)
            {
                using (var request = Request("acquire", 2))
                {
                    attempted = true; active = request;
                    yield return request.SendWebRequest(); active = null;
                    if (request.isNetworkError || request.responseCode == 404)
                    {
                        // Retain offline capture support and compatibility with older receivers.
                        // A known managed preview, however, must acknowledge its pause first.
                        if (!coordinated) { MayCapture = true; yield break; }
                        break;
                    }
                    if (request.responseCode != 200)
                    { Error = "电脑未能协调摄像头，请检查连接或稍后再试。HTTP " + request.responseCode; yield break; }
                    Reply reply = null;
                    try { reply = JsonUtility.FromJson<Reply>(request.downloadHandler.text); } catch (Exception) { }
                    if (reply == null || reply.leaseId != id) { Error = "摄像头协调回执无效，未拍照。"; yield break; }
                    coordinated = true;
                    if (reply.ready)
                    {
                        if (reply.previewCount > 0)
                        {
                            status("直播已暂停，正在释放摄像头…");
                            yield return new WaitForSecondsRealtime(Mathf.Clamp(reply.settleMs, 0, 1500) / 1000f);
                        }
                        MayCapture = true; yield break;
                    }
                    status("已识别合拢手势，正在自动暂停直播…\n无需再次合拢或点击网页");
                }
                yield return new WaitForSecondsRealtime(.15f);
            }
            Error = "直播暂停尚未确认，未拍照。请保持系统直播控制页打开后重试。";
        }

        public IEnumerator Release()
        {
            if (!attempted) yield break;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using (var request = Request("release", 1))
                {
                    active = request; yield return request.SendWebRequest(); active = null;
                    if (!request.isNetworkError && (request.responseCode == 200 || request.responseCode == 404)) yield break;
                }
            }
            Debug.LogWarning("Camera release could not reach the PC. The temporary live pause will expire automatically.");
        }

        private UnityWebRequest Request(string action, int timeout)
        {
            var request = new UnityWebRequest(config.serverUrl.TrimEnd('/') + "/api/camera-lease", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(new Body { leaseId = id, action = action })));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", "Bearer " + config.apiToken);
            request.SetRequestHeader("Content-Type", "application/json");
            request.chunkedTransfer = false; request.timeout = timeout;
            return request;
        }
        public void Dispose() { if (active != null) { active.Abort(); active.Dispose(); active = null; } }
    }
}
