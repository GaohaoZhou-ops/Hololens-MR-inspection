using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace HoloLensCaptureDemo.Editor
{
    public static class DemoBuild
    {
        public const string ScenePath = "Assets/CaptureDemo/Scenes/CaptureDemo.unity";
        public static string Root { get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); } }

        [MenuItem("Capture Demo/Configure project")]
        public static void Configure()
        {
            if (!Application.unityVersion.StartsWith("2019.4.")) throw new Exception("Use Unity 2019.4 for HoloLens 1.");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WSAPlayer)
                throw new Exception("Switch to Universal Windows Platform first.");
            PlayerSettings.companyName = "HoloLensWorkspace";
            PlayerSettings.productName = "HoloLensCaptureDemo";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.WSA, "local.hololens.CaptureDemo");
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.WSA, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.WSA, ApiCompatibilityLevel.NET_4_6);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WSAPlayer, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WSAPlayer, new[] { GraphicsDeviceType.Direct3D11 });
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.stereoRenderingPath = StereoRenderingPath.Instancing;
            PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.SpatialPerception, true);
            PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.WebCam, true);
            PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.InternetClient, true);
            PlayerSettings.WSA.SetCapability(PlayerSettings.WSACapability.PrivateNetworkClientServer, true);
            EditorUserBuildSettings.wsaArchitecture = "x86";
            EditorUserBuildSettings.wsaSubtarget = WSASubtarget.HoloLens;
            EditorUserBuildSettings.wsaUWPBuildType = WSAUWPBuildType.D3D;
            EditorUserBuildSettings.wsaUWPSDK = "10.0.19041.0";
            EditorUserBuildSettings.wsaMinUWPSDK = "10.0.17763.0";
            EditorUserBuildSettings.wsaUWPVisualStudioVersion = string.Empty;
#pragma warning disable 0618
            var sdk = PlayerSettings.GetAvailableVirtualRealitySDKs(BuildTargetGroup.WSA)
                .Where(s => s.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (sdk.Length == 0) throw new Exception("Unity UWP / legacy Windows XR support is missing.");
            PlayerSettings.SetVirtualRealitySDKs(BuildTargetGroup.WSA, sdk);
            PlayerSettings.SetVirtualRealitySupported(BuildTargetGroup.WSA, true);
#pragma warning restore 0618
            EditorSettings.serializationMode = SerializationMode.ForceText;
            QualitySettings.vSyncCount = 0;
            QualitySettings.antiAliasing = 0;
            var environment = Path.GetFullPath(Path.Combine(Root, "../HoloLens1Environment"));
            var giCache = Path.Combine(environment, "cache", "gi");
            Directory.CreateDirectory(giCache);
            EditorPrefs.SetString("GICacheFolder", giCache);
            EditorPrefs.SetBool("GICacheEnableCustomPath", true);
            var refresh = typeof(Lightmapping).GetMethod("UpdateCachePath",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (refresh != null) refresh.Invoke(null, null);
            if (!File.Exists(ScenePath)) CreateScene();
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var ide = Path.Combine(environment, "VS", "Common7", "IDE", "devenv.exe");
            if (File.Exists(ide)) Unity.CodeEditor.CodeEditor.SetExternalScriptEditor(ide);
            Debug.Log("CAPTURE_DEMO_CONFIGURED");
        }

        private static TextMesh CreateText(string name, Transform parent, Vector3 position, string value, float size)
        {
            var text = new GameObject(name).AddComponent<TextMesh>();
            text.transform.SetParent(parent, false);
            text.transform.localPosition = position;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
            text.fontSize = 40;
            text.characterSize = size;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = new Color(0.4f, 0.9f, 1f);
            text.text = value;
            return text;
        }

        private static void CreateScene()
        {
            Directory.CreateDirectory("Assets/CaptureDemo/Scenes");
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
            camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            camera.fieldOfView = 40f;
            camera.gameObject.AddComponent<EditorCameraMovement>();
            new GameObject("Photo capture controller").AddComponent<CaptureController>();
            CreateText("Center reticle", camera.transform, new Vector3(0f, 0f, 1.2f), "+", 0.003f);
            Lightmapping.giWorkflowMode = Lightmapping.GIWorkflowMode.OnDemand;
            Lightmapping.realtimeGI = false;
            Lightmapping.bakedGI = false;
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        [MenuItem("Capture Demo/Open scene")]
        public static void Open()
        {
            if (!File.Exists(ScenePath)) Configure();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("Capture Demo/Export UWP (real camera)")]
        public static void Export() { ExportCore(false); }
        [MenuItem("Capture Demo/Export UWP (simulated camera)")]
        public static void ExportSimulated() { ExportCore(true); }

        private static void ExportCore(bool simulation)
        {
            Configure();
            // UWP rejects changed contents under an already-installed identity.
            // Persist a new build number for every export, including camera-mode changes.
            var version = PlayerSettings.WSA.packageVersion;
            if (version.Build >= 65535) throw new Exception("Increment the package minor version before exporting again.");
            PlayerSettings.WSA.packageVersion = new Version(version.Major, version.Minor, version.Build + 1, 0);
            AssetDatabase.SaveAssets();
            var configPath = Path.Combine(Application.streamingAssetsPath, "demo-config.json");
            var original = File.ReadAllText(configPath);
            try
            {
                var config = JsonUtility.FromJson<DemoConfig>(original);
                config.simulateCamera = simulation;
                File.WriteAllText(configPath, JsonUtility.ToJson(config, true));
                AssetDatabase.Refresh();
                var output = Path.Combine(Root, "builds", simulation ? "UWP-Simulated" : "UWP");
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath }, locationPathName = output,
                    target = BuildTarget.WSAPlayer, options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded) throw new Exception("UWP export failed: " + report.summary.result);
                // Unity preserves an existing UWP manifest on incremental export. Keep
                // its customizations, but explicitly synchronize the package identity.
                var manifestPath = Path.Combine(output, "HoloLensCaptureDemo", "Package.appxmanifest");
                var manifest = XDocument.Load(manifestPath, LoadOptions.PreserveWhitespace);
                var identity = manifest.Root.Element(manifest.Root.Name.Namespace + "Identity");
                if (identity == null) throw new Exception("UWP package identity is missing.");
                identity.SetAttributeValue("Version", PlayerSettings.WSA.packageVersion.ToString());
                manifest.Save(manifestPath, SaveOptions.DisableFormatting);
                Debug.Log("CAPTURE_DEMO_PACKAGE_VERSION " + PlayerSettings.WSA.packageVersion);
                Debug.Log("CAPTURE_DEMO_EXPORTED " + output);
            }
            finally
            {
                File.WriteAllText(configPath, original);
                AssetDatabase.Refresh();
            }
        }

        public static void SmokeTest()
        {
            Configure();
            EditorSceneManager.OpenScene(ScenePath);
            DemoSmokeTest.Begin();
        }

        public static void OfflineSmokeTest()
        {
            Configure();
            EditorSceneManager.OpenScene(ScenePath);
            DemoOfflineSmokeTest.Begin();
        }

        public static void PoseSmokeTest()
        {
            Configure();
            EditorSceneManager.OpenScene(ScenePath);
            DemoPoseSmokeTest.Begin();
        }

        public static void HistorySmokeTest()
        {
            Configure();
            EditorSceneManager.OpenScene(ScenePath);
            DemoHistorySmokeTest.Begin();
        }
        public static void CaptureSmokeTest()
        {
            Configure(); EditorSceneManager.OpenScene(ScenePath); DemoCaptureSmokeTest.Begin();
        }
    }
}
