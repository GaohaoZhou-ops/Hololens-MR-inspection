using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HoloLensCaptureDemo
{
    [Serializable] public sealed class PoseSession
    {
        public string sessionId, startedAt, deviceName, clientKind;
    }
    [Serializable] public sealed class PoseSample
    {
        public int sequence, segment;
        public float elapsed;
        public string sampledAt, trackingState;
        public bool tracked;
        public float x, y, z, qx, qy, qz, qw;
    }
    [Serializable] public sealed class PoseBatch
    {
        public PoseSession session;
        public PoseSample[] samples;
    }

    // Append-only segments: <=60 samples/file. An ACK is durable when that segment
    // is removed; a crash before removal replays identical IDs, which the PC deduplicates.
    public sealed class PoseOutbox
    {
        public const int Capacity = 7200; // Two hours of pending 1 Hz samples.
        private sealed class Segment
        {
            public string path;
            public PoseSession session;
            public readonly List<PoseSample> samples = new List<PoseSample>();
            public int written;
        }
        private readonly List<Segment> segments = new List<Segment>();
        private readonly string directory;
        private readonly int capacity;
        private Segment active;
        public int Count { get { return segments.Sum(s => s.samples.Count); } }
        public long StoredBytes { get { return Directory.GetFiles(directory, "*.jsonl").Sum(p => new FileInfo(p).Length); } }

        public int ClearPending()
        {
            int count = 0;
            foreach (var segment in segments.ToArray())
            {
                File.Delete(segment.path);
                count += segment.samples.Count;
                segments.Remove(segment);
                if (active == segment) active = null;
            }
            return count;
        }

        public PoseOutbox(string directory, int capacity = Capacity)
        {
            this.directory = directory;
            this.capacity = capacity;
            Directory.CreateDirectory(directory);
            foreach (var path in Directory.GetFiles(directory, "*.jsonl"))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                // A torn final append is not a complete sample. Never append to this file again.
                var lastNewline = text.LastIndexOf('\n');
                if (lastNewline < 0) throw new InvalidDataException("Pose segment header is damaged; file retained.");
                var lines = text.Substring(0, lastNewline).Split('\n');
                var segment = new Segment { path = path, session = JsonUtility.FromJson<PoseSession>(lines[0]) };
                if (segment.session == null || string.IsNullOrEmpty(segment.session.sessionId))
                    throw new InvalidDataException("Pose session is damaged; file retained.");
                int previous = -1;
                foreach (var line in lines.Skip(1))
                {
                    var sample = JsonUtility.FromJson<PoseSample>(line);
                    if (sample == null || sample.sequence <= previous || string.IsNullOrEmpty(sample.sampledAt))
                        throw new InvalidDataException("Pose sample is damaged; file retained.");
                    segment.samples.Add(sample); previous = sample.sequence;
                }
                if (segment.samples.Count > 60) throw new InvalidDataException("Pose segment exceeds capacity.");
                segment.written = segment.samples.Count;
                if (segment.samples.Count > 0) segments.Add(segment);
                else File.Delete(path);
            }
            segments.Sort((a, b) => {
                int session = string.CompareOrdinal(a.session.startedAt, b.session.startedAt);
                if (session == 0) session = string.CompareOrdinal(a.session.sessionId, b.session.sessionId);
                return session != 0 ? session : a.samples[0].sequence.CompareTo(b.samples[0].sequence);
            });
        }

        public bool Append(PoseSession session, PoseSample sample)
        {
            if (Count >= capacity) return false;
            if (active == null || active.written >= 60 || active.session.sessionId != session.sessionId)
            {
                active = new Segment { session = session,
                    path = Path.Combine(directory, session.sessionId + "-" + sample.sequence.ToString("D10") + ".jsonl") };
                File.WriteAllText(active.path, JsonUtility.ToJson(session) + "\n", new UTF8Encoding(false));
                segments.Add(active);
            }
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(sample) + "\n");
            using (var stream = new FileStream(active.path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            active.samples.Add(sample); active.written++;
            return true;
        }

        public PoseBatch Oldest()
        {
            if (segments.Count == 0) return null;
            return new PoseBatch { session = segments[0].session, samples = segments[0].samples.ToArray() };
        }

        public void Acknowledge(PoseBatch batch)
        {
            var first = segments[0];
            if (first.session.sessionId != batch.session.sessionId) throw new InvalidOperationException("Pose ACK session mismatch.");
            var through = batch.samples[batch.samples.Length - 1].sequence;
            if (first.samples.All(s => s.sequence <= through))
            {
                File.Delete(first.path); // Failed deletion keeps the batch pending for an idempotent retry.
                segments.RemoveAt(0);
                if (active == first) active = null;
            }
            else first.samples.RemoveAll(s => s.sequence <= through);
        }
    }
}
