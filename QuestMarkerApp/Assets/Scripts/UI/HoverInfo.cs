using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// A laser from the pointing hand into the site: it snaps onto the sensor it is aimed at
    /// (near or 100 m away) and a small tag above that sensor tells what it is — name, type,
    /// state, height, distance, latest movement — without pulling the trigger. The trigger
    /// still opens the full card.
    /// </summary>
    public class HoverInfo : MonoBehaviour
    {
        private const float IdleLength = 4f;       // metres of ray when aimed at nothing
        private const float ShowAfter = 0.12f;     // hover this long before the tag appears
        private const float TagW = 380f, TagH = 118f;

        private ProjectAlignment project;
        private MarkerManager markers;
        private LineRenderer ray;
        private Transform dot;
        private Transform tag;
        private RectTransform tagCanvas;
        private Image tagIcon, tagDisc, stateDot;
        private TextMeshProUGUI tagName, tagSub, tagState, tagExtra;
        private Marker target;
        private float targetSince;
        private int subKey = int.MinValue;
        private ProjectPoint subPoint;
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        public void Init(ProjectAlignment alignment, MarkerManager markerManager)
        {
            project = alignment;
            markers = markerManager;
            Build();
        }

        private void LateUpdate()
        {
            Camera cam = Headset.Camera;
            Transform hand = null;
#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && Hands.DominantConnected) hand = Hands.DominantAnchor(rig);
#endif
            // The world ray belongs to "looking at the site": not while aligning (the tip
            // cursor rules then), not under a menu / list / keyboard, not over a panel.
            bool worldMode = project != null && project.HasProject && !project.IsModeActive
                             && !MainMenu.IsOpen && !FlowPanel.IsOpen && !VrKeyboard.IsOpen;
            bool show = hand != null && cam != null && worldMode && !VrPointer.IsHovering && !VrPointer.IsDragging;
            if (!show)
            {
                SetVisible(false);
                SetTarget(null);
                return;
            }
            SetVisible(true);

            Marker aimed = markers.Highlighted;
            if (aimed != null && !aimed.gameObject.activeInHierarchy) aimed = null;
            if (aimed != target) SetTarget(aimed);

            Vector3 start = hand.position;
            Vector3 end = target != null ? target.transform.position : start + hand.forward * IdleLength;
            ray.SetPosition(0, start);
            ray.SetPosition(1, end);
            Color c = target != null ? Brand.Fg : Brand.Primary;
            ray.startColor = c.WithAlpha(0.85f);
            ray.endColor = c.WithAlpha(target != null ? 0.6f : 0f);
            float dist = Vector3.Distance(cam.transform.position, end);
            ray.startWidth = 0.004f;
            ray.endWidth = target != null ? Mathf.Max(0.003f, dist * 0.002f) : 0.001f;

            dot.gameObject.SetActive(target == null);
            if (target == null) dot.position = start + hand.forward * 0.12f;

            bool tagOn = target != null && Time.time - targetSince >= ShowAfter
                         && (!SensorCard.IsOpen || SensorCard.MarkerIdOpen != target.MarkerId);
            if (tag.gameObject.activeSelf != tagOn) tag.gameObject.SetActive(tagOn);
            if (tagOn) PlaceTag(cam, dist);
        }

        private void SetVisible(bool on)
        {
            if (ray.gameObject.activeSelf != on) ray.gameObject.SetActive(on);
            if (!on)
            {
                dot.gameObject.SetActive(false);
                tag.gameObject.SetActive(false);
            }
        }

        private void SetTarget(Marker m)
        {
            target = m;
            targetSince = Time.time;
            if (m == null || !project.TryGetPoint(m.MarkerId, out ProjectPoint p))
            {
                target = null;
                return;
            }
#if !MOBILE_PREVIEW
            Feedback.Buzz(Hands.Dominant, 0.12f, 0.02f, 0.9f); // a faint "snap" onto the sensor
#endif
            Fill(p);
        }

        private void Fill(ProjectPoint p)
        {
            Color family = ColorUtility.TryParseHtmlString(p.color ?? "", out Color pc) ? pc : Brand.Primary;
            tagDisc.color = family;
            tagIcon.sprite = Brand.IconSprite(string.IsNullOrEmpty(p.icon) ? "sensor-point" : p.icon);
            tagName.text = string.IsNullOrEmpty(p.label) ? p.id : p.label;

            switch ((p.alertState ?? "none").ToLowerInvariant())
            {
                case "alarm": stateDot.color = Brand.Danger; tagState.text = L.T("Συναγερμός", "Alarm"); break;
                case "alert": stateDot.color = Brand.Warning; tagState.text = L.T("Προειδοποίηση", "Alert"); break;
                case "reference": stateDot.color = Brand.Control; tagState.text = L.T("Αναφορά", "Reference"); break;
                case "none": stateDot.color = Brand.Primary; tagState.text = L.T("Κανονικό", "Normal"); break;
                default: stateDot.color = Brand.FgFaint; tagState.text = L.T("Άγνωστο", "Unknown"); break;
            }
            tagState.text += "  ·  " + SensorCard.FormatDate(p.latestDate);

            // Latest movement from the loaded survey history (no network on hover).
            string extra = "";
            if (SensorHistory.TryDisplacementAt(p.sensorId, DateTime.MaxValue, out SensorHistory.Sample s))
            {
                double toMm = SensorHistory.MetresPerUnit(p.sensorId) * 1000.0; // the type's unit, not assumed mm
                double mm = Math.Sqrt((s.Dx ?? 0) * (s.Dx ?? 0) + (s.Dy ?? 0) * (s.Dy ?? 0) + (s.Dz ?? 0) * (s.Dz ?? 0)) * toMm;
                double dz = (s.Dz ?? 0) * toMm;
                extra = L.En
                    ? $"Displacement <b>{mm:F1} mm</b>  ·  dz {dz:+0.0;-0.0;0.0} mm"
                    : $"Μετακίνηση <b>{mm:F1} mm</b>  ·  dz {dz:+0.0;-0.0;0.0} mm";
            }
            if (p.approx) extra = (extra.Length > 0 ? extra + "  ·  " : "") + L.T("θέση κατά προσέγγιση", "approximate position");
            tagExtra.text = extra.Length > 0 ? extra : L.T("Trigger: πλήρης καρτέλα", "Trigger: full card");
        }

        // The tag floats at arm's length (TagDistance) on the line of sight to the sensor, just
        // above that line, so it reads the same at 2 m and at 80 m and nothing nearer can hide
        // it. It used to sit on the sensor itself, scaled with distance: that kept the letters
        // at a third of the menu's size, and a far sensor's tag vanished behind nearer icons.
        private const float TagDistance = 0.9f;
        private const float TagScale = 0.0009f;

        private void PlaceTag(Camera cam, float dist)
        {
            ProjectPoint p = null;
            if (target != null) project.TryGetPoint(target.MarkerId, out p);
            float h = Vector3.ProjectOnPlane(target.transform.position - cam.transform.position, Vector3.up).magnitude;
            int key = h < 10f ? Mathf.RoundToInt(h * 10f) : 1000000 + Mathf.RoundToInt(h);
            if (key != subKey || !ReferenceEquals(p, subPoint))
            {
                subKey = key;
                subPoint = p;
                string type = p != null && !string.IsNullOrEmpty(p.typeLabel) ? p.typeLabel + "  ·  " : "";
                tagSub.text = p != null
                    ? $"{type}H {p.h:F2}  ·  {(h < 10f ? h.ToString("F1") : h.ToString("F0"))} m"
                    : "";
            }

            Vector3 toTarget = target.transform.position - cam.transform.position;
            float d = Mathf.Max(0.05f, toTarget.magnitude);
            Vector3 dir = toTarget / d;
            // A sensor nearer than arm's length keeps the tag on itself, same apparent size.
            float place = Mathf.Min(d, TagDistance);
            float k = place / TagDistance;
            tagCanvas.localScale = Vector3.one * TagScale * k;
            float iconRadius = Mathf.Max(0.06f, d * 0.03f) * 0.5f;
            float lift = d < TagDistance ? Mathf.Max(0.05f * k, iconRadius * 1.6f) : 0.05f;
            tag.position = cam.transform.position + dir * place + cam.transform.up * lift;
            VrUi.FaceViewer(tag, cam.transform);
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        private void Build()
        {
            var rayGo = new GameObject("PointerRay");
            rayGo.transform.SetParent(transform, false);
            ray = rayGo.AddComponent<LineRenderer>();
            ray.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            ray.positionCount = 2;
            ray.useWorldSpace = true;
            ray.numCapVertices = 2;
            ray.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ray.receiveShadows = false;
            ray.sortingOrder = 140;
            rayGo.SetActive(false);

            var dotGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(dotGo.GetComponent<Collider>());
            dotGo.name = "RayDot";
            dotGo.transform.SetParent(transform, false);
            dotGo.transform.localScale = Vector3.one * 0.008f;
            var dr = dotGo.GetComponent<Renderer>();
            dr.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = Brand.Primary };
            dr.sortingOrder = 141;
            dot = dotGo.transform;
            dotGo.SetActive(false);

            tag = new GameObject("HoverTag").transform;
            tag.SetParent(transform, false);
            tagCanvas = VrUi.CreateCanvas("Canvas", tag, new Vector2(TagW, TagH), TagScale);
            tagCanvas.pivot = new Vector2(0.5f, 0f); // bottom edge sits just above the line of sight
            tagCanvas.GetComponent<Canvas>().sortingOrder = 110;
            VrUi.Card("Bg", tagCanvas, 0, 0, TagW, TagH, Brand.Surface.WithAlpha(0.95f), Brand.BorderStrong, 18);
            tagDisc = VrUi.Icon("Disc", tagCanvas, 16, 16, 44, Brand.DiscSprite, Brand.Primary);
            tagIcon = VrUi.Icon("Glyph", tagCanvas, 26, 26, 24, null, Color.white);
            tagName = VrUi.Text("Name", tagCanvas, 72, 10, TagW - 86, 30, "", 20f, Brand.Fg, true);
            tagSub = VrUi.Text("Sub", tagCanvas, 72, 38, TagW - 86, 24, "", 14f, Brand.FgSubtle);
            stateDot = VrUi.Icon("StateDot", tagCanvas, 18, 72, 10, Brand.DiscSprite, Brand.Primary);
            tagState = VrUi.Text("State", tagCanvas, 36, 64, TagW - 50, 24, "", 14f, Brand.FgMuted);
            tagExtra = VrUi.Text("Extra", tagCanvas, 18, 88, TagW - 32, 24, "", 14f, Brand.Fg);
            tag.gameObject.SetActive(false);
        }
    }
}
