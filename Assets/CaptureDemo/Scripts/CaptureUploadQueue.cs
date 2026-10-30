using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace HoloLensCaptureDemo
{
    // Runs on Unity's main thread. Only one request is active, including health probes.
    public sealed class CaptureUploadQueue : IDisposable
    {
        public const float ProbeIntervalSeconds = 2f;
        public const int ProbeTimeoutSeconds = 2;
        public string State { get; private set; }
        public string Detail { get; private set; }
        public bool IsPaused { get; private set; }
        public int ProbeCount { get; private set; }
        public int UploadAttempts { get; private set; }
        private readonly PendingPhotoStore store;
        private readonly DemoConfig config;
        private readonly Action<PendingPhoto, CaptureResponse> uploaded;
        private readonly Action changed;
        private UnityWebRequest activeRequest;
        private bool disposed;
        private bool needsProbe = true;
        private float nextProbeAt;

        [Serializable]
        private sealed class Health { public string service; public string status; }

        public CaptureUploadQueue(PendingPhotoStore store, DemoConfig config, Action<PendingPhoto, CaptureResponse> uploaded, Action changed)
        {
            this.store = store; this.config = config; this.uploaded = uploaded; this.changed = changed;
            State = "idle"; Detail = "";
        }

        public void Wake()
        {
            IsPaused = false; needsProbe = true; nextProbeAt = 0f;
            SetState(store.Items.Count > 0 ? "checking" : "idle", "");
        }

        public IEnumerator Run()
        {
            while (!disposed)
            {
                if (store.Items.Count == 0)
                {
                    needsProbe = true;
                    SetState("idle", "");
                    yield return null;
                    continue;
                }
                if (IsPaused) { yield return null; continue; }
                if (needsProbe)
                {
                    if (Time.realtimeSinceStartup < nextProbeAt) { yield return null; continue; }
                    nextProbeAt = Time.realtimeSinceStartup + ProbeIntervalSeconds;
                    ProbeCount++;
                    using (var probe = UnityWebRequest.Get(config.serverUrl.TrimEnd('/') + "/health"))
                    {
                        activeRequest = probe;
                        probe.timeout = ProbeTimeoutSeconds;
                        if (State != "offline") SetState("checking", "Checking PC");
                        yield return probe.SendWebRequest();
                        activeRequest = null;
                        bool healthy = false;
                        if (!probe.isNetworkError && probe.responseCode == 200)
                        {
                            try
                            {
                                var health = JsonUtility.FromJson<Health>(probe.downloadHandler.text);
                                healthy = health != null && health.service == "HoloLensCaptureDemo" && health.status == "ok";
                            }
                            catch (Exception) { }
                        }
                        if (!healthy)
                        {
                            SetState("offline", "PC unavailable; checking every 2s");
                            continue;
                        }
                    }
                    needsProbe = false;
                }
                var photo = store.Items[0];
                byte[] image = null;
                string fileError = null;
                try { image = store.ReadImage(photo); }
                catch (Exception error) { fileError = error.Message; }
                if (fileError != null) { Pause(fileError); continue; }
                UploadAttempts++;
                SetState("uploading", "Sending oldest queued photo");
                using (var request = new UnityWebRequest(config.serverUrl.TrimEnd('/') + "/api/captures", "POST"))
                {
                    activeRequest = request;
                    request.uploadHandler = new UploadHandlerRaw(image);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "image/jpeg");
                    request.SetRequestHeader("Authorization", "Bearer " + config.apiToken);
                    request.SetRequestHeader("X-Capture-Id", photo.captureId);
                    request.SetRequestHeader("X-Captured-At", photo.capturedAt);
                    request.chunkedTransfer = false;
                    request.timeout = config.requestTimeoutSeconds;
                    yield return request.SendWebRequest();
                    activeRequest = null;
                    var code = request.responseCode;
                    if (request.isNetworkError || code == 408 || code == 429 || code >= 500)
                    {
                        needsProbe = true; nextProbeAt = Time.realtimeSinceStartup + ProbeIntervalSeconds;
                        SetState("offline", "Upload interrupted; photo kept; checking every 2s");
                        continue;
                    }
                    if (code != 200)
                    {
                        Pause("HTTP " + code + "; check PC settings. Air tap to retry queued photos.");
                        continue;
                    }
                    CaptureResponse response = null;
                    string errorMessage = null;
                    try
                    {
                        response = JsonUtility.FromJson<CaptureResponse>(request.downloadHandler.text);
                        if (response == null || response.captureId != photo.captureId ||
                            string.IsNullOrEmpty(response.serial) || !Regex.IsMatch(response.serial, "^HL1-[0-9]{6,12}$") ||
                            Mathf.Abs(response.centerX - 0.5f) > 0.0001f || Mathf.Abs(response.centerY - 0.5f) > 0.0001f ||
                            float.IsNaN(response.centerX) || float.IsNaN(response.centerY))
                            throw new InvalidDataException("Unexpected response. Photo retained; Air tap to retry.");
                        store.Complete(photo);
                    }
                    catch (Exception error) { errorMessage = error.Message; }
                    if (errorMessage != null) { Pause(errorMessage); continue; }
                    uploaded(photo, response);
                }
            }
        }

        private void Pause(string message)
        {
            IsPaused = true;
            SetState("paused", message);
        }

        private void SetState(string state, string detail)
        {
            if (State == state && Detail == detail) return;
            State = state; Detail = detail;
            Debug.Log("OUTBOX " + state + " pending=" + store.Items.Count + " " + detail);
            changed();
        }

        public void Dispose()
        {
            disposed = true;
            if (activeRequest != null) { activeRequest.Abort(); activeRequest.Dispose(); activeRequest = null; }
        }
    }
}
