using TMPro;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Guides the user to one sensor: a flat arrow floating at chest height turns towards it,
    /// with name, horizontal distance and height difference beneath; the target icon pulses.
    /// Stops on its own a few seconds after arrival, or from the menu.
    /// </summary>
    public class SensorNavigator : MonoBehaviour
    {
        private const float ArrivedWithin = 1.2f;   // metres, horizontal
        private const float StopAfterArrival = 6f;  // seconds

        public static bool IsActive { get; private set; }
        /// <summary>Frame a B press stopped navigation, so the same press does nothing else.</summary>
        public static int StoppedFrame { get; private set; } = -1;
        public static string TargetName { get; private set; }

        private MarkerManager markers;
        private string targetId;
        private ProjectPoint targetPoint;
        private Transform guide, arrow;
        private Renderer arrowRenderer;
        private TextMeshPro title, detail, hint;
        private Marker targetMarker;
        private int textKey = int.MinValue;
        private bool textArrived;
        private float arrivedAt = -1f;
        private float nextPulse;
        private Core.ProjectAlignment alignment;
        private const float PulseWithin = 25f; // metres: the "getting warmer" pulses start here
        // One outlined material per font face, shared by the labels: setting outlineWidth per
        // label cloned a material each that was never destroyed.
        private static readonly System.Collections.Generic.Dictionary<TMP_FontAsset, Material> outlineMaterials =
            new System.Collections.Generic.Dictionary<TMP_FontAsset, Material>();

        public void Init(MarkerManager markerManager)
        {
            markers = markerManager;
            Build();
            Hands.OnChanged += RefreshHint;
        }

        private void RefreshHint()
        {
            if (hint != null) hint.text = $"{Hands.SecondaryName}: διακοπή πλοήγησης";
        }

        private void OnDestroy()
        {
            Hands.OnChanged -= RefreshHint;
            if (IsActive) { IsActive = false; TargetName = null; }
        }

        public void Navigate(string markerId, ProjectPoint point)
        {
            Stop();
            targetId = markerId;
            targetPoint = point;
            TargetName = point != null ? (string.IsNullOrEmpty(point.label) ? point.id : point.label) : markerId;
            IsActive = true;
            arrivedAt = -1f;
            textKey = int.MinValue; // new target: the name must be re-written
            nextPulse = 0f;
            Feedback.Play(Feedback.Sound.Click);
            guide.gameObject.SetActive(true);
            // The guide is already active by the first PlaceGuide, so its "snap when just shown"
            // never fired: it slid across the room from where the last navigation ended.
            snapGuide = true;
        }

        private bool snapGuide;

        public void Stop()
        {
            if (targetMarker != null) targetMarker.SetNavigationTarget(false);
            targetMarker = null;
            IsActive = false;
            TargetName = null;
            if (guide != null) guide.gameObject.SetActive(false);
        }

        private void Build()
        {
            guide = new GameObject("NavigationGuide").transform;
            guide.SetParent(transform, false);

            arrow = new GameObject("Arrow").transform;
            arrow.SetParent(guide, false);
            var mf = arrow.gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = ArrowMesh();
            arrowRenderer = arrow.gameObject.AddComponent<MeshRenderer>();
            arrowRenderer.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = Brand.Primary };
            arrowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            arrowRenderer.sortingOrder = 120;

            title = Label("Title", 0.022f, Brand.Fg, true, -0.075f);
            detail = Label("Detail", 0.016f, Brand.FgMuted, false, -0.105f);
            hint = Label("Hint", 0.012f, Brand.FgSubtle, false, -0.13f);
            RefreshHint();
            guide.gameObject.SetActive(false);
        }

        private TextMeshPro Label(string name, float size, Color color, bool bold, float y)
        {
            var go = new GameObject(name);
            go.transform.SetParent(guide, false);
            go.transform.localPosition = new Vector3(0f, y, 0f);
            var t = go.AddComponent<TextMeshPro>();
            Brand.Style(t, bold);
            // TMP 3D: font size 10 is ~1 unit tall, so at 0.01 scale size*1000 gives `size` metres.
            t.fontSize = size * 1000f;
            t.transform.localScale = Vector3.one * 0.01f;
            t.alignment = TextAlignmentOptions.Center;
            t.color = color;
            t.rectTransform.sizeDelta = new Vector2(60f, 6f);
            Material outlined = OutlineMaterial(t.font);
            if (outlined != null) t.fontSharedMaterial = outlined;
            return t;
        }

        private static Material OutlineMaterial(TMP_FontAsset face)
        {
            if (face == null || face.material == null) return null;
            if (outlineMaterials.TryGetValue(face, out Material m) && m != null) return m;
            m = new Material(face.material) { name = "NavLabel" };
            m.EnableKeyword(ShaderUtilities.Keyword_Outline);
            m.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.25f);
            m.SetColor(ShaderUtilities.ID_OutlineColor, Brand.Canvas);
            outlineMaterials[face] = m;
            return m;
        }

        // A flat chevron arrow in the XZ plane pointing +Z, 16 cm long.
        private static Mesh ArrowMesh()
        {
            var v = new[]
            {
                new Vector3(0f, 0f, 0.09f),       // tip
                new Vector3(-0.07f, 0f, 0.01f),
                new Vector3(-0.025f, 0f, 0.01f),
                new Vector3(-0.025f, 0f, -0.07f),
                new Vector3(0.025f, 0f, -0.07f),
                new Vector3(0.025f, 0f, 0.01f),
                new Vector3(0.07f, 0f, 0.01f),
            };
            var tris = new[] { 0, 6, 1, 2, 5, 4, 2, 4, 3 };
            var mesh = new Mesh { name = "NavArrow", vertices = v, triangles = tris };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private void Update()
        {
#if !MOBILE_PREVIEW
            // B stops it, unless a panel owns the buttons (then B is that panel's "back").
            if (alignment == null) alignment = FindAnyObjectByType<Core.ProjectAlignment>();
            bool aligning = alignment != null && alignment.IsModeActive; // B is "undo point" there
            if (IsActive && !aligning && !UiState.InputBlocked && Hands.DominantSecondaryDown())
            {
                Stop();
                StoppedFrame = Time.frameCount;
                Feedback.Play(Feedback.Sound.Undo, 0.6f);
            }
#endif
        }

        private void LateUpdate()
        {
            if (!IsActive) return;
            Camera cam = Camera.main;
            if (cam == null || markers == null) return;

            // A re-solve or reload can recycle the pooled marker for another sensor: re-check.
            if (targetMarker != null && (targetMarker.MarkerId != targetId || !targetMarker.gameObject.activeInHierarchy))
            {
                targetMarker.SetNavigationTarget(false);
                targetMarker = null;
            }
            if (targetMarker == null)
            {
                if (!markers.ActiveMarkers.TryGetValue(targetId, out targetMarker) || targetMarker == null)
                {
                    title.text = TargetName;
                    detail.text = "Ο αισθητήρας δεν φαίνεται — ευθυγράμμισε το έργο";
                    PlaceGuide(cam, cam.transform.forward);
                    return;
                }
                targetMarker.SetNavigationTarget(true);
            }

            Vector3 head = cam.transform.position;
            Vector3 to = targetMarker.transform.position - head;
            Vector3 flat = new Vector3(to.x, 0f, to.z);
            float horizontal = flat.magnitude;
            // Eye-relative, like the sensor card (the tracking floor is flat; sites aren't).
            float dh = targetMarker.transform.position.y - cam.transform.position.y;

            PlaceGuide(cam, flat.sqrMagnitude > 1e-4f ? flat.normalized : cam.transform.forward);

            bool arrived = horizontal <= ArrivedWithin;
            if (arrived && arrivedAt < 0f)
            {
                arrivedAt = Time.time;
                Feedback.Play(Feedback.Sound.Success);
#if !MOBILE_PREVIEW
                Feedback.Buzz(Hands.Dominant, 0.9f, 0.3f);
#endif
            }
            if (!arrived) arrivedAt = -1f;
            if (!arrived) Pulse(horizontal);

            arrowRenderer.sharedMaterial.color = arrived ? Brand.Control : Brand.Primary;
            // Re-format only when a shown digit changes (these ran every frame).
            int key = arrived ? Mathf.RoundToInt(dh * 100f)
                : (horizontal < 10f ? Mathf.RoundToInt(horizontal * 10f) : 1000000 + Mathf.RoundToInt(horizontal)) * 4096
                  + Mathf.RoundToInt(Mathf.Abs(dh) * 10f) + (dh >= 0 ? 2048 : 0);
            if (key != textKey || arrived != textArrived)
            {
                textKey = key;
                textArrived = arrived;
                title.text = arrived ? $"Έφτασες · {TargetName}" : TargetName;
                detail.text = arrived
                    ? $"{(dh >= 0 ? "+" : "")}{dh:F2} m από τα μάτια σου"
                    : $"{(horizontal < 10f ? horizontal.ToString("F1") : horizontal.ToString("F0"))} m   ·   "
                      + $"{(dh >= 0 ? "↑" : "↓")} {Mathf.Abs(dh):F1} m";
            }

            if (arrivedAt > 0f && Time.time - arrivedAt > StopAfterArrival) Stop();
        }

        // "Getting warmer": a tap on the pointing hand that comes faster and harder as the
        // target nears (every ~1.2 s at 25 m, ~5 a second at arrival), a soft ping within 4 m.
        private void Pulse(float horizontal)
        {
            if (horizontal > PulseWithin || Time.time < nextPulse) return;
            float k = Mathf.Clamp01(horizontal / PulseWithin);
            nextPulse = Time.time + Mathf.Lerp(0.18f, 1.2f, k);
#if !MOBILE_PREVIEW
            Feedback.Buzz(Hands.Dominant, Mathf.Lerp(0.7f, 0.2f, k), 0.04f, 0.8f);
#endif
            if (horizontal < 4f) Feedback.Play(Feedback.Sound.Ping, 0.35f);
        }

        // The guide rides 55 cm ahead at chest height; the arrow lies flat and turns to the
        // target, the labels turn to the viewer.
        private void PlaceGuide(Camera cam, Vector3 direction)
        {
            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 target = cam.transform.position + forward * 0.55f + Vector3.down * 0.32f;
            guide.position = snapGuide ? target : Vector3.Lerp(guide.position, target, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            snapGuide = false;
            arrow.rotation = Quaternion.LookRotation(new Vector3(direction.x, 0f, direction.z).normalized, Vector3.up)
                             * Quaternion.Euler(-12f, 0f, 0f); // tilt up a touch so it reads from above
            Quaternion face = Quaternion.LookRotation(guide.position - cam.transform.position, Vector3.up);
            title.transform.rotation = face;
            detail.transform.rotation = face;
            hint.transform.rotation = face;
        }
    }
}
