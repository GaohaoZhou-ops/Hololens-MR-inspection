using UnityEngine;
#if UNITY_WSA && !UNITY_EDITOR
using UnityEngine.XR.WSA;
using UnityEngine.XR.WSA.Persistence;
#endif

namespace HoloLensCaptureDemo
{
    public sealed class AnchoredLabel : MonoBehaviour
    {
        private TextMesh text;
        private bool hasResult;
#if UNITY_WSA && !UNITY_EDITOR
        private WorldAnchor anchor;
#endif
        public string Serial { get; private set; }
        public bool IsLocated
        {
            get
            {
#if UNITY_WSA && !UNITY_EDITOR
                return anchor != null && anchor.isLocated;
#else
                return true;
#endif
            }
        }

        public static AnchoredLabel Create(Vector3 point)
        {
            var label = CreateVisual(point);
#if UNITY_WSA && !UNITY_EDITOR
            label.anchor = label.gameObject.AddComponent<WorldAnchor>();
#endif
            return label;
        }

        private static AnchoredLabel CreateVisual(Vector3 point)
        {
            var root = new GameObject("Capture anchor");
            root.transform.position = point;
            var label = root.AddComponent<AnchoredLabel>();
            var visual = new GameObject("Serial label");
            visual.transform.SetParent(root.transform, false);
            label.text = visual.AddComponent<TextMesh>();
            label.text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.text.GetComponent<MeshRenderer>().sharedMaterial = label.text.font.material;
            label.text.fontSize = 48;
            label.text.characterSize = 0.008f;
            label.text.anchor = TextAnchor.MiddleCenter;
            label.text.alignment = TextAlignment.Center;
            label.text.color = new Color(0.25f, 1f, 0.65f);
            visual.SetActive(false);
            // Establish the anchor BEFORE the network round trip. The response only
            // updates its text, never its position or parent.
            return label;
        }

#if UNITY_WSA && !UNITY_EDITOR
        public bool SavePending(WorldAnchorStore store, string id)
        {
            return store != null && anchor != null && anchor.isLocated && store.Save(id, anchor);
        }

        public static AnchoredLabel RestorePending(WorldAnchorStore store, string id)
        {
            if (store == null) return null;
            var label = CreateVisual(Vector3.zero);
            try { label.anchor = store.Load(id, label.gameObject); }
            catch (System.Exception error) { Debug.LogWarning("Pending anchor restore: " + error.Message); }
            if (label.anchor != null) return label;
            Destroy(label.gameObject);
            return null;
        }
#endif

        public void SetResult(string serial)
        {
            Serial = serial;
            name = "Capture " + serial;
            text.text = serial;
            hasResult = true;
        }

        private void LateUpdate()
        {
            if (text == null) return;
            text.gameObject.SetActive(hasResult && IsLocated);
            var camera = Camera.main;
            if (camera != null && hasResult)
            {
                var direction = text.transform.position - camera.transform.position;
                if (direction.sqrMagnitude > 0.0001f)
                    text.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
            }
        }
    }
}
