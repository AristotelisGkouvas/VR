using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// For sensors in alarm or alert that are out of view: an arrow at the edge of the view
    /// pointing the way to turn, red for alarm, amber for alert, nearest first. Without them a
    /// sensor in alarm behind the user goes unseen. Menu switch, on by default.
    /// </summary>
    public class AlarmBeacons : MonoBehaviour
    {
        private const string EnabledKey = "ui.alarmBeacons";
        private const int MaxBeacons = 5;
        /// <summary>Within this angle of the gaze the icon itself is in view: no arrow.</summary>
        private const float InViewDegrees = 35f;
        private const float RingDistance = 0.8f, RingRadius = 0.22f;
        private const float ScanSeconds = 0.25f;

        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(EnabledKey, 1) == 1;
            set { PlayerPrefs.SetInt(EnabledKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        private MarkerManager markers;
        private readonly List<Marker> alerting = new List<Marker>();
        private readonly List<RectTransform> arrows = new List<RectTransform>();
        private readonly List<Image> arrowImages = new List<Image>();
        private float nextScan;

        public void Init(MarkerManager markerManager) => markers = markerManager;

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            bool on = Enabled && markers != null && cam != null && !UiState.AnyModal;
            if (on && Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + ScanSeconds;
                alerting.Clear();
                foreach (var kv in markers.ActiveMarkers)
                {
                    Marker m = kv.Value;
                    if (m != null && m.gameObject.activeInHierarchy && MarkerManager.IsAlerting(m.Data)) alerting.Add(m);
                }
                Vector3 eye = cam.transform.position;
                // Alarms before alerts, then nearest first.
                alerting.Sort((a, b) =>
                {
                    int sa = a.Data.status == "alarm" ? 0 : 1, sb = b.Data.status == "alarm" ? 0 : 1;
                    if (sa != sb) return sa.CompareTo(sb);
                    return (a.transform.position - eye).sqrMagnitude.CompareTo((b.transform.position - eye).sqrMagnitude);
                });
            }

            int used = 0;
            if (on)
            {
                Transform head = cam.transform;
                foreach (Marker m in alerting)
                {
                    if (used >= MaxBeacons) break;
                    if (m == null || !m.gameObject.activeInHierarchy) continue;
                    Vector3 to = m.transform.position - head.position;
                    if (Vector3.Angle(head.forward, to) <= InViewDegrees) continue;
                    // The way to turn, in the view plane: x right, y up.
                    Vector3 local = head.InverseTransformDirection(to);
                    Vector2 dir = new Vector2(local.x, local.y);
                    if (dir.sqrMagnitude < 1e-6f) dir = Vector2.right; // straight behind: turn right
                    dir.Normalize();
                    RectTransform arrow = Arrow(used++);
                    arrow.position = head.position + head.forward * RingDistance
                                     + (head.right * dir.x + head.up * dir.y) * RingRadius;
                    // chevron-right points along +x; roll it towards the target.
                    arrow.rotation = head.rotation * Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
                    arrowImages[used - 1].color = m.Data.status == "alarm" ? Brand.Danger : Brand.Warning;
                }
            }
            for (int i = used; i < arrows.Count; i++)
                if (arrows[i].gameObject.activeSelf) arrows[i].gameObject.SetActive(false);
        }

        private RectTransform Arrow(int i)
        {
            while (arrows.Count <= i)
            {
                RectTransform c = VrUi.CreateCanvas("AlarmBeacon", transform, new Vector2(64f, 64f), 0.0006f);
                VrUi.Icon("Halo", c, 0, 0, 64, Brand.DiscSprite, Brand.Surface.WithAlpha(0.85f));
                arrowImages.Add(VrUi.Icon("Arrow", c, 12, 12, 40, Brand.UiIconSprite("chevron-right"), Brand.Danger));
                arrows.Add(c);
            }
            RectTransform a = arrows[i];
            if (!a.gameObject.activeSelf) a.gameObject.SetActive(true);
            return a;
        }
    }
}
