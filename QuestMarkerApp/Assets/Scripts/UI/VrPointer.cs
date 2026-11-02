using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.Core;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Marks a panel the user has dragged somewhere: it stays put instead of re-centering
    /// in front of them, until "reset panel positions" in the main menu.
    /// </summary>
    public class PanelPin : MonoBehaviour
    {
        private static readonly List<PanelPin> All = new List<PanelPin>();
        public bool Pinned
        {
            get => pinned;
            set
            {
                pinned = value;
                Camera cam = Headset.Camera;
                if (value && cam != null) pinnedEyeHeight = cam.transform.position.y;
            }
        }
        private bool pinned;
        private float pinnedEyeHeight;

        private void Awake() => All.Add(this);
        private void OnDestroy() => All.Remove(this);

        /// <summary>
        /// Pinned and still sensible: a pin is dropped when the user has sat down / stood up
        /// (eye height changed by over 0.4 m) or walked away (over 4 m), so a panel never
        /// reopens out of reach or behind them.
        /// </summary>
        public static bool IsPinned(Component panel)
        {
            if (panel == null || !panel.TryGetComponent(out PanelPin pin) || !pin.pinned) return false;
            Camera cam = Headset.Camera;
            if (cam == null) return true;
            bool heightChanged = Mathf.Abs(cam.transform.position.y - pin.pinnedEyeHeight) > 0.4f;
            bool tooFar = Vector3.Distance(cam.transform.position, panel.transform.position) > 4f;
            if (heightChanged || tooFar) pin.pinned = false;
            return pin.pinned;
        }

        public static void ResetAll()
        {
            foreach (PanelPin pin in All) pin.Pinned = false;
        }
    }

    /// <summary>
    /// Point-and-click for the code-built panels: the right controller's aim ray is tested
    /// against registered rects, the hovered one lights up under a cursor dot, and the right
    /// trigger clicks it. Runs before everything else so a click is marked consumed and the
    /// same trigger press doesn't also place a control point or open a sensor card.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class VrPointer : MonoBehaviour
    {
        private const float CursorSize = 0.012f;

        /// <summary>True on the frame the trigger clicked a panel element.</summary>
        public static bool ConsumedThisFrame => consumedFrame == Time.frameCount;
        /// <summary>The ray is over some panel element (hovering, not necessarily clicking).</summary>
        public static bool IsHovering => hovered != null;
        /// <summary>A panel is being carried with the grip; panels ignore their stick meanwhile.</summary>
        public static bool IsDragging => dragging != null;

        private static readonly List<Draggable> Draggables = new List<Draggable>();
        private static Draggable dragging;
        private float dragDistance;
        private Vector3 dragOffset;

        private class Draggable
        {
            public RectTransform Area;
            public Transform Panel;
        }

        /// <summary>
        /// Let the user carry <paramref name="panel"/> by pointing anywhere on <paramref name="area"/>
        /// and holding the right grip. Releasing pins it there (<see cref="PanelPin"/>).
        /// </summary>
        public static void RegisterDraggable(RectTransform area, Transform panel)
        {
            if (!panel.TryGetComponent(out PanelPin _)) panel.gameObject.AddComponent<PanelPin>();
            Draggables.Add(new Draggable { Area = area, Panel = panel });
        }

        private static readonly List<Target> Targets = new List<Target>();
        private static Target hovered;
        private static int consumedFrame = -1;

        private Transform cursor;
        // The ray itself, from the controller to the cursor: with only a dot it was hard to tell
        // where the hand pointed until it happened to land on a button.
        private LineRenderer beam;
        private const float IdleBeam = 0.45f; // metres shown under an open panel, aimed at nothing
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        private class Target
        {
            public RectTransform Rect;
            public Action Click;
            public Action Hover;
            public Func<bool> Enabled;
            public Image Highlight;
            public Color Normal, Lit;
        }

        /// <summary>
        /// Make <paramref name="rect"/> clickable. <paramref name="highlight"/>, when given, is
        /// tinted while hovered; <paramref name="hover"/> runs when the ray enters (e.g. to move
        /// a list selection there).
        /// </summary>
        public static void Register(RectTransform rect, Action click, Action hover = null, Func<bool> enabled = null,
            Image highlight = null, Color? lit = null)
        {
            Targets.Add(new Target
            {
                Rect = rect,
                Click = click,
                Hover = hover,
                Enabled = enabled ?? (() => true),
                Highlight = highlight,
                Normal = highlight != null ? highlight.color : Color.clear,
                Lit = lit ?? Brand.BorderStrong
            });
        }

        private void Awake()
        {
            var go = new GameObject("PointerCursor");
            go.transform.SetParent(transform, false);
            var outer = MakeDisc(go.transform, "Ring", Brand.Canvas, 1f, 0.001f);
            MakeDisc(go.transform, "Dot", Brand.Fg, 0.62f, 0f);
            cursor = go.transform;
            cursor.localScale = Vector3.one * CursorSize;
            cursor.gameObject.SetActive(false);
            outer.sortingOrder = 30000; // draw over the panels it sits on

            var beamGo = new GameObject("PointerBeam");
            beamGo.transform.SetParent(transform, false);
            beam = beamGo.AddComponent<LineRenderer>();
            beam.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            beam.positionCount = 2;
            beam.useWorldSpace = true;
            beam.numCapVertices = 2;
            beam.startWidth = 0.003f;
            beam.endWidth = 0.0015f;
            beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            beam.receiveShadows = false;
            beam.sortingOrder = 29990;
            beamGo.SetActive(false);
        }

        // What the beam ends on, kept for the late redraw: a panel plane, or a stub this long.
        private RectTransform beamTarget;
        private float beamStub;
        private bool lateHooked;

        // The rig moves the controllers again just before rendering (late controller update);
        // the beam and cursor built in Update would trail the hand by a frame. Redrawn here
        // from the fresh pose, on the same panel or stub, so they stay glued to it.
        private void LateBeam()
        {
            if (beam == null || !beam.gameObject.activeSelf || dragging != null) return;
#if !MOBILE_PREVIEW
            if (rig == null || !Hands.DominantConnected) return;
            Transform hand = Hands.DominantAnchor(rig);
            var ray = new Ray(hand.position, hand.forward);
            Vector3 end = ray.GetPoint(beamStub);
            if (beamTarget != null)
            {
                var plane = new Plane(beamTarget.forward, beamTarget.position);
                if (!plane.Raycast(ray, out float d)) return;
                end = ray.GetPoint(d) - ray.direction * 0.004f;
                cursor.position = end;
                cursor.rotation = Quaternion.LookRotation(ray.direction, Vector3.up);
            }
            beam.SetPosition(0, ray.origin);
            beam.SetPosition(1, end);
#endif
        }

        private void OnDestroy()
        {
            if (lateHooked) Application.onBeforeRender -= LateBeam;
        }

        // hit: the beam ends on a panel (bright to the end); otherwise a short fading stub.
        private void ShowBeam(bool on, Ray ray = default, Vector3 end = default, bool hit = false)
        {
            if (beam.gameObject.activeSelf != on) beam.gameObject.SetActive(on);
            if (!on) return;
            beamTarget = hit && hovered != null ? hovered.Rect : null;
            beamStub = Vector3.Distance(ray.origin, end);
            beam.SetPosition(0, ray.origin);
            beam.SetPosition(1, end);
            Color c = hit ? Brand.Fg : Brand.Primary;
            beam.startColor = c.WithAlpha(hit ? 0.55f : 0.5f);
            beam.endColor = c.WithAlpha(hit ? 0.8f : 0f);
        }

        // A panel that takes the pointer is up (the world ray of HoverInfo is off then).
        private static bool PanelUp => MainMenu.IsOpen || FlowPanel.IsOpen || VrKeyboard.IsOpen;

        private static Renderer MakeDisc(Transform parent, string name, Color color, float size, float depth)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(parent, false);
            q.transform.localScale = Vector3.one * size;
            q.transform.localPosition = new Vector3(0f, 0f, depth);
            var r = q.GetComponent<Renderer>();
            r.material = new Material(Shader.Find("Sprites/Default")) { mainTexture = Brand.DiscTexture, color = color };
            r.material.renderQueue = 4000;
            r.sortingOrder = 30001;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        private void Update()
        {
            // After the rig's own onBeforeRender (it subscribes on start), so the pose is fresh.
            if (!lateHooked) { Application.onBeforeRender += LateBeam; lateHooked = true; }
            Targets.RemoveAll(t => t.Rect == null);
            Draggables.RemoveAll(d => d.Area == null || d.Panel == null);

            if (!TryGetRay(out Ray ray, out bool fromHand))
            {
                SetHovered(null);
                dragging = null;
                cursor.gameObject.SetActive(false);
                ShowBeam(false);
                return;
            }

            if (UpdateDrag(ray))
            {
                if (fromHand) ShowBeam(true, ray, cursor.position, true);
                return;
            }

            Target best = null;
            float bestDist = float.MaxValue;
            Vector3 bestHit = Vector3.zero;
            foreach (Target t in Targets)
            {
                if (!t.Rect.gameObject.activeInHierarchy || !t.Enabled()) continue;
                var plane = new Plane(t.Rect.forward, t.Rect.position);
                if (!plane.Raycast(ray, out float d) || d > 5f || d >= bestDist) continue;
                Vector3 hit = ray.GetPoint(d);
                if (!t.Rect.rect.Contains(t.Rect.InverseTransformPoint(hit))) continue;
                best = t;
                bestDist = d;
                bestHit = hit;
            }

            SetHovered(best);
            cursor.gameObject.SetActive(best != null);
            if (best != null)
            {
                // Just in front of the panel, facing back along the ray.
                cursor.position = bestHit - ray.direction * 0.004f;
                cursor.rotation = Quaternion.LookRotation(ray.direction, Vector3.up);
            }
            // Head-gaze fallback (no controller): no beam, it would start between the eyes.
            if (!fromHand) ShowBeam(false);
            else if (best != null) ShowBeam(true, ray, cursor.position, true);
            else ShowBeam(PanelUp, ray, ray.GetPoint(IdleBeam));

            if (best != null && TriggerDown())
            {
                consumedFrame = Time.frameCount;
                Feedback.Play(Feedback.Sound.Click);
#if !MOBILE_PREVIEW
                Feedback.Buzz(Hands.Dominant, 0.3f, 0.03f);
#endif
                best.Click?.Invoke();
            }
        }

        // Grip down on a panel picks it up; it rides the ray at the grab distance, the right
        // stick pushes / pulls it, and releasing the grip leaves it pinned there.
        private bool UpdateDrag(Ray ray)
        {
            if (dragging == null)
            {
                if (!GripDown()) return false;
                float best = float.MaxValue;
                foreach (Draggable d in Draggables)
                {
                    if (!d.Area.gameObject.activeInHierarchy) continue;
                    var plane = new Plane(d.Area.forward, d.Area.position);
                    if (!plane.Raycast(ray, out float dist) || dist > 5f || dist >= best) continue;
                    Vector3 hit = ray.GetPoint(dist);
                    if (!d.Area.rect.Contains(d.Area.InverseTransformPoint(hit))) continue;
                    best = dist;
                    dragging = d;
                    dragDistance = dist;
                    dragOffset = d.Panel.position - hit;
                }
                if (dragging == null) return false;
                SetHovered(null);
            }

            // The panel closed under the hand (B / Menu while carrying it): let go, or IsDragging
            // would keep blocking world input until the grip is released.
            if (!dragging.Area.gameObject.activeInHierarchy)
            {
                dragging = null;
                return false;
            }

            if (!GripHeld())
            {
                dragging.Panel.GetComponent<PanelPin>().Pinned = true;
                dragging = null;
                return false;
            }

            dragDistance = Mathf.Clamp(dragDistance + StickY() * 1.2f * Time.unscaledDeltaTime, 0.25f, 4f);
            Vector3 point = ray.GetPoint(dragDistance);
            dragging.Panel.position = point + dragOffset;
            Camera cam = Headset.Camera;
            if (cam != null) VrUi.FaceViewer(dragging.Panel, cam.transform);

            cursor.gameObject.SetActive(true);
            cursor.position = point - ray.direction * 0.004f;
            cursor.rotation = Quaternion.LookRotation(ray.direction, Vector3.up);
            return true;
        }

        private static bool GripDown()
        {
#if !MOBILE_PREVIEW
            return Hands.GripDown();
#else
            return Input.GetMouseButtonDown(1);
#endif
        }

        private static bool GripHeld()
        {
#if !MOBILE_PREVIEW
            return Hands.GripHeld();
#else
            return Input.GetMouseButton(1);
#endif
        }

        private static float StickY()
        {
#if !MOBILE_PREVIEW
            return Hands.DominantStickY();
#else
            return Input.mouseScrollDelta.y;
#endif
        }

        private static void SetHovered(Target t)
        {
            if (hovered == t) return;
            if (hovered?.Highlight != null) hovered.Highlight.color = hovered.Normal;
            hovered = t;
            if (hovered == null) return;
            if (hovered.Highlight != null)
            {
                hovered.Normal = hovered.Highlight.color;
                hovered.Highlight.color = hovered.Lit;
            }
#if !MOBILE_PREVIEW
            // A faint tick in the hand on entering each element: the same "you are on it"
            // everywhere, also for rows whose only visual is the cursor.
            Feedback.Buzz(Hands.Dominant, 0.25f, 0.02f, 0.9f); // shorter than a frame was not felt
#endif
            hovered.Hover?.Invoke();
        }

        private bool TryGetRay(out Ray ray, out bool fromHand)
        {
            fromHand = false;
#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && Hands.DominantConnected)
            {
                Transform hand = Hands.DominantAnchor(rig);
                ray = new Ray(hand.position, hand.forward);
                fromHand = true;
                return true;
            }
#endif
            Camera cam = Headset.Camera;
            ray = cam != null ? new Ray(cam.transform.position, cam.transform.forward) : default;
            return cam != null;
        }

        private static bool TriggerDown()
        {
#if !MOBILE_PREVIEW
            return Hands.TriggerDown();
#else
            return Input.GetMouseButtonDown(0);
#endif
        }
    }
}
