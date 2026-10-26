using System;
using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Markers
{
    /// <summary>
    /// History ribbons: next to every rendered project sensor that has survey history floats a
    /// small 3D sparkline of its displacement magnitude over the last 30 days (or the newest
    /// ten readings when the month holds fewer than two). The ribbon rides the same rules as
    /// the icon it belongs to — turned to the viewer, constant on-screen size near and far —
    /// so a glance across the site shows which sensors are moving, not just which exist.
    ///
    /// Geometry is rebuilt only when the marker set, the alignment or the history store
    /// changes (dirty flag, applied on the next LateUpdate); the per-frame pass only
    /// positions, scales and culls the ribbons that already exist, with no allocations.
    /// </summary>
    public class HistoryRibbons : MonoBehaviour
    {
        private const string PrefKey = "ui.ribbons";
        private const int WindowDays = 30;
        private const int FallbackSamples = 10;
        private const int MaxPoints = 40;
        /// <summary>Metres beyond which ribbons are hidden (icons are already specks there).</summary>
        private const float HideDistance = 80f;
        // Marker's icon rule, repeated here so the ribbon sizes exactly like the icon it follows.
        private const float IconAngularSize = 0.03f, IconMinSize = 0.06f;

        // Layout in icon-size units (the root is scaled by the icon size each frame; the icon's
        // own halo spans 1.0 = its diameter). The ribbon's left edge sits 1.4 radii right of
        // the icon's centre; the chart is 2.2 wide × 1.0 high, plotted inside a small margin so
        // the line width and the end dot stay in the box.
        private const float Left = 0.7f, Width = 2.2f, Height = 1f;
        private const float PlotMargin = 0.08f;
        private const float LineWidth = 0.03f;          // ~3 % of the ribbon height
        private const float DotSize = 0.1f;
        private const float LabelScale = 0.2f;           // TMP size 10 ≈ 1 unit tall → 0.2 icon sizes
        private const float LabelTop = -Height * 0.5f - 0.06f;

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

        // One material for every ribbon line: colour rides in start/endColor, so the lines batch.
        private static Material lineMaterial;
        private static Material LineMaterial =>
            lineMaterial != null ? lineMaterial : (lineMaterial = new Material(Shader.Find("Sprites/Default")) { name = "HistoryRibbon" });

        private class Ribbon
        {
            public string MarkerId;
            public Marker Marker;
            public Transform Root;
            public LineRenderer Line, Baseline;
            public Transform Dot;
            public SpriteRenderer DotSprite;
            public TextMeshPro Label;
            public bool HasData;
            // Current SetActive state, so it is only toggled on change.
            public bool On;
        }

        private ProjectAlignment project;
        private MarkerManager markers;
        private readonly Dictionary<string, Ribbon> byId = new Dictionary<string, Ribbon>();
        private readonly List<Ribbon> items = new List<Ribbon>();
        // Ribbons that no longer qualify, collected during a rebuild (no per-rebuild list alloc).
        private readonly List<string> stale = new List<string>();
        // Scratch for one ribbon's samples and plotted points; sized once.
        private readonly List<int> windowIndices = new List<int>(256);
        private NativeArray<Vector3> pointBuf;
        private bool dirty = true;
        private bool anyShown;

        // Change detection for the history store: a project reloaded on the headset refreshes
        // SensorHistory after the markers already exist, so the marker events never fire.
        private int lastHistoryCount = -1, lastHistoryProject = -1;
        private DateTime lastHistoryLast = DateTime.MinValue;

        private Action<Marker> onMarkerChanged;
        private Action onMarkersCleared, onAlignmentChanged;

        /// <summary>Ribbons currently built (0 while the feature is off).</summary>
        public int RibbonCount => Enabled ? items.Count : 0;

        public void Init(ProjectAlignment alignment, MarkerManager markerManager)
        {
            project = alignment;
            markers = markerManager;
            if (!pointBuf.IsCreated)
                pointBuf = new NativeArray<Vector3>(MaxPoints, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            onMarkerChanged = _ => dirty = true;
            onMarkersCleared = () => dirty = true;
            onAlignmentChanged = () => dirty = true;
            if (markers != null)
            {
                markers.OnMarkerCreated += onMarkerChanged;
                markers.OnMarkerUpdated += onMarkerChanged;
                markers.OnMarkerRemoved += onMarkerChanged;
                markers.OnMarkersCleared += onMarkersCleared;
            }
            if (project != null) project.OnAlignmentChanged += onAlignmentChanged;
            dirty = true;
        }

        /// <summary>Why ribbons can't be shown right now, or null when they can.</summary>
        public string Unavailable()
        {
            if (project == null || !project.HasProject) return "Χωρίς έργο";
            if (!SensorHistory.HasDisplacements || SensorHistory.ProjectId != project.TerrawebProjectId)
                return "Χωρίς ιστορικό μετρήσεων";
            if (!project.IsAligned) return "Πρώτα ευθυγράμμιση";
            return null;
        }

        private void OnDestroy()
        {
            if (markers != null)
            {
                markers.OnMarkerCreated -= onMarkerChanged;
                markers.OnMarkerUpdated -= onMarkerChanged;
                markers.OnMarkerRemoved -= onMarkerChanged;
                markers.OnMarkersCleared -= onMarkersCleared;
            }
            if (project != null) project.OnAlignmentChanged -= onAlignmentChanged;
            foreach (Ribbon r in items)
                if (r.Root != null) Destroy(r.Root.gameObject);
            items.Clear();
            byId.Clear();
            if (pointBuf.IsCreated) pointBuf.Dispose();
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

            // The history store has no change event; its (project, count, newest) triple is
            // enough to notice a reload.
            if (SensorHistory.Count != lastHistoryCount || SensorHistory.ProjectId != lastHistoryProject
                || SensorHistory.Last != lastHistoryLast)
            {
                lastHistoryCount = SensorHistory.Count;
                lastHistoryProject = SensorHistory.ProjectId;
                lastHistoryLast = SensorHistory.Last;
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
                Ribbon r = items[i];
                Marker m = r.Marker;
                // Pooled markers are reused under another id once removed.
                if (!r.HasData || m == null || m.MarkerId != r.MarkerId || !m.gameObject.activeInHierarchy)
                {
                    SetOn(r, false);
                    continue;
                }
                Vector3 pos = m.transform.position;
                Vector3 toMarker = pos - camPos;
                float dist = toMarker.magnitude;
                if (dist > HideDistance || dist < 1e-4f)
                {
                    SetOn(r, false);
                    continue;
                }
                SetOn(r, true);
                shown = true;

                // Same orientation as the icon's billboard (+Z away from the viewer) and the
                // same constant-angle size, so ribbon and icon move as one piece.
                float baseScale = m.Data != null && m.Data.scale > 0f ? m.Data.scale : IconMinSize;
                float size = Mathf.Max(baseScale, dist * IconAngularSize);
                r.Root.SetPositionAndRotation(pos, Quaternion.LookRotation(toMarker, camUp));
                r.Root.localScale = new Vector3(size, size, size);
            }
            anyShown = shown;
        }

        private void HideAll()
        {
            for (int i = 0; i < items.Count; i++) SetOn(items[i], false);
            anyShown = false;
        }

        private static void SetOn(Ribbon r, bool on)
        {
            if (r.On == on || r.Root == null) return;
            r.On = on;
            r.Root.gameObject.SetActive(on);
        }

        // ------------------------------------------------------------------
        //  Build
        // ------------------------------------------------------------------

        // One ribbon per project marker whose sensor has at least two displacement readings in
        // the window; ribbons of markers that vanished or lost their history are destroyed.
        private void Rebuild()
        {
            dirty = false;
            stale.Clear();
            foreach (Ribbon r in items) r.HasData = false;

            if (markers != null && project != null && project.HasProject)
            {
                foreach (KeyValuePair<string, Marker> kv in markers.ActiveMarkers)
                {
                    Marker m = kv.Value;
                    if (m == null || kv.Key == null || !kv.Key.StartsWith(ProjectAlignment.MarkerPrefix)) continue;
                    if (!project.TryGetPoint(kv.Key, out ProjectPoint point)) continue;
                    IReadOnlyList<SensorHistory.Sample> series = SensorHistory.Of(point.sensorId);
                    if (series == null || !SelectWindow(series, out bool fallback)) continue;

                    if (!byId.TryGetValue(kv.Key, out Ribbon r))
                    {
                        r = CreateRibbon(kv.Key);
                        byId.Add(kv.Key, r);
                        items.Add(r);
                    }
                    r.Marker = m;
                    r.HasData = true;
                    Plot(r, series, fallback, point.alertState);
                }
            }

            foreach (Ribbon r in items)
                if (!r.HasData) stale.Add(r.MarkerId);
            foreach (string id in stale)
            {
                Ribbon r = byId[id];
                byId.Remove(id);
                items.Remove(r);
                if (r.Root != null) Destroy(r.Root.gameObject);
            }
            stale.Clear();
        }

        // Indices (into the series) of the samples to plot, oldest first, in windowIndices:
        // the last 30 days of readings with a displacement, or the newest ten when the month
        // holds fewer than two. False when even that gives fewer than two.
        private bool SelectWindow(IReadOnlyList<SensorHistory.Sample> series, out bool fallback)
        {
            windowIndices.Clear();
            fallback = false;
            DateTime from = SensorHistory.Last - TimeSpan.FromDays(WindowDays);
            for (int i = 0; i < series.Count; i++)
            {
                SensorHistory.Sample s = series[i];
                if (s.HasDisplacement && s.Date >= from) windowIndices.Add(i);
            }
            if (windowIndices.Count >= 2) return true;

            fallback = true;
            windowIndices.Clear();
            for (int i = series.Count - 1; i >= 0 && windowIndices.Count < FallbackSamples; i--)
                if (series[i].HasDisplacement) windowIndices.Add(i);
            windowIndices.Reverse();
            return windowIndices.Count >= 2;
        }

        private void Plot(Ribbon r, IReadOnlyList<SensorHistory.Sample> series, bool fallback, string alertState)
        {
            int count = windowIndices.Count;
            // At most MaxPoints vertices: evenly thinned, always keeping the newest reading.
            int n = Mathf.Min(count, MaxPoints);
            double min = double.MaxValue, max = double.MinValue;
            float lastMm = 0f;
            for (int k = 0; k < n; k++)
            {
                int idx = windowIndices[k == n - 1 ? count - 1 : (int)((long)k * count / n)];
                double mm = Magnitude(series[idx]);
                if (mm < min) min = mm;
                if (mm > max) max = mm;
                // Stash the value in x for the second pass; y is filled once min/max are known.
                pointBuf[k] = new Vector3((float)mm, 0f, 0f);
                lastMm = (float)mm;
            }
            if (max - min < 1e-9) { max += 1; min -= 1; }   // flat series: a line in the middle

            float plotH = Height - 2f * PlotMargin, plotW = Width - 2f * PlotMargin;
            float x0 = Left + PlotMargin, y0 = -Height * 0.5f + PlotMargin;
            for (int k = 0; k < n; k++)
            {
                float x = x0 + plotW * (n > 1 ? k / (float)(n - 1) : 1f);
                float y = y0 + plotH * (float)((pointBuf[k].x - min) / (max - min));
                pointBuf[k] = new Vector3(x, y, 0f);
            }
            if (r.Line.positionCount != n) r.Line.positionCount = n;
            r.Line.SetPositions(pointBuf.GetSubArray(0, n));

            Color color = alertState == "alarm" ? Brand.Danger
                        : alertState == "alert" ? Brand.Warning
                        : Brand.Primary;
            r.Line.startColor = color.WithAlpha(0.45f);   // older readings fade back
            r.Line.endColor = color;
            r.DotSprite.color = color;
            // Just in front of the line (the root's +Z faces away from the viewer).
            Vector3 end = pointBuf[n - 1];
            r.Dot.localPosition = new Vector3(end.x, end.y, -0.01f);

            r.Label.text = fallback
                ? $"{count} μετρ. · {lastMm:F1} mm"
                : $"{WindowDays} ημ · {lastMm:F1} mm";
        }

        private static double Magnitude(in SensorHistory.Sample s)
        {
            double dx = s.Dx ?? 0, dy = s.Dy ?? 0, dz = s.Dz ?? 0;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private Ribbon CreateRibbon(string markerId)
        {
            var root = new GameObject("Ribbon_" + markerId).transform;
            root.SetParent(transform, false);

            LineRenderer Line(string name, int order, float width)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = LineMaterial;
                lr.useWorldSpace = false;
                lr.positionCount = 2;
                lr.startWidth = lr.endWidth = width;
                lr.numCornerVertices = 2;
                lr.numCapVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.sortingOrder = order;
                return lr;
            }

            // Faint floor of the chart box: gives the line something to be read against.
            LineRenderer baseline = Line("Baseline", 6, LineWidth * 0.5f);
            baseline.SetPosition(0, new Vector3(Left, -Height * 0.5f, 0.01f));
            baseline.SetPosition(1, new Vector3(Left + Width, -Height * 0.5f, 0.01f));
            baseline.startColor = baseline.endColor = Brand.FgSubtle.WithAlpha(0.35f);

            LineRenderer line = Line("Line", 7, LineWidth);

            // Newest reading: a dot on the end of the line, as on the card's chart.
            var dotGo = new GameObject("End");
            dotGo.transform.SetParent(root, false);
            Sprite disc = Brand.DiscSprite;
            float spriteWidth = disc != null && disc.bounds.size.x > 0f ? disc.bounds.size.x : 1f;
            dotGo.transform.localScale = Vector3.one * (DotSize / spriteWidth);
            var dot = dotGo.AddComponent<SpriteRenderer>();
            dot.sprite = disc;
            dot.sharedMaterial = Brand.MarkerSpriteFadeMaterial;
            dot.sortingOrder = 8;
            dot.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dot.receiveShadows = false;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(root, false);
            labelGo.transform.localPosition = new Vector3(Left + Width * 0.5f, LabelTop, 0f);
            labelGo.transform.localScale = Vector3.one * LabelScale;
            var label = labelGo.AddComponent<TextMeshPro>();
            Brand.Style(label, true);
            label.fontSize = 10f;
            label.alignment = TextAlignmentOptions.Top;
            label.rectTransform.pivot = new Vector2(0.5f, 1f);
            label.rectTransform.sizeDelta = new Vector2(Width / LabelScale, 2f);
            label.color = Color.white;
            label.sortingOrder = 8;
            // The marker labels' shared outlined material: per-label outline settings would
            // clone a material per ribbon.
            Material outlined = Brand.LabelMaterial;
            if (outlined != null) label.fontSharedMaterial = outlined;

            // Born hidden: the per-frame pass shows it once it is placed.
            root.gameObject.SetActive(false);
            return new Ribbon
            {
                MarkerId = markerId, Root = root, Line = line, Baseline = baseline,
                Dot = dotGo.transform, DotSprite = dot, Label = label
            };
        }
    }
}
