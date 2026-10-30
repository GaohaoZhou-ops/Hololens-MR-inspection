using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HoloLensCaptureDemo.Editor
{
    // Exercises actual Play Mode -> JPEG upload -> delayed HTTP response -> world label.
    [InitializeOnLoad]
    public static class DemoSmokeTest
    {
        private const string Flag = "HoloLensCaptureDemo.SmokeTest";
        private static int stage;
        private static double started;
        private static Vector3 firstPoint;
        private static Vector3 secondPoint;
        private static string firstSerial;
        private static CaptureController controller;

        static DemoSmokeTest()
        {
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        public static void Begin()
        {
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_DISABLE_POSE", "1");
            // Verify camera math independently of the headset's current pose.
            var projection = Matrix4x4.Perspective(60f, 16f / 9f, 0.1f, 20f);
            var pose = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 90f, 0f), new Vector3(1f, 1f, -1f));
            var ray = CaptureGeometry.ImageRay(pose, projection, 0.5f, 0.5f);
            if (Vector3.Distance(ray.GetPoint(2f), new Vector3(3f, 2f, 3f)) > 0.001f)
                throw new Exception("Capture projection geometry test failed.");
            SessionState.SetBool(Flag, true);
            EditorApplication.isPlaying = true;
        }

        private static void OnPlayMode(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Flag, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                started = EditorApplication.timeSinceStartup;
                stage = 0;
                EditorApplication.update += Tick;
            }
        }

        private static void Tick()
        {
            try
            {
                if (EditorApplication.timeSinceStartup - started > 60) throw new Exception("Play Mode round trip timed out.");
                if (controller == null) controller = UnityEngine.Object.FindObjectOfType<CaptureController>();
                if (controller == null) return;
                if (!string.IsNullOrEmpty(controller.LastError)) throw new Exception(controller.LastError);
                if (!controller.IsReady) return;
                var camera = Camera.main;
                if (stage == 0)
                {
                    controller.TriggerCapture();
                    stage = 1;
                }
                else if (stage == 1 && controller.PendingLabel != null)
                {
                    firstPoint = controller.PendingLabel.transform.position;
                    camera.transform.position = new Vector3(0.75f, 0.1f, 0f);
                    camera.transform.rotation = Quaternion.Euler(0f, 25f, 0f);
                    stage = 2;
                }
                else if (stage == 2 && !controller.IsBusy)
                {
                    if (controller.Labels.Count != 1 || Vector3.Distance(firstPoint, controller.Labels[0].transform.position) > 0.0001f)
                        throw new Exception("First label moved with the camera while awaiting the server.");
                    firstSerial = controller.LastSerial;
                    controller.TriggerCapture();
                    stage = 3;
                }
                else if (stage == 3 && controller.PendingLabel != null)
                {
                    secondPoint = controller.PendingLabel.transform.position;
                    camera.transform.position = new Vector3(-0.4f, 0f, 0f);
                    camera.transform.rotation = Quaternion.identity;
                    stage = 4;
                }
                else if (stage == 4 && !controller.IsBusy)
                {
                    var networkHud = camera.GetComponent<NetworkStatusHud>();
                    var diagnostic = camera.transform.Find("Device network HUD");
                    if (networkHud == null || string.IsNullOrEmpty(networkHud.DisplayText) ||
                        !networkHud.DisplayText.Contains("IPv4:") || diagnostic == null)
                        throw new Exception("Network HUD was not initialized.");
                    var corner = camera.WorldToViewportPoint(diagnostic.position);
                    if (Mathf.Abs(corner.x - 0.06f) > 0.01f || Mathf.Abs(corner.y - 0.93f) > 0.01f)
                        throw new Exception("Network HUD did not remain in the upper-left view after camera movement.");
                    if (controller.Labels.Count != 2 || firstSerial == controller.LastSerial ||
                        Vector3.Distance(firstPoint, controller.Labels[0].transform.position) > 0.0001f ||
                        Vector3.Distance(secondPoint, controller.Labels[1].transform.position) > 0.0001f ||
                        Vector3.Distance(firstPoint, secondPoint) < 0.5f ||
                        controller.Labels[0].transform.parent != null || controller.Labels[1].transform.parent != null)
                        throw new Exception("Serial uniqueness or world-space placement validation failed.");
                    Finish(true, "Two JPEG uploads completed; labels stayed in world space and the network HUD stayed in the upper-left view while the camera moved.");
                }
            }
            catch (Exception error) { Finish(false, error.Message); }
        }

        private static void Finish(bool success, string message)
        {
            EditorApplication.update -= Tick;
            SessionState.SetBool(Flag, false);
            var result = new SmokeResult
            {
                passed = success, message = message,
                firstSerial = firstSerial, secondSerial = controller == null ? null : controller.LastSerial,
                firstPoint = firstPoint, secondPoint = secondPoint,
                networkHud = Camera.main != null && Camera.main.GetComponent<NetworkStatusHud>() != null
                    ? Camera.main.GetComponent<NetworkStatusHud>().DisplayText : null,
                verifiedAt = DateTime.UtcNow.ToString("o")
            };
            Directory.CreateDirectory(Path.Combine(DemoBuild.Root, "logs"));
            File.WriteAllText(Path.Combine(DemoBuild.Root, "logs", "editor-smoke.json"), JsonUtility.ToJson(result, true));
            Debug.Log("CAPTURE_DEMO_SMOKE " + success + " " + message);
            EditorApplication.Exit(success ? 0 : 1);
        }

        [Serializable]
        private class SmokeResult
        {
            public bool passed;
            public string message;
            public string verifiedAt;
            public string firstSerial;
            public string secondSerial;
            public string networkHud;
            public Vector3 firstPoint;
            public Vector3 secondPoint;
        }
    }
}
