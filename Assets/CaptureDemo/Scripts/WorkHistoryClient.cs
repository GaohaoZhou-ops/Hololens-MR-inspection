using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
#if UNITY_WSA && !UNITY_EDITOR
using UnityEngine.XR.WSA.Sharing;
#endif

namespace HoloLensCaptureDemo
{
    // The PC owns completed records and spatial backups. The device retains bounded
    // unsynchronized backups, a small downloaded-anchor cache, and its work identity.
    public sealed class WorkHistoryClient : MonoBehaviour
    {
        [Serializable] private sealed class LocalState
        {
            public string deviceId, workId, executingCommand;
            public CommandAck lastAck;
        }
        [Serializable] private sealed class PendingArchive
        {
            public string captureId, workId, anchorKind, createdAt;
            public Vector3 point;
            public bool archived;
        }
        [Serializable] private sealed class CacheInfo
        {
            public int downloadedAnchors, visibleLabels, pendingPhotos, pendingPoses, pendingArchives;
            public long downloadBytes, photoBytes, poseBytes;
        }
        [Serializable] private sealed class PollBody
        {
            public string deviceId, deviceName, clientKind, workId, status;
            public CacheInfo cache;
        }
        [Serializable] private sealed class DeviceCommand
        {
            public string commandId, kind, workId, expiresAt;
            public string[] categories;
        }
        [Serializable] private sealed class PollResponse { public DeviceCommand command; }
        [Serializable] private sealed class CommandAck { public string deviceId, commandId, status, detail; }
        [Serializable] private sealed class ArchiveAck
        {
            public string captureId, anchorSha256;
            public int anchorBytes;
            public bool archived;
        }
        [Serializable] private sealed class HistoryRecord
        {
            public string captureId, serial, anchorKind, anchorSha256;
            public int anchorBytes;
        }
        [Serializable] private sealed class Manifest
        {
            public string workId, title;
            public int total;
            public HistoryRecord[] records;
        }

        private const long DownloadLimit = 64L * 1024 * 1024;
        private const long PendingLimit = 128L * 1024 * 1024;
        private const int AnchorLimit = 32 * 1024 * 1024;
        private readonly Dictionary<string, PendingArchive> pending = new Dictionary<string, PendingArchive>();
        private CaptureController controller;
        private DemoConfig config;
        private LocalState state;
        private string root, pendingPath, cachePath;
        private UnityWebRequest active;
        private UnityWebRequest heartbeat;
        private bool operationBusy;
        private bool disposed, paused, ackDelivered;
        private string restoreDetail;
        private bool restorePartial;
        public string DeviceId { get { return state == null ? null : state.deviceId; } }
        public string WorkId { get { return state == null ? null : state.workId; } }
        public string Status { get; private set; }
        public IEnumerable<string> PendingIds { get { return pending.Keys; } }
        public int PendingCount { get { return pending.Count; } }
        public bool IsCurrentWork(string captureId)
        {
            PendingArchive record;
            return !pending.TryGetValue(captureId, out record) || record.workId == state.workId;
        }
        public bool HasCapacity { get { return pending.Count < 100 && FolderBytes(pendingPath) < PendingLimit; } }

        public void Initialize(CaptureController owner, DemoConfig settings, string directory)
        {
            controller = owner; config = settings; root = directory;
            pendingPath = Path.Combine(root, "pending"); cachePath = Path.Combine(root, "cache");
            Directory.CreateDirectory(pendingPath); Directory.CreateDirectory(cachePath);
            var statePath = Path.Combine(root, "state.json");
            state = File.Exists(statePath) ? JsonUtility.FromJson<LocalState>(File.ReadAllText(statePath)) :
                new LocalState { deviceId = Guid.NewGuid().ToString("N"), workId = Guid.NewGuid().ToString("N") };
            if (state == null || !Id(state.deviceId) || !Id(state.workId)) throw new InvalidDataException("Invalid history identity; files retained.");
            if (!string.IsNullOrEmpty(state.executingCommand))
            {
                state.lastAck = new CommandAck { deviceId = state.deviceId, commandId = state.executingCommand,
                    status = "interrupted", detail = "应用在执行期间退出。未自动重做清理；请检查后重新发送。" };
                state.executingCommand = null;
            }
            SaveState();
            foreach (var file in Directory.GetFiles(pendingPath, "*.json"))
            {
                var record = JsonUtility.FromJson<PendingArchive>(File.ReadAllText(file));
                if (record == null || !Id(record.captureId) || !Id(record.workId) || Path.GetFileNameWithoutExtension(file) != record.captureId)
                    throw new InvalidDataException("Invalid pending spatial backup; files retained.");
                pending.Add(record.captureId, record);
            }
            Status = "等待电脑历史服务";
        }

        public void Begin() { StartCoroutine(Run()); StartCoroutine(HeartbeatDuringWork()); }

        public void Register(string captureId, Vector3 point)
        {
            if (!HasCapacity) throw new IOException("Spatial archive queue full; wait for PC backup.");
            var record = new PendingArchive { captureId = captureId, workId = state.workId, point = point,
                createdAt = DateTime.UtcNow.ToString("o"), anchorKind = Application.isEditor ? "editor" : "wsa" };
            WriteJson(Path.Combine(pendingPath, captureId + ".json"), record);
            pending[captureId] = record;
        }

        public void CancelPending(string captureId)
        {
            // Delete metadata last: a failure must leave a discoverable pending backup.
            File.Delete(Path.Combine(pendingPath, captureId + ".anchor"));
            File.Delete(Path.Combine(pendingPath, captureId + ".json"));
            pending.Remove(captureId);
        }

        private IEnumerator Run()
        {
            // This also falls back to the last bounded download cache when offline.
            operationBusy = true;
            yield return Restore(state.workId, false);
            operationBusy = false;
            while (!disposed)
            {
                if (paused) { yield return null; continue; }
                if (state.lastAck != null && !ackDelivered)
                {
                    using (var request = JsonRequest("/api/device-acks", state.lastAck))
                    {
                        active = request; yield return request.SendWebRequest(); active = null;
                        ackDelivered = !request.isNetworkError && (request.responseCode == 200 || request.responseCode == 400 || request.responseCode == 404);
                    }
                    if (!ackDelivered) { Status = "回执待同步；电脑不可达"; yield return new WaitForSecondsRealtime(2); continue; }
                }
                PollResponse reply = null;
                using (var request = JsonRequest("/api/device-poll", PollSnapshot()))
                {
                    active = request; yield return request.SendWebRequest(); active = null;
                    if (!request.isNetworkError && request.responseCode == 200) reply = Parse<PollResponse>(request.downloadHandler.text);
                    else Status = "历史服务离线；每 2 秒重试";
                }
                if (reply != null)
                {
                    operationBusy = true;
                    if (reply.command != null && Id(reply.command.commandId)) yield return Execute(reply.command);
                    else if (!controller.IsCapturing && pending.Count > 0) yield return ArchiveNext();
                    operationBusy = false;
                }
                yield return new WaitForSecondsRealtime(2);
            }
        }

        private IEnumerator HeartbeatDuringWork()
        {
            // Spatial export/import may take longer than the normal two-second poll.
            // Keep progress visible; commands remain queued until the worker acknowledges them.
            while (!disposed)
            {
                yield return new WaitForSecondsRealtime(2);
                if (paused || !operationBusy) continue;
                using (var request = JsonRequest("/api/device-poll", PollSnapshot()))
                {
                    heartbeat = request; yield return request.SendWebRequest(); heartbeat = null;
                }
            }
        }

        private PollBody PollSnapshot()
        {
            var pose = GetComponent<PoseTelemetry>();
            return new PollBody { deviceId = state.deviceId, deviceName = SystemInfo.deviceName,
                clientKind = Application.isEditor ? "editor" : "hololens-uwp", workId = state.workId,
                status = (Status ?? "") + " | 已定位标签 " + controller.Labels.Count(l => l != null && l.IsLocated),
                cache = new CacheInfo { downloadedAnchors = Directory.GetFiles(cachePath, "*.anchor").Length,
                    downloadBytes = FolderBytes(cachePath), visibleLabels = controller.Labels.Count,
                    pendingPhotos = controller.PendingCount, photoBytes = controller.PendingPhotoBytes,
                    pendingPoses = pose == null ? 0 : pose.PendingCount, poseBytes = pose == null ? 0 : pose.PendingBytes,
                    pendingArchives = pending.Count } };
        }

        private IEnumerator ArchiveNext()
        {
            var record = pending.Values.Where(p => !controller.IsPhotoPending(p.captureId)).OrderBy(p => p.createdAt).FirstOrDefault();
            if (record == null) yield break;
            if (record.archived)
            {
                if (Try(() => CancelPending(record.captureId))) controller.ForgetCaptureAnchor(record.captureId);
                yield break;
            }
            var blob = Path.Combine(pendingPath, record.captureId + ".anchor");
            if (!File.Exists(blob))
            {
                byte[] data = null;
                if (record.anchorKind == "editor") data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(record.point));
#if UNITY_WSA && !UNITY_EDITOR
                else
                {
                    var label = controller.FindCaptureLabel(record.captureId);
                    bool owned = label == null;
                    if (owned) label = controller.LoadSavedCaptureAnchor(record.captureId);
                    if (label == null) { record.anchorKind = "none"; data = new byte[0]; }
                    else if (!label.IsLocated)
                    {
                        if (owned) Destroy(label.gameObject);
                        Status = "空间备份等待定位，请回到拍摄区域并环顾四周";
                        yield break;
                    }
                    else
                    {
                        bool done = false, abandoned = false;
                        string failure = null;
                        var buffer = new MemoryStream();
                        var batch = new WorldAnchorTransferBatch();
                        try
                        {
                            if (!batch.AddWorldAnchor(record.captureId, label.SpatialAnchor)) throw new InvalidOperationException("Anchor not ready.");
                            WorldAnchorTransferBatch.ExportAsync(batch, bytes =>
                            {
                                if (abandoned || failure != null) return;
                                if (buffer.Length + bytes.Length > AnchorLimit) { failure = "Spatial backup exceeds 32 MiB."; return; }
                                buffer.Write(bytes, 0, bytes.Length);
                            }, reason =>
                            {
                                if (!abandoned && failure == null && reason == SerializationCompletionReason.Succeeded) data = buffer.ToArray();
                                else if (failure == null) failure = "Anchor export: " + reason;
                                buffer.Dispose(); batch.Dispose(); done = true;
                            });
                        }
                        catch (Exception error) { failure = error.Message; buffer.Dispose(); batch.Dispose(); done = true; }
                        var deadline = Time.realtimeSinceStartup + 60;
                        while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                        if (!done) { abandoned = true; failure = "Anchor export timed out; local anchor retained."; }
                        if (owned && label != null) Destroy(label.gameObject);
                        if (failure != null) { Status = failure; yield break; }
                    }
                }
#else
                else { record.anchorKind = "none"; data = new byte[0]; }
#endif
                if (data == null) { Status = "等待空间备份"; yield break; }
                if (FolderBytes(pendingPath) + data.Length > PendingLimit) { Status = "空间备份暂存已满；保留本地锚点"; yield break; }
                if (!Try(() => { WriteBytes(blob, data); WriteJson(Path.Combine(pendingPath, record.captureId + ".json"), record); })) yield break;
            }
            byte[] bytesToSend = null;
            if (!Try(() => bytesToSend = File.ReadAllBytes(blob))) yield break;
            using (var request = Request("/api/work-anchors/" + record.captureId, "POST", 60))
            {
                request.uploadHandler = new UploadHandlerRaw(bytesToSend);
                request.SetRequestHeader("Content-Type", "application/octet-stream");
                request.SetRequestHeader("X-Device-Id", state.deviceId);
                request.SetRequestHeader("X-Work-Id", record.workId);
                request.SetRequestHeader("X-Anchor-Kind", record.anchorKind);
                active = request; Status = "正在归档空间标签到电脑";
                yield return request.SendWebRequest(); active = null;
                if (request.isNetworkError || request.responseCode != 200) { Status = "归档待重试 HTTP " + request.responseCode; yield break; }
                var ack = Parse<ArchiveAck>(request.downloadHandler.text);
                if (ack == null || !ack.archived || ack.captureId != record.captureId || ack.anchorBytes != bytesToSend.Length || ack.anchorSha256 != Hash(bytesToSend))
                { Status = "空间归档回执不匹配，保留本地数据"; yield break; }
                record.archived = true;
                if (!Try(() => WriteJson(Path.Combine(pendingPath, record.captureId + ".json"), record))) { record.archived = false; yield break; }
                if (!Try(() => CancelPending(record.captureId))) yield break;
                controller.ForgetCaptureAnchor(record.captureId);
                Status = record.anchorKind == "none" ? "结果已归档，但原空间锚点不可用" : "标签与空间锚点已归档";
            }
        }

        private IEnumerator Restore(string workId, bool replace)
        {
            restorePartial = false; restoreDetail = "未获取历史记录";
            Manifest manifest = null;
            using (var request = Request("/api/device-history/" + workId + "?limit=" + config.maxLabels, "GET", 3))
            {
                active = request; yield return request.SendWebRequest(); active = null;
                if (!request.isNetworkError && request.responseCode == 200) manifest = Parse<Manifest>(request.downloadHandler.text);
            }
            var manifestPath = Path.Combine(cachePath, "manifest.json");
            if (manifest == null && File.Exists(manifestPath)) Try(() => manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath)));
            if (manifest == null || manifest.workId != workId || manifest.records == null)
            { restorePartial = true; restoreDetail = "该工作尚无可读取的归档，请连接电脑后重试"; yield break; }
            Try(() => WriteJson(manifestPath, manifest));
            if (replace) controller.ClearVisibleLabels();
            int loaded = 0, located = 0, failed = 0;
            foreach (var record in manifest.records.Take(config.maxLabels))
            {
                if (!Id(record.captureId) || !Regex.IsMatch(record.serial ?? "", "^HL1-[0-9]{6,12}$") ||
                    record.anchorBytes <= 0 || record.anchorBytes > AnchorLimit || !Regex.IsMatch(record.anchorSha256 ?? "", "^[a-f0-9]{64}$"))
                { failed++; continue; }
                var existing = controller.FindCaptureLabel(record.captureId);
                if (existing != null) { loaded++; if (existing.IsLocated) located++; continue; }
                if (Application.isEditor != (record.anchorKind == "editor")) { failed++; continue; }
                var file = Path.Combine(cachePath, record.captureId + ".anchor");
                byte[] bytes = null;
                if (File.Exists(file)) Try(() => bytes = File.ReadAllBytes(file));
                if (bytes == null || bytes.Length != record.anchorBytes || Hash(bytes) != record.anchorSha256)
                {
                    using (var request = Request("/api/device-anchor/" + record.captureId, "GET", 60))
                    {
                        active = request; yield return request.SendWebRequest(); active = null;
                        bytes = !request.isNetworkError && request.responseCode == 200 ? request.downloadHandler.data : null;
                    }
                    if (bytes == null || bytes.Length != record.anchorBytes || Hash(bytes) != record.anchorSha256) { failed++; continue; }
                    Try(() => { PruneCache(bytes.Length); WriteBytes(file, bytes); });
                }
                AnchoredLabel label = null;
                if (Application.isEditor)
                    Try(() => label = AnchoredLabel.Create(JsonUtility.FromJson<Vector3>(Encoding.UTF8.GetString(bytes))));
#if UNITY_WSA && !UNITY_EDITOR
                else
                {
                    for (int attempt = 0; attempt < 3 && label == null; attempt++)
                    {
                        bool done = false, abandoned = false;
                        WorldAnchorTransferBatch imported = null;
                        try
                        {
                            WorldAnchorTransferBatch.ImportAsync(bytes, (reason, batch) =>
                            {
                                if (!abandoned && reason == SerializationCompletionReason.Succeeded) imported = batch;
                                else if (batch != null) batch.Dispose();
                                done = true;
                            });
                        }
                        catch (Exception) { done = true; }
                        var deadline = Time.realtimeSinceStartup + 45;
                        while (!done && Time.realtimeSinceStartup < deadline) yield return null;
                        if (!done) abandoned = true;
                        if (imported != null)
                        {
                            Try(() => label = AnchoredLabel.Import(imported, record.captureId));
                            imported.Dispose();
                        }
                        if (label == null) yield return new WaitForSecondsRealtime(1);
                    }
                }
#endif
                if (label == null) { failed++; continue; }
                controller.AddHistoryLabel(label, record.captureId, record.serial);
                loaded++; if (label.IsLocated) located++;
                Status = "恢复标签 " + loaded + "/" + manifest.records.Length + "；未定位时请环顾原区域";
                yield return null;
            }
            restorePartial = failed > 0 || located < loaded;
            restoreDetail = "载入 " + loaded + "，已定位 " + located + "，不可恢复 " + failed +
                (manifest.total > config.maxLabels ? "（仅最近 " + config.maxLabels + " 个标签）" : "");
            Status = restoreDetail;
        }

        private IEnumerator Execute(DeviceCommand command)
        {
            if (state.lastAck != null && state.lastAck.commandId == command.commandId) { ackDelivered = false; yield break; }
            var pose = GetComponent<PoseTelemetry>();
            var deadline = Time.realtimeSinceStartup + 10;
            while ((controller.IsCapturing || controller.UploadActive || (pose != null && pose.IsRequestActive)) && Time.realtimeSinceStartup < deadline) yield return null;
            string result = "completed", detail = "";
            if (controller.IsCapturing || controller.UploadActive || (pose != null && pose.IsRequestActive))
            { result = "failed"; detail = "设备正在采集或上传，请稍后重新发送"; }
            else if (command.kind == "clear_cache" && (!DateTime.TryParse(command.expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry) || expiry.ToUniversalTime() < DateTime.UtcNow))
            { result = "expired"; detail = "清理指令已过期，未删除数据"; }
            else
            {
                state.executingCommand = command.commandId;
                if (!Try(SaveState)) yield break;
                if (command.kind == "resume" || command.kind == "new_work")
                {
                    if (!Id(command.workId)) { result = "failed"; detail = "无效工作 ID"; }
                    else
                    {
                        state.workId = command.workId;
                        if (!Try(SaveState)) yield break;
                        if (command.kind == "new_work") { controller.ClearVisibleLabels(); detail = "新工作已开始"; }
                        else { yield return Restore(command.workId, true); result = restorePartial ? "partial" : "completed"; detail = restoreDetail; }
                    }
                }
                else if (command.kind == "clear_cache")
                {
                    var categories = command.categories ?? new string[0];
                    bool success = Try(() =>
                    {
                        if (categories.Contains("pendingPhotos") && !controller.TryClearPendingPhotos()) throw new IOException("Photo upload became busy; data retained.");
                        if (categories.Contains("pendingPoses") && pose != null && !pose.TryClearPending()) throw new IOException("Pose upload became busy; data retained.");
                        if (categories.Contains("downloadedAnchors"))
                        {
                            foreach (var file in Directory.GetFiles(cachePath, "*.anchor")) File.Delete(file);
                            File.Delete(Path.Combine(cachePath, "manifest.json"));
                        }
                        if (categories.Contains("visibleLabels")) controller.ClearVisibleLabels();
                    });
                    result = success ? "completed" : "partial";
                    detail = success ? "所选头显缓存已清理，电脑工作归档保留" : "部分清理未完成：" + Status;
                }
                else { result = "failed"; detail = "不支持的命令"; }
            }
            state.lastAck = new CommandAck { deviceId = state.deviceId, commandId = command.commandId, status = result, detail = detail };
            state.executingCommand = null;
            if (Try(SaveState)) { ackDelivered = false; Status = detail; }
        }

        private UnityWebRequest Request(string path, string method, int timeout = 3)
        {
            var request = new UnityWebRequest(config.serverUrl.TrimEnd('/') + path, method);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", "Bearer " + config.apiToken);
            request.chunkedTransfer = false; request.timeout = timeout;
            return request;
        }
        private UnityWebRequest JsonRequest(string path, object body)
        {
            var request = Request(path, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
            request.SetRequestHeader("Content-Type", "application/json");
            return request;
        }
        private T Parse<T>(string json) where T : class
        {
            try { return JsonUtility.FromJson<T>(json); }
            catch (Exception error) { Status = error.Message; return null; }
        }
        private bool Try(Action action)
        {
            try { action(); return true; }
            catch (Exception error) { Status = error.Message; Debug.LogWarning("HISTORY " + error.Message); return false; }
        }
        private void SaveState() { WriteJson(Path.Combine(root, "state.json"), state); }
        private static bool Id(string value) { return value != null && Regex.IsMatch(value, "^[a-f0-9]{32}$"); }
        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static long FolderBytes(string directory)
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory).Sum(f => new FileInfo(f).Length) : 0;
        }
        private static void WriteJson(string path, object value) { WriteBytes(path, Encoding.UTF8.GetBytes(JsonUtility.ToJson(value, true))); }
        private static void WriteBytes(string path, byte[] bytes)
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        private void PruneCache(int bytes)
        {
            foreach (var file in Directory.GetFiles(cachePath, "*.anchor").OrderBy(File.GetLastWriteTimeUtc))
            {
                if (FolderBytes(cachePath) + bytes <= DownloadLimit) break;
                File.Delete(file);
            }
        }
        private void OnApplicationPause(bool value) { paused = value; }
        private void OnDestroy()
        {
            disposed = true;
            if (active != null) { active.Abort(); active.Dispose(); active = null; }
            if (heartbeat != null) { heartbeat.Abort(); heartbeat.Dispose(); heartbeat = null; }
        }
    }
}
