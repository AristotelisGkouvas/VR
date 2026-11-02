using TMPro;
using UnityEngine;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Tape measure: the trigger drops a point at the controller tip, or on the sensor the ray
    /// is on; a second one closes the span and reads its length, plus ΔE / ΔN / ΔH in the
    /// project's grid once aligned (clearances, a check point off the control points). A third
    /// trigger starts over; B leaves. Opened from the menu (Display page).
    /// </summary>
    public class MeasureTool : MonoBehaviour
    {
        private const float TipForward = 0.06f; // the controller tip, as ProjectAlignment touches with it

        public static bool IsActive { get; private set; }
        /// <summary>Frame it closed, so the B that closed it does nothing else.</summary>
        public static int ClosedFrame { get; private set; } = -1;

        private ProjectAlignment project;
        private MarkerManager markers;
        private LineRenderer line;
        private Transform dotA, dotB;
        private TextMeshPro readout;
        private Vector3? a, b;
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        public void Init(ProjectAlignment alignment, MarkerManager markerManager)
        {
            project = alignment;
            markers = markerManager;
        }

        public void Open()
        {
            if (line == null) Build();
            a = b = null;
            IsActive = true;
            Feedback.Play(Feedback.Sound.Whoosh, 0.5f);
        }

        public void Close()
        {
            if (IsActive) ClosedFrame = Time.frameCount;
            IsActive = false;
            a = b = null;
            if (line != null) Show(false, false, false);
        }

        private void Update()
        {
            if (!IsActive) return;
#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            Transform hand = rig != null && Hands.DominantConnected ? Hands.DominantAnchor(rig) : null;
            if (hand == null) return;
            Vector3 tip = Pick(hand, out bool onSensor);

            if (!UiState.InputBlocked)
            {
                if (Hands.DominantSecondaryDown() && !SensorNavigator.IsActive
                    && SensorNavigator.StoppedFrame != Time.frameCount)
                {
                    Feedback.Play(Feedback.Sound.Undo, 0.6f);
                    Close();
                    return;
                }
                if (Hands.TriggerDown() && !VrPointer.ConsumedThisFrame)
                {
                    if (!a.HasValue || b.HasValue) { a = tip; b = null; }
                    else b = tip;
                    Feedback.Play(Feedback.Sound.Click);
                    Feedback.Buzz(Hands.Dominant, onSensor ? 0.5f : 0.3f, 0.04f);
                }
            }

            // Until the second point lands the span follows the hand.
            Vector3? end = b ?? (a.HasValue ? tip : (Vector3?)null);
            Show(a.HasValue, end.HasValue, true);
            dotB.position = end ?? tip;
            if (!a.HasValue) { readout.text = L.T("Trigger: πρώτο σημείο", "Trigger: first point"); Place(tip, tip); return; }
            dotA.position = a.Value;
            line.SetPosition(0, a.Value);
            line.SetPosition(1, end.Value);
            readout.text = Text(a.Value, end.Value) + (b.HasValue ? "" : L.T("\nTrigger: δεύτερο σημείο", "\nTrigger: second point"));
            Place(a.Value, end.Value);
#endif
        }

#if !MOBILE_PREVIEW
        // The sensor under the ray snaps; otherwise the controller tip.
        private Vector3 Pick(Transform hand, out bool onSensor)
        {
            Marker aimed = markers != null ? markers.Highlighted : null;
            onSensor = aimed != null;
            return onSensor ? aimed.transform.position : hand.position + hand.forward * TipForward;
        }
#endif

        private string Text(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            string length = $"{d.magnitude:0.00} m";
            if (project != null && project.TryProjectVector(d, out Vector3 p))
                return length + $"\nΔE {p.x:+0.00;-0.00} · ΔN {p.z:+0.00;-0.00} · ΔH {p.y:+0.00;-0.00}";
            float flat = new Vector2(d.x, d.z).magnitude;
            return length + (L.En ? $"\nlevel {flat:0.00} · height {d.y:+0.00;-0.00}" : $"\nοριζ. {flat:0.00} · ύψος {d.y:+0.00;-0.00}");
        }

        // The readout over the middle of the span, facing the eyes, readable at any distance.
        private void Place(Vector3 from, Vector3 to)
        {
            Camera cam = Headset.Camera;
            if (cam == null) return;
            Vector3 mid = (from + to) * 0.5f + Vector3.up * 0.06f;
            float dist = Mathf.Max(0.3f, Vector3.Distance(cam.transform.position, mid));
            readout.transform.position = mid;
            readout.transform.rotation = Quaternion.LookRotation(mid - cam.transform.position, Vector3.up);
            readout.transform.localScale = Vector3.one * dist * 0.012f;
            line.widthMultiplier = Mathf.Max(0.003f, dist * 0.003f);
            dotA.localScale = dotB.localScale = Vector3.one * Mathf.Max(0.012f, dist * 0.012f);
            Quaternion face = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
            dotA.rotation = dotB.rotation = face;
        }

        private void Show(bool first, bool span, bool label)
        {
            dotA.gameObject.SetActive(first);
            dotB.gameObject.SetActive(IsActive);
            line.gameObject.SetActive(first && span);
            readout.gameObject.SetActive(label && IsActive);
        }

        private void Build()
        {
            var mat = new Material(Shader.Find("Sprites/Default"));
            var lineGo = new GameObject("MeasureLine");
            lineGo.transform.SetParent(transform, false);
            line = lineGo.AddComponent<LineRenderer>();
            line.sharedMaterial = mat;
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.numCapVertices = 2;
            line.startColor = line.endColor = Brand.Control;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            dotA = Dot("A");
            dotB = Dot("B");
            var textGo = new GameObject("MeasureReadout");
            textGo.transform.SetParent(transform, false);
            readout = textGo.AddComponent<TextMeshPro>();
            Brand.Style(readout, true);
            readout.fontSize = 10f;
            readout.alignment = TextAlignmentOptions.Bottom;
            readout.rectTransform.pivot = new Vector2(0.5f, 0f);
            readout.rectTransform.sizeDelta = new Vector2(40f, 4f);
            readout.textWrappingMode = TextWrappingModes.NoWrap;
            readout.color = Color.white;
            Material outlined = Brand.LabelMaterial;
            if (outlined != null) readout.fontSharedMaterial = outlined;
            Show(false, false, false);
        }

        private Transform Dot(string name)
        {
            var go = new GameObject("Measure" + name);
            go.transform.SetParent(transform, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = Brand.DiscSprite;
            r.color = Brand.Control;
            r.sharedMaterial = Brand.MarkerSpriteFadeMaterial;
            return go.transform;
        }
    }
}
