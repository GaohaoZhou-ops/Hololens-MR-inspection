using System;
using System.IO;
using UnityEngine;

namespace HoloLensCaptureDemo.Editor
{
    internal static class PhotoQueueChecks
    {
        public static void Run()
        {
            var path = Path.Combine(DemoBuild.Root, "logs", "queue-store-check-" + Guid.NewGuid().ToString("N"));
            var jpeg = new byte[] {255, 216, 255, 7, 8, 9, 255, 217};
            try
            {
                var store = new PendingPhotoStore(path, 2, 1024);
                var first = store.Enqueue(Guid.NewGuid().ToString("N"), jpeg);
                var second = store.Enqueue(Guid.NewGuid().ToString("N"), jpeg);
                Require(!store.HasCapacity(), "Queue item limit was not enforced.");
                bool full = false;
                try { store.Enqueue(Guid.NewGuid().ToString("N"), jpeg); }
                catch (IOException) { full = true; }
                Require(full && store.Items.Count == 2, "Full queue lost an older photo.");
                store = new PendingPhotoStore(path, 2, 1024);
                Require(store.Items[0].captureId == first.captureId && store.Items[1].captureId == second.captureId, "Disk reload lost FIFO order or request IDs.");
                Require(Convert.ToBase64String(store.ReadImage(store.Items[0])) == Convert.ToBase64String(jpeg), "JPEG bytes changed after reload.");
                var photoPath = Path.Combine(path, first.captureId, "photo.jpg");
                var damaged = (byte[])jpeg.Clone(); damaged[3] = 17; File.WriteAllBytes(photoPath, damaged);
                bool rejected = false;
                try { store.ReadImage(store.Items[0]); } catch (InvalidDataException) { rejected = true; }
                Require(rejected && File.Exists(photoPath), "Corrupt photo was not retained and rejected.");
                File.WriteAllBytes(photoPath, jpeg);
                store.Complete(store.Items[0]);
                Require(!Directory.Exists(Path.Combine(path, first.captureId)), "Acknowledged photo still occupies queue storage.");
                store = new PendingPhotoStore(path, 2, 8);
                Require(!store.HasCapacity(1), "Queue byte limit was not enforced.");
                Directory.Move(Path.Combine(path, second.captureId), Path.Combine(path, ".sent-" + second.captureId));
                Directory.CreateDirectory(Path.Combine(path, ".writing-incomplete"));
                store = new PendingPhotoStore(path, 2, 1024);
                Require(store.Items.Count == 0 && Directory.GetDirectories(path).Length == 0, "Interrupted cleanup would resend an acknowledged photo.");
                Debug.Log("CAPTURE_QUEUE_STORAGE_CHECKS_PASSED");
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
