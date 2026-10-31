using System.Collections.Generic;

namespace HoloLensCaptureDemo
{
    // One rising edge per source. Busy presses are consumed, never queued for later.
    public sealed class CapturePressGate
    {
        private readonly HashSet<uint> held = new HashSet<uint>();
        private float nextPress;
        public bool Press(uint source, float now, bool ready)
        {
            if (!held.Add(source) || !ready || now < nextPress) return false;
            nextPress = now + .3f;
            return true;
        }
        public void Release(uint source) { held.Remove(source); }
        public void Reset() { held.Clear(); nextPress = 0; }
    }
}
