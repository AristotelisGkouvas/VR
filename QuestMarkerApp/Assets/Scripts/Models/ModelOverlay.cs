using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Models
{
    /// <summary>
    /// The design model in the room: an unlit fill per group (flat colour, or the diffuse map
    /// of a textured photogrammetry mesh), see-through below full opacity, plus edge lines
    /// for untextured groups, parked under the marker reference point with the same rotation
    /// and translation the alignment gives the sensors, so a georeferenced building lands on
    /// the real one. The importer and the image file reads run on a worker thread; meshes
    /// and textures are built over several frames (one texture decode per frame), so a
    /// 400 MB model never freezes the headset. Nothing happens per frame except a
    /// visibility check: the model only moves when the alignment or the reference anchor does,
    /// or, as a preview, when the user grabs it or walks through the life-size copy by stick.
    /// Without an alignment (at home, or before the site is aligned) the model is shown as a
    /// preview instead: a tabletop miniature or a life-size copy around the user, placed once
    /// in front of them and moved with the off-hand grip.
    /// </summary>
    public class ModelOverlay : MonoBehaviour
    {
        /// <summary>The menu choice: follow the alignment when there is one, or always preview.</summary>
        public enum ViewMode { Auto = 0, Tabletop = 1, LifeSize = 2 }

        /// <summary>Where the model is drawn right now.</summary>
        public enum Placement { Project, Tabletop, LifeSize }

        private const string ShowKey = "model.show";
        private const string AlphaKey = "model.alpha";
        private const string EdgesKey = "model.edges";
        private const string ViewKey = "model.view";
        /// <summary>Longest horizontal side of the tabletop miniature, metres.</summary>
        private const float TabletopSize = 1f;
        private const float TabletopDistance = 0.9f;
        /// <summary>Below eye level: table height for someone standing, lap height seated.</summary>
        private const float TabletopDrop = 0.55f;
        /// <summary>Head to floor for the life-size copy, which stands the user on the model's ground.</summary>
        private const float EyeHeight = 1.6f;
        /// <summary>Stick walking through the life-size copy: steady walking pace (no ramp-up,
        /// which is what makes artificial motion queasy) and snap turns.</summary>
        private const string WalkSpeedKey = "model.walkSpeed";
        /// <summary>The menu's walk speeds, m/s; 1.4 is a steady walking pace.</summary>
        public static readonly float[] WalkSpeeds = { 0.7f, 1.4f, 3f, 6f };
        /// <summary>Rising / sinking by stick runs at this fraction of the walk speed (about 1 m/s by default).</summary>
        private const float VerticalSpeedFactor = 0.7f;
        private const float SnapTurnDegrees = 30f;
        private const float StickDeadZone = 0.25f;
        /// <summary>Radius around the model's centre sampled for the life-size ground height.</summary>
        private const float GroundProbeRadius = 5f;
        /// <summary>Mesh work per frame while building: triangles, plus edges at half weight.</summary>
        private const int FrameBudget = 200_000;
        /// <summary>
        /// Vertices per piece of a fill-only group (photogrammetry): 16-bit indices, one piece's
        /// upload fits a frame, and at 1:1 the pieces behind the user are culled.
        /// </summary>
        private const int ChunkVertices = ushort.MaxValue;
        /// <summary>Beyond this a group's wireframe is skipped: the lines mesh would dwarf the fill.</summary>
        private const int MaxEdgesPerGroup = 300_000;
        private const float EdgeAlpha = 0.6f;
        /// <summary>At or above this the fill is drawn opaque (depth-writing, no blending).</summary>
        private const float OpaqueFrom = 0.99f;
        /// <summary>
        /// Largest texture side accepted. The PC tool sends 2048 px; a raw 16k photogrammetry
        /// atlas would need a gigabyte to decode, so it is refused before decoding.
        /// </summary>
        private const int MaxTextureSize = 4096;
        private const long MaxTextureFileBytes = 64L * 1024 * 1024;
        private const string TexturedShaderName = "QuestMarker/ModelTextured";
        private static readonly Color PlainColor = new Color(0.85f, 0.88f, 0.9f, 1f);

        /// <summary>Reads a model file into ModelData; runs on a worker thread, so no Unity API inside.</summary>
        public delegate ModelData Importer(string path, ImportOptions options, Action<float> progress, CancellationToken cancel);

        /// <summary>
        /// Importer per sidecar format. OBJ (and IFC, which arrives converted to OBJ) are built
        /// in; the controller registers "dxf" with DxfImporter so this class does not depend on it.
        /// </summary>
        public static readonly Dictionary<string, Importer> Importers = new Dictionary<string, Importer>(StringComparer.OrdinalIgnoreCase)
        {
            { "obj", ObjImporter.Load },
            { "ifc", ObjImporter.Load }
        };

        private static bool? enabledCache;

        private static ViewMode? viewCache;

        private static float? walkSpeedCache;

        /// <summary>Stick walking speed through the life-size copy, m/s (PlayerPrefs "model.walkSpeed", default 1.4).</summary>
        public static float WalkSpeed
        {
            get => walkSpeedCache ?? (float)(walkSpeedCache = Mathf.Clamp(PlayerPrefs.GetFloat(WalkSpeedKey, 1.4f), 0.1f, 20f));
            set
            {
                walkSpeedCache = Mathf.Clamp(value, 0.1f, 20f);
                PlayerPrefs.SetFloat(WalkSpeedKey, walkSpeedCache.Value);
                PlayerPrefs.Save();
            }
        }

        /// <summary>The next speed of <see cref="WalkSpeeds"/> after the current one, wrapping round.</summary>
        public static void CycleWalkSpeed()
        {
            float now = WalkSpeed;
            foreach (float s in WalkSpeeds)
                if (s > now + 0.01f) { WalkSpeed = s; return; }
            WalkSpeed = WalkSpeeds[0];
        }

        /// <summary>Master switch (PlayerPrefs "model.show").</summary>
        public static bool Enabled
        {
            get => enabledCache ?? (bool)(enabledCache = PlayerPrefs.GetInt(ShowKey, 1) == 1);
            set
            {
                enabledCache = value;
                PlayerPrefs.SetInt(ShowKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        /// <summary>Raised on the main thread when a load finishes (or fails) and on unload.</summary>
        public event Action OnChanged;

        private ProjectAlignment alignment;
        private MarkerManager markers;
        private Transform root;
        private ModelData model;
        private string loadedName;
        private string error;
        private bool loading;
        private bool building; // the previous model is already gone; meshes are being built
        private volatile float importProgress;
        private float buildProgress;
        private int generation;
        private CancellationTokenSource cts;
        private float opacity = -1f;
        private bool? showEdges;
        // Model-local extent and the ground height near its centre, for the previews.
        private Bounds modelBounds;
        private float groundY;
        // Placement last applied; null makes Update place the model again (a preview is placed
        // once, in front of the user, and then stays where they leave it).
        private Placement? placed;
        private bool grabbing;
        private bool snapArmed = true; // the turn stick must return to centre between snaps

        /// <summary>
        /// True while the sticks walk and turn through the life-size copy (ControllerHints
        /// labels them). The user stands still in the real room; the model moves past them.
        /// </summary>
        public static bool WalkActive { get; private set; }

        /// <summary>The sticks moved the user through the life-size copy this frame (ComfortVignette).</summary>
        public static bool Moving { get; private set; }

        /// <summary>True while a tabletop or life-size preview is on show (ControllerHints labels the grips).</summary>
        public static bool PreviewShown { get; private set; }

        /// <summary>
        /// True while the life-size copy is on show outside the alignment: BoundaryControl blacks
        /// out passthrough and brings the Guardian back. Aligning, the room stays in view and the
        /// boundary off, to walk between points metres apart.
        /// </summary>
        public static bool LifeSizeShown { get; private set; }

        /// <summary>Two-handed zoom may shrink the tabletop to this fraction of its default size.</summary>
        private const float MinTabletopZoom = 0.2f;
        /// <summary>Hold time of the scale readout after the last change, seconds.</summary>
        private const float ScaleReadoutHold = 1.2f;
#if !MOBILE_PREVIEW
        private enum GrabMode { None, One, Two }
        private GrabMode grabMode;
        // One hand: the pose at grab start, relative to the off hand.
        private Vector3 grabHandPos, grabRootPos;
        private float grabHandYaw;
        private Quaternion grabRootRot;
        // Two hands: midpoint, spread and heading of the pair at grab start, and the root then.
        private Vector3 pairMid, pairRootPos;
        private float pairDist, pairScale, pairYaw, pairTurn;
        private Quaternion pairRootRot;
        private OVRCameraRig rig;
#endif
        private RectTransform scaleTag;
        private TMPro.TextMeshProUGUI scaleText;
        private float scaleShownAt = -10f;

        private readonly List<GameObject> groupObjects = new List<GameObject>();
        private readonly List<GameObject> edgeObjects = new List<GameObject>();
        private readonly List<Mesh> meshes = new List<Mesh>();
        // One untextured fill material per distinct colour, shared by every group with that
        // colour and kept across models (they hold only the built-in white texture).
        private readonly Dictionary<Color, Material> fillMaterials = new Dictionary<Color, Material>();
        // Per loaded model: decoded diffuse maps by file path and one material per (map, tint).
        // Both are destroyed with the model's visuals; a texture is tens of MB of GPU memory.
        private readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly Dictionary<(string, Color), Material> texturedMaterials = new Dictionary<(string, Color), Material>();
        private bool hasTextures;
        private Material edgeMaterial;
        private static Shader texturedShader;
        private static bool texturedShaderLooked;

        public bool IsLoaded => model != null && !loading;
        public bool IsLoading => loading;
        public string LoadedName => loadedName;
        public ModelData Model => model;
        /// <summary>The transform the model's groups hang under (project-local metres, x East, y Up, z North).</summary>
        public Transform Root => root;
        /// <summary>Model-local extent of every vertex of the loaded model.</summary>
        public Bounds ModelBounds => modelBounds;
        public IReadOnlyList<ModelGroup> Groups => model != null ? (IReadOnlyList<ModelGroup>)model.Groups : Array.Empty<ModelGroup>();

        /// <summary>
        /// Fill alpha 0.05..1 (PlayerPrefs "model.alpha"); applied to the shared materials at
        /// once. From 0.99 up the fill turns opaque (depth write, no blending), which is what a
        /// textured photogrammetry mesh needs to look solid instead of seeing through itself.
        /// </summary>
        public float Opacity
        {
            get
            {
                if (opacity < 0f) opacity = Mathf.Clamp(PlayerPrefs.GetFloat(AlphaKey, 0.35f), 0.05f, 1f);
                return opacity;
            }
            set
            {
                float v = Mathf.Clamp(value, 0.05f, 1f);
                if (Mathf.Approximately(v, Opacity)) return;
                opacity = v;
                PlayerPrefs.SetFloat(AlphaKey, v);
                PlayerPrefs.Save();
                foreach (var kv in fillMaterials) SetFillAlpha(kv.Value, kv.Key, v);
                foreach (var kv in texturedMaterials) SetFillAlpha(kv.Value, kv.Key.Item2, v);
            }
        }

        /// <summary>Edge lines on/off (PlayerPrefs "model.edges").</summary>
        public bool ShowEdges
        {
            get => showEdges ?? (bool)(showEdges = PlayerPrefs.GetInt(EdgesKey, 1) == 1);
            set
            {
                if (value == ShowEdges) return;
                showEdges = value;
                PlayerPrefs.SetInt(EdgesKey, value ? 1 : 0);
                PlayerPrefs.Save();
                foreach (GameObject go in edgeObjects) if (go != null) go.SetActive(value);
            }
        }

        /// <summary>Menu choice (PlayerPrefs "model.view"); changing it places the model again.</summary>
        public ViewMode View
        {
            get => viewCache ?? (ViewMode)(viewCache = (ViewMode)Mathf.Clamp(PlayerPrefs.GetInt(ViewKey, 0), 0, 2));
            set
            {
                viewCache = value;
                PlayerPrefs.SetInt(ViewKey, (int)value);
                PlayerPrefs.Save();
                placed = null;
            }
        }

        private bool Aligning => alignment != null && alignment.IsModeActive;

        /// <summary>True when the alignment can put this model on the real site.</summary>
        public bool CanPlaceInProject =>
            model != null && model.Georeferenced && alignment != null && alignment.IsAligned;

        /// <summary>
        /// Where the model goes: on the site when aligned (Auto), else the chosen preview. While
        /// aligning it is always 1:1: on the site once solved, else the life-size copy, which
        /// stays put as the user walks between the points (the sticks do not move it then).
        /// </summary>
        public Placement CurrentPlacement =>
            Aligning ? (CanPlaceInProject ? Placement.Project : Placement.LifeSize)
            : View == ViewMode.Tabletop ? Placement.Tabletop
            : View == ViewMode.LifeSize ? Placement.LifeSize
            : CanPlaceInProject ? Placement.Project : Placement.Tabletop;

        /// <summary>Places the preview in front of the user again, at its default scale (no effect on the site placement).</summary>
        public void ResetPreview()
        {
            if (CurrentPlacement != Placement.Project) placed = null;
        }

        /// <summary>One line for the menu: what is shown, how far a load got, or why nothing is.</summary>
        public string Status
        {
            get
            {
                if (loading) return L.En ? $"Loading {Mathf.RoundToInt(Progress * 100f)}%" : $"Φόρτωση {Mathf.RoundToInt(Progress * 100f)}%";
                if (error != null) return L.T("Σφάλμα: ", "Error: ") + error;
                if (model == null) return L.T("Χωρίς μοντέλο", "No model");
                return $"{loadedName} · {CountText(model)}" + (hasTextures ? L.T(" · με υφή", " · textured") : "");
            }
        }

        /// <summary>0..1 across import (first 80%) and mesh building.</summary>
        public float Progress => loading ? Mathf.Clamp01(importProgress * 0.8f + buildProgress * 0.2f) : (model != null ? 1f : 0f);

        /// <summary>Why the model cannot be shown right now, or null when it can (a preview always can).</summary>
        public string Unavailable()
        {
            if (loading) return Status;
            if (error != null) return Status;
            if (model == null) return L.T("Χωρίς μοντέλο", "No model");
            return null;
        }

        public void Init(ProjectAlignment projectAlignment, MarkerManager markerManager)
        {
            Detach();
            alignment = projectAlignment;
            markers = markerManager;
            if (alignment != null) alignment.OnAlignmentChanged += ApplyPose;
            if (markers != null) markers.OnReferencePointChanged += OnReferencePointChanged;
            EnsureRoot();
            placed = null;
        }

        private void Detach()
        {
            if (alignment != null) alignment.OnAlignmentChanged -= ApplyPose;
            if (markers != null) markers.OnReferencePointChanged -= OnReferencePointChanged;
        }

        private void Update()
        {
            if (root == null) return;
            bool show = model != null && !loading && Enabled;
            bool walk = false;
            bool preview = false;
            bool lifeSize = false;
            Moving = false;
            if (show)
            {
                Placement p = CurrentPlacement;
                if (placed != p) Place(p);
                preview = p != Placement.Project;
                lifeSize = p == Placement.LifeSize && !Aligning;
#if !MOBILE_PREVIEW
                // Aligning, the 1:1 copy stays put: the off-hand grip holds the alignment panel.
                if (preview && !Aligning) Grab();
                else EndGrab();
                // Only the life-size preview: on the site the model must stay on the real building.
                walk = p == Placement.LifeSize && !grabbing && WalkAllowed();
                if (walk) Walk();
#endif
            }
            WalkActive = walk;
            PreviewShown = preview;
            LifeSizeShown = lifeSize;
            if (root.gameObject.activeSelf != show) root.gameObject.SetActive(show);
            if (scaleTag != null && scaleTag.gameObject.activeSelf
                && (!preview || Time.unscaledTime - scaleShownAt > ScaleReadoutHold))
                scaleTag.gameObject.SetActive(false);
        }

        // Disabling stops BuildRoutine without a word; left alone the status would read
        // "Φόρτωση…" forever and a half-built model would stay.
        private void OnDisable()
        {
            WalkActive = false;
            PreviewShown = false;
            LifeSizeShown = false;
            Moving = false;
            if (!loading) return;
            bool partial = building;
            CancelPending();
            generation++;
            building = false;
            if (partial)
            {
                DestroyVisuals();
                model = null;
                loadedName = null;
            }
            error = L.T("Η φόρτωση διακόπηκε", "Loading was interrupted");
            OnChanged?.Invoke();
        }

        private void OnDestroy()
        {
            Detach();
            CancelPending();
            DestroyVisuals(); // textured materials and textures go with it
            foreach (var kv in fillMaterials) if (kv.Value != null) Destroy(kv.Value);
            fillMaterials.Clear();
            if (edgeMaterial != null) Destroy(edgeMaterial);
            if (root != null) Destroy(root.gameObject);
        }

        // ------------------------------------------------------------------
        //  Placement
        // ------------------------------------------------------------------

        private void EnsureRoot()
        {
            if (root != null) return;
            var go = new GameObject("ModelOverlay");
            root = go.transform;
            go.SetActive(false);
        }

        private void AttachToReference()
        {
            EnsureRoot();
            Transform parent = markers != null && markers.ReferencePoint != null ? markers.ReferencePoint : transform;
            if (root.parent != parent) root.SetParent(parent, false);
        }

        private void OnReferencePointChanged(Transform reference) => ApplyPose();

        private void Place(Placement p)
        {
            placed = p;
#if !MOBILE_PREVIEW
            EndGrab();
#endif
            grabbing = false;
            if (p == Placement.Project) ApplyPose();
            else PlacePreview(p);
        }

        // Project-local metres p render at reference.TransformPoint(rot * p + trans). The model's
        // vertices are relative to its own origin, which may differ from the alignment's (a
        // model uploaded before the project, or rounded to 100 m), so the difference is folded
        // into the root position rather than touching a million vertices.
        private void ApplyPose()
        {
            if (placed != Placement.Project) return; // a preview keeps the pose the user gave it
            AttachToReference();
            if (root == null || alignment == null) return;
            if (!alignment.TryGetLocalPose(out Quaternion rot, out Vector3 trans)) return;
            Vector3 shift = Vector3.zero;
            if (model != null && alignment.TryGetOrigin(out double ae, out double an, out double ah))
                shift = new Vector3((float)(model.OriginE - ae), (float)(model.OriginH - ah), (float)(model.OriginN - an));
            root.localRotation = rot;
            root.localPosition = trans + rot * shift;
            root.localScale = Vector3.one;
        }

        // Model axes are x = East, y = Up, z = North, so turning the root to the user's heading
        // puts north straight ahead. Tabletop: the whole model shrunk into a metre, its lowest
        // point on a table in front. Life size: the ground at the model's centre under the feet.
        private void PlacePreview(Placement p)
        {
            Camera cam = Camera.main;
            Transform head = cam != null ? cam.transform : transform;
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(head.up, Vector3.up); // looking straight down
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            Quaternion yaw = Quaternion.LookRotation(forward, Vector3.up);

            float scale;
            Vector3 pivot, target;
            if (p == Placement.Tabletop)
            {
                scale = TabletopScale;
                pivot = new Vector3(modelBounds.center.x, modelBounds.min.y, modelBounds.center.z);
                target = head.position + forward * TabletopDistance + Vector3.down * TabletopDrop;
            }
            else
            {
                scale = 1f;
                pivot = new Vector3(modelBounds.center.x, groundY, modelBounds.center.z);
                target = head.position + Vector3.down * EyeHeight;
            }

            EnsureRoot();
            root.SetParent(null, false);
            root.localScale = Vector3.one * scale;
            root.rotation = yaw;
            root.position = target - yaw * (pivot * scale);
        }

        /// <summary>Root scale of the default tabletop miniature: the longest horizontal side becomes 1 m.</summary>
        public float DefaultTabletopScale => TabletopScale;

        private float TabletopScale => TabletopSize / Mathf.Max(modelBounds.size.x, modelBounds.size.z, 0.01f);

        // Zoom limits. Tabletop: from a fifth of the default miniature up to real size. Life
        // size: down to the default miniature, so the walk-through copy can be shrunk to look
        // at as a whole. A model smaller than a metre starts above 1:1, which stays allowed.
        private float ClampScale(float s)
        {
            float tabletop = TabletopScale;
            float hi = Mathf.Max(1f, tabletop);
            float lo = placed == Placement.LifeSize ? Mathf.Min(tabletop, 1f) : tabletop * MinTabletopZoom;
            return Mathf.Clamp(s, Mathf.Min(lo, hi), hi);
        }

#if !MOBILE_PREVIEW
        private void EndGrab()
        {
            grabMode = GrabMode.None;
            grabbing = false;
        }

        // Off-hand grip held: the preview follows that controller (position, and turning about
        // the vertical only, so a miniature never tips over); the dominant stick zooms about
        // the hand meanwhile. Both grips: zoom by the spread of the hands about their midpoint,
        // turn with the line between them, carry with the midpoint. Each change of mode starts
        // from the pose of that frame, so letting go of one hand never makes the model jump.
        private void Grab()
        {
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            Transform off = Hands.OffHandAnchor(rig), dom = Hands.DominantAnchor(rig);
            bool offHeld = off != null && Hands.OffHandGripHeld();
            bool domHeld = dom != null && Hands.GripHeld();

            // The dominant grip also carries panels (VrPointer runs first and claims it on the
            // press): a grip that picked up a panel never starts a two-handed grab.
            GrabMode want = GrabMode.None;
            if (offHeld && domHeld && (grabMode == GrabMode.Two || !VrPointer.IsDragging)) want = GrabMode.Two;
            else if (offHeld) want = GrabMode.One;

            if (want != grabMode)
            {
                grabMode = want;
                grabbing = want != GrabMode.None;
                if (want == GrabMode.One) StartOneHand(off);
                else if (want == GrabMode.Two) StartTwoHands(off.position, dom.position);
                return;
            }
            if (grabMode == GrabMode.One) OneHand(off, domHeld);
            else if (grabMode == GrabMode.Two) TwoHands(off.position, dom.position);
        }

        private void StartOneHand(Transform hand)
        {
            grabHandPos = hand.position;
            grabHandYaw = hand.eulerAngles.y;
            grabRootPos = root.position;
            grabRootRot = root.rotation;
        }

        private void OneHand(Transform hand, bool domHeld)
        {
            // Dominant stick up/down: exponential zoom about the grabbing hand, x2 per second at
            // full tilt. Not while it drives a panel (dragging, scrolling a menu or list), nor
            // under the time machine, whose scale rides on the same stick.
            float stick = Hands.DominantStickY();
            if (!domHeld && Mathf.Abs(stick) > StickDeadZone && !UiState.InputBlocked && !DisplacementView.IsActive)
            {
                float s = root.localScale.x;
                float next = ClampScale(s * Mathf.Pow(2f, stick * Time.unscaledDeltaTime));
                if (!Mathf.Approximately(next, s))
                {
                    // Scaling the start offset is the same as scaling about the hand from here on.
                    grabRootPos = grabHandPos + (grabRootPos - grabHandPos) * (next / s);
                    root.localScale = Vector3.one * next;
                }
                ShowScale(hand.position + Vector3.up * 0.09f);
            }
            Quaternion turn = Quaternion.Euler(0f, Mathf.DeltaAngle(grabHandYaw, hand.eulerAngles.y), 0f);
            root.rotation = turn * grabRootRot;
            root.position = hand.position + turn * (grabRootPos - grabHandPos);
        }

        private void StartTwoHands(Vector3 off, Vector3 dom)
        {
            pairMid = (off + dom) * 0.5f;
            // A floor on the spread: hands touching would turn a twitch into a huge zoom.
            pairDist = Mathf.Max(Vector3.Distance(off, dom), 0.05f);
            pairYaw = PairYaw(off, dom, 0f);
            pairTurn = 0f;
            pairScale = root.localScale.x;
            pairRootPos = root.position;
            pairRootRot = root.rotation;
        }

        private void TwoHands(Vector3 off, Vector3 dom)
        {
            Vector3 mid = (off + dom) * 0.5f;
            float s = ClampScale(pairScale * Mathf.Max(Vector3.Distance(off, dom), 0.01f) / pairDist);
            float ratio = s / pairScale;
            // Summed frame by frame, so turning the pair past 180° keeps going instead of flipping.
            float yaw = PairYaw(off, dom, pairYaw);
            pairTurn += Mathf.DeltaAngle(pairYaw, yaw);
            pairYaw = yaw;
            Quaternion turn = Quaternion.Euler(0f, pairTurn, 0f);
            root.localScale = Vector3.one * s;
            root.rotation = turn * pairRootRot;
            root.position = mid + turn * ((pairRootPos - pairMid) * ratio);
            ShowScale(mid + Vector3.up * 0.07f);
        }

        // Heading of the level line from the off hand to the dominant hand; hands stacked one
        // above the other have none, and the last heading stands.
        private static float PairYaw(Vector3 off, Vector3 dom, float previous)
        {
            Vector3 v = dom - off;
            v.y = 0f;
            return v.sqrMagnitude < 0.0004f ? previous : Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        }
#endif

        // A short-lived "1:250" over the hands while zooming.
        private void ShowScale(Vector3 position)
        {
            if (scaleTag == null)
            {
                scaleTag = VrUi.CreateCanvas("ModelScale", transform, new Vector2(150f, 46f), 0.0006f);
                VrUi.Card("Bg", scaleTag, 0, 0, 150, 46, Brand.Surface.WithAlpha(0.92f), Brand.BorderStrong, 14);
                scaleText = VrUi.Text("Scale", scaleTag, 8, 6, 134, 34, "", 22f, Brand.Fg, true, TMPro.TextAlignmentOptions.Center);
            }
            scaleShownAt = Time.unscaledTime;
            if (!scaleTag.gameObject.activeSelf) scaleTag.gameObject.SetActive(true);
            scaleText.text = ScaleText(root.localScale.x);
            scaleTag.position = position;
            Camera cam = Camera.main;
            if (cam != null) VrUi.FaceViewer(scaleTag, cam.transform);
        }

        // Rounded the way a drawing scale is written: 1:7, 1:85, 1:250, 1:1200.
        private static string ScaleText(float s)
        {
            if (s >= 0.995f && s <= 1.005f) return "1:1";
            if (s > 1f) return s.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + ":1";
            float n = 1f / Mathf.Max(s, 1e-6f);
            int step = n < 20f ? 1 : n < 200f ? 5 : n < 2000f ? 10 : 100;
            return "1:" + Mathf.Max(1, Mathf.RoundToInt(n / step) * step);
        }

#if !MOBILE_PREVIEW

        // The sticks belong to whatever is open: menus and lists scroll with them, the
        // alignment panel picks sensors, the time machine scrubs.
        private bool WalkAllowed()
        {
            if (UiState.InputBlocked || DisplacementView.IsActive) return false;
            if (alignment != null && alignment.IsModeActive) return false;
            return !Hands.OffHandStickClickHeld(); // push-to-talk is a click on the same stick
        }

        // Moving through the life-size copy without walking in the real room, where the walls
        // and furniture are not where the model's are. The model slides the opposite way,
        // so nothing real (anchor, sensors, guardian) is touched: "Νέα θέση μπροστά μου" undoes it.
        private void Walk()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Transform head = cam.transform;

            // Off-hand stick: move along the level gaze; sideways strafes.
            Vector2 move = new Vector2(Hands.OffHandStickX(), Hands.OffHandStickY());
            if (move.magnitude > StickDeadZone)
            {
                Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
                if (forward.sqrMagnitude > 1e-4f)
                {
                    forward.Normalize();
                    Vector3 right = Vector3.Cross(Vector3.up, forward);
                    Vector2 dir = Vector2.ClampMagnitude(move, 1f);
                    // Constant speed once past the dead zone: half-pushed sticks creeping along
                    // are worse for comfort than one steady pace.
                    Vector3 step = (forward * dir.y + right * dir.x).normalized * WalkSpeed * Time.unscaledDeltaTime;
                    root.position -= step;
                    Moving = true;
                }
            }

            // Dominant stick up/down: rise or sink (to a floor above, or under a bridge deck),
            // steady pace like walking. Only when the push is more vertical than sideways, so
            // a slightly diagonal snap turn does not also lift the user.
            float turn = Hands.DominantStickX();
            float lift = Hands.DominantStickY();
            if (Mathf.Abs(lift) > StickDeadZone && Mathf.Abs(lift) > Mathf.Abs(turn))
            {
                root.position -= Vector3.up * (Mathf.Sign(lift) * WalkSpeed * VerticalSpeedFactor * Time.unscaledDeltaTime);
                Moving = true;
            }

            // Dominant stick left/right: snap turn about the user's head.
            if (Mathf.Abs(turn) < 0.3f) snapArmed = true;
            else if (snapArmed && Mathf.Abs(turn) > 0.7f && Mathf.Abs(turn) >= Mathf.Abs(lift))
            {
                snapArmed = false;
                // Turning the user right = turning the world left around them.
                Quaternion q = Quaternion.Euler(0f, turn > 0 ? -SnapTurnDegrees : SnapTurnDegrees, 0f);
                Vector3 pivot = new Vector3(head.position.x, root.position.y, head.position.z);
                root.position = pivot + q * (root.position - pivot);
                root.rotation = q * root.rotation;
                Feedback.Play(Feedback.Sound.Tick, 0.5f);
            }
        }
#endif

        // ------------------------------------------------------------------
        //  Load / unload
        // ------------------------------------------------------------------

        /// <summary>
        /// Import on a worker thread, then build meshes over several frames. A second Load
        /// while one is running discards the first; the old model stays visible until the new
        /// one is ready.
        /// </summary>
        public void Load(ModelStore.ModelInfo info)
        {
            if (info == null) return;
            CancelPending();
            int gen = ++generation;
            error = null;
            if (!Importers.TryGetValue(info.Format ?? "", out Importer importer))
            {
                error = L.T($"Μη υποστηριζόμενη μορφή: {info.Format}", $"Unsupported format: {info.Format}");
                OnChanged?.Invoke();
                return;
            }

            loading = true;
            importProgress = 0f;
            buildProgress = 0f;
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            string path = info.Path;
            ImportOptions options = info.Options;
            Task<Prepared> task = Task.Run(() =>
            {
                ModelData data = importer(path, options, p => importProgress = p, token);
                token.ThrowIfCancellationRequested();
                return Prepare(data, token);
            }, token);
            StartCoroutine(BuildRoutine(task, info, gen));
        }

        public void Unload()
        {
            CancelPending();
            generation++;
            DestroyVisuals();
            model = null;
            loadedName = null;
            error = null;
            ModelStore.LastLoadedName = null;
            if (root != null) root.gameObject.SetActive(false);
            OnChanged?.Invoke();
        }

        public void SetGroupVisible(int index, bool visible)
        {
            if (model == null || index < 0 || index >= model.Groups.Count) return;
            model.Groups[index].Visible = visible;
            if (index < groupObjects.Count && groupObjects[index] != null) groupObjects[index].SetActive(visible);
        }

        private void CancelPending()
        {
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
                cts = null;
            }
            loading = false;
            building = false;
        }

        private IEnumerator BuildRoutine(Task<Prepared> task, ModelStore.ModelInfo info, int gen)
        {
            while (!task.IsCompleted) yield return null;
            if (gen != generation) yield break; // superseded by a newer Load / Unload

            if (task.IsCanceled || task.IsFaulted)
            {
                loading = false;
                if (task.IsFaulted)
                {
                    Exception e = task.Exception != null ? task.Exception.GetBaseException() : null;
                    error = e != null ? e.Message : L.T("άγνωστο σφάλμα", "unknown error");
                    Debug.LogWarning($"[Model] Import of {info.Name} failed: {error}");
                    OnChanged?.Invoke();
                }
                yield break;
            }

            Prepared prepared = task.Result;
            // The previous model is replaced only now, so a failed load leaves it in place.
            DestroyVisuals();
            building = true;
            model = prepared.Model;
            loadedName = info.Name;
            modelBounds = prepared.Bounds;
            groundY = prepared.GroundY;
            EnsureRoot();
            root.gameObject.SetActive(false);

            // Diffuse maps: the files were read on the worker; decoding, compressing and the
            // GPU upload must happen here, one image per frame (a 2048 px JPG is ~20-40 ms).
            foreach (var kv in prepared.TextureBytes)
            {
                Texture2D tex = DecodeTexture(kv.Key, kv.Value);
                if (tex != null) textures[kv.Key] = tex;
                else
                {
                    string msg = L.T($"Η εικόνα υφής {System.IO.Path.GetFileName(kv.Key)} δεν διαβάστηκε",
                        $"Texture image {System.IO.Path.GetFileName(kv.Key)} could not be read");
                    model.Warnings.Add(msg);
                    Debug.LogWarning($"[Model] {msg} ({kv.Key})");
                }
                yield return null;
                if (gen != generation) { DestroyVisuals(); yield break; }
            }
            prepared.TextureBytes.Clear(); // let the compressed file bytes go before the meshes are built
            hasTextures = textures.Count > 0;

            // Piece by piece within the frame budget: one 2M-triangle group no longer lands in a
            // single frame. Each group's vertex lists go as soon as its meshes are on the GPU.
            int spent = 0;
            for (int i = 0; i < prepared.Groups.Count; i++)
            {
                GroupBuild gb = prepared.Groups[i];
                spent += BuildGroup(i, gb);
                for (int c = 0; c < gb.Chunks.Count; c++)
                {
                    if (spent >= FrameBudget)
                    {
                        spent = 0;
                        yield return null;
                        if (gen != generation) { DestroyVisuals(); yield break; }
                    }
                    spent += BuildChunk(i, gb, gb.Chunks[c]);
                    gb.Chunks[c] = null;
                    buildProgress = (i + (c + 1f) / gb.Chunks.Count) / prepared.Groups.Count;
                }
                gb.Group.Release();
                buildProgress = (i + 1f) / prepared.Groups.Count;
                if (spent >= FrameBudget)
                {
                    spent = 0;
                    yield return null;
                    if (gen != generation) { DestroyVisuals(); yield break; }
                }
            }

            loading = false;
            building = false;
            cts?.Dispose();
            cts = null;
            ModelStore.LastLoadedName = info.Name;
            placed = null; // Update places it: on the site if aligned, else as a preview
            Debug.Log($"[Model] '{info.Name}' ready: {model.Groups.Count} groups, {model.TriangleCount} tris, " +
                      $"{model.LineCount} lines, {textures.Count} textures, origin E{model.OriginE} N{model.OriginN} H{model.OriginH}" +
                      (model.Warnings.Count > 0 ? " — " + string.Join(" | ", model.Warnings) : ""));
            OnChanged?.Invoke();
        }

        // ------------------------------------------------------------------
        //  Worker-thread preparation: normals and wireframe edges
        // ------------------------------------------------------------------

        private sealed class Prepared
        {
            public ModelData Model;
            // Worker-thread results, never serialized; without the attribute the player build's
            // layout check counts these Unity-typed fields and the editor does not, and refuses to build.
            [NonSerialized] public Bounds Bounds;
            [NonSerialized] public float GroundY;
            public readonly List<GroupBuild> Groups = new List<GroupBuild>();
            /// <summary>Encoded image file per distinct texture path that exists and passed the size checks.</summary>
            public readonly Dictionary<string, byte[]> TextureBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        private sealed class GroupBuild
        {
            public ModelGroup Group;
            /// <summary>Pairs of indices into Group.Vertices; empty when skipped.</summary>
            public int[] EdgeIndices = Array.Empty<int>();
            /// <summary>The fill of a group without edges or lines, cut into 16-bit pieces (empty otherwise).</summary>
            public readonly List<Chunk> Chunks = new List<Chunk>();
            /// <summary>The group's IsTextured, kept: its UV list is released once chunked.</summary>
            public bool Textured;
            /// <summary>The fill material, shared by the group's pieces.</summary>
            public Material Fill;
        }

        /// <summary>One piece of a fill: its own vertices, UVs (or none) and 16-bit triangles.</summary>
        private sealed class Chunk
        {
            public Vector3[] Vertices;
            public Vector2[] UVs;
            public ushort[] Triangles;
        }

        private static Prepared Prepare(ModelData data, CancellationToken token)
        {
            var result = new Prepared { Model = data };
            var missing = new HashSet<string>(StringComparer.Ordinal);
            // First: it reads every vertex, and the chunking below lets the lists go.
            Measure(data, result);
            foreach (ModelGroup g in data.Groups)
            {
                token.ThrowIfCancellationRequested();
                var gb = new GroupBuild { Group = g, Textured = g.IsTextured };
                if (g.IsTextured)
                {
                    // A photogrammetry mesh: the image is the content. No normals (the shader is
                    // unlit) and no wireframe, which over millions of tiny triangles is noise.
                    if (!result.TextureBytes.ContainsKey(g.TexturePath) && !missing.Contains(g.TexturePath))
                    {
                        byte[] bytes = ReadTexture(g.TexturePath, data.Warnings);
                        if (bytes != null) result.TextureBytes[g.TexturePath] = bytes;
                        else missing.Add(g.TexturePath);
                    }
                }
                else if (g.Triangles.Count > 0)
                {
                    // No normals: ModelTextured is unlit (see BuildGroup).
                    gb.EdgeIndices = BuildEdges(g, MaxEdgesPerGroup, token, out bool skipped);
                    if (skipped) data.Warnings.Add(L.T($"Ομάδα «{g.Name}»: πάρα πολλές ακμές, χωρίς περίγραμμα",
                        $"Group \u201c{g.Name}\u201d: too many edges, no outline"));
                }
                // Fill only (no outline or lines need the shared vertex list): cut it into
                // pieces here, off the main thread, and let the group's own lists go.
                if (g.Triangles.Count > 0 && gb.EdgeIndices.Length == 0 && g.LineVertices.Count == 0)
                {
                    Split(g, gb.Chunks, token);
                    g.Release();
                }
                result.Groups.Add(gb);
            }
            return result;
        }

        /// <summary>
        /// Cuts a group's triangles, in file order, into pieces of at most ChunkVertices vertices
        /// each, re-indexed from zero. Photogrammetry exports run tile by tile, so the pieces stay
        /// compact enough to cull. Worker thread.
        /// </summary>
        private static void Split(ModelGroup g, List<Chunk> into, CancellationToken token)
        {
            List<Vector3> v = g.Vertices;
            List<int> t = g.Triangles;
            bool uv = g.UVs.Count == v.Count && g.UVs.Count > 0;
            var stamp = new int[v.Count];   // piece number + 1 that last used each vertex
            var local = new int[v.Count];   // its index in that piece
            int piece = 0;
            var pv = new List<Vector3>(ChunkVertices);
            var puv = new List<Vector2>(uv ? ChunkVertices : 0);
            var pt = new List<ushort>(ChunkVertices * 2);

            void Flush()
            {
                if (pt.Count == 0) return;
                into.Add(new Chunk { Vertices = pv.ToArray(), UVs = uv ? puv.ToArray() : null, Triangles = pt.ToArray() });
                pv.Clear();
                puv.Clear();
                pt.Clear();
                piece++;
            }

            int Map(int i)
            {
                if (stamp[i] != piece + 1)
                {
                    stamp[i] = piece + 1;
                    local[i] = pv.Count;
                    pv.Add(v[i]);
                    if (uv) puv.Add(g.UVs[i]);
                }
                return local[i];
            }

            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                if ((i & 0x3FFFF) == 0) token.ThrowIfCancellationRequested();
                if (pv.Count + 3 > ChunkVertices) Flush();
                int a = Map(t[i]), b = Map(t[i + 1]), c = Map(t[i + 2]);
                pt.Add((ushort)a);
                pt.Add((ushort)b);
                pt.Add((ushort)c);
            }
            Flush();
        }

        /// <summary>Extent of every vertex, and the median height near the centre (life-size ground). Worker thread.</summary>
        private static void Measure(ModelData data, Prepared result)
        {
            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            foreach (ModelGroup g in data.Groups)
            {
                Grow(g.Vertices);
                Grow(g.LineVertices);
            }
            void Grow(List<Vector3> points)
            {
                foreach (Vector3 v in points)
                {
                    if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) continue;
                    if (!any) { min = max = v; any = true; }
                    else { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
                }
            }
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            result.Bounds = bounds;

            var heights = new List<float>();
            float r2 = GroundProbeRadius * GroundProbeRadius;
            foreach (ModelGroup g in data.Groups)
                foreach (Vector3 v in g.Vertices)
                {
                    float dx = v.x - bounds.center.x, dz = v.z - bounds.center.z;
                    if (dx * dx + dz * dz <= r2 && !float.IsNaN(v.y)) heights.Add(v.y);
                }
            if (heights.Count == 0) { result.GroundY = min.y; return; }
            heights.Sort();
            result.GroundY = heights[heights.Count / 2];
        }

        /// <summary>
        /// The encoded image at <paramref name="path"/>, or null with a warning when it is
        /// missing, unreadable, or so large that decoding it would exhaust the headset's memory
        /// (checked from the PNG/JPEG header, before any pixel is decoded). Worker thread.
        /// </summary>
        private static byte[] ReadTexture(string path, List<string> warnings)
        {
            string file = System.IO.Path.GetFileName(path);
            string msg = null;
            byte[] bytes = null;
            try
            {
                var fi = new System.IO.FileInfo(path);
                if (!fi.Exists) msg = L.T($"Λείπει η εικόνα υφής {file}", $"Texture image {file} is missing");
                else if (fi.Length > MaxTextureFileBytes)
                    msg = L.T($"Η εικόνα υφής {file} είναι πολύ μεγάλη ({fi.Length / (1 << 20)} MB)",
                        $"Texture image {file} is too large ({fi.Length / (1 << 20)} MB)");
                else
                {
                    bytes = System.IO.File.ReadAllBytes(path);
                    if (TryImageSize(bytes, out int w, out int h) && (w > MaxTextureSize || h > MaxTextureSize))
                    {
                        msg = L.T($"Η εικόνα υφής {file} είναι {w}x{h} px, μέγιστο {MaxTextureSize}",
                            $"Texture image {file} is {w}x{h} px, max {MaxTextureSize}");
                        bytes = null;
                    }
                }
            }
            catch (Exception e)
            {
                msg = L.T($"Η εικόνα υφής {file} δεν διαβάστηκε: {e.Message}", $"Texture image {file} could not be read: {e.Message}");
                bytes = null;
            }
            if (msg != null)
            {
                warnings.Add(msg);
                Debug.LogWarning($"[Model] {msg} ({path})");
            }
            return bytes;
        }

        /// <summary>Width/height from a PNG IHDR or a JPEG SOF marker; false for anything else.</summary>
        private static bool TryImageSize(byte[] b, out int width, out int height)
        {
            width = height = 0;
            if (b == null) return false;
            if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            {
                width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return true;
            }
            if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return false;
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }                       // fill byte
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                if (marker == 0xD9 || marker == 0xDA) return false;           // no SOF before the image data
                int length = (b[i + 2] << 8) | b[i + 3];
                bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (sof)
                {
                    height = (b[i + 5] << 8) | b[i + 6];
                    width = (b[i + 7] << 8) | b[i + 8];
                    return true;
                }
                if (length < 2) return false;
                i += 2 + length;
            }
            return false;
        }

        // Area-weighted vertex normals. The x-ray shader only needs "how edge-on is this face",
        // so smoothing across shared vertices is fine and flat quads stay flat anyway.
        // Currently unused: the fill is ModelTextured (unlit, no NORMAL input). Call it from
        // Prepare and SetNormals in BuildGroup again only if ModelXray is wired back in.
        private static Vector3[] BuildNormals(ModelGroup g)
        {
            var normals = new Vector3[g.Vertices.Count];
            List<Vector3> v = g.Vertices;
            List<int> t = g.Triangles;
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                normals[a] += n; normals[b] += n; normals[c] += n;
            }
            for (int i = 0; i < normals.Length; i++)
            {
                float m = normals[i].magnitude;
                normals[i] = m > 1e-12f ? normals[i] / m : Vector3.up;
            }
            return normals;
        }

        private struct EdgeRec
        {
            public int A, B;
            public Vector3 Normal;
            public bool Hidden;
        }

        /// <summary>
        /// Unique triangle edges as index pairs. Vertices are matched by position (0.1 mm) since
        /// the importer does not de-duplicate, and an edge shared by two coplanar triangles is
        /// dropped: that is the diagonal of a triangulated quad, not a real edge of the design.
        /// </summary>
        private static int[] BuildEdges(ModelGroup g, int maxEdges, CancellationToken token, out bool skipped)
        {
            skipped = false;
            List<Vector3> v = g.Vertices;
            List<int> t = g.Triangles;
            // A closed mesh has 1.5 edges per triangle: no point hashing a million of them first.
            if (t.Count / 3 > maxEdges) { skipped = true; return Array.Empty<int>(); }

            var canonOf = new int[v.Count];
            var canon = new Dictionary<(int, int, int), int>(v.Count);
            for (int i = 0; i < v.Count; i++)
            {
                Vector3 p = v[i];
                var key = (Mathf.RoundToInt(p.x * 10000f), Mathf.RoundToInt(p.y * 10000f), Mathf.RoundToInt(p.z * 10000f));
                if (!canon.TryGetValue(key, out int c)) { c = i; canon[key] = i; }
                canonOf[i] = c;
            }

            var index = new Dictionary<long, int>(t.Count);
            var records = new List<EdgeRec>(t.Count);
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                if ((i & 0x3FFFF) == 0) token.ThrowIfCancellationRequested();
                int a = t[i], b = t[i + 1], c = t[i + 2];
                Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]).normalized;
                if (!AddEdge(a, b) || !AddEdge(b, c) || !AddEdge(c, a))
                {
                    skipped = true;
                    return Array.Empty<int>();
                }

                bool AddEdge(int p, int q)
                {
                    int cp = canonOf[p], cq = canonOf[q];
                    if (cp == cq) return true;
                    long key = cp < cq ? ((long)cp << 32) | (uint)cq : ((long)cq << 32) | (uint)cp;
                    if (index.TryGetValue(key, out int r))
                    {
                        EdgeRec rec = records[r];
                        if (!rec.Hidden && Mathf.Abs(Vector3.Dot(rec.Normal, n)) > 0.9995f)
                        {
                            rec.Hidden = true;
                            records[r] = rec;
                        }
                        return true;
                    }
                    if (records.Count >= maxEdges) return false;
                    index[key] = records.Count;
                    records.Add(new EdgeRec { A = p, B = q, Normal = n });
                    return true;
                }
            }

            int visible = 0;
            foreach (EdgeRec r in records) if (!r.Hidden) visible++;
            var edges = new int[visible * 2];
            int k = 0;
            foreach (EdgeRec r in records)
            {
                if (r.Hidden) continue;
                edges[k++] = r.A;
                edges[k++] = r.B;
            }
            return edges;
        }

        // ------------------------------------------------------------------
        //  Main-thread mesh building
        // ------------------------------------------------------------------

        /// <summary>
        /// The group's object, its whole fill when it was not cut into pieces, and its edges and
        /// lines. Returns the work done (FrameBudget units).
        /// </summary>
        private int BuildGroup(int index, GroupBuild gb)
        {
            ModelGroup g = gb.Group;
            var go = new GameObject(string.IsNullOrEmpty(g.Name) ? $"group {index}" : g.Name);
            go.transform.SetParent(root, false);
            go.SetActive(g.Visible);
            groupObjects.Add(go);
            if (gb.Textured && textures.TryGetValue(g.TexturePath, out Texture2D tex)) gb.Fill = TexturedMaterial(g.TexturePath, tex, g.Color);
            else if (gb.Textured) gb.Fill = FillMaterial(PlainColor); // image missing: warned in Prepare / decode
            else gb.Fill = FillMaterial(g.Color);

            int work = g.Triangles.Count / 3 + gb.EdgeIndices.Length / 4 + g.LineVertices.Count / 2;
            if (g.Triangles.Count > 0)
            {
                var mesh = new Mesh { name = g.Name + " fill", indexFormat = IndexFormatFor(g.Vertices.Count) };
                mesh.SetVertices(g.Vertices);
                // No normals: the fill shader is unlit and never reads them, and on Quest every
                // vertex attribute is fetched (interleaved) whether the shader uses it or not.
                if (g.UVs.Count > 0 && g.UVs.Count == g.Vertices.Count) mesh.SetUVs(0, g.UVs);
                mesh.SetTriangles(g.Triangles, 0, true);
                // Unity drops its CPU copy, and the group's lists go once it is built.
                mesh.UploadMeshData(true);
                meshes.Add(mesh);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                Configure(go.AddComponent<MeshRenderer>(), gb.Fill);
            }

            int edgeCount = gb.EdgeIndices.Length / 2;
            int lineCount = g.LineVertices.Count / 2;
            if (edgeCount + lineCount == 0) return work;

            var edgesGo = new GameObject("edges");
            edgesGo.transform.SetParent(go.transform, false);
            edgesGo.SetActive(ShowEdges);
            edgeObjects.Add(edgesGo);

            var lines = new Mesh { name = g.Name + " edges" };
            int[] indices;
            if (lineCount == 0)
            {
                lines.indexFormat = IndexFormatFor(g.Vertices.Count);
                lines.SetVertices(g.Vertices);
                indices = gb.EdgeIndices;
            }
            else
            {
                // Triangle edges index the group's vertices; DXF-style segments bring their own
                // points, appended after them so one mesh carries both.
                int baseCount = edgeCount > 0 ? g.Vertices.Count : 0;
                var verts = new Vector3[baseCount + g.LineVertices.Count];
                if (baseCount > 0) g.Vertices.CopyTo(verts, 0);
                g.LineVertices.CopyTo(verts, baseCount);
                lines.indexFormat = IndexFormatFor(verts.Length);
                lines.SetVertices(verts);
                indices = new int[gb.EdgeIndices.Length + g.LineVertices.Count];
                Array.Copy(gb.EdgeIndices, indices, gb.EdgeIndices.Length);
                for (int i = 0; i < g.LineVertices.Count; i++) indices[gb.EdgeIndices.Length + i] = baseCount + i;
            }
            lines.SetIndices(indices, MeshTopology.Lines, 0, true);
            lines.UploadMeshData(true);
            meshes.Add(lines);
            edgesGo.AddComponent<MeshFilter>().sharedMesh = lines;
            Configure(edgesGo.AddComponent<MeshRenderer>(), EdgeMaterial());
            return work;
        }

        /// <summary>One piece of a cut fill, under its group's object. Returns its triangles.</summary>
        private int BuildChunk(int index, GroupBuild gb, Chunk chunk)
        {
            Transform parent = groupObjects[index].transform;
            var go = new GameObject("piece");
            go.transform.SetParent(parent, false);
            var mesh = new Mesh { name = gb.Group.Name + " fill", indexFormat = IndexFormat.UInt16 };
            mesh.SetVertices(chunk.Vertices);
            if (chunk.UVs != null) mesh.SetUVs(0, chunk.UVs);
            mesh.SetIndices(chunk.Triangles, MeshTopology.Triangles, 0, true);
            mesh.UploadMeshData(true);
            meshes.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            Configure(go.AddComponent<MeshRenderer>(), gb.Fill);
            return chunk.Triangles.Length / 3;
        }

        // 16-bit indices whenever the vertices fit (most CAD groups): half the index memory and
        // index fetch of 32-bit. Only a group past 65535 vertices needs 32-bit.
        private static IndexFormat IndexFormatFor(int vertexCount) =>
            vertexCount <= ushort.MaxValue ? IndexFormat.UInt16 : IndexFormat.UInt32;

        private static void Configure(MeshRenderer r, Material m)
        {
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        private void DestroyVisuals()
        {
            foreach (GameObject go in groupObjects) if (go != null) Destroy(go);
            groupObjects.Clear();
            edgeObjects.Clear();
            foreach (Mesh m in meshes) if (m != null) Destroy(m);
            meshes.Clear();
            foreach (var kv in texturedMaterials) if (kv.Value != null) Destroy(kv.Value);
            texturedMaterials.Clear();
            foreach (var kv in textures) if (kv.Value != null) Destroy(kv.Value);
            textures.Clear();
            hasTextures = false;
        }

        // ------------------------------------------------------------------
        //  Textures (main thread)
        // ------------------------------------------------------------------

        /// <summary>
        /// Encoded PNG/JPG to a mipmapped GPU texture. Compressed at runtime when the platform
        /// can (a 2048 px RGBA32 atlas is 22 MB with mips; ETC2/DXT a quarter of that) and made
        /// non-readable so the CPU copy is freed. Null when the bytes are not an image.
        /// </summary>
        private static Texture2D DecodeTexture(string path, byte[] bytes)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = System.IO.Path.GetFileNameWithoutExtension(path)
            };
            bool ok;
            try { ok = ImageConversion.LoadImage(tex, bytes, false); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Model] Texture decode failed for {path}: {e.Message}");
                ok = false;
            }
            if (!ok)
            {
                // LoadImage leaves a placeholder ("?" image) behind on failure: never show it.
                Destroy(tex);
                return null;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4; // terrain seen at a low angle
            bool compressed = false;
            try
            {
                tex.Compress(true);
                compressed = tex.format != TextureFormat.RGBA32 && tex.format != TextureFormat.RGB24 && tex.format != TextureFormat.ARGB32;
            }
            catch (Exception e)
            {
                Debug.Log($"[Model] Runtime texture compression unavailable ({e.Message}); {tex.name} stays uncompressed");
            }
            // Compress already compressed every mip level LoadImage generated; recomputing mips
            // is only possible (and only needed) on the uncompressed fallback.
            tex.Apply(!compressed, true);
            return tex;
        }

        // ------------------------------------------------------------------
        //  Materials (shared: one per colour, one per texture, one for every edge mesh)
        // ------------------------------------------------------------------

        /// <summary>
        /// QuestMarker/ModelTextured from Resources (so it always ships), else by name; null
        /// when neither works, and the materials then use the built-in Unlit fallbacks.
        /// </summary>
        private static Shader TexturedShader()
        {
            if (texturedShaderLooked) return texturedShader;
            texturedShaderLooked = true;
            texturedShader = Resources.Load<Shader>("Shaders/ModelTextured");
            if (texturedShader == null) texturedShader = Shader.Find(TexturedShaderName);
            if (texturedShader == null)
                Debug.LogWarning("[Model] Resources/Shaders/ModelTextured missing: fill falls back to Unlit/Texture and Unlit/Transparent");
            return texturedShader;
        }

        private static Shader FallbackShader(bool opaque)
        {
            Shader s = Shader.Find(opaque ? "Unlit/Texture" : "Unlit/Transparent");
            if (s == null) s = Shader.Find(opaque ? "Unlit/Transparent" : "Unlit/Texture");
            if (s == null) s = Shader.Find("Sprites/Default");
            // The error shader always exists: a magenta model beats a null-material exception.
            return s != null ? s : Shader.Find("Hidden/InternalErrorShader");
        }

        private Material FillMaterial(Color color)
        {
            Color key = new Color(color.r, color.g, color.b, 1f);
            if (fillMaterials.TryGetValue(key, out Material m) && m != null) return m;
            // Same shader as the textured groups, with the built-in white texture: one code
            // path for blending and opacity, the colour comes from _Color alone.
            m = NewFillMaterial("ModelFill", Texture2D.whiteTexture, key);
            fillMaterials[key] = m;
            return m;
        }

        private Material TexturedMaterial(string path, Texture2D tex, Color tint)
        {
            var key = (path, new Color(tint.r, tint.g, tint.b, 1f));
            if (texturedMaterials.TryGetValue(key, out Material m) && m != null) return m;
            m = NewFillMaterial("ModelTextured " + tex.name, tex, key.Item2);
            texturedMaterials[key] = m;
            return m;
        }

        private Material NewFillMaterial(string name, Texture tex, Color tint)
        {
            float alpha = Opacity;
            Shader shader = TexturedShader() ?? FallbackShader(alpha >= OpaqueFrom);
            var m = new Material(shader) { name = name, mainTexture = tex };
            SetFillAlpha(m, tint, alpha);
            return m;
        }

        /// <summary>
        /// Tint and opacity, plus the blend state that goes with it: opaque (Geometry queue,
        /// depth write, no blending) from <see cref="OpaqueFrom"/> up, alpha-blended
        /// (Transparent queue, no depth write) below. On the Unlit fallback the shader itself
        /// is swapped, since Unlit/Texture cannot blend and Unlit/Transparent cannot be opaque.
        /// </summary>
        private static void SetFillAlpha(Material m, Color tint, float alpha)
        {
            if (m == null) return;
            bool opaque = alpha >= OpaqueFrom;
            if (m.shader == null || m.shader.name != TexturedShaderName)
            {
                Shader fallback = FallbackShader(opaque);
                if (fallback != null && m.shader != fallback) m.shader = fallback;
            }
            else
            {
                m.SetFloat("_SrcBlend", (float)(opaque ? BlendMode.One : BlendMode.SrcAlpha));
                m.SetFloat("_DstBlend", (float)(opaque ? BlendMode.Zero : BlendMode.OneMinusSrcAlpha));
                m.SetFloat("_ZWrite", opaque ? 1f : 0f);
            }
            m.color = new Color(tint.r, tint.g, tint.b, opaque ? 1f : alpha);
            m.renderQueue = (int)(opaque ? RenderQueue.Geometry : RenderQueue.Transparent);
        }

        private Material EdgeMaterial()
        {
            if (edgeMaterial != null) return edgeMaterial;
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            edgeMaterial = new Material(shader) { name = "ModelEdges" };
            edgeMaterial.color = Brand.Fg.WithAlpha(EdgeAlpha);
            // Just after the fills, so the lines draw over their own group's faces.
            edgeMaterial.renderQueue = (int)RenderQueue.Transparent + 1;
            return edgeMaterial;
        }

        // ------------------------------------------------------------------
        //  Text
        // ------------------------------------------------------------------

        private static string CountText(ModelData m)
        {
            var parts = new List<string>(2);
            if (m.TriangleCount > 0) parts.Add(Compact(m.TriangleCount) + L.T(" τρίγωνα", m.TriangleCount == 1 ? " triangle" : " triangles"));
            if (m.LineCount > 0) parts.Add(Compact(m.LineCount) + L.T(" γραμμές", m.LineCount == 1 ? " line" : " lines"));
            return parts.Count > 0 ? string.Join(" · ", parts) : L.T("χωρίς γεωμετρία", "no geometry");
        }

        private static string Compact(int n)
        {
            if (n >= 1_000_000) return (n / 1_000_000f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
            if (n >= 10_000) return (n / 1000).ToString() + "k";
            if (n >= 1000) return (n / 1000f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k";
            return n.ToString();
        }
    }
}
