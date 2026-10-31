using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
#if UNITY_WSA && !UNITY_EDITOR
using UnityEngine.Windows.WebCam;
using UnityEngine.XR.WSA.Input;
using UnityEngine.XR.WSA.Persistence;
#endif

namespace HoloLensCaptureDemo
{
    [Serializable]
    public sealed class DemoConfig
    {
        public string serverUrl;
        public string apiToken;
        public float distanceMeters = 2f;
        public int requestTimeoutSeconds = 15;
        public int maxLabels = 12;
        public bool simulateCamera;
        public int maxQueuedCaptures = 20;
        public int maxQueueMegabytes = 64;
    }

    [Serializable]
    public sealed class CaptureResponse
    {
        public string captureId;
        public string serial;
        public float centerX;
        public float centerY;
        public int processingMs;
    }

    public sealed class CaptureController : MonoBehaviour
    {
        public string StatusMessage { get; private set; }
        public DemoConfig Config { get; private set; }
        public bool IsReady { get; private set; }
        public bool IsCapturing { get; private set; }
        public bool IsBusy { get { return IsCapturing || PendingCount > 0; } }
        public int PendingCount { get { return outbox == null ? 0 : outbox.Items.Count; } }
        public string UploadState { get { return uploader == null ? "starting" : uploader.State; } }
        public int ProbeCount { get { return uploader == null ? 0 : uploader.ProbeCount; } }
        public int UploadAttempts { get { return uploader == null ? 0 : uploader.UploadAttempts; } }
        public string QueueDirectory { get { return outbox == null ? null : outbox.DirectoryPath; } }
        public string LastError { get; private set; }
        public string LastSerial { get; private set; }
        public AnchoredLabel PendingLabel
        {
            get
            {
                if (captureLabel != null) return captureLabel;
                AnchoredLabel pending;
                return outbox != null && outbox.Items.Count > 0 && pendingLabels.TryGetValue(outbox.Items[0].captureId, out pending) ? pending : null;
            }
        }
        public IReadOnlyList<AnchoredLabel> Labels { get { return labels; } }
        public WorkHistoryClient History { get; private set; }
        public long PendingPhotoBytes { get { return outbox == null ? 0 : outbox.StoredBytes; } }
        public bool UploadActive { get { return uploader != null && uploader.IsRequestActive; } }
        public bool IsPhotoPending(string id) { return outbox != null && outbox.Items.Any(photo => photo.captureId == id); }

        private readonly List<AnchoredLabel> labels = new List<AnchoredLabel>();
        private readonly Dictionary<string, AnchoredLabel> pendingLabels = new Dictionary<string, AnchoredLabel>();
        private PendingPhotoStore outbox;
        private CaptureUploadQueue uploader;
        private AnchoredLabel captureLabel;
        private string captureMessage;
        private const string AnchorPrefix = "capture-pending-";
        private bool disposed;
        private int simulationNumber;
        private float nextHistoryStatus;
        private readonly CapturePressGate pressGate = new CapturePressGate();
        private bool handoffActive, applicationPaused;
        private CameraCaptureLease cameraLease;
#if UNITY_WSA && !UNITY_EDITOR
        private bool inputSubscribed;
        private PhotoCapture cameraCapture;
        private bool photoMode;
        private WorldAnchorStore anchorStore;
#endif

        private void Awake()
        {
            var camera = Camera.main;
            if (camera != null && camera.GetComponent<NetworkStatusHud>() == null)
                camera.gameObject.AddComponent<NetworkStatusHud>();
        }

        private IEnumerator Start()
        {
            SetStatus("Loading configuration...");
            string configText = null;
            string configError = null;
            try
            {
                configText = File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "demo-config.json"));
                Config = JsonUtility.FromJson<DemoConfig>(configText);
#if UNITY_EDITOR
                var testServer = Environment.GetEnvironmentVariable("HOLOLENS_DEMO_SERVER_URL");
                var testToken = Environment.GetEnvironmentVariable("HOLOLENS_DEMO_API_TOKEN");
                if (!string.IsNullOrEmpty(testServer)) Config.serverUrl = testServer;
                if (!string.IsNullOrEmpty(testToken)) Config.apiToken = testToken;
#endif
                Uri endpoint;
                if (Config == null || !Uri.TryCreate(Config.serverUrl, UriKind.Absolute, out endpoint) ||
                    (endpoint.Scheme != "http" && endpoint.Scheme != "https") ||
                    string.IsNullOrEmpty(Config.apiToken) || Config.apiToken.Length < 24)
                    throw new InvalidDataException("Run Configure-Lan.cmd before opening or building the project.");
                if (Config.distanceMeters < 0.5f || Config.distanceMeters > 5f)
                    throw new InvalidDataException("distanceMeters must be between 0.5 and 5.");
                Config.requestTimeoutSeconds = Mathf.Clamp(Config.requestTimeoutSeconds, 3, 60);
                Config.maxLabels = Mathf.Clamp(Config.maxLabels, 1, 30);
                Config.maxQueuedCaptures = Config.maxQueuedCaptures <= 0 ? 20 : Mathf.Clamp(Config.maxQueuedCaptures, 1, 100);
                Config.maxQueueMegabytes = Config.maxQueueMegabytes <= 0 ? 64 : Mathf.Clamp(Config.maxQueueMegabytes, 8, 512);
                string queuePath;
#if UNITY_EDITOR
                queuePath = Environment.GetEnvironmentVariable("HOLOLENS_DEMO_OUTBOX_DIR");
                if (string.IsNullOrEmpty(queuePath)) queuePath = Path.GetFullPath(Path.Combine(Application.dataPath, "../logs/editor-outbox"));
#elif UNITY_WSA
                queuePath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "CaptureOutbox");
#else
                queuePath = Path.Combine(Application.persistentDataPath, "CaptureOutbox");
#endif
                outbox = new PendingPhotoStore(queuePath, Config.maxQueuedCaptures, Config.maxQueueMegabytes * 1024L * 1024L);
            }
            catch (Exception error) { configError = error.Message; }
            if (configError != null)
            {
                LastError = configError;
                SetStatus("Configuration error\n" + configError);
                yield break;
            }
            try
            {
                History = gameObject.AddComponent<WorkHistoryClient>();
                History.Initialize(this, Config, outbox.DirectoryPath + "-history");
            }
            catch (Exception error)
            {
                configError = "History initialization: " + error.Message;
            }
            if (configError != null) { LastError = configError; SetStatus(LastError); yield break; }
#if UNITY_EDITOR
            if (Environment.GetEnvironmentVariable("HOLOLENS_DEMO_DISABLE_POSE") != "1")
#endif
                gameObject.AddComponent<PoseTelemetry>().Initialize(Config,
                    outbox.DirectoryPath + "-poses");
#if UNITY_WSA && !UNITY_EDITOR
            WorldAnchorStore.GetAsync(store => { if (disposed) { if (store != null) store.Dispose(); } else anchorStore = store; });
            yield return WaitForCallback(() => anchorStore != null, 8f);
            if (anchorStore != null)
            {
                var pendingIds = new HashSet<string>(outbox.Items.Select(item => AnchorPrefix + item.captureId));
                foreach (var id in History.PendingIds) pendingIds.Add(AnchorPrefix + id);
                foreach (var id in anchorStore.GetAllIds())
                    if (id.StartsWith(AnchorPrefix) && !pendingIds.Contains(id)) ForgetAnchor(id);
                foreach (var item in outbox.Items)
                {
                    var label = AnchoredLabel.RestorePending(anchorStore, AnchorPrefix + item.captureId);
                    if (label != null) pendingLabels[item.captureId] = label;
                }
            }
            SubscribeInput();
#endif
            uploader = new CaptureUploadQueue(outbox, Config, Uploaded, ReadyStatus);
            IsReady = true;
            ReadyStatus();
            StartCoroutine(uploader.Run());
            History.Begin();
        }

        private void Update()
        {
            if (History != null && Time.realtimeSinceStartup >= nextHistoryStatus)
            {
                nextHistoryStatus = Time.realtimeSinceStartup + 1;
                ReadyStatus();
            }
            if (Input.GetKeyDown(KeyCode.Space)) TriggerCapture();
            if (Input.GetKeyDown(KeyCode.R) && uploader != null) uploader.Wake();
            if (Input.GetKeyDown(KeyCode.Backspace) && !IsCapturing)
            {
                ClearVisibleLabels();
                ReadyStatus();
            }
        }

#if UNITY_WSA && !UNITY_EDITOR
        private void SubscribeInput()
        {
            if (inputSubscribed) return;
            InteractionManager.InteractionSourcePressed += OnSelectPressed;
            InteractionManager.InteractionSourceReleased += OnSelectReleased;
            InteractionManager.InteractionSourceLost += OnSourceLost;
            inputSubscribed = true;
        }
        private void UnsubscribeInput()
        {
            if (!inputSubscribed) return;
            InteractionManager.InteractionSourcePressed -= OnSelectPressed;
            InteractionManager.InteractionSourceReleased -= OnSelectReleased;
            InteractionManager.InteractionSourceLost -= OnSourceLost;
            inputSubscribed = false;
        }
        private void OnSelectPressed(InteractionSourcePressedEventArgs args)
        {
            if (args.pressType == InteractionSourcePressType.Select) SelectPressed(args.state.source.id);
        }
        private void OnSelectReleased(InteractionSourceReleasedEventArgs args)
        {
            if (args.pressType == InteractionSourcePressType.Select) SelectReleased(args.state.source.id);
        }
        private void OnSourceLost(InteractionSourceLostEventArgs args) { SelectReleased(args.state.source.id); }
#endif

        public void SelectPressed(uint source)
        {
            if (pressGate.Press(source, Time.realtimeSinceStartup, IsReady && !IsCapturing && !handoffActive && !applicationPaused && isActiveAndEnabled && !disposed))
                TriggerCapture();
        }
        public void SelectReleased(uint source) { pressGate.Release(source); }

        public void TriggerCapture()
        {
            if (!IsReady || IsCapturing || handoffActive || applicationPaused || disposed) return;
            // Retrying old uploads must not consume the user's only shutter gesture.
            if (uploader.IsPaused) uploader.Wake();
            try
            {
                if (History != null && !History.HasCapacity) { Fail("History archive queue full; wait for PC backup."); return; }
                if (!outbox.HasCapacity()) { Fail("Upload queue full. Existing photos kept; wait for the PC."); return; }
            }
            catch (Exception error) { Fail(error.Message); return; }
            IsCapturing = true;
            LastError = null;
            captureMessage = null;
            SetStatus("正在拍照并保存…\n不需要再次点击");
            StartCoroutine(CaptureWithLiveHandoff());
        }

        private IEnumerator CaptureWithLiveHandoff()
        {
            handoffActive = true;
            cameraLease = new CameraCaptureLease(Config);
            try
            {
                yield return cameraLease.Acquire(SetStatus);
                if (cameraLease.MayCapture) yield return CaptureAndEnqueue();
                else Fail(cameraLease.Error);
                // Release after PhotoCapture has stopped/disposed, before video resumes.
                yield return cameraLease.Release();
            }
            finally
            {
                if (cameraLease != null) cameraLease.Dispose();
                cameraLease = null;
                handoffActive = false; IsCapturing = false;
                ReadyStatus();
            }
        }

        private IEnumerator CaptureAndEnqueue()
        {
            byte[] image = null;
            Matrix4x4 pose = Matrix4x4.identity;
            Matrix4x4 projection = Matrix4x4.identity;
            string errorMessage = null;
            bool simulated = Application.isEditor || Config.simulateCamera;
            if (simulated)
            {
                pose = Camera.main.cameraToWorldMatrix;
                projection = Camera.main.projectionMatrix;
                image = CreateSimulationPhoto();
            }
#if UNITY_WSA && !UNITY_EDITOR
            else
            {
                bool cancelled = false;
                bool done = false;
                bool success = false;
                PhotoCaptureFrame frame = null;
                PhotoCapture.CreateAsync(false, capture =>
                {
                    if (cancelled || disposed) { if (capture != null) capture.Dispose(); return; }
                    cameraCapture = capture;
                    done = true;
                });
                yield return WaitForCallback(() => done, 15f);
                if (!done || cameraCapture == null)
                {
                    cancelled = true;
                    Fail("摄像头不可用。请允许相机权限；关闭旧直播页，使用新版系统直播控制页自动切换。");
                    yield break;
                }
                var resolutions = PhotoCapture.SupportedResolutions.OrderBy(r => r.width * r.height).ToArray();
                if (resolutions.Length == 0)
                {
                    cameraCapture.Dispose(); cameraCapture = null;
                    Fail("No photo resolution available on this device.");
                    yield break;
                }
                var resolution = resolutions.FirstOrDefault(r => r.width >= 1280);
                if (resolution.width == 0) resolution = resolutions[resolutions.Length - 1];
                var ownedCapture = cameraCapture;
                done = false;
                ownedCapture.StartPhotoModeAsync(new CameraParameters
                {
                    hologramOpacity = 0f,
                    cameraResolutionWidth = resolution.width,
                    cameraResolutionHeight = resolution.height,
                    pixelFormat = CapturePixelFormat.BGRA32
                }, result =>
                {
                    if (cancelled || disposed) return;
                    success = result.success; photoMode = result.success; done = true;
                });
                yield return WaitForCallback(() => done, 15f);
                if (!done || !success) errorMessage = "未能启动相机。请检查权限或关闭未受控制的旧直播窗口，然后松开再合拢重试。";
                if (errorMessage == null)
                {
                    done = false;
                    ownedCapture.TakePhotoAsync((result, photo) =>
                    {
                        if (cancelled || disposed) { if (photo != null) photo.Dispose(); return; }
                        success = result.success; frame = photo; done = true;
                    });
                    yield return WaitForCallback(() => done, 15f);
                    if (!done || !success || frame == null) errorMessage = "Photo capture failed or timed out.";
                    else
                    {
                        Texture2D texture = null;
                        try
                        {
                            if (!frame.hasLocationData || !frame.TryGetCameraToWorldMatrix(out pose) ||
                                !frame.TryGetProjectionMatrix(0.1f, 20f, out projection))
                                throw new InvalidOperationException("Photo has no spatial pose. Look around to restore tracking, then retry.");
                            texture = new Texture2D(resolution.width, resolution.height, TextureFormat.BGRA32, false);
                            frame.UploadImageDataToTexture(texture);
                            image = texture.EncodeToJPG(85);
                        }
                        catch (Exception error) { errorMessage = error.Message; }
                        finally { frame.Dispose(); if (texture != null) Destroy(texture); }
                    }
                }
                cancelled = true;
                // Freeze a spatial anchor while the photo's camera pose is current.
                if (errorMessage == null) errorMessage = PrepareAnchor(pose, projection);
                if (photoMode && cameraCapture != null)
                {
                    done = false;
                    ownedCapture.StopPhotoModeAsync(result =>
                    {
                        if (cameraCapture == ownedCapture) photoMode = false;
                        done = true;
                    });
                    yield return WaitForCallback(() => done, 5f);
                }
                ownedCapture.Dispose();
                if (cameraCapture == ownedCapture) cameraCapture = null;
                photoMode = false;
            }
#else
            else { errorMessage = "Real photo capture requires a HoloLens UWP build."; }
#endif
            if (errorMessage == null && captureLabel == null) errorMessage = PrepareAnchor(pose, projection);
            if (errorMessage != null || image == null || image.Length == 0)
            {
                Fail(errorMessage ?? "Camera produced an empty image.");
                yield break;
            }
            string captureId = Guid.NewGuid().ToString("N");
            bool wasEmpty = outbox.Items.Count == 0;
#if UNITY_WSA && !UNITY_EDITOR
            bool savedAnchor = false;
            var anchorDeadline = Time.realtimeSinceStartup + 2f;
            while (anchorStore != null && Time.realtimeSinceStartup < anchorDeadline && !savedAnchor)
            {
                try { savedAnchor = captureLabel.SavePending(anchorStore, AnchorPrefix + captureId); }
                catch (Exception error) { Debug.LogWarning("Pending anchor save: " + error.Message); break; }
                if (!savedAnchor) yield return null;
            }
#endif
            try
            {
                captureLabel.CaptureId = captureId;
                History.Register(captureId, captureLabel.transform.position);
                outbox.Enqueue(captureId, image);
                pendingLabels.Add(captureId, captureLabel);
                Debug.Log("Capture queued: " + captureId + " point=" + captureLabel.transform.position.ToString("F4") + " simulated=" + simulated);
                captureLabel = null;
            }
            catch (Exception error) { errorMessage = error.Message; }
            if (errorMessage != null)
            {
                History.CancelPending(captureId);
#if UNITY_WSA && !UNITY_EDITOR
                if (savedAnchor) ForgetAnchor(AnchorPrefix + captureId);
#endif
                Fail(errorMessage);
                yield break;
            }
            IsCapturing = false;
            captureMessage = "Photo saved locally";
#if UNITY_WSA && !UNITY_EDITOR
            if (!savedAnchor) captureMessage += " (anchor available only this session)";
#endif
            if (wasEmpty) uploader.Wake();
            ReadyStatus();
        }

        private void Uploaded(PendingPhoto photo, CaptureResponse response)
        {
#if UNITY_WSA && !UNITY_EDITOR
            if (!pendingLabels.ContainsKey(photo.captureId) && anchorStore != null)
            {
                var restored = AnchoredLabel.RestorePending(anchorStore, AnchorPrefix + photo.captureId);
                if (restored != null) pendingLabels[photo.captureId] = restored;
            }
#endif
            AnchoredLabel label;
            if (pendingLabels.TryGetValue(photo.captureId, out label) && label != null)
            {
                if (!History.PendingIds.Contains(photo.captureId))
                    History.Register(photo.captureId, label.transform.position);
                label.CaptureId = photo.captureId;
                if (History.IsCurrentWork(photo.captureId))
                {
                    label.SetResult(response.serial);
                    labels.Add(label);
                    pendingLabels.Remove(photo.captureId);
                    captureMessage = response.serial + " placed at capture location";
                }
                else captureMessage = response.serial + " belongs to the previous work; archiving on PC";
                while (labels.Count > Config.maxLabels)
                {
                    RetireLabel(labels[0]);
                    labels.RemoveAt(0);
                }
            }
            else captureMessage = response.serial + " on PC; saved anchor unavailable";
            LastSerial = response.serial;
            LastError = null;
            // A completed photo no longer needs its JPEG, but its local anchor remains
            // until the PC has durably acknowledged the exported spatial backup.
            Debug.Log("Capture completed: " + response.serial + " (" + response.processingMs + " ms server)");
            ReadyStatus();
        }

        private string PrepareAnchor(Matrix4x4 pose, Matrix4x4 projection)
        {
            try
            {
                var ray = CaptureGeometry.ImageRay(pose, projection, 0.5f, 0.5f);
                captureLabel = AnchoredLabel.Create(ray.GetPoint(Config.distanceMeters));
                return null;
            }
            catch (Exception error) { return error.Message; }
        }

        public AnchoredLabel FindCaptureLabel(string id)
        {
            AnchoredLabel pending;
            if (pendingLabels.TryGetValue(id, out pending) && pending != null) return pending;
            return labels.FirstOrDefault(label => label != null && label.CaptureId == id);
        }

        public AnchoredLabel LoadSavedCaptureAnchor(string id)
        {
#if UNITY_WSA && !UNITY_EDITOR
            return anchorStore == null ? null : AnchoredLabel.RestorePending(anchorStore, AnchorPrefix + id);
#else
            return null;
#endif
        }

        public void ForgetCaptureAnchor(string id)
        {
            AnchoredLabel hidden;
            if (!IsPhotoPending(id) && pendingLabels.TryGetValue(id, out hidden))
            {
                if (hidden != null) Destroy(hidden.gameObject);
                pendingLabels.Remove(id);
            }
#if UNITY_WSA && !UNITY_EDITOR
            ForgetAnchor(AnchorPrefix + id);
#endif
        }

        public void AddHistoryLabel(AnchoredLabel label, string id, string serial)
        {
            if (FindCaptureLabel(id) != null) { Destroy(label.gameObject); return; }
            label.CaptureId = id;
            label.SetResult(serial);
            labels.Add(label);
            while (labels.Count > Config.maxLabels) { RetireLabel(labels[0]); labels.RemoveAt(0); }
        }

        private void RetireLabel(AnchoredLabel label)
        {
            if (label == null) return;
            if (!disposed && History != null && History.PendingIds.Contains(label.CaptureId))
            {
                // Keep an unbacked anchor alive even if its visual is cleared or evicted.
                label.HideResult(); pendingLabels[label.CaptureId] = label;
            }
            else Destroy(label.gameObject);
        }

        public void ClearVisibleLabels()
        {
            foreach (var label in labels) RetireLabel(label);
            labels.Clear();
        }

        public bool TryClearPendingPhotos()
        {
            if (IsCapturing || UploadActive) return false;
            foreach (var photo in outbox.Items.ToArray())
            {
                outbox.Complete(photo);
                AnchoredLabel label;
                if (pendingLabels.TryGetValue(photo.captureId, out label) && label != null) Destroy(label.gameObject);
                pendingLabels.Remove(photo.captureId);
                History.CancelPending(photo.captureId);
                ForgetCaptureAnchor(photo.captureId);
            }
            uploader.Wake();
            return true;
        }

#if UNITY_WSA && !UNITY_EDITOR
        private void ForgetAnchor(string id)
        {
            try { if (anchorStore != null) anchorStore.Delete(id); }
            catch (Exception error) { Debug.LogWarning("Pending anchor cleanup: " + error.Message); }
        }
#endif

        private static IEnumerator WaitForCallback(Func<bool> completed, float seconds)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (!completed() && Time.realtimeSinceStartup < deadline) yield return null;
        }

        private byte[] CreateSimulationPhoto()
        {
            const int width = 640, height = 360;
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var pixels = new Color32[width * height];
            simulationNumber++;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    bool cross = Mathf.Abs(x - width / 2) < 3 || Mathf.Abs(y - height / 2) < 3;
                    pixels[y * width + x] = cross ? new Color32(255, 255, 255, 255) :
                        new Color32((byte)(x * 255 / width), (byte)(y * 255 / height),
                                    (byte)(50 + simulationNumber % 10 * 15), 255);
                }
            texture.SetPixels32(pixels);
            texture.Apply();
            var image = texture.EncodeToJPG(85);
            Destroy(texture);
            return image;
        }

        private void Fail(string message)
        {
            LastError = message;
            IsCapturing = false;
            if (captureLabel != null) Destroy(captureLabel.gameObject);
            captureLabel = null;
            captureMessage = message;
            ReadyStatus();
            Debug.LogWarning(message);
        }

        private void ReadyStatus()
        {
            if (!IsReady || Config == null || IsCapturing || handoffActive) return;
            var mode = Application.isEditor || Config.simulateCamera ? "SIMULATED PHOTO" : "HOLOLENS CAMERA";
            var queueState = uploader == null ? "starting" : uploader.State;
            var detail = uploader == null ? "" : uploader.Detail;
            // A queue/camera error must remain visible even while reconnect probes continue.
            if (!string.IsNullOrEmpty(LastError)) detail = LastError;
            var hint = "合拢即拍一张 · 松开后可再拍 · 直播自动暂停/恢复";
            SetStatus(hint + "\n" + mode + " | Queued: " + PendingCount + "/" + Config.maxQueuedCaptures + " | " + queueState +
                "\n" + (string.IsNullOrEmpty(detail) ? (captureMessage ?? Config.serverUrl) : detail) +
                (History == null ? "" : "\n工作 " + History.WorkId.Substring(0, 8) + " | 待归档 " + History.PendingCount + " | " + History.Status));
        }

        private void SetStatus(string message) { StatusMessage = message; }

        private void OnApplicationPause(bool paused)
        {
            applicationPaused = paused;
            pressGate.Reset();
            if (!paused && uploader != null) uploader.Wake();
        }

        private void OnEnable()
        {
#if UNITY_WSA && !UNITY_EDITOR
            if (IsReady) SubscribeInput();
#endif
        }
        private void OnDisable()
        {
            pressGate.Reset();
#if UNITY_WSA && !UNITY_EDITOR
            UnsubscribeInput();
#endif
        }

        private void OnDestroy()
        {
            disposed = true;
            if (uploader != null) uploader.Dispose();
            if (cameraLease != null) cameraLease.Dispose();
            ClearVisibleLabels();
            if (captureLabel != null) Destroy(captureLabel.gameObject);
            foreach (var label in pendingLabels.Values) if (label != null) Destroy(label.gameObject);
#if UNITY_WSA && !UNITY_EDITOR
            UnsubscribeInput();
            if (cameraCapture != null) { cameraCapture.Dispose(); cameraCapture = null; }
            if (anchorStore != null) { anchorStore.Dispose(); anchorStore = null; }
#endif
        }
    }
}
