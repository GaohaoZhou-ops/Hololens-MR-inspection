using System;
using System.IO;
using System.Linq;
using System.Net;
using UnityEditor;
using UnityEngine;

namespace HoloLensCaptureDemo.Editor
{
    [InitializeOnLoad]
    public static class DemoPoseSmokeTest
    {
        private const string Flag = "HoloLensCaptureDemo.PoseSmoke";
        private static double started, pauseAt;
        private static int stage, beforePause;
        private static CaptureController controller;
        private static PoseTelemetry telemetry;
        private static string firstSession;
        private static float[] intervals;
        private static string Control { get { return Path.Combine(DemoBuild.Root, "logs/pose-smoke-control.json"); } }
        static DemoPoseSmokeTest() { EditorApplication.playModeStateChanged += OnPlay; }

        public static void Begin()
        {
            StorageChecks();
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_DISABLE_POSE", null);
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_OUTBOX_DIR", Path.Combine(DemoBuild.Root, "logs/pose-smoke-" + Guid.NewGuid().ToString("N"), "photos"));
            File.WriteAllText(Control, "{\"mode\":\"offline\"}");
            SessionState.SetBool(Flag, true); EditorApplication.isPlaying = true;
        }

        private static void OnPlay(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Flag, false) || state != PlayModeStateChange.EnteredPlayMode) return;
            started = EditorApplication.timeSinceStartup; stage = 0; EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup - started > 90) throw new Exception("Pose smoke timed out.");
                if (controller == null) controller = UnityEngine.Object.FindObjectOfType<CaptureController>();
                if (controller == null || !controller.IsReady) return;
                if (telemetry == null) telemetry = UnityEngine.Object.FindObjectOfType<PoseTelemetry>();
                if (telemetry == null) throw new Exception("Pose telemetry was not initialized.");
                if (telemetry.LastError != null) throw new Exception(telemetry.LastError);
                var camera = Camera.main;
                if (stage == 0)
                {
                    controller.Config.serverUrl = "http://127.0.0.1:8768";
                    controller.Config.apiToken = "pose-test-token-not-production";
                    camera.transform.SetPositionAndRotation(new Vector3(2, 1, 3), Quaternion.Euler(0, 90, 0));
                    firstSession = telemetry.SessionId; stage = 1;
                }
                else if (stage == 1 && telemetry.SampleCount == 1)
                {
                    camera.transform.SetPositionAndRotation(new Vector3(3, 1.2f, 3), Quaternion.Euler(10, 135, 20)); stage = 2;
                }
                else if (stage == 2 && telemetry.SampleCount == 2)
                {
                    var p = telemetry.Latest;
                    Require(Mathf.Abs(p.x) < .001f && Mathf.Abs(p.y - .2f) < .001f && Mathf.Abs(p.z - 1) < .001f, "Pose frame must use startup position and heading.");
                    var expected = Quaternion.Inverse(Quaternion.Euler(0, 90, 0)) * camera.transform.rotation;
                    Require(Quaternion.Angle(expected, new Quaternion(p.qx, p.qy, p.qz, p.qw)) < .01f, "Relative head orientation changed.");
                    camera.transform.position = new Vector3(3, 1.3f, 2); stage = 3;
                }
                else if (stage == 3 && telemetry.SampleCount >= 5)
                {
                    var events = Events();
                    var probes = events.Where(e => e.kind == "health").ToArray();
                    if (probes.Length < 3) return;
                    Require(!events.Any(e => e.kind == "pose_ack"), "Pose uploads occurred while offline.");
                    Require(probes.Skip(1).Select((e,i) => e.time-probes[i].time).All(delta => delta > 1.8 && delta < 3.2), "Offline probes were not 2 seconds apart.");
                    var restored = new PoseOutbox(controller.QueueDirectory + "-poses");
                    Require(restored.Count == telemetry.SampleCount && restored.Oldest().samples[0].sequence == 0, "Pose samples did not survive disk reload.");
                    File.WriteAllText(Control, "{\"mode\":\"online-drop-first\"}"); stage = 4;
                }
                else if (stage == 4 && telemetry.PendingCount == 0 && telemetry.SampleCount >= 7)
                {
                    var page = Read(firstSession);
                    Require(page.samples.Length == telemetry.SampleCount, "Recovery lost or duplicated samples.");
                    intervals = page.samples.Skip(1).Select((p,i) => p.elapsed-page.samples[i].elapsed).ToArray();
                    Require(intervals.All(delta => delta > .8 && delta < 1.3), "Sampling cadence was not 1 Hz.");
                    Require(Mathf.Abs(page.samples[0].x)<.001f && Mathf.Abs(page.samples[1].z-1)<.001f && Mathf.Abs(page.samples[2].x-1)<.001f, "Recovered coordinates changed.");
                    Require(Events().Any(e => e.kind == "dropped_response"), "Lost-response recovery was not exercised.");
                    telemetry.enabled = false; beforePause = telemetry.SampleCount; pauseAt = EditorApplication.timeSinceStartup; stage = 5;
                }
                else if (stage == 5 && EditorApplication.timeSinceStartup-pauseAt > 3.1)
                { telemetry.enabled = true; stage = 6; }
                else if (stage == 6 && telemetry.SampleCount > beforePause && telemetry.PendingCount == 0)
                {
                    var page = Read(firstSession);
                    Require(page.samples.Last().segment > page.samples[beforePause-1].segment, "Suspension did not break the trajectory.");
                    UnityEngine.Object.DestroyImmediate(telemetry);
                    telemetry = controller.gameObject.AddComponent<PoseTelemetry>();
                    telemetry.Initialize(controller.Config, controller.QueueDirectory + "-poses");
                    Require(telemetry.SessionId != firstSession, "New app session reused the old frame."); stage = 7;
                }
                else if (stage == 7 && telemetry.SampleCount >= 2 && telemetry.PendingCount == 0)
                {
                    var page = Read(telemetry.SessionId);
                    Require(page.samples.Length >= 2 && Mathf.Abs(page.samples[0].x) < .001f && Mathf.Abs(page.samples[0].y) < .001f && Mathf.Abs(page.samples[0].z) < .001f, "New session did not reset its spatial origin.");
                    Finish(true, "1 Hz poses, relative position/quaternion, durable queue reload, offline recovery, lost-response deduplication, suspension gaps and separate session origins verified.");
                }
            }
            catch (Exception error) { Finish(false, error.ToString()); }
        }

        private static Page Read(string id)
        {
            using (var client = new WebClient()) return JsonUtility.FromJson<Page>(client.DownloadString("http://127.0.0.1:8768/api/pose-sessions/" + id));
        }
        private static Event[] Events()
        {
            using (var stream = new FileStream(Path.Combine(DemoBuild.Root, "logs/pose-smoke-events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                var text = reader.ReadToEnd(); var end = text.LastIndexOf('\n');
                return end < 0 ? new Event[0] : text.Substring(0, end).Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).Select(JsonUtility.FromJson<Event>).ToArray();
            }
        }
        private static void Require(bool value, string error) { if (!value) throw new Exception(error); }
        private static void StorageChecks()
        {
            var directory = Path.Combine(DemoBuild.Root, "logs/pose-store-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new PoseOutbox(directory, 2);
                var session = new PoseSession { sessionId = Guid.NewGuid().ToString("N"), startedAt = DateTime.UtcNow.ToString("o"), deviceName = "test", clientKind = "editor" };
                store.Append(session, new PoseSample { sequence = 0, sampledAt = session.startedAt });
                var first = store.Oldest();
                store.Append(session, new PoseSample { sequence = 1, sampledAt = session.startedAt });
                Require(!store.Append(session, new PoseSample { sequence = 2 }), "Queue overflow replaced old samples.");
                store.Acknowledge(first);
                Require(store.Count == 1, "ACK removed a newly appended sample.");
                var path = Directory.GetFiles(directory, "*.jsonl")[0]; File.AppendAllText(path, "{torn");
                store = new PoseOutbox(directory);
                Require(store.Count == 2, "A crash must replay uncheckpointed samples and ignore a torn final append.");
                store.Acknowledge(store.Oldest());
                Require(Directory.GetFiles(directory).Length == 0, "Acknowledged segment was not removed.");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private static void Finish(bool passed, string message)
        {
            EditorApplication.update -= Tick; SessionState.SetBool(Flag, false);
            File.WriteAllText(Path.Combine(DemoBuild.Root, "logs/pose-smoke.json"), JsonUtility.ToJson(new Result { passed = passed, message = message, intervals = intervals, sessionId = firstSession, nextSessionId = telemetry == null ? null : telemetry.SessionId, verifiedAt = DateTime.UtcNow.ToString("o") }, true));
            // Keep the isolated fixture online for the browser checks; the wrapper stops it.
            File.WriteAllText(Control, "{\"mode\":\"online\"}");
            Debug.Log("CAPTURE_DEMO_POSE_SMOKE " + passed + " " + message); EditorApplication.Exit(passed ? 0 : 1);
        }
        [Serializable] private sealed class Event { public string kind; public double time; }
        [Serializable] private sealed class Page { public PoseSample[] samples; }
        [Serializable] private sealed class Result { public bool passed; public string message, verifiedAt, sessionId, nextSessionId; public float[] intervals; }
    }
}
