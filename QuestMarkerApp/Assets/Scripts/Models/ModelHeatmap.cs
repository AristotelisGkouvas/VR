using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Models
{
    /// <summary>
    /// Displacement heatmap over the model, from the sensors' survey history, interpolated
    /// linearly between them and laid over the texture. Three measures (menu): the total
    /// movement (green → amber → red at RedAtMm, like the arrows), the vertical one dz (blue
    /// down, red up, a diverging scale either side of 0) and the rate over the last 30 days
    /// (red at RedAtRate mm/day). A legend under the view says which and what the colours
    /// mean; aiming the ray at the model reads the interpolated value at that spot.
    ///
    /// Linear: the sensors are joined into triangles in plan (Delaunay) and a point inside one
    /// takes the barycentric mix of its three corners; outside every triangle it takes the
    /// value along the nearest edge. Triangles longer than two reaches are dropped, so a gap
    /// with no sensors is not painted across. The colour fades out beyond the reach from the
    /// nearest sensor: 1.5 × the typical spacing between neighbours (5–30 m), scaled by the
    /// menu's narrow / wide setting.
    ///
    /// Plan view: a 128 × 128 grid over the model's footprint (east/north), computed here only
    /// when the data or the date changes, sampled by ModelTextured from the vertex's model-local
    /// x/z. With the time machine open it follows its date (and its compare-from date).
    /// Menu switch on the Model page, off by default.
    /// </summary>
    public class ModelHeatmap : MonoBehaviour
    {
        private const string EnabledKey = "model.heatmap";
        private const int Grid = 128;
        /// <summary>Movement drawn fully red, mm (the arrows' scale); ± that for the vertical.</summary>
        public const float RedAtMm = 20f;
        /// <summary>Rate drawn fully red, mm per day.</summary>
        public const float RedAtRate = 0.5f;
        private const int RateDays = 30;

        public enum Measure { Total = 0, Vertical = 1, Rate = 2 }
        private const string MeasureKey = "model.heatMeasure";
        private static readonly string[] MeasureNamesEl = { "Συνολική", "Κατακόρυφη (dz)", "Ρυθμός" };
        private static readonly string[] MeasureNamesEn = { "Total", "Vertical (dz)", "Rate" };

        public static Measure Shown
        {
            get => (Measure)Mathf.Clamp(PlayerPrefs.GetInt(MeasureKey, 0), 0, 2);
            set { PlayerPrefs.SetInt(MeasureKey, (int)value); PlayerPrefs.Save(); }
        }

        public static string MeasureName => (L.En ? MeasureNamesEn : MeasureNamesEl)[(int)Shown];
        /// <summary>Colour over the texture where a sensor is near.</summary>
        private const float Opacity = 0.6f;
        private const float RecomputeSeconds = 0.25f;
        private const string ReachKey = "model.heatReach";
        private static readonly float[] ReachFactors = { 1f, 0.6f, 1.6f };
        private static readonly string[] ReachNamesEl = { "Αυτόματη", "Στενή", "Ευρεία" };
        private static readonly string[] ReachNamesEn = { "Automatic", "Narrow", "Wide" };

        /// <summary>0 automatic, 1 narrow (×0.6), 2 wide (×1.6).</summary>
        public static int ReachSetting
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(ReachKey, 0), 0, 2);
            set { PlayerPrefs.SetInt(ReachKey, Mathf.Clamp(value, 0, 2)); PlayerPrefs.Save(); }
        }

        public static string ReachName => (L.En ? ReachNamesEn : ReachNamesEl)[ReachSetting];

        /// <summary>The reach in use after the last computation, metres (for the menu).</summary>
        public float Reach { get; private set; }

        private static readonly int HeatTexId = Shader.PropertyToID("_ModelHeatTex");
        private static readonly int HeatRectId = Shader.PropertyToID("_ModelHeatRect");
        private static readonly int HeatOpacityId = Shader.PropertyToID("_ModelHeatOpacity");

        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(EnabledKey, 0) == 1;
            set { PlayerPrefs.SetInt(EnabledKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>One line for the menu: how many sensors feed it, or why it is empty.</summary>
        public string Status { get; private set; } = "";

        /// <summary>Sensors in the last computation.</summary>
        public int Count { get; private set; }

        private ModelOverlay overlay;
        private ModelSensors sensors;
        private Texture2D tex;
        private Color32[] pixels;
        private readonly List<(Vector2 at, float mm)> samples = new List<(Vector2, float)>();
        private readonly System.Text.StringBuilder sampleIds = new System.Text.StringBuilder();
        // Per pixel, the interpolation (up to 3 sensors and weights, and the fade): it depends
        // on where the sensors are, not on their values, so a new date only recombines them.
        private string geometryKey;
        private int[] wIndex;
        private float[] wWeight, wAlpha;
        private string key;
        private float nextCompute;
        private bool shown;
        // The last interpolation's inputs, for the value under the pointer.
        private Vector2[] lastPts = Array.Empty<Vector2>();
        private float[] lastVals = Array.Empty<float>();
        private List<int> lastTris = new List<int>();
        private List<(int, int)> lastEdges = new List<(int, int)>();
        private float lastReach;
        private Measure computed;

        private RectTransform legend, probe;
        private TextMeshProUGUI legendTitle, legendLow, legendMid, legendHigh, probeText;
        private Image legendBar;
        private Texture2D legendTex;
        private Measure legendShown = (Measure)(-1);
        private bool legendGliding;
        private int probeKey = int.MinValue;
        private Sprite legendSprite;
#if !MOBILE_PREVIEW
        private OVRCameraRig rig;
#endif

        public void Init(ModelOverlay modelOverlay, ModelSensors modelSensors)
        {
            overlay = modelOverlay;
            sensors = modelSensors;
            Shader.SetGlobalFloat(HeatOpacityId, 0f);
        }

        private void Update()
        {
            bool on = Enabled && overlay != null && sensors != null && overlay.IsLoaded
                      && overlay.Root != null && overlay.Root.gameObject.activeInHierarchy;
            if (!on)
            {
                if (shown) { Shader.SetGlobalFloat(HeatOpacityId, 0f); shown = false; }
                Status = Enabled ? L.T("Χωρίς μοντέλο", "No model") : L.T("Όχι", "Off");
                ShowUi(false);
                key = null; // shown again: computed again (the texture is zeroed out of view)
                return;
            }
            ShowUi(shown && !UiState.AnyModal);
            if (Time.unscaledTime < nextCompute) return;
            nextCompute = Time.unscaledTime + RecomputeSeconds;

            DateTime at = DisplacementView.IsActive && DisplacementView.ShownAt.HasValue ? DisplacementView.ShownAt.Value : DateTime.MaxValue;
            DateTime? from = DisplacementView.IsActive ? DisplacementView.ComparedFrom : null;
            string k = $"{sensors.Version}|{SensorHistory.Count}|{SensorHistory.Last.Ticks}|{at.Ticks}|{from?.Ticks}|{overlay.LoadedName}|{ReachSetting}|{(int)Shown}";
            if (k == key) return;
            key = k;
            Compute(at, from);
        }

        // Each sensor on the model with a displacement at the date: its plan position and the
        // chosen measure (mm, signed mm for dz, mm/day for the rate).
        private void Collect(DateTime at, DateTime? from, Measure measure)
        {
            samples.Clear();
            sampleIds.Clear();
            foreach ((ProjectPoint p, Vector3 local) in sensors.DotPoints)
            {
                if (!SensorHistory.TryDisplacementAt(p.sensorId, at, out SensorHistory.Sample s)) continue;
                double toMm = SensorHistory.MetresPerUnit(p.sensorId) * 1000.0;
                double value;
                if (measure == Measure.Rate)
                {
                    // Change of the total movement over the last RateDays before the reading.
                    DateTime back = s.Date.AddDays(-RateDays);
                    if (!SensorHistory.TryDisplacementAt(p.sensorId, back, out SensorHistory.Sample o)) continue;
                    double days = (s.Date - o.Date).TotalDays;
                    if (days < RateDays * 0.5) continue; // too short a span to call a rate
                    double ddx = (s.Dx ?? 0) - (o.Dx ?? 0), ddy = (s.Dy ?? 0) - (o.Dy ?? 0), ddz = (s.Dz ?? 0) - (o.Dz ?? 0);
                    value = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz) * toMm / days;
                }
                else
                {
                    double dx = s.Dx ?? 0, dy = s.Dy ?? 0, dz = s.Dz ?? 0;
                    if (from.HasValue && SensorHistory.TryDisplacementAt(p.sensorId, from.Value, out SensorHistory.Sample z))
                    {
                        dx -= z.Dx ?? 0;
                        dy -= z.Dy ?? 0;
                        dz -= z.Dz ?? 0;
                    }
                    value = measure == Measure.Vertical ? dz * toMm : Math.Sqrt(dx * dx + dy * dy + dz * dz) * toMm;
                }
                samples.Add((new Vector2(local.x, local.z), (float)value));
                sampleIds.Append(p.id).Append(',');
            }
        }

        private void Compute(DateTime at, DateTime? from)
        {
            Measure measure = Shown;
            Collect(at, from, measure);
            if (samples.Count == 0)
            {
                Shader.SetGlobalFloat(HeatOpacityId, 0f);
                shown = false;
                Status = L.T("Χωρίς μετακινήσεις στο μοντέλο", "No displacements on the model");
                Count = 0;
                return;
            }
            if (tex == null)
            {
                tex = new Texture2D(Grid, Grid, TextureFormat.RGBA32, false)
                {
                    name = "ModelHeatmap", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                };
                pixels = new Color32[Grid * Grid];
            }

            Bounds b = overlay.ModelBounds;
            float size = Mathf.Max(b.size.x, b.size.z, 1f);
            float minX = b.center.x - size * 0.5f, minZ = b.center.z - size * 0.5f;

            var vals = new float[samples.Count];
            for (int i = 0; i < samples.Count; i++) vals[i] = samples[i].mm;
            string geo = $"{sensors.Version}|{ReachSetting}|{overlay.LoadedName}|{sampleIds}";
            if (geo != geometryKey)
            {
                geometryKey = geo;
                BuildWeights(minX, minZ, size);
            }

            for (int k = 0; k < Grid * Grid; k++)
            {
                int j = k * 3;
                if (wIndex[j] < 0) { pixels[k] = default; continue; }
                float value = wWeight[j] * vals[wIndex[j]]
                              + (wIndex[j + 1] >= 0 ? wWeight[j + 1] * vals[wIndex[j + 1]] : 0f)
                              + (wIndex[j + 2] >= 0 ? wWeight[j + 2] * vals[wIndex[j + 2]] : 0f);
                Color c = Ramp(value, measure);
                pixels[k] = new Color(c.r, c.g, c.b, wAlpha[k]);
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);

            Shader.SetGlobalTexture(HeatTexId, tex);
            Shader.SetGlobalVector(HeatRectId, new Vector4(minX, minZ, 1f / size, 1f / size));
            Shader.SetGlobalFloat(HeatOpacityId, Opacity);
            shown = true;
            Count = samples.Count;
            lastVals = vals;
            computed = measure;
            Status = L.En ? $"On · {samples.Count} sensors" : $"Ναι · {samples.Count} αισθητήρες";
        }

        // Where the sensors are → per pixel which of them mix, with what weights, and the fade.
        private void BuildWeights(float minX, float minZ, float size)
        {
            int n = Grid * Grid;
            if (wIndex == null) { wIndex = new int[n * 3]; wWeight = new float[n * 3]; wAlpha = new float[n]; }
            float cell = size / Grid;
            // Colour reaches this far from the nearest sensor, fading over the last half.
            float reach = Mathf.Clamp(1.5f * TypicalSpacing(), 5f, 30f) * ReachFactors[ReachSetting];
            Reach = reach;
            float fadeFrom = reach * 0.5f;

            var pts = new Vector2[samples.Count];
            for (int i = 0; i < samples.Count; i++) pts[i] = samples[i].at;
            List<int> tris = Delaunay.Triangulate(pts);
            // Drop triangles that bridge a gap with no sensors, and zero-area slivers (collinear).
            float maxEdge = 2f * reach;
            var keep = new List<int>(tris.Count);
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                Vector2 a = pts[tris[t]], c1 = pts[tris[t + 1]], c2 = pts[tris[t + 2]];
                float e1 = (a - c1).magnitude, e2 = (c1 - c2).magnitude, e3 = (c2 - a).magnitude;
                if (e1 > maxEdge || e2 > maxEdge || e3 > maxEdge) continue;
                float longest = Mathf.Max(e1, Mathf.Max(e2, e3));
                float area2 = Mathf.Abs((c1.x - a.x) * (c2.y - a.y) - (c2.x - a.x) * (c1.y - a.y));
                if (area2 < 1e-3f * longest * longest) continue;
                keep.Add(tris[t]); keep.Add(tris[t + 1]); keep.Add(tris[t + 2]);
            }
            List<(int, int)> edges = Edges(keep, pts, maxEdge);
            lastPts = pts;
            lastTris = keep;
            lastEdges = edges;
            lastReach = reach;

            for (int gy = 0; gy < Grid; gy++)
            {
                float z = minZ + (gy + 0.5f) * cell;
                for (int gx = 0; gx < Grid; gx++)
                {
                    int k = gy * Grid + gx, j = k * 3;
                    var q = new Vector2(minX + (gx + 0.5f) * cell, z);
                    float nearest = float.MaxValue;
                    int nearestIndex = 0;
                    for (int i = 0; i < pts.Length; i++)
                    {
                        float d = (q - pts[i]).sqrMagnitude;
                        if (d < nearest) { nearest = d; nearestIndex = i; }
                    }
                    nearest = Mathf.Sqrt(nearest);
                    if (nearest > reach) { wIndex[j] = -1; wAlpha[k] = 0f; continue; }
                    Weights(q, pts, keep, edges, nearestIndex, out int i0, out int i1, out int i2, out float w0, out float w1, out float w2);
                    wIndex[j] = i0; wIndex[j + 1] = i1; wIndex[j + 2] = i2;
                    wWeight[j] = w0; wWeight[j + 1] = w1; wWeight[j + 2] = w2;
                    wAlpha[k] = nearest <= fadeFrom ? 1f : Mathf.Clamp01(1f - (nearest - fadeFrom) / (reach - fadeFrom));
                }
            }
        }

        // Median distance from each sensor to its nearest neighbour (plan).
        private float TypicalSpacing()
        {
            if (samples.Count < 2) return 10f;
            var d = new List<float>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < samples.Count; j++)
                    if (i != j) best = Mathf.Min(best, (samples[i].at - samples[j].at).sqrMagnitude);
                if (best > 1e-6f) d.Add(Mathf.Sqrt(best)); // two prisms at one spot say nothing of spacing
            }
            if (d.Count == 0) return 10f;
            d.Sort();
            return d[d.Count / 2];
        }

        // Unique edges of the kept triangles; with none (sensors in a line) each sensor's link
        // to its nearest neighbour, so a row of prisms still interpolates along itself.
        private static List<(int, int)> Edges(List<int> tris, Vector2[] pts, float maxEdge)
        {
            var set = new HashSet<(int, int)>();
            for (int t = 0; t + 2 < tris.Count; t += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = tris[t + e], c = tris[t + (e + 1) % 3];
                    set.Add(a < c ? (a, c) : (c, a));
                }
            if (set.Count == 0)
                for (int i = 0; i < pts.Length; i++)
                {
                    int best = -1;
                    float bd = float.MaxValue;
                    for (int j = 0; j < pts.Length; j++)
                    {
                        if (i == j) continue;
                        float d = (pts[i] - pts[j]).sqrMagnitude;
                        if (d < bd) { bd = d; best = j; }
                    }
                    if (best >= 0 && Mathf.Sqrt(bd) <= maxEdge) set.Add(i < best ? (i, best) : (best, i));
                }
            return new List<(int, int)>(set);
        }

        // Inside a triangle: barycentric. Outside: along the nearest edge (clamped); between
        // equally near edges the shorter one, so a row of sensors interpolates neighbour to
        // neighbour and never skips the one in the middle. No edge: the nearest sensor.
        private static void Weights(Vector2 q, Vector2[] pts, List<int> tris, List<(int, int)> edges, int nearest,
            out int i0, out int i1, out int i2, out float w0, out float w1, out float w2)
        {
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                Vector2 a = pts[tris[t]], b = pts[tris[t + 1]], c = pts[tris[t + 2]];
                float den = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                if (Mathf.Abs(den) < 1e-9f) continue;
                float u = ((b.y - c.y) * (q.x - c.x) + (c.x - b.x) * (q.y - c.y)) / den;
                float v = ((c.y - a.y) * (q.x - c.x) + (a.x - c.x) * (q.y - c.y)) / den;
                float w = 1f - u - v;
                if (u >= -1e-4f && v >= -1e-4f && w >= -1e-4f)
                {
                    i0 = tris[t]; i1 = tris[t + 1]; i2 = tris[t + 2];
                    w0 = u; w1 = v; w2 = w;
                    return;
                }
            }
            i0 = nearest; i1 = -1; i2 = -1; w0 = 1f; w1 = 0f; w2 = 0f;
            float bestD = float.MaxValue, bestLen = float.MaxValue;
            foreach ((int i, int j) in edges)
            {
                Vector2 a = pts[i], ab = pts[j] - a;
                float len2 = ab.sqrMagnitude;
                float s = len2 > 1e-9f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / len2) : 0f;
                float d = (q - (a + ab * s)).sqrMagnitude;
                bool closer = d < bestD - 1e-6f || (Mathf.Abs(d - bestD) <= 1e-6f && len2 < bestLen);
                if (!closer) continue;
                bestD = d;
                bestLen = len2;
                i0 = i; i1 = j; i2 = -1;
                w0 = 1f - s; w1 = s; w2 = 0f;
            }
        }

        private static float Linear(Vector2 q, Vector2[] pts, float[] vals, List<int> tris, List<(int, int)> edges, int nearest)
        {
            Weights(q, pts, tris, edges, nearest, out int i0, out int i1, out int i2, out float w0, out float w1, out float w2);
            return w0 * vals[i0] + (i1 >= 0 ? w1 * vals[i1] : 0f) + (i2 >= 0 ? w2 * vals[i2] : 0f);
        }

        /// <summary>
        /// Total and rate: green → amber at half the red mark → red, as the arrows. Vertical:
        /// diverging either side of 0, blue going down (settlement), red going up.
        /// </summary>
        public static Color Ramp(float value, Measure measure)
        {
            if (measure == Measure.Vertical)
            {
                float s = Mathf.Clamp(value / RedAtMm, -1f, 1f);
                return s < 0f ? Color.Lerp(Brand.Primary, Down, -s) : Color.Lerp(Brand.Primary, Brand.Danger, s);
            }
            float t = Mathf.Clamp01(value / (measure == Measure.Rate ? RedAtRate : RedAtMm));
            return t < 0.5f ? Color.Lerp(Brand.Primary, Brand.Warning, t * 2f)
                            : Color.Lerp(Brand.Warning, Brand.Danger, (t - 0.5f) * 2f);
        }

        /// <summary>Settlement blue of the vertical scale.</summary>
        private static readonly Color Down = new Color32(0x3d, 0x7f, 0xe0, 0xff);

        // ------------------------------------------------------------------
        //  Legend and the value under the pointer
        // ------------------------------------------------------------------

        private void ShowUi(bool on)
        {
            if (on && legend == null) BuildUi();
            if (legend == null) return;
            if (legend.gameObject.activeSelf != on) legend.gameObject.SetActive(on);
            if (!on)
            {
                if (probe.gameObject.activeSelf) probe.gameObject.SetActive(false);
                return;
            }
            Camera cam = Camera.main;
            if (cam == null) return;
            // Low on the left of the view (the time machine's bar takes the middle).
            Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 target = cam.transform.position + fwd * 0.8f + Vector3.down * 0.32f - right * 0.32f;
            if (!legendGliding && (legend.position - target).magnitude > 0.25f) legendGliding = true;
            if (legendGliding)
            {
                legend.position = Vector3.Lerp(legend.position, target, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
                if ((legend.position - target).sqrMagnitude < 1e-4f) legendGliding = false;
            }
            VrUi.FaceViewer(legend, cam.transform);
            if (legendShown != computed) FillLegend(computed);
            Probe(cam);
        }

        private void FillLegend(Measure m)
        {
            legendShown = m;
            for (int x = 0; x < legendTex.width; x++)
            {
                float u = x / (legendTex.width - 1f);
                float v = m == Measure.Vertical ? Mathf.Lerp(-RedAtMm, RedAtMm, u) : u * (m == Measure.Rate ? RedAtRate : RedAtMm);
                legendTex.SetPixel(x, 0, Ramp(v, m));
            }
            legendTex.Apply(false);
            switch (m)
            {
                case Measure.Vertical:
                    legendTitle.text = L.T("Κατακόρυφη μετακίνηση dz (mm)", "Vertical movement dz (mm)");
                    legendLow.text = L.T("−20 κάτω", "−20 down"); legendMid.text = "0"; legendHigh.text = L.T("+20 πάνω", "+20 up");
                    break;
                case Measure.Rate:
                    legendTitle.text = L.T($"Ρυθμός, τελευταίες {RateDays} ημ. (mm/ημ.)", $"Rate, last {RateDays} d (mm/day)");
                    legendLow.text = "0"; legendMid.text = "0.25"; legendHigh.text = "0.5+";
                    break;
                default:
                    legendTitle.text = L.T("Συνολική μετακίνηση (mm)", "Total movement (mm)");
                    legendLow.text = "0"; legendMid.text = "10"; legendHigh.text = "20+";
                    break;
            }
        }

        // The interpolated value where the ray meets the model, unless it is on a panel or a dot.
        private void Probe(Camera cam)
        {
            bool on = false;
#if !MOBILE_PREVIEW
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            Transform hand = rig != null && Hands.DominantConnected ? Hands.DominantAnchor(rig) : null;
            if (hand != null && !VrPointer.IsHovering && !sensors.Aiming && lastPts.Length > 0
                && overlay.TryRaycastSurface(new Ray(hand.position, hand.forward), 60f, out Vector3 local))
            {
                var q = new Vector2(local.x, local.z);
                int nearest = 0;
                float best = float.MaxValue;
                for (int i = 0; i < lastPts.Length; i++)
                {
                    float d = (q - lastPts[i]).sqrMagnitude;
                    if (d < best) { best = d; nearest = i; }
                }
                if (Mathf.Sqrt(best) <= lastReach)
                {
                    float v = Linear(q, lastPts, lastVals, lastTris, lastEdges, nearest);
                    // Rebuilt only when the shown (rounded) value changes, not every frame.
                    int shownKey = Mathf.RoundToInt(v * (computed == Measure.Rate ? 100f : 10f)) * 4 + (int)computed;
                    if (shownKey != probeKey)
                    {
                        probeKey = shownKey;
                        probeText.text = computed == Measure.Rate ? $"≈ {v:0.00} " + L.T("mm/ημ.", "mm/day")
                            : computed == Measure.Vertical ? $"dz ≈ {v:+0.0;-0.0;0.0} mm" : $"≈ {v:0.0} mm";
                        probeText.color = Ramp(v, computed);
                    }
                    Vector3 world = overlay.Root.TransformPoint(local);
                    float dist = Vector3.Distance(cam.transform.position, world);
                    probe.position = world + Vector3.up * Mathf.Max(0.01f, dist * 0.02f);
                    probe.localScale = Vector3.one * 0.0006f * Mathf.Clamp(dist / 0.7f, 0.3f, 6f);
                    VrUi.FaceViewer(probe, cam.transform);
                    on = true;
                }
            }
#endif
            if (probe.gameObject.activeSelf != on) probe.gameObject.SetActive(on);
        }

        private void BuildUi()
        {
            legend = VrUi.CreateCanvas("HeatLegend", transform, new Vector2(300f, 74f), 0.0006f);
            VrUi.Card("Bg", legend, 0, 0, 300, 74, Brand.Surface.WithAlpha(0.94f), Brand.BorderStrong, 14);
            legendTitle = VrUi.Text("Title", legend, 14, 4, 272, 22, "", 13f, Brand.Fg, true);
            legendTex = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            legendBar = VrUi.Panel("Bar", legend, 14, 30, 272, 14, Color.white, 0);
            legendSprite = Sprite.Create(legendTex, new Rect(0, 0, 64, 1), new Vector2(0.5f, 0.5f));
            legendBar.sprite = legendSprite;
            legendBar.type = Image.Type.Simple;
            legendLow = VrUi.Text("Low", legend, 14, 46, 100, 22, "", 12f, Brand.FgMuted);
            legendMid = VrUi.Text("Mid", legend, 100, 46, 100, 22, "", 12f, Brand.FgMuted, false, TextAlignmentOptions.Center);
            legendHigh = VrUi.Text("High", legend, 186, 46, 100, 22, "", 12f, Brand.FgMuted, false, TextAlignmentOptions.MidlineRight);
            legend.gameObject.SetActive(false);
            legendGliding = true;

            probe = VrUi.CreateCanvas("HeatProbe", transform, new Vector2(160f, 34f), 0.0006f);
            VrUi.Card("Bg", probe, 0, 0, 160, 34, Brand.Surface.WithAlpha(0.92f), Brand.BorderStrong, 10);
            probeText = VrUi.Text("Value", probe, 8, 2, 144, 30, "", 16f, Brand.Fg, true, TextAlignmentOptions.Center);
            probe.pivot = new Vector2(0.5f, 0f);
            probe.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            Shader.SetGlobalFloat(HeatOpacityId, 0f);
            if (tex != null) Destroy(tex);
            if (legendSprite != null) Destroy(legendSprite);
            if (legendTex != null) Destroy(legendTex);
        }
    }
}
