using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HoloLensCaptureDemo.Editor
{
    [InitializeOnLoad]
    public static class DemoHistorySmokeTest
    {
        private const string Flag = "HoloLensCaptureDemo.HistorySmoke";
        private static int stage;
        private static double started, nextTick;
        private static CaptureController controller;
        private static string deviceId, workId, commandId, queuePath;
        private static Vector3 originalPoint;
        private static string Control { get { return Path.Combine(DemoBuild.Root, "logs/history-smoke-control.json"); } }
        [Serializable] private sealed class Command { public string commandId, deviceId, kind, workId; public string[] categories; }
        [Serializable] private sealed class Device { public string deviceId; public CommandResult command; }
        [Serializable] private sealed class CommandResult { public string commandId, status; }
        [Serializable] private sealed class Overview { public Device[] devices; }

        static DemoHistorySmokeTest() { EditorApplication.playModeStateChanged += OnPlay; }
        public static void Begin()
        {
            queuePath = Path.Combine(DemoBuild.Root, "logs/history-smoke-" + Guid.NewGuid().ToString("N"), "photos");
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_OUTBOX_DIR", queuePath);
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_SERVER_URL", "http://127.0.0.1:8769");
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_API_TOKEN", "history-test-token-not-production");
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_DISABLE_POSE", null);
            File.WriteAllText(Control, "{\"mode\":\"online\"}");
            SessionState.SetBool(Flag, true); EditorApplication.isPlaying = true;
        }
        private static void OnPlay(PlayModeStateChange change)
        {
            if (!SessionState.GetBool(Flag, false) || change != PlayModeStateChange.EnteredPlayMode) return;
            stage = 0; started = EditorApplication.timeSinceStartup; nextTick = 0; EditorApplication.update += Tick;
        }
        private static void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextTick) return;
            nextTick = EditorApplication.timeSinceStartup + .1;
            try
            {
                if (EditorApplication.timeSinceStartup - started > 150) throw new Exception("History smoke timed out at stage " + stage + ": " + (controller == null || controller.History == null ? "" : controller.History.Status));
                if (controller == null) controller = UnityEngine.Object.FindObjectOfType<CaptureController>();
                if (controller == null || !controller.IsReady) return;
                if (controller.LastError != null) throw new Exception(controller.LastError);
                if (stage == 0)
                {
                    queuePath = controller.QueueDirectory;
                    deviceId = controller.History.DeviceId; workId = controller.History.WorkId;
                    controller.TriggerCapture(); stage = 1;
                }
                else if (stage == 1 && controller.PendingCount == 0 && !controller.IsCapturing && controller.History.PendingCount == 0 && controller.Labels.Count == 1)
                {
                    originalPoint = controller.Labels[0].transform.position;
                    Require(Directory.GetFiles(queuePath, "*.jpg", SearchOption.AllDirectories).Length == 0, "Uploaded JPEG retained on device.");
                    UnityEngine.Object.Destroy(controller.gameObject); controller = null;
                    new GameObject("Restarted capture controller").AddComponent<CaptureController>(); stage = 2;
                }
                else if (stage == 2 && controller.Labels.Count == 1)
                {
                    Require(controller.History.DeviceId == deviceId && controller.History.WorkId == workId, "Work identity did not survive restart.");
                    Require(Vector3.Distance(controller.Labels[0].transform.position, originalPoint) < .001f, "Restored editor anchor moved.");
                    Require(Directory.GetFiles(queuePath + "-history/cache", "*.anchor").Length == 1, "Historical anchor was not downloaded.");
                    Send("clear_cache", "downloadedAnchors", "visibleLabels"); stage = 3;
                }
                else if (stage == 3 && Acknowledged())
                {
                    Require(controller.Labels.Count == 0, "Visible cache was not cleared.");
                    Require(Directory.GetFiles(queuePath + "-history/cache", "*.anchor").Length == 0, "Downloaded cache was not cleared.");
                    Send("resume"); stage = 4;
                }
                else if (stage == 4 && Acknowledged())
                {
                    Require(controller.Labels.Count == 1 && Vector3.Distance(controller.Labels[0].transform.position, originalPoint) < .001f, "PC history did not restore the label after cache clear.");
                    Send("new_work"); stage = 5;
                }
                else if (stage == 5 && Acknowledged())
                {
                    Require(controller.History.WorkId != workId && controller.Labels.Count == 0, "New work did not switch identity and view.");
                    Camera.main.transform.position = new Vector3(1, 0, 0);
                    controller.TriggerCapture(); stage = 6;
                }
                else if (stage == 6 && controller.PendingCount == 0 && !controller.IsCapturing && controller.History.PendingCount == 0 && controller.Labels.Count == 1)
                {
                    File.WriteAllText(Control, "{\"mode\":\"offline-data\"}");
                    controller.TriggerCapture(); stage = 7;
                }
                else if (stage == 7 && controller.PendingCount == 1 && controller.GetComponent<PoseTelemetry>().PendingCount >= 3)
                {
                    Send("clear_cache", "pendingPhotos", "pendingPoses"); stage = 8;
                }
                else if (stage == 8 && Acknowledged())
                {
                    Require(controller.PendingCount == 0 && controller.History.PendingCount == 0, "Selected pending photo cache remains.");
                    Require(controller.GetComponent<PoseTelemetry>().DroppedSamples >= 3, "Selected unsent pose cache was not cleared.");
                    File.WriteAllText(Control, "{\"mode\":\"online\"}");
                    Finish(true, "Editor work archive, restart, exact-position restore, remote cache clear, re-download, new work, and explicit unsent-photo/pose clear passed.");
                }
            }
            catch (Exception error) { Finish(false, error.Message); }
        }
        private static void Send(string kind, params string[] categories)
        {
            commandId = Guid.NewGuid().ToString("N");
            var body = new Command { commandId = commandId, deviceId = deviceId, kind = kind, workId = workId, categories = categories };
            using (var client = new WebClient())
            {
                client.Headers["Content-Type"] = "application/json"; client.Headers["X-Demo-Action"] = "device-command";
                client.UploadData("http://127.0.0.1:8769/api/device-commands", Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
            }
        }
        private static bool Acknowledged()
        {
            using (var client = new WebClient())
            {
                var page = JsonUtility.FromJson<Overview>(client.DownloadString("http://127.0.0.1:8769/api/work-history"));
                var command = page.devices.First(d => d.deviceId == deviceId).command;
                if (command == null || command.commandId != commandId || command.status == "queued") return false;
                Require(command.status == "completed", "Device command failed: " + command.status);
                return true;
            }
        }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Finish(bool passed, string message)
        {
            EditorApplication.update -= Tick; SessionState.SetBool(Flag, false);
            File.WriteAllText(Path.Combine(DemoBuild.Root, "logs/history-smoke.json"), "{\"passed\":" + (passed ? "true" : "false") + ",\"stage\":" + stage + ",\"message\":" + JsonUtility.ToJson(new TextValue { text = message }) + "}");
            Debug.Log("CAPTURE_DEMO_HISTORY_SMOKE " + passed + " " + message); EditorApplication.Exit(passed ? 0 : 1);
        }
        [Serializable] private sealed class TextValue { public string text; }
    }
}
