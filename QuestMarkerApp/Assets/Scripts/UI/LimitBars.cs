using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Markers;

namespace QuestMarkerApp.UI
{
    /// <summary>
    /// Limit bars (feature 10): a vertical bullet gauge to the LEFT of every sensor icon that has
    /// an alert limit (the history ribbon is on the right). Its length is U, the "% of limit"
    /// the headset computes (<see cref="LimitMath"/>); its colour is always the server's tier
    /// (ProjectPoint.alertState, kept fresh by LiveAlerts), never one the headset worked out.
    /// Ticks mark reference / alert / alarm; the scale runs to 120 %, with a spike above that.
    /// Where the headset finds U ≥ 100 % but the server's tier is lower, a "?" sits on the bar.
    /// A sensor without a limit gets no bar at all — an empty bar would read "0 %".
    ///
    /// Levels of detail for ~200 sensors: beyond 25 m nothing, 8–25 m the bar, under 8 m also
    /// "72% · dz". Same mechanics as <see cref="HistoryRibbons"/>: one small mesh per bar with
    /// vertex colours on one shared material (they batch), rebuilt only when the markers, the
    /// limits or a server tier change; the per-frame pass only places, scales and culls.
    /// Also hosts the "Near limits" list.
    /// </summary>
    public class LimitBars : MonoBehaviour
    {
        private const string PrefKey = "ui.limitbars";
        private const float BarDistance = 25f, LabelDistance = 8f;
        private const int NearLimitsCount = 15;
        // Marker's icon rule, as in HistoryRibbons, so the bar sizes exactly like its icon.
        private const float IconAngularSize = 0.03f, IconMinSize = 0.06f;

        // Layout in icon-size units (the icon's halo spans 1.0). Height ≈ 1.4 icon diameters.
        private const float Right = -0.72f, BarWidth = 0.14f, Bottom = -0.7f, BarHeight = 1.4f;
        private const float Outline = 0.025f;
        private const float TickOverhang = 0.05f, TickHeight = 0.028f;
        private const float SpikeHeight = 0.14f;
        /// <summary>Top of the scale: 120 % of the top limit.</summary>
        private const double MaxU = 1.2;
        private const float LabelScale = 0.2f;

        /// <summary>Below reference: a neutral grey that recedes (no green for "OK").</summary>
        public static readonly Color Neutral = Brand.Hex(0x7f9491);
        /// <summary>Reference tier: light amber (displacement-visualization.md §2.3 table A).</summary>
        public static readonly Color ReferenceColor = Brand.Hex(0xf3d27a);

        private static bool? enabledPref;

        /// <summary>Main-menu switch, persisted; off by default.</summary>
        public static bool Enabled
        {
            get => enabledPref ?? (bool)(enabledPref = PlayerPrefs.GetInt(PrefKey, 0) == 1);
            set
            {
                enabledPref = value;
                PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        // One material for every bar: colour rides in the vertices, so the bars batch.
        private static Material barMaterial;
        private static Material BarMaterial =>
            barMaterial != null ? barMaterial : (barMaterial = new Material(Shader.Find("Sprites/Default")) { name = "LimitBar" });

        private class Bar
        {
            public string MarkerId;
            public Marker Marker;
            public ProjectPoint Point;
            public SensorLimits.Entry Entry;
            public Transform Root;
            public Mesh Mesh;
            public TextMeshPro Label, Question;
            // The server tier the mesh was coloured for (compared by reference each frame).
            public string BuiltState;
            public bool HasData, On, LabelOn;
        }

        private ProjectAlignment project;
        private MarkerManager markers;
        private readonly Dictionary<string, Bar> byId = new Dictionary<string, Bar>();
        private readonly List<Bar> items = new List<Bar>();
        private readonly List<string> stale = new List<string>();
        // Mesh scratch, reused for every bar.
        private readonly List<Vector3> verts = new List<Vector3>(64);
        private readonly List<Color32> colors = new List<Color32>(64);
        private readonly List<int> tris = new List<int>(96);
        private readonly List<SensorLimits.Entry> ranked = new List<SensorLimits.Entry>();
        private bool dirty = true;
        private bool anyShown;
        private int lastVersion = -1;

        private Action<Marker> onMarkerChanged;
        private Action onMarkersCleared, onAlignmentChanged;

        /// <summary>Bars currently built (0 while the feature is off).</summary>
        public int BarCount => Enabled ? items.Count : 0;

        public void Init(ProjectAlignment alignment, MarkerManager markerManager)
        {
            project = alignment;
            markers = markerManager;
            onMarkerChanged = _ => dirty = true;
            onMarkersCleared = () => dirty = true;
            onAlignmentChanged = () => dirty = true;
            if (markers != null)
            {
                markers.OnMarkerCreated += onMarkerChanged;
                markers.OnMarkerRemoved += onMarkerChanged;
                markers.OnMarkersCleared += onMarkersCleared;
            }
            if (project != null) project.OnAlignmentChanged += onAlignmentChanged;
            dirty = true;
        }

        /// <summary>Why bars (and the near-limits list) can't be shown now, or null when they can.</summary>
        public string Unavailable()
        {
            if (project == null || !project.HasProject) return L.T("Χωρίς έργο", "No project");
            string why = SensorLimits.Unavailable(project.TerrawebProjectId);
            if (why != null) return why;
            if (!project.IsAligned) return L.T("Πρώτα ευθυγράμμιση", "Align first");
            return null;
        }

        private void OnDestroy()
        {
            if (markers != null)
            {
                markers.OnMarkerCreated -= onMarkerChanged;
                markers.OnMarkerRemoved -= onMarkerChanged;
                markers.OnMarkersCleared -= onMarkersCleared;
            }
            if (project != null) project.OnAlignmentChanged -= onAlignmentChanged;
            foreach (Bar b in items) DestroyBar(b);
            items.Clear();
            byId.Clear();
        }

        // ------------------------------------------------------------------
        //  Per frame
        // ------------------------------------------------------------------

        private void LateUpdate()
        {
            if (!Enabled)
            {
                if (anyShown) HideAll();
                return;
            }
            if (markers == null || project == null) return;
            if (SensorLimits.Version != lastVersion)
            {
                lastVersion = SensorLimits.Version;
                dirty = true;
            }
            if (dirty) Rebuild();

            Camera cam = Camera.main;
            if (cam == null || items.Count == 0) return;
            if (!markers.MarkersVisible || Unavailable() != null)
            {
                if (anyShown) HideAll();
                return;
            }

            Transform camT = cam.transform;
            Vector3 camPos = camT.position, camUp = camT.up;
            bool shown = false;
            for (int i = 0; i < items.Count; i++)
            {
                Bar b = items[i];
                Marker m = b.Marker;
                // Pooled markers are reused under another id once removed.
                if (!b.HasData || m == null || m.MarkerId != b.MarkerId || !m.gameObject.activeInHierarchy)
                {
                    SetOn(b, false);
                    continue;
                }
                Vector3 pos = m.transform.position;
                Vector3 toMarker = pos - camPos;
                float dist = toMarker.magnitude;
                if (dist > BarDistance || dist < 1e-4f)
                {
                    SetOn(b, false);
                    continue;
                }
                // LiveAlerts changed the server's tier: recolour this bar only.
                if (!ReferenceEquals(b.Point.alertState, b.BuiltState)) Fill(b);
                SetOn(b, true);
                SetLabel(b, dist < LabelDistance);
                shown = true;

                float baseScale = m.Data != null && m.Data.scale > 0f ? m.Data.scale : IconMinSize;
                float size = Mathf.Max(baseScale, dist * IconAngularSize);
                b.Root.SetPositionAndRotation(pos, Quaternion.LookRotation(toMarker, camUp));
                b.Root.localScale = new Vector3(size, size, size);
            }
            anyShown = shown;
        }

        private void HideAll()
        {
            for (int i = 0; i < items.Count; i++) SetOn(items[i], false);
            anyShown = false;
        }

        private static void SetOn(Bar b, bool on)
        {
            if (b.On == on || b.Root == null) return;
            b.On = on;
            b.Root.gameObject.SetActive(on);
        }

        private static void SetLabel(Bar b, bool on)
        {
            if (b.LabelOn == on) return;
            b.LabelOn = on;
            b.Label.gameObject.SetActive(on);
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        private void Rebuild()
        {
            dirty = false;
            stale.Clear();
            foreach (Bar b in items) b.HasData = false;

            if (project.HasProject && SensorLimits.ProjectId == project.TerrawebProjectId)
            {
                foreach (KeyValuePair<string, Marker> kv in markers.ActiveMarkers)
                {
                    Marker m = kv.Value;
                    if (m == null || kv.Key == null || !kv.Key.StartsWith(ProjectAlignment.MarkerPrefix)) continue;
                    if (!project.TryGetPoint(kv.Key, out ProjectPoint point)) continue;
                    // No limit, or no reading to judge: no bar (never an empty one).
                    if (!SensorLimits.TryGet(point.sensorId, out SensorLimits.Entry entry) || entry.Result?.U == null) continue;

                    if (!byId.TryGetValue(kv.Key, out Bar b))
                    {
                        b = CreateBar(kv.Key);
                        byId.Add(kv.Key, b);
                        items.Add(b);
                    }
                    b.Marker = m;
                    b.Point = point;
                    b.Entry = entry;
                    b.HasData = true;
                    Fill(b);
                }
            }

            foreach (Bar b in items)
                if (!b.HasData) stale.Add(b.MarkerId);
            foreach (string id in stale)
            {
                Bar b = byId[id];
                byId.Remove(id);
                items.Remove(b);
                DestroyBar(b);
            }
            stale.Clear();
        }

        private static void DestroyBar(Bar b)
        {
            if (b.Mesh != null) Destroy(b.Mesh);
            if (b.Root != null) Destroy(b.Root.gameObject);
        }

        // Geometry, colour and text of one bar, from its entry and the server's current tier.
        private void Fill(Bar b)
        {
            b.BuiltState = b.Point.alertState;
            LimitMath.FieldResult f = b.Entry.Result.Driving;
            double u = f.U.Value;
            Color fill = TierColor(LimitMath.Rank(b.Point.alertState));

            verts.Clear();
            colors.Clear();
            tris.Clear();
            float left = Right - BarWidth, top = Bottom + BarHeight;
            // Rule 3 of §2.3: colour never touches the world directly — a dark rim around it all.
            Quad(left - Outline, Bottom - Outline, Right + Outline, top + Outline, Brand.Canvas);
            Quad(left, Bottom, Right, top, Brand.SurfaceSunken);
            float fillTop = Y(u);
            if (fillTop > Bottom) Quad(left, Bottom, Right, fillTop, fill);
            for (int t = LimitMath.Reference; t <= LimitMath.Alarm; t++)
            {
                if (!(f.Ticks[t] is double pos) || pos <= 0 || pos > MaxU) continue;
                float y = Y(pos);
                Quad(left - TickOverhang - Outline, y - TickHeight - Outline, Right + TickOverhang + Outline, y + TickHeight + Outline, Brand.Canvas);
                Quad(left - TickOverhang, y - TickHeight, Right + TickOverhang, y + TickHeight, Brand.Fg);
            }
            if (u > MaxU) // past the scale: a spike on top
            {
                int i = verts.Count;
                verts.Add(new Vector3(left, top + Outline, 0f));
                verts.Add(new Vector3(Right, top + Outline, 0f));
                verts.Add(new Vector3((left + Right) * 0.5f, top + Outline + SpikeHeight, 0f));
                Color32 c = fill;
                colors.Add(c); colors.Add(c); colors.Add(c);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
            }
            b.Mesh.Clear();
            b.Mesh.SetVertices(verts);
            b.Mesh.SetColors(colors);
            b.Mesh.SetTriangles(tris, 0);
            b.Mesh.RecalculateBounds();

            b.Label.text = $"{Percent(u)} · {f.Field}";
            bool disagrees = SensorLimits.Disagrees(b.Entry, b.Point.alertState);
            if (disagrees && b.Question == null) b.Question = MakeText(b.Root, "Question", "?", 12f,
                new Vector3((left + Right) * 0.5f, top + SpikeHeight + 0.22f, 0f), new Vector2(0.5f, 0.5f), TextAlignmentOptions.Center);
            if (b.Question != null)
            {
                b.Question.color = Brand.Warning;
                b.Question.gameObject.SetActive(disagrees);
            }
        }

        private static float Y(double u) => Bottom + BarHeight * (float)(Math.Min(u, MaxU) / MaxU);

        private void Quad(float x0, float y0, float x1, float y1, Color color)
        {
            int i = verts.Count;
            verts.Add(new Vector3(x0, y0, 0f));
            verts.Add(new Vector3(x1, y0, 0f));
            verts.Add(new Vector3(x1, y1, 0f));
            verts.Add(new Vector3(x0, y1, 0f));
            Color32 c = color;
            colors.Add(c); colors.Add(c); colors.Add(c); colors.Add(c);
            // Drawn in list order (no depth write): later quads sit on top of earlier ones.
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
            tris.Add(i); tris.Add(i + 3); tris.Add(i + 2);
        }

        private Bar CreateBar(string markerId)
        {
            var root = new GameObject("LimitBar_" + markerId).transform;
            root.SetParent(transform, false);
            var mesh = new Mesh { name = "LimitBar" };
            mesh.MarkDynamic();
            root.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = root.gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = BarMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            mr.sortingOrder = 7;

            // "72% · dz" to the left of the bar, right-aligned so it never runs into the icon.
            TextMeshPro label = MakeText(root, "Label", "", 10f,
                new Vector3(Right - BarWidth - TickOverhang - 0.06f, 0f, 0f), new Vector2(1f, 0.5f), TextAlignmentOptions.MidlineRight);
            label.gameObject.SetActive(false);

            // Born hidden: the per-frame pass shows it once it is placed.
            root.gameObject.SetActive(false);
            return new Bar { MarkerId = markerId, Root = root, Mesh = mesh, Label = label };
        }

        private static TextMeshPro MakeText(Transform root, string name, string text, float size, Vector3 at,
            Vector2 pivot, TextAlignmentOptions align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.localPosition = at;
            go.transform.localScale = Vector3.one * LabelScale;
            var t = go.AddComponent<TextMeshPro>();
            Brand.Style(t, true);
            t.fontSize = size;
            t.alignment = align;
            t.rectTransform.pivot = pivot;
            t.rectTransform.sizeDelta = new Vector2(16f, 2f);
            t.color = Color.white;
            t.sortingOrder = 8;
            t.text = text;
            // The marker labels' shared outlined material (a per-label outline clones one each).
            Material outlined = Brand.LabelMaterial;
            if (outlined != null) t.fontSharedMaterial = outlined;
            return t;
        }

        // ------------------------------------------------------------------
        //  Near limits
        // ------------------------------------------------------------------

        /// <summary>Sensors the near-limits list would show (with a measurable U).</summary>
        public int MeasuredCount =>
            project != null && project.HasProject && SensorLimits.ProjectId == project.TerrawebProjectId ? SensorLimits.MeasuredCount : 0;

        /// <summary>
        /// The 15 highest-U sensors, "P14 · dz · 96% · alert"; picking one navigates to it.
        /// The tier named is the server's.
        /// </summary>
        public void ShowNearLimits(FlowPanel panel, SensorNavigator navigator)
        {
            if (panel == null || project == null || !project.HasProject) return;
            SensorLimits.Ranked(ranked, NearLimitsCount);
            var points = new Dictionary<string, ProjectPoint>();
            foreach (ProjectPoint p in project.Points)
                if (p != null && !string.IsNullOrEmpty(p.sensorId)) points[p.sensorId] = p;

            var list = new List<FlowItem>();
            foreach (SensorLimits.Entry e in ranked)
            {
                if (!points.TryGetValue(e.SensorId, out ProjectPoint p)) continue;
                string markerId = ProjectAlignment.MarkerPrefix + p.id;
                if (markers == null || !markers.ActiveMarkers.ContainsKey(markerId)) continue;
                LimitMath.FieldResult f = e.Result.Driving;
                int server = LimitMath.Rank(p.alertState);
                string name = string.IsNullOrEmpty(p.label) ? p.id : p.label;
                string label = $"{name} · {f.Field} · {Percent(f.U.Value)}";
                if (server > LimitMath.None) label += " · " + LimitMath.TierNames[server];
                if (SensorLimits.Disagrees(e, p.alertState)) label += " · ?";
                list.Add(new FlowItem
                {
                    Label = label,
                    Sub = NextText(f, SensorLimits.Unit(e.TypeId, f.Field)),
                    Icon = string.IsNullOrEmpty(p.icon) ? "sensor-point" : p.icon,
                    Color = TierColor(server),
                    Tag = (markerId, p)
                });
            }
            panel.ShowList(L.T("ΟΡΙΑ", "LIMITS"), L.T("Κοντά στα όρια", "Near limits"), list, item =>
            {
                // Navigating by an unaligned project points at the wrong place.
                if (!project.IsAligned || navigator == null) { Feedback.Play(Feedback.Sound.Warn); return; }
                var (markerId, point) = ((string, ProjectPoint))item.Tag;
                panel.Close();
                navigator.Navigate(markerId, point);
            }, panel.Close);
        }

        // ------------------------------------------------------------------
        //  Shared formatting (the sensor card uses these too)
        // ------------------------------------------------------------------

        /// <summary>The server tier's colour: neutral below reference, then amber / Warning / Danger.</summary>
        public static Color TierColor(int rank)
        {
            switch (rank)
            {
                case LimitMath.Alarm: return Brand.Danger;
                case LimitMath.Alert: return Brand.Warning;
                case LimitMath.Reference: return ReferenceColor;
                default: return Neutral;
            }
        }

        public static string Percent(double u) => Math.Round(u * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        /// <summary>A threshold or margin, in the field's own unit: 15.0, 0.8, 0.0012.</summary>
        public static string FormatValue(double v)
        {
            double a = Math.Abs(v);
            return a >= 100 ? v.ToString("F0", CultureInfo.InvariantCulture)
                : a >= 1 ? v.ToString("F1", CultureInfo.InvariantCulture)
                : a == 0 ? "0"
                : v.ToString("G2", CultureInfo.InvariantCulture);
        }

        /// <summary>"next: alert in 0.8 mm"; tier names stay English in both languages.</summary>
        public static string NextText(LimitMath.FieldResult f, string unit)
        {
            if (f == null) return "";
            if (f.Next == LimitMath.None)
                return f.Reached == LimitMath.None ? "" : L.T("πέρα από το ανώτερο όριο", "past the top limit");
            string tier = LimitMath.TierNames[f.Next];
            string margin = FormatValue(f.Margin) + (string.IsNullOrEmpty(unit) ? "" : " " + unit);
            return L.En ? $"next: {tier} in {margin}" : $"επόμενο: {tier} σε {margin}";
        }
    }
}
