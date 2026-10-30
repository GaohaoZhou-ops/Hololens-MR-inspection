using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
#if UNITY_WSA && !UNITY_EDITOR
using UnityEngine.XR.WSA;
#endif

namespace HoloLensCaptureDemo
{
    public sealed class PoseTelemetry : MonoBehaviour
    {
        public string SessionId { get { return session == null ? null : session.sessionId; } }
        public int PendingCount { get { return outbox == null ? 0 : outbox.Count; } }
        public int SampleCount { get; private set; }
        public int DroppedSamples { get; private set; }
        public string State { get; private set; }
        public string LastError { get; private set; }
        public PoseSample Latest { get; private set; }
        private DemoConfig config;
        private PoseSession session;
        private PoseOutbox outbox;
        private GameObject origin;
        private float started, nextSample, lastSample = -1;
        private int segment;
        private bool previousTracked, paused, disposed;
        private UnityWebRequest request;
#if UNITY_WSA && !UNITY_EDITOR
        private WorldAnchor anchor;
#endif
        [Serializable] private sealed class Ack { public string sessionId; public int acceptedThrough = -1; }
        [Serializable] private sealed class Health { public string service; public string status; }

        public void Initialize(DemoConfig settings, string directory)
        {
            config = settings;
            started = Time.realtimeSinceStartup;
            nextSample = started + 1f;
            session = new PoseSession { sessionId = Guid.NewGuid().ToString("N"), startedAt = DateTime.UtcNow.ToString("o"),
                deviceName = SystemInfo.deviceName, clientKind = Application.isEditor ? "editor" : "hololens-uwp" };
            try { outbox = new PoseOutbox(directory); }
            catch (Exception error) { Fail(error.Message); return; }
#if UNITY_WSA && !UNITY_EDITOR
            WorldManager.OnPositionalLocatorStateChanged += TrackingChanged;
#endif
            State = "ready";
            StartCoroutine(Send());
        }

        private void LateUpdate()
        {
            if (outbox == null || paused || disposed || Time.realtimeSinceStartup < nextSample || LastError != null) return;
            var now = Time.realtimeSinceStartup;
            // Do not fabricate missed poses after a stall/suspension, or burst to catch up.
            nextSample += 1f;
            if (nextSample <= now) nextSample = now + 1f;
            var camera = Camera.main;
            bool tracked = camera != null;
            string tracking = Application.isEditor ? "Editor" : "Unavailable";
#if UNITY_WSA && !UNITY_EDITOR
            tracking = WorldManager.state.ToString();
            tracked = tracked && WorldManager.state == PositionalLocatorState.Active;
#endif
            if (tracked && origin == null)
            {
                origin = new GameObject("Trajectory session origin");
                origin.transform.position = camera.transform.position;
                var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);
                origin.transform.rotation = Quaternion.LookRotation(forward.sqrMagnitude < 0.001f ? Vector3.forward : forward, Vector3.up);
#if UNITY_WSA && !UNITY_EDITOR
                anchor = origin.AddComponent<WorldAnchor>();
#endif
            }
#if UNITY_WSA && !UNITY_EDITOR
            if (tracked && (anchor == null || !anchor.isLocated)) { tracked = false; tracking = "AnchorUnavailable"; }
#endif
            if (lastSample >= 0 && (now - lastSample > 2.5f || tracked != previousTracked)) segment++;
            var point = tracked ? origin.transform.InverseTransformPoint(camera.transform.position) : Vector3.zero;
            var rotation = tracked ? Quaternion.Inverse(origin.transform.rotation) * camera.transform.rotation : Quaternion.identity;
            var sample = new PoseSample { sequence = SampleCount++, segment = segment, elapsed = now - started,
                sampledAt = DateTime.UtcNow.ToString("o"), tracked = tracked, trackingState = tracking,
                x = point.x, y = point.y, z = point.z, qx = rotation.x, qy = rotation.y, qz = rotation.z, qw = rotation.w };
            Latest = sample; lastSample = now; previousTracked = tracked;
            try { if (!outbox.Append(session, sample)) DroppedSamples++; }
            catch (Exception error) { Fail(error.Message); }
        }

        private IEnumerator Send()
        {
            bool probeNeeded = true;
            float nextProbe = 0;
            while (!disposed)
            {
                if (paused || LastError != null || outbox.Count == 0) { yield return null; continue; }
                if (probeNeeded)
                {
                    if (Time.realtimeSinceStartup < nextProbe) { yield return null; continue; }
                    nextProbe = Time.realtimeSinceStartup + 2f;
                    using (var probe = UnityWebRequest.Get(config.serverUrl.TrimEnd('/') + "/health"))
                    {
                        request = probe; probe.timeout = 2;
                        yield return probe.SendWebRequest(); request = null;
                        bool healthy = false;
                        if (!probe.isNetworkError && probe.responseCode == 200)
                        {
                            try { var h = JsonUtility.FromJson<Health>(probe.downloadHandler.text); healthy = h.service == "HoloLensCaptureDemo" && h.status == "ok"; }
                            catch (Exception) { }
                        }
                        if (!healthy) { State = "offline"; continue; }
                    }
                    probeNeeded = false;
                }
                var batch = outbox.Oldest();
                using (var upload = new UnityWebRequest(config.serverUrl.TrimEnd('/') + "/api/poses", "POST"))
                {
                    request = upload; upload.timeout = 2; upload.chunkedTransfer = false;
                    upload.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(batch)));
                    upload.downloadHandler = new DownloadHandlerBuffer();
                    upload.SetRequestHeader("Content-Type", "application/json");
                    upload.SetRequestHeader("Authorization", "Bearer " + config.apiToken);
                    State = "sending";
                    yield return upload.SendWebRequest(); request = null;
                    if (upload.isNetworkError || upload.responseCode == 408 || upload.responseCode == 429 || upload.responseCode >= 500)
                    {
                        State = "offline"; probeNeeded = true; nextProbe = Time.realtimeSinceStartup + 2f; continue;
                    }
                    if (upload.responseCode != 200) { Fail("Pose HTTP " + upload.responseCode + "; update receiver/configuration, then reopen app. Samples kept."); continue; }
                    try
                    {
                        var ack = JsonUtility.FromJson<Ack>(upload.downloadHandler.text);
                        if (ack == null || ack.sessionId != batch.session.sessionId || ack.acceptedThrough != batch.samples[batch.samples.Length - 1].sequence)
                            throw new InvalidOperationException("Pose ACK mismatch. Samples kept.");
                        outbox.Acknowledge(batch); State = "online";
                    }
                    catch (Exception error) { Fail(error.Message); }
                }
                yield return null;
            }
        }

        private void Fail(string message) { LastError = message; State = "paused"; Debug.LogWarning("POSE " + message); }
        private void OnApplicationPause(bool value) { paused = value; }
#if UNITY_WSA && !UNITY_EDITOR
        private void TrackingChanged(PositionalLocatorState before, PositionalLocatorState after) { segment++; }
#endif
        private void OnDestroy()
        {
            disposed = true;
            if (request != null) { request.Abort(); request.Dispose(); request = null; }
#if UNITY_WSA && !UNITY_EDITOR
            WorldManager.OnPositionalLocatorStateChanged -= TrackingChanged;
#endif
            if (origin != null) Destroy(origin);
        }
    }
}
