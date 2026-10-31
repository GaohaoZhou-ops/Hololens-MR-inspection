using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEngine;

namespace HoloLensCaptureDemo
{
    [Serializable]
    public sealed class PendingPhoto
    {
        public int version = 1;
        public string captureId;
        public string capturedAt;
        public long order;
        public int imageBytes;
        public string sha256;
    }

    // A committed directory contains a complete JPEG and its immutable request identity.
    public sealed class PendingPhotoStore
    {
        public const int MaxImageBytes = 8 * 1024 * 1024;
        private readonly List<PendingPhoto> items = new List<PendingPhoto>();
        private readonly int maxItems;
        private readonly long maxBytes;
        public string DirectoryPath { get; private set; }
        public IReadOnlyList<PendingPhoto> Items { get { return items; } }
        public long StoredBytes { get { return Directory.GetFiles(DirectoryPath, "*.jpg", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length); } }

        public PendingPhotoStore(string path, int maxItems, long maxBytes)
        {
            DirectoryPath = path;
            this.maxItems = maxItems;
            this.maxBytes = maxBytes;
            Directory.CreateDirectory(path);
            foreach (var directory in Directory.GetDirectories(path))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith(".writing-") || name.StartsWith(".sent-"))
                {
                    TryCleanup(directory);
                    continue;
                }
                if (!Regex.IsMatch(name, "^[a-f0-9]{32}$")) continue;
                var photo = JsonUtility.FromJson<PendingPhoto>(File.ReadAllText(Path.Combine(directory, "capture.json")));
                if (photo == null || photo.version != 1 || photo.captureId != name || photo.imageBytes < 4 ||
                    photo.imageBytes > MaxImageBytes || string.IsNullOrEmpty(photo.sha256) ||
                    new FileInfo(Path.Combine(directory, "photo.jpg")).Length != photo.imageBytes)
                    throw new InvalidDataException("Pending photo is damaged; files retained: " + name);
                items.Add(photo);
            }
            items.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : string.CompareOrdinal(a.captureId, b.captureId));
        }

        public bool HasCapacity(int imageBytes = 0)
        {
            return items.Count < maxItems && StoredBytes + imageBytes <= maxBytes;
        }

        public PendingPhoto Enqueue(string captureId, byte[] image)
        {
            if (!Regex.IsMatch(captureId, "^[a-f0-9]{32}$")) throw new InvalidDataException("Invalid capture ID.");
            if (image == null || image.Length < 4 || image.Length > MaxImageBytes)
                throw new InvalidDataException("Photo must fit the receiver's 8 MiB limit.");
            if (!HasCapacity(image.Length)) throw new IOException("Upload queue is full. Existing photos have been kept.");
            var photo = new PendingPhoto
            {
                captureId = captureId, capturedAt = DateTime.UtcNow.ToString("o"), imageBytes = image.Length,
                sha256 = Hash(image), order = Math.Max(DateTime.UtcNow.Ticks, items.Count == 0 ? 0 : items[items.Count - 1].order + 1)
            };
            var temporary = Path.Combine(DirectoryPath, ".writing-" + captureId);
            try
            {
                Directory.CreateDirectory(temporary);
                File.WriteAllBytes(Path.Combine(temporary, "photo.jpg"), image);
                File.WriteAllText(Path.Combine(temporary, "capture.json"), JsonUtility.ToJson(photo, true));
                Directory.Move(temporary, Path.Combine(DirectoryPath, captureId));
                items.Add(photo);
                return photo;
            }
            catch { TryCleanup(temporary); throw; }
        }

        public byte[] ReadImage(PendingPhoto photo)
        {
            var image = File.ReadAllBytes(Path.Combine(DirectoryPath, photo.captureId, "photo.jpg"));
            if (image.Length != photo.imageBytes || Hash(image) != photo.sha256)
                throw new InvalidDataException("Pending image integrity check failed. Original files retained.");
            return image;
        }

        public void Complete(PendingPhoto photo)
        {
            var sent = Path.Combine(DirectoryPath, ".sent-" + photo.captureId);
            Directory.Move(Path.Combine(DirectoryPath, photo.captureId), sent);
            items.Remove(photo);
            // A crash after this rename must not turn an acknowledged upload into a pending one.
            TryCleanup(sent);
        }

        public int ClearPending()
        {
            int count = 0;
            foreach (var photo in items.ToArray()) { Complete(photo); count++; }
            return count;
        }

        private static string Hash(byte[] image)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(image)).Replace("-", "").ToLowerInvariant();
        }

        private static void TryCleanup(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (IOException) { Debug.LogWarning("Outbox cleanup deferred: " + Path.GetFileName(path)); }
            catch (UnauthorizedAccessException) { Debug.LogWarning("Outbox cleanup deferred: " + Path.GetFileName(path)); }
        }
    }
}
