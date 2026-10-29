using System;
using System.Collections.Generic;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Models
{
    /// <summary>
    /// Displacement heatmap over the model: each sensor's total movement (mm, from its survey
    /// history), interpolated linearly between the sensors and laid over the texture, green →
    /// amber → red like the time machine's arrows (red at RedAtMm).
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
        /// <summary>Movement drawn fully red, mm (the arrows' scale).</summary>
        public const float RedAtMm = 20f;
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

        private ModelOverlay overlay;
        private ModelSensors sensors;
        private Texture2D tex;
        private Color32[] pixels;
        private readonly List<(Vector2 at, float mm)> samples = new List<(Vector2, float)>();
        private string key;
        private float nextCompute;
        private bool shown;

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
                return;
            }
            if (Time.unscaledTime < nextCompute) return;
            nextCompute = Time.unscaledTime + RecomputeSeconds;

            DateTime at = DisplacementView.IsActive && DisplacementView.ShownAt.HasValue ? DisplacementView.ShownAt.Value : DateTime.MaxValue;
            DateTime? from = DisplacementView.IsActive ? DisplacementView.ComparedFrom : null;
            string k = $"{sensors.Version}|{SensorHistory.Count}|{SensorHistory.Last.Ticks}|{at.Ticks}|{from?.Ticks}|{overlay.LoadedName}|{ReachSetting}";
            if (k == key) return;
            key = k;
            Compute(at, from);
        }

        // Each sensor on the model with a displacement at the date: its plan position and mm.
        private void Collect(DateTime at, DateTime? from)
        {
            samples.Clear();
            foreach ((ProjectPoint p, Vector3 local) in sensors.DotPoints)
            {
                if (!SensorHistory.TryDisplacementAt(p.sensorId, at, out SensorHistory.Sample s)) continue;
                double dx = s.Dx ?? 0, dy = s.Dy ?? 0, dz = s.Dz ?? 0;
                if (from.HasValue && SensorHistory.TryDisplacementAt(p.sensorId, from.Value, out SensorHistory.Sample z))
                {
                    dx -= z.Dx ?? 0;
                    dy -= z.Dy ?? 0;
                    dz -= z.Dz ?? 0;
                }
                double mm = Math.Sqrt(dx * dx + dy * dy + dz * dz) * SensorHistory.MetresPerUnit(p.sensorId) * 1000.0;
                samples.Add((new Vector2(local.x, local.z), (float)mm));
            }
        }

        private void Compute(DateTime at, DateTime? from)
        {
            Collect(at, from);
            if (samples.Count == 0)
            {
                Shader.SetGlobalFloat(HeatOpacityId, 0f);
                shown = false;
                Status = L.T("Χωρίς μετακινήσεις στο μοντέλο", "No displacements on the model");
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
            float minX = b.center.x - size * 0.5f, minZ = b.center.z - size * 0.5f, cell = size / Grid;
            // Colour reaches this far from the nearest sensor, fading over the last half.
            float reach = Mathf.Clamp(1.5f * TypicalSpacing(), 5f, 30f) * ReachFactors[ReachSetting];
            Reach = reach;
            float fadeFrom = reach * 0.5f;

            var pts = new Vector2[samples.Count];
            var vals = new float[samples.Count];
            for (int i = 0; i < samples.Count; i++) { pts[i] = samples[i].at; vals[i] = samples[i].mm; }
            List<int> tris = Delaunay.Triangulate(pts);
            // Drop triangles that bridge a gap with no sensors.
            float maxEdge = 2f * reach;
            var keep = new List<int>(tris.Count);
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                Vector2 a = pts[tris[t]], c1 = pts[tris[t + 1]], c2 = pts[tris[t + 2]];
                if ((a - c1).magnitude > maxEdge || (c1 - c2).magnitude > maxEdge || (c2 - a).magnitude > maxEdge) continue;
                keep.Add(tris[t]); keep.Add(tris[t + 1]); keep.Add(tris[t + 2]);
            }
            List<(int, int)> edges = Edges(keep, pts, maxEdge);

            for (int gy = 0; gy < Grid; gy++)
            {
                float z = minZ + (gy + 0.5f) * cell;
                for (int gx = 0; gx < Grid; gx++)
                {
                    float x = minX + (gx + 0.5f) * cell;
                    var q = new Vector2(x, z);
                    float nearest = float.MaxValue;
                    int nearestIndex = 0;
                    for (int i = 0; i < pts.Length; i++)
                    {
                        float d = (q - pts[i]).sqrMagnitude;
                        if (d < nearest) { nearest = d; nearestIndex = i; }
                    }
                    nearest = Mathf.Sqrt(nearest);
                    if (nearest > reach) { pixels[gy * Grid + gx] = default; continue; }
                    float value = Linear(q, pts, vals, keep, edges, nearestIndex);
                    float alpha = nearest <= fadeFrom ? 1f : Mathf.Clamp01(1f - (nearest - fadeFrom) / (reach - fadeFrom));
                    Color c = Ramp(value);
                    pixels[gy * Grid + gx] = new Color(c.r, c.g, c.b, alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);

            Shader.SetGlobalTexture(HeatTexId, tex);
            Shader.SetGlobalVector(HeatRectId, new Vector4(minX, minZ, 1f / size, 1f / size));
            Shader.SetGlobalFloat(HeatOpacityId, Opacity);
            shown = true;
            Status = L.En ? $"On · {samples.Count} sensors" : $"Ναι · {samples.Count} αισθητήρες";
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

        // Inside a triangle: barycentric. Outside: along the nearest edge (clamped). No edge
        // near: the nearest sensor's own value.
        private static float Linear(Vector2 q, Vector2[] pts, float[] vals, List<int> tris, List<(int, int)> edges, int nearest)
        {
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                Vector2 a = pts[tris[t]], b = pts[tris[t + 1]], c = pts[tris[t + 2]];
                float den = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                if (Mathf.Abs(den) < 1e-9f) continue;
                float w1 = ((b.y - c.y) * (q.x - c.x) + (c.x - b.x) * (q.y - c.y)) / den;
                float w2 = ((c.y - a.y) * (q.x - c.x) + (a.x - c.x) * (q.y - c.y)) / den;
                float w3 = 1f - w1 - w2;
                if (w1 >= -1e-4f && w2 >= -1e-4f && w3 >= -1e-4f)
                    return w1 * vals[tris[t]] + w2 * vals[tris[t + 1]] + w3 * vals[tris[t + 2]];
            }
            float bestD = float.MaxValue, best = vals[nearest];
            foreach ((int i, int j) in edges)
            {
                Vector2 a = pts[i], ab = pts[j] - a;
                float len2 = ab.sqrMagnitude;
                float u = len2 > 1e-9f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / len2) : 0f;
                float d = (q - (a + ab * u)).sqrMagnitude;
                if (d < bestD) { bestD = d; best = Mathf.Lerp(vals[i], vals[j], u); }
            }
            return best;
        }

        /// <summary>Green → amber at half the red mark → red, as the arrows.</summary>
        public static Color Ramp(float mm)
        {
            float t = Mathf.Clamp01(mm / RedAtMm);
            return t < 0.5f ? Color.Lerp(Brand.Primary, Brand.Warning, t * 2f)
                            : Color.Lerp(Brand.Warning, Brand.Danger, (t - 0.5f) * 2f);
        }

        private void OnDestroy()
        {
            Shader.SetGlobalFloat(HeatOpacityId, 0f);
            if (tex != null) Destroy(tex);
        }
    }
}
