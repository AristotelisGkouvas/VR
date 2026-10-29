using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;
using QuestMarkerApp.Models;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// How the ground moved: every survey point grows one arrow along its measured displacement
    /// (dx/dy/dz from the zero reading) at the chosen date, exaggerated ×20…×5000 so millimetres
    /// read at arm's length, coloured green → amber → red by size. A time bar replays the whole
    /// record: the stick scrubs, A plays it as a film. On the aligned site the arrows stand on
    /// the real markers; while the model is previewed (table or 1:1) on its sensor dots, so no
    /// alignment is needed. No path of past readings: its zigzag of survey noise read as a
    /// tangle of extra lines around the one arrow that matters.
    /// </summary>
    public class DisplacementView : MonoBehaviour
    {
        private static readonly float[] Scales = { 20f, 50f, 100f, 200f, 500f, 1000f, 2000f, 5000f };
        /// <summary>On a miniature, opening picks the first scale that draws RedAtMm about this long (metres).</summary>
        private const float TabletopArrow = 0.1f;
        /// <summary>Displacement (mm) drawn fully red; half of it is amber.</summary>
        private const float RedAtMm = 20f;
        private const float PlaySeconds = 10f;   // whole record, start to end
        private const float ScrubSeconds = 5f;   // stick held fully over: whole record
        private const float LabelWithin = 25f;   // metres: mm readouts only on nearby arrows
        private static readonly string[] Months =
            { "Ιαν", "Φεβ", "Μαρ", "Απρ", "Μαΐ", "Ιουν", "Ιουλ", "Αυγ", "Σεπ", "Οκτ", "Νοε", "Δεκ" };

        public static bool IsActive { get; private set; }
        /// <summary>Frame it last closed, so the B that closed it does nothing else (route skip).</summary>
        public static int ClosedFrame { get; private set; } = -1;

        private ProjectAlignment project;
        // Opening places the time bar in front at once; afterwards it trails the head smoothly.
        private bool hudSnap;
        private MarkerManager markers;
        private ModelSensors modelSensors;
        private ModelOverlay modelOverlay;
        private readonly List<Item> items = new List<Item>();
        private Material lineMaterial;
        private Mesh headMesh;
        private const int SiteScaleIndex = 2; // ×100
        private int scaleIndex = SiteScaleIndex;
        /// <summary>
        /// Compare mode: the arrows show the movement since this date ("what moved since the
        /// inspection of 3/10") instead of since the zero reading. The grip sets it at the
        /// date on the bar and clears it again.
        /// </summary>
        private DateTime? baseline;
        private Image hudBase;
        private float progress = 1f;
        private bool playing;
        private float nextScaleStep;

        private Transform hud;
        private TextMeshProUGUI hudTitle, hudDate, hudFrom, hudTo, hudCount, hudHint;
        private Image hudFill, hudPlay;
        private const float TrackW = 520f;

        // What the HUD currently shows, so strings are only rebuilt when the shown value changes.
        private float hudScaleShown = float.NaN;
        private float hudScaleBar = float.NaN;
        private DateTime hudDateShown = DateTime.MinValue;
        private float hudProgressShown = -1f;
        private bool? hudPlayingShown;

        // Change detection for the per-frame pass: the heavy work (trig per sample, line mesh
        // rebuilds) only happens when time, scale or alignment moved; the light camera-relative
        // work (widths, label facing) only when the camera or the data moved.
        private int alignmentVersion;                 // bumped by ProjectAlignment.OnAlignmentChanged
        private int cacheVersion;                     // alignment version + yaws folded in; items key on it
        private int lastAlignmentVersion = -1, lastCacheVersion = -1;
        private float lastGridYaw = float.NaN, lastSolvedYaw = float.NaN;
        private bool lastOnModel;
        private Matrix4x4 lastModelFrame;
        private DateTime lastAt = DateTime.MinValue;
        private float lastScale = float.NaN;
        private Vector3 lastCamPos;
        private Quaternion lastCamRot;
        private bool framePrimed;

        private class Item
        {
            public string MarkerId, PointId, SensorId;
            /// <summary>Metres per unit of this sensor's dx/dy/dz (SensorHistory.MetresPerUnit).</summary>
            public double MetresPerUnit = 0.001;
            public IReadOnlyList<SensorHistory.Sample> Series;
            public Transform Root, Head;
            public LineRenderer Shaft;
            public Material HeadMaterial;
            public TextMeshPro Label;

            // Key of the cached arrow: recomputed only when one of these moves.
            public bool HasCache;
            public int CachedIndex = -1;              // last sample index with Date <= at (-1: before first)
            public float CachedScale;
            public Vector3 CachedBase;
            public int CachedVersion;
            // Cached arrow state for the reading in force at CachedIndex.
            public bool Valid;                        // a displacement reading exists and maps to world
            public Vector3 Vec;
            public float Mm;
            public Color Color;
            public string Text;
            // Current SetActive state, so it is only toggled on change.
            public bool RootOn = true, ArrowOn = true, LabelOn = true;
        }

        public void Init(ProjectAlignment alignment, MarkerManager markerManager)
        {
            project = alignment;
            markers = markerManager;
            // A named handler, so OnDestroy can unsubscribe (the lambda kept this view alive).
            project.OnAlignmentChanged += OnAlignmentChanged;
            Hands.OnChanged += RefreshHint;
            L.Changed += OnLanguageChanged;
        }

        // The hint now; the HUD texts on the next frame once their keys are reset.
        private void OnLanguageChanged()
        {
            RefreshHint();
            hudScaleShown = float.NaN;
            hudDateShown = DateTime.MinValue;
            if (IsActive) SetConstantTexts();
        }

        private void OnAlignmentChanged()
        {
            alignmentVersion++;
            if (IsActive && !CanDraw) Close();
        }

        /// <summary>The model preview shows the sensor dots: the arrows go there.</summary>
        private bool OnModel
        {
            get
            {
                if (modelSensors == null) modelSensors = FindAnyObjectByType<ModelSensors>();
                return modelSensors != null && modelSensors.Showing;
            }
        }

        private bool CanDraw => project != null && project.HasProject && (OnModel || project.IsAligned);

        // The HUD hint names A / B after the dominant hand.
        private void RefreshHint()
        {
            if (hudHint != null)
                hudHint.text = L.En
                    ? $"{Hands.PrimaryName} play · stick ↔ time · ↕ scale · grip: compare from here · {Hands.SecondaryName} exit"
                    : $"{Hands.PrimaryName} αναπαραγωγή · stick ↔ χρόνος · ↕ κλίμακα · grip: σύγκριση από εδώ · {Hands.SecondaryName} έξοδος";
        }

        /// <summary>Why it can't open right now, or null when it can.</summary>
        public string Unavailable()
        {
            if (project == null || !project.HasProject) return L.T("Φόρτωσε έργο από Terraweb", "Load a project from Terraweb");
            if (!SensorHistory.HasDisplacements || SensorHistory.ProjectId != project.TerrawebProjectId)
                return L.T("Χωρίς ιστορικό · φόρτωσε ξανά το έργο", "No history · reload the project");
            if (!OnModel && !project.IsAligned)
                return L.T("Άνοιξε το μοντέλο ή κάνε ευθυγράμμιση", "Open the model or align first");
            return null;
        }

        public void Toggle()
        {
            if (IsActive) Close();
            else Open();
        }

        public bool Open()
        {
            if (Unavailable() != null) return false;
            Build();
            items.Clear();
            foreach (ProjectPoint p in project.Points)
            {
                var series = SensorHistory.Of(p.sensorId);
                if (series == null) continue;
                bool any = false;
                foreach (var s in series) if (s.HasDisplacement) { any = true; break; }
                if (!any) continue;
                Item item = CreateItem(ProjectAlignment.MarkerPrefix + p.id, series);
                item.PointId = p.id;
                item.SensorId = p.sensorId;
                item.MetresPerUnit = SensorHistory.MetresPerUnit(p.sensorId);
                items.Add(item);
            }
            if (items.Count == 0) return false;

            IsActive = true;
            hudSnap = true; // it used to glide in from wherever it was last closed (or the origin)
            hud.gameObject.SetActive(true);
            SetConstantTexts();
            hudScaleShown = float.NaN;
            hudDateShown = DateTime.MinValue;
            hudProgressShown = -1f;
            hudPlayingShown = null;
            framePrimed = false;
            baseline = null;
            hudBase.gameObject.SetActive(false);
            lastOnModel = OnModel;
            scaleIndex = lastOnModel ? ModelScaleIndex() : SiteScaleIndex;
            // Opening plays the whole record once: the site moving from day one to today.
            progress = 0f;
            playing = true;
            Feedback.Play(Feedback.Sound.Whoosh, 0.7f);
            return true;
        }

        public void Close()
        {
            if (IsActive) ClosedFrame = Time.frameCount;
            IsActive = false;
            playing = false;
            foreach (Item it in items)
            {
                if (it.HeadMaterial != null) Destroy(it.HeadMaterial);
                if (it.Root != null) Destroy(it.Root.gameObject);
            }
            items.Clear();
            if (hud != null) hud.gameObject.SetActive(false);
        }

        // Constant while open (and re-set on a language change).
        private void SetConstantTexts()
        {
            hudScaleBar = float.NaN; // the count line now carries the scale (UpdateHud)
            hudFrom.text = Format(SensorHistory.First);
            hudTo.text = Format(SensorHistory.Last);
        }

        private void OnDestroy()
        {
            Hands.OnChanged -= RefreshHint;
            L.Changed -= OnLanguageChanged;
            if (project != null) project.OnAlignmentChanged -= OnAlignmentChanged;
            IsActive = false;
            items.Clear();
        }

        // A miniature is ~1/100 of the site: ×100 would draw 20 mm as 2 cm. The first scale that
        // makes it about TabletopArrow; at 1:1 that is the low end, as on the site.
        private int ModelScaleIndex()
        {
            if (modelOverlay == null) modelOverlay = FindAnyObjectByType<ModelOverlay>();
            Transform root = modelOverlay != null ? modelOverlay.Root : null;
            float rootScale = root != null ? Mathf.Max(root.lossyScale.x, 1e-6f) : 1f;
            float want = TabletopArrow / (RedAtMm * 0.001f * rootScale);
            for (int i = 0; i < Scales.Length; i++)
                if (Scales[i] >= want) return i;
            return Scales.Length - 1;
        }

        // ------------------------------------------------------------------
        //  Per frame
        // ------------------------------------------------------------------

        private void Update()
        {
            if (!IsActive) return;
            if (!CanDraw) { Close(); return; }
#if !MOBILE_PREVIEW
            if (!UiState.InputBlocked && !project.IsModeActive) HandleInput();
#endif
            if (playing)
            {
                progress += Time.deltaTime / PlaySeconds;
                if (progress >= 1f)
                {
                    progress = 1f;
                    playing = false;
                    Feedback.Play(Feedback.Sound.Ping, 0.5f);
                }
            }
        }

        private void LateUpdate()
        {
            if (!IsActive) return;
            Camera cam = Camera.main;
            if (cam == null) return;

            // Between the model and the site the sizes differ ~100-fold: re-pick the scale, or a
            // miniature's ×500 draws 20 mm as a 10 m arrow on the site.
            bool onModel = OnModel;
            if (onModel != lastOnModel) scaleIndex = onModel ? ModelScaleIndex() : SiteScaleIndex;

            DateTime at = CurrentAt();
            float scale = Scales[scaleIndex];

            // Alignment key: the change event plus the yaws Draw's maths reads from the project.
            // On the model: where it stands, turns and how far it is zoomed.
            float gridYaw = project.GridYawDeg, solvedYaw = project.YawDeg;
            Transform frame = onModel ? modelSensors.Container : null;
            Matrix4x4 modelFrame = frame != null ? frame.localToWorldMatrix : Matrix4x4.identity;
            if (alignmentVersion != lastAlignmentVersion || gridYaw != lastGridYaw || solvedYaw != lastSolvedYaw
                || onModel != lastOnModel || modelFrame != lastModelFrame)
            {
                lastAlignmentVersion = alignmentVersion;
                lastGridYaw = gridYaw;
                lastSolvedYaw = solvedYaw;
                lastOnModel = onModel;
                lastModelFrame = modelFrame;
                cacheVersion++;
            }

            Transform camT = cam.transform;
            Vector3 camPos = camT.position;
            Quaternion camRot = camT.rotation;
            bool dataChanged = !framePrimed || at != lastAt || scale != lastScale || cacheVersion != lastCacheVersion;
            bool camChanged = !framePrimed || camPos != lastCamPos || camRot != lastCamRot;
            if (dataChanged || camChanged)
            {
                double yaw = gridYaw * Math.PI / 180.0;
                double cosYaw = Math.Cos(yaw), sinYaw = Math.Sin(yaw);
                foreach (Item it in items) Draw(it, at, scale, cosYaw, sinYaw, camPos, frame);
                lastAt = at;
                lastScale = scale;
                lastCacheVersion = cacheVersion;
                lastCamPos = camPos;
                lastCamRot = camRot;
                framePrimed = true;
            }
            UpdateHud(cam, at, scale);
        }

#if !MOBILE_PREVIEW
        private void HandleInput()
        {
            float x = Hands.AnyStickX();
            if (Mathf.Abs(x) > 0.15f)
            {
                playing = false;
                // Squared: a light push creeps, a full push sweeps.
                progress = Mathf.Clamp01(progress + x * Mathf.Abs(x) * Time.deltaTime / ScrubSeconds);
            }

            float y = Hands.AnyStickY();
            if (Mathf.Abs(y) > 0.7f && Mathf.Abs(x) < 0.5f && Time.unscaledTime >= nextScaleStep)
            {
                int next = Mathf.Clamp(scaleIndex + (y > 0 ? 1 : -1), 0, Scales.Length - 1);
                if (next != scaleIndex)
                {
                    scaleIndex = next;
                    Feedback.Play(Feedback.Sound.Tick);
                    Feedback.Buzz(Hands.Dominant, 0.2f, 0.03f);
                }
                nextScaleStep = Time.unscaledTime + 0.35f;
            }
            else if (Mathf.Abs(y) < 0.3f) nextScaleStep = 0f;

            if (Hands.GripDown() && !Hands.OffHandGripHeld())
            {
                baseline = baseline.HasValue ? (DateTime?)null : CurrentAt();
                cacheVersion++; // every arrow is now measured from elsewhere
                hudScaleShown = float.NaN;
                Feedback.Play(Feedback.Sound.Click);
                Feedback.Buzz(Hands.Dominant, 0.3f, 0.04f);
            }

            if (Hands.DominantPrimaryDown())
            {
                if (!playing && progress >= 1f) progress = 0f;
                playing = !playing;
                Feedback.Play(Feedback.Sound.Click);
            }
            // B stops navigation first, if that is running.
            if (Hands.DominantSecondaryDown() && !SensorNavigator.IsActive
                && SensorNavigator.StoppedFrame != Time.frameCount
                && !MeasureTool.IsActive && MeasureTool.ClosedFrame != Time.frameCount)
            {
                Feedback.Play(Feedback.Sound.Undo, 0.6f);
                Close();
            }
        }
#endif

        // Survey-grid millimetres → a world vector, exaggerated. Yaw's cos/sin are hoisted per frame.
        // On the model the project frame is the model's own (x East, y Up, z North), zoom included.
        private bool ToWorld(in SensorHistory.Sample s, double metresPerUnit, float scale, double cosYaw, double sinYaw,
            Transform modelFrame, out Vector3 world)
        {
            double dx = s.Dx ?? 0, dy = s.Dy ?? 0, dz = s.Dz ?? 0;
            double e = dx * cosYaw - dy * sinYaw;
            double n = dx * sinYaw + dy * cosYaw;
            var v = new Vector3((float)e, (float)dz, (float)n) * (float)(metresPerUnit * scale);
            if (modelFrame == null) return project.TryWorldVector(v, out world);
            world = modelFrame.TransformVector(v);
            return true;
        }

        // Last sample index with Date <= at (-1 before the first). O(1) while the index is unchanged
        // (paused, or time moving within one reading), binary search otherwise.
        private static int IndexAt(IReadOnlyList<SensorHistory.Sample> s, DateTime at)
        {
            int lo = 0, hi = s.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (s[mid].Date <= at) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }

        private static int IndexAt(Item it, DateTime at)
        {
            var s = it.Series;
            int n = s.Count, i = it.CachedIndex;
            if (it.HasCache && i >= -1 && i < n
                && (i < 0 || s[i].Date <= at) && (i + 1 >= n || s[i + 1].Date > at))
                return i;
            int lo = 0, hi = n - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (s[mid].Date <= at) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found;
        }

        private static void SetOn(GameObject go, ref bool state, bool on)
        {
            if (state == on) return;
            state = on;
            go.SetActive(on);
        }

        private void Draw(Item it, DateTime at, float scale, double cosYaw, double sinYaw, Vector3 camPos, Transform modelFrame)
        {
            Transform anchor = null;
            if (modelFrame != null) anchor = modelSensors.DotOf(it.PointId);
            else if (markers.ActiveMarkers.TryGetValue(it.MarkerId, out Marker m) && m != null) anchor = m.transform;
            if (anchor == null || !anchor.gameObject.activeInHierarchy)
            {
                SetOn(it.Root.gameObject, ref it.RootOn, false);
                return;
            }

            Vector3 basePos = anchor.position;
            int index = IndexAt(it, at);

            // The arrow is a pure function of (sample index, scale, base position, alignment):
            // the reading in force does not change between two sample dates, so a paused or
            // slowly advancing clock costs nothing here.
            bool sameFrame = it.HasCache && scale == it.CachedScale && basePos == it.CachedBase && it.CachedVersion == cacheVersion;
            if (!sameFrame || index != it.CachedIndex)
            {
                // Head: newest reading at or before `at` that has a displacement (as TryDisplacementAt).
                it.Valid = false;
                // Compare mode: the reading in force at the baseline is the new zero.
                bool hasBase = false;
                SensorHistory.Sample zero = default;
                if (baseline.HasValue)
                {
                    for (int i = IndexAt(it.Series, baseline.Value); i >= 0; i--)
                        if (it.Series[i].HasDisplacement) { zero = it.Series[i]; hasBase = true; break; }
                }
                for (int i = index; i >= 0; i--)
                {
                    SensorHistory.Sample now = it.Series[i];
                    if (!now.HasDisplacement) continue;
                    if (hasBase)
                    {
                        now.Dx -= zero.Dx ?? 0;
                        now.Dy -= zero.Dy ?? 0;
                        now.Dz = (now.Dz ?? 0) - (zero.Dz ?? 0);
                    }
                    if (ToWorld(now, it.MetresPerUnit, scale, cosYaw, sinYaw, modelFrame, out Vector3 vec))
                    {
                        it.Valid = true;
                        it.Vec = vec;
                        it.Mm = (float)(Math.Sqrt(Sq(now.Dx) + Sq(now.Dy) + Sq(now.Dz)) * it.MetresPerUnit * 1000.0);
                        it.Color = Heat(it.Mm);
                        it.Text = $"{it.Mm:F1} mm";
                    }
                    break;
                }

                if (it.Valid)
                {
                    Color color = it.Color;
                    it.Shaft.startColor = color.WithAlpha(0.75f);
                    it.Shaft.endColor = color;
                    it.HeadMaterial.color = color;
                    it.Label.text = it.Text;
                    it.Label.color = color;
                }

                it.HasCache = true;
                it.CachedIndex = index;
                it.CachedScale = scale;
                it.CachedBase = basePos;
                it.CachedVersion = cacheVersion;
            }

            if (!it.Valid)
            {
                SetOn(it.Root.gameObject, ref it.RootOn, false);
                return;
            }
            SetOn(it.Root.gameObject, ref it.RootOn, true);

            // Camera-relative sizing: cheap, and the only part that follows the viewer.
            float camDist = Vector3.Distance(camPos, basePos);
            float width = Mathf.Max(0.006f, camDist * 0.0035f);
            Vector3 vecNow = it.Vec;
            float length = vecNow.magnitude;
            bool visible = length > width * 2f;
            if (it.ArrowOn != visible)
            {
                it.ArrowOn = visible;
                it.Shaft.gameObject.SetActive(visible);
                it.Head.gameObject.SetActive(visible);
            }
            Vector3 tip = basePos + vecNow;
            if (visible)
            {
                Vector3 dir = vecNow / length;
                float headLen = Mathf.Min(width * 4f, length * 0.5f);
                it.Shaft.SetPosition(0, basePos);
                it.Shaft.SetPosition(1, tip - dir * headLen * 0.9f);
                it.Shaft.startWidth = it.Shaft.endWidth = width;
                it.Head.SetPositionAndRotation(tip - dir * headLen, Quaternion.LookRotation(dir));
                it.Head.localScale = new Vector3(width * 1.6f, width * 1.6f, headLen);
            }

            bool label = visible && camDist < LabelWithin;
            SetOn(it.Label.gameObject, ref it.LabelOn, label);
            if (label)
            {
                Vector3 pos = tip + Vector3.up * (width * 3f);
                it.Label.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - camPos));
                // TMP 3D: font size 10 is ~1 unit tall, so this is ~1.2 cm of text per metre away.
                it.Label.transform.localScale = Vector3.one * Mathf.Max(0.01f, camDist * 0.012f);
            }
        }

        private static double Sq(double? v) => v.HasValue ? v.Value * v.Value : 0;

        // Green → amber at half the red mark → red.
        private static Color Heat(float mm)
        {
            float t = Mathf.Clamp01(mm / RedAtMm);
            return t < 0.5f ? Color.Lerp(Brand.Primary, Brand.Warning, t * 2f)
                            : Color.Lerp(Brand.Warning, Brand.Danger, (t - 0.5f) * 2f);
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        private Item CreateItem(string markerId, IReadOnlyList<SensorHistory.Sample> series)
        {
            var root = new GameObject("Displacement_" + markerId).transform;
            root.SetParent(transform, false);

            LineRenderer Line(string name, int order)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = lineMaterial;
                lr.positionCount = 2;
                lr.useWorldSpace = true;
                lr.numCapVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.sortingOrder = order;
                return lr;
            }

            LineRenderer shaft = Line("Shaft", 9);

            var head = new GameObject("Head");
            head.transform.SetParent(root, false);
            head.AddComponent<MeshFilter>().sharedMesh = headMesh;
            var hr = head.AddComponent<MeshRenderer>();
            var mat = new Material(lineMaterial);
            hr.sharedMaterial = mat;
            hr.sortingOrder = 9;
            hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(root, false);
            var label = labelGo.AddComponent<TextMeshPro>();
            Brand.Style(label, true);
            label.fontSize = 10f;
            label.alignment = TextAlignmentOptions.Bottom;
            label.rectTransform.sizeDelta = new Vector2(20f, 3f);
            label.sortingOrder = 10;
            // One shared outlined material (the marker labels' one): setting outlineWidth per
            // label cloned a material per arrow that was never destroyed.
            Material outlined = Brand.LabelMaterial;
            if (outlined != null) label.fontSharedMaterial = outlined;

            return new Item
            {
                MarkerId = markerId, Series = series, Root = root, Head = head.transform,
                Shaft = shaft, HeadMaterial = mat, Label = label
            };
        }

        private void Build()
        {
            if (lineMaterial == null) lineMaterial = new Material(Shader.Find("Sprites/Default"));
            if (headMesh == null) headMesh = Cone(12);
            if (hud == null) BuildHud();
        }

        // Unit cone along +Z: base radius 1 at z=0, tip at z=1.
        private static Mesh Cone(int sides)
        {
            var v = new List<Vector3> { new Vector3(0, 0, 1), Vector3.zero };
            for (int i = 0; i < sides; i++)
            {
                float a = i * Mathf.PI * 2f / sides;
                v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f));
            }
            var t = new List<int>();
            for (int i = 0; i < sides; i++)
            {
                int a = 2 + i, b = 2 + (i + 1) % sides;
                t.AddRange(new[] { 0, b, a, 1, a, b });
            }
            var mesh = new Mesh { name = "ArrowHead" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------
        //  Time bar
        // ------------------------------------------------------------------

        private void BuildHud()
        {
            hud = new GameObject("TimeMachineHud").transform;
            hud.SetParent(transform, false);
            RectTransform c = VrUi.CreateCanvas("Canvas", hud, new Vector2(560f, 160f), 0.0006f);
            VrUi.Card("Bg", c, 0, 0, 560, 160, Brand.Surface.WithAlpha(0.96f), Brand.BorderStrong, 22);
            VrUi.Icon("Clock", c, 20, 16, 26, Brand.UiIconSprite("clock"), Brand.Primary);
            hudTitle = VrUi.Text("Title", c, 56, 12, 330, 34, "", 18f, Brand.Fg, true);
            hudCount = VrUi.Text("Count", c, 330, 12, 160, 34, "", 14f, Brand.FgSubtle, false, TextAlignmentOptions.MidlineRight);
            hudPlay = VrUi.Icon("Play", c, 510, 16, 26, Brand.UiIconSprite("play"), Brand.Fg);
            hudDate = VrUi.Text("Date", c, 20, 48, 520, 44, "", 30f, Brand.Fg, true, TextAlignmentOptions.Center);
            VrUi.Panel("Track", c, 20, 100, TrackW, 8, Brand.SurfaceSunken, 4);
            hudFill = VrUi.Panel("Fill", c, 20, 100, 8, 8, Brand.Primary, 4);
            hudBase = VrUi.Panel("Baseline", c, 20, 94, 4, 20, Brand.Info, 2);
            hudBase.gameObject.SetActive(false);
            hudFrom = VrUi.Text("From", c, 20, 110, 250, 22, "", 13f, Brand.FgSubtle);
            hudTo = VrUi.Text("To", c, 290, 110, 250, 22, "", 13f, Brand.FgSubtle, false, TextAlignmentOptions.MidlineRight);
            hudHint = VrUi.Text("Hint", c, 20, 132, 520, 22, "", 13f, Brand.FgFaint, false, TextAlignmentOptions.Center);
            // The full button legend is wider than the bar at 13 px: shrink rather than "…".
            hudHint.enableAutoSizing = true;
            hudHint.fontSizeMin = 11f;
            hudHint.fontSizeMax = 13f;
            RefreshHint();
            hud.gameObject.SetActive(false);
        }

        private void UpdateHud(Camera cam, DateTime at, float scale)
        {
            // Below the line of sight, a little further than the navigation arrow.
            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            Vector3 target = cam.transform.position + forward.normalized * 0.75f + Vector3.down * 0.45f;
            hud.position = hudSnap ? target : Vector3.Lerp(hud.position, target, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
            hudSnap = false;
            VrUi.FaceViewer(hud, cam.transform);

            // Always on screen: the arrows are a diagram, not true size. "10 mm = 1.0 m" is how
            // long 10 mm of movement is drawn where the arrows stand (site, table or 1:1 model).
            float zoom = lastOnModel && modelSensors != null && modelSensors.Container != null
                ? modelSensors.Container.lossyScale.x : 1f;
            float drawn = 0.01f * scale * zoom;
            if (!Mathf.Approximately(drawn, hudScaleBar))
            {
                hudScaleBar = drawn;
                string len = drawn >= 1f ? $"{drawn:0.0} m" : $"{drawn * 100f:0} cm";
                hudCount.text = $"10 mm = {len}";
            }

            if (scale != hudScaleShown)
            {
                hudScaleShown = scale;
                hudTitle.text = baseline.HasValue
                    ? (L.En ? $"Since {Format(baseline.Value)} · ×{scale:0}" : $"Από {Format(baseline.Value)} · ×{scale:0}")
                    : (L.En ? $"Displacements · ×{scale:0}" : $"Μετακινήσεις · ×{scale:0}");
                bool on = baseline.HasValue;
                if (hudBase.gameObject.activeSelf != on) hudBase.gameObject.SetActive(on);
                if (on) hudBase.rectTransform.anchoredPosition = new Vector2(20f + TrackW * Fraction(baseline.Value) - 2f,
                    hudBase.rectTransform.anchoredPosition.y);
            }
            DateTime day = at.Date;
            if (day != hudDateShown)
            {
                hudDateShown = day;
                hudDate.text = Format(at);
            }
            if (progress != hudProgressShown)
            {
                hudProgressShown = progress;
                hudFill.rectTransform.sizeDelta = new Vector2(Mathf.Max(8f, TrackW * progress), 8f);
            }
            if (hudPlayingShown != playing)
            {
                hudPlayingShown = playing;
                hudPlay.sprite = Brand.UiIconSprite(playing ? "pause" : "play");
            }
        }

        private DateTime CurrentAt()
        {
            DateTime first = SensorHistory.First, last = SensorHistory.Last;
            return first + TimeSpan.FromTicks((long)((last - first).Ticks * (double)progress));
        }

        // Where a date sits on the bar, 0..1.
        private static float Fraction(DateTime d)
        {
            long span = (SensorHistory.Last - SensorHistory.First).Ticks;
            return span <= 0 ? 0f : Mathf.Clamp01((float)((double)(d - SensorHistory.First).Ticks / span));
        }

        // English: "4 Jul 2026" from the invariant culture's month names (always present on device).
        private static string Format(DateTime d) => L.En
            ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : $"{d.Day} {Months[d.Month - 1]} {d.Year}";
    }
}
