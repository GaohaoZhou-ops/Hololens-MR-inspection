using System;
using System.Linq;
using System.Threading;
using UnityEngine;
#if UNITY_WSA && !UNITY_EDITOR
using Windows.Networking;
using Windows.Networking.Connectivity;
#else
using System.Net.NetworkInformation;
using System.Net.Sockets;
#endif

namespace HoloLensCaptureDemo
{
    // This diagnostic belongs to the view, while capture labels stay in world space.
    public sealed class NetworkStatusHud : MonoBehaviour
    {
        public string DisplayText { get; private set; }
        private Camera view;
        private TextMesh text;
        private Font font;
        private int refreshRequested = 1;
        private float nextRefresh;
        private PoseTelemetry pose;
        private float nextPoseLookup;

        private void Awake()
        {
            view = GetComponent<Camera>();
            var visual = new GameObject("Device network HUD");
            visual.transform.SetParent(transform, false);
            text = visual.AddComponent<TextMesh>();
            // Prefer a CJK font for Chinese SSIDs; use the built-in font as a fallback.
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial" }, 40);
            text.font = font != null ? font : Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
            text.GetComponent<MeshRenderer>().sortingOrder = 100;
            text.fontSize = 40;
            text.characterSize = 0.003f;
            text.anchor = TextAnchor.UpperLeft;
            text.alignment = TextAlignment.Left;
            text.richText = false;
            text.color = new Color(0.7f, 1f, 0.85f);
        }

        private void OnEnable()
        {
            Interlocked.Exchange(ref refreshRequested, 1);
#if UNITY_WSA && !UNITY_EDITOR
            NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
#endif
        }

        private void OnDisable()
        {
#if UNITY_WSA && !UNITY_EDITOR
            NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
#endif
        }

        private void OnDestroy()
        {
            if (text != null) Destroy(text.gameObject);
            if (font != null) Destroy(font);
        }

#if UNITY_WSA && !UNITY_EDITOR
        private void OnNetworkChanged(object sender)
        {
            // WinRT raises this on a background thread. Touch Unity objects in Update only.
            Interlocked.Exchange(ref refreshRequested, 1);
        }
#endif

        private void OnApplicationFocus(bool focused)
        {
            if (focused) Interlocked.Exchange(ref refreshRequested, 1);
        }

        private void Update()
        {
            if (Interlocked.Exchange(ref refreshRequested, 0) == 0 && Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 5f;
            string value;
            try { value = ReadNetwork(); }
            catch (Exception error)
            {
                value = "Wi-Fi: unavailable\nIPv4: unavailable";
                if (DisplayText != value) Debug.LogWarning("NETWORK_HUD " + error.GetType().Name + ": " + error.Message);
            }
            if (value == DisplayText) return;
            DisplayText = value;
            text.text = value;
            Debug.Log("NETWORK_HUD " + value.Replace('\n', ' '));
        }

        private void LateUpdate()
        {
            if (view == null || text == null) return;
            if (pose == null && Time.unscaledTime >= nextPoseLookup)
            {
                pose = FindObjectOfType<PoseTelemetry>(); nextPoseLookup = Time.unscaledTime + 2f;
            }
            if (pose != null)
                text.text = DisplayText + "\nPose 1Hz: " + pose.State + " | queued " + pose.PendingCount +
                    (pose.DroppedSamples > 0 ? " | skipped " + pose.DroppedSamples : "");
            // Inset from the optical edge. Use the camera projection so resizing and XR work.
            const float depth = 1.4f;
            text.transform.position = view.ViewportToWorldPoint(new Vector3(0.06f, 0.93f, depth));
            text.transform.localRotation = Quaternion.identity;
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "(SSID unavailable)";
            // SSIDs are data, never rich text or extra diagnostic lines.
            return new string(name.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        }

        private static string ReadNetwork()
        {
#if UNITY_WSA && !UNITY_EDITOR
            var hosts = NetworkInformation.GetHostNames().Where(h => h.Type == HostNameType.Ipv4 &&
                h.IPInformation != null && h.IPInformation.NetworkAdapter != null).ToArray();
            var preferred = NetworkInformation.GetInternetConnectionProfile();
            var profiles = NetworkInformation.GetConnectionProfiles()
                .Where(p => p.NetworkAdapter != null && p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
                .OrderByDescending(p => p.IsWlanConnectionProfile)
                .ThenByDescending(p => preferred != null && preferred.NetworkAdapter != null &&
                    p.NetworkAdapter.NetworkAdapterId == preferred.NetworkAdapter.NetworkAdapterId).ToArray();
            var profile = profiles.FirstOrDefault();
            if (profile == null) return "Wi-Fi: disconnected\nIPv4: --";
            var addresses = hosts.Where(h => h.IPInformation.NetworkAdapter.NetworkAdapterId == profile.NetworkAdapter.NetworkAdapterId)
                .Select(h => h.CanonicalName).Distinct().OrderBy(ip => ip.StartsWith("169.254.")).ToArray();
            var network = profile.IsWlanConnectionProfile
                ? "Wi-Fi: " + SafeName(profile.WlanConnectionProfileDetails.GetConnectedSsid())
                : "Wi-Fi: -- (Ethernet / virtual)";
            return network + "\nIPv4: " + (addresses.Length == 0 ? "waiting for address" : string.Join("\n      ", addresses));
#else
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString()).FirstOrDefault();
            return "NETWORK (EDITOR PC)\nWi-Fi: check on headset\nIPv4: " + (address ?? "disconnected");
#endif
        }
    }
}
