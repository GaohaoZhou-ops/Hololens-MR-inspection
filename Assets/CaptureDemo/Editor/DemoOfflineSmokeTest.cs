using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HoloLensCaptureDemo.Editor
{
    [InitializeOnLoad]
    public static class DemoOfflineSmokeTest
    {
        private const string Flag = "HoloLensCaptureDemo.OfflineSmoke";
        private static int stage;
        private static double started;
        private static CaptureController controller;
        private static AnchoredLabel firstLabel, secondLabel;
        private static Vector3 firstPoint, secondPoint;
        private static string firstId;
        private static double[] intervals;
        private static string ControlPath { get { return Path.Combine(DemoBuild.Root, "logs", "offline-smoke-control.json"); } }

        static DemoOfflineSmokeTest() { EditorApplication.playModeStateChanged += OnPlayMode; }

        public static void Begin()
        {
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_DISABLE_POSE", "1");
            PhotoQueueChecks.Run();
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_OUTBOX_DIR", Path.Combine(DemoBuild.Root, "logs", "offline-smoke-outbox-" + Guid.NewGuid().ToString("N")));
            File.WriteAllText(ControlPath, "{\"mode\":\"offline\"}");
            SessionState.SetBool(Flag, true);
            EditorApplication.isPlaying = true;
        }

        private static void OnPlayMode(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Flag, false) || state != PlayModeStateChange.EnteredPlayMode) return;
            stage = 0; started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup - started > 100) throw new Exception("Offline queue round trip timed out.");
                if (controller == null) controller = UnityEngine.Object.FindObjectOfType<CaptureController>();
                if (controller == null) return;
                if (!string.IsNullOrEmpty(controller.LastError)) throw new Exception(controller.LastError);
                if (!controller.IsReady) return;
                var camera = Camera.main;
                if (stage == 0)
                {
                    controller.Config.serverUrl = "http://127.0.0.1:8767";
                    controller.Config.apiToken = "offline-test-token-no-production-access";
                    controller.Config.requestTimeoutSeconds = 3;
                    controller.TriggerCapture(); stage = 1;
                }
                else if (stage == 1 && controller.PendingCount == 1 && !controller.IsCapturing)
                {
                    firstLabel = controller.PendingLabel; firstPoint = firstLabel.transform.position;
                    firstId = new PendingPhotoStore(controller.QueueDirectory, 20, 67108864).Items[0].captureId;
                    camera.transform.position = new Vector3(0.8f, 0.1f, 0f);
                    camera.transform.rotation = Quaternion.Euler(0f, 25f, 0f);
                    controller.TriggerCapture(); stage = 2;
                }
                else if (stage == 2 && controller.PendingCount == 2 && !controller.IsCapturing)
                {
                    secondLabel = UnityEngine.Object.FindObjectsOfType<AnchoredLabel>().First(l => l != firstLabel);
                    secondPoint = secondLabel.transform.position;
                    var recovered = new PendingPhotoStore(controller.QueueDirectory, 20, 67108864);
                    if (recovered.Items.Count != 2 || recovered.Items[0].captureId != firstId) throw new Exception("Offline capture was not durable.");
                    camera.transform.position = new Vector3(-0.5f, 0f, 0f); camera.transform.rotation = Quaternion.identity;
                    stage = 3;
                }
                else if (stage == 3 && controller.ProbeCount >= 3)
                {
                    var events = Events();
                    var probes = events.Where(e => e.kind == "health").Select(e => e.time).ToArray();
                    if (probes.Length < 3) return;
                    intervals = probes.Skip(1).Select((t, i) => t - probes[i]).ToArray();
                    if (events.Any(e => e.kind == "upload") || intervals.Any(d => d < 1.8 || d > 3.5))
                        throw new Exception("Offline probing cadence or photo suppression failed.");
                    if (controller.Labels.Count != 0 || Vector3.Distance(firstLabel.transform.position, firstPoint) > 0.001f ||
                        Vector3.Distance(secondLabel.transform.position, secondPoint) > 0.001f)
                        throw new Exception("Queued anchors moved or showed a premature result.");
                    File.WriteAllText(ControlPath, "{\"mode\":\"online-drop-first\"}");
                    stage = 4;
                }
                else if (stage == 4 && !controller.IsBusy && controller.Labels.Count == 2)
                {
                    var uploads = Events().Where(e => e.kind == "upload").ToArray();
                    if (uploads.Length != 3 || uploads[0].captureId != uploads[1].captureId || uploads[0].captureId == uploads[2].captureId ||
                        controller.Labels[0].Serial != "HL1-000001" || controller.Labels[1].Serial != "HL1-000002" ||
                        Vector3.Distance(firstPoint, controller.Labels[0].transform.position) > 0.001f ||
                        Vector3.Distance(secondPoint, controller.Labels[1].transform.position) > 0.001f ||
                        Directory.GetFiles(controller.QueueDirectory, "*.jpg", SearchOption.AllDirectories).Length != 0)
                        throw new Exception("Recovery, idempotent retry, FIFO order, anchor stability or cleanup failed.");
                    Finish(true, "Offline captures persisted; 2-second probes sent no photos; recovery retried a lost response with the same ID and retained both capture-time anchors.");
                }
            }
            catch (Exception error) { Finish(false, error.Message); }
        }

        private static TestEvent[] Events()
        {
            return File.ReadAllLines(Path.Combine(DemoBuild.Root, "logs", "offline-smoke-events.jsonl"))
                .Where(line => !string.IsNullOrWhiteSpace(line)).Select(JsonUtility.FromJson<TestEvent>).ToArray();
        }

        private static void Finish(bool passed, string message)
        {
            EditorApplication.update -= Tick; SessionState.SetBool(Flag, false);
            var result = new Result { passed = passed, message = message, verifiedAt = DateTime.UtcNow.ToString("o"),
                probeIntervalsSeconds = intervals, uploadAttempts = controller == null ? 0 : controller.UploadAttempts,
                queueDirectory = controller == null ? null : controller.QueueDirectory };
            File.WriteAllText(Path.Combine(DemoBuild.Root, "logs", "offline-smoke.json"), JsonUtility.ToJson(result, true));
            File.WriteAllText(ControlPath, "{\"mode\":\"stop\"}");
            Debug.Log("CAPTURE_DEMO_OFFLINE_SMOKE " + passed + " " + message);
            EditorApplication.Exit(passed ? 0 : 1);
        }

        [Serializable] private sealed class TestEvent { public string kind; public double time; public string captureId; }
        [Serializable] private sealed class Result { public bool passed; public string message; public string verifiedAt; public double[] probeIntervalsSeconds; public int uploadAttempts; public string queueDirectory; }
    }
}
