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
    /// history) spread between the sensors by inverse-distance weighting and laid over the
    /// texture, green → amber → red like the time machine's arrows (red at RedAtMm). Far from
    /// every sensor the colour fades out, so ground without measurements stays unpainted.
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
        /// <summary>IDW power: 2 is the usual survey choice (near points dominate).</summary>
        private const float Power = 2f;
        private const float RecomputeSeconds = 0.25f;

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
            string k = $"{sensors.Version}|{SensorHistory.Count}|{SensorHistory.Last.Ticks}|{at.Ticks}|{from?.Ticks}|{overlay.LoadedName}";
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
            float reach = Mathf.Max(15f, size * 0.12f);
            float fadeFrom = reach * 0.5f;

            for (int gy = 0; gy < Grid; gy++)
            {
                float z = minZ + (gy + 0.5f) * cell;
                for (int gx = 0; gx < Grid; gx++)
                {
                    float x = minX + (gx + 0.5f) * cell;
                    double wSum = 0, vSum = 0;
                    float nearest = float.MaxValue;
                    float exact = -1f;
                    foreach ((Vector2 p, float mm) in samples)
                    {
                        float dx = x - p.x, dz = z - p.y;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < 1e-6f) { exact = mm; nearest = 0f; break; }
                        float d = Mathf.Sqrt(d2);
                        if (d < nearest) nearest = d;
                        double w = 1.0 / Math.Pow(d, Power);
                        wSum += w;
                        vSum += w * mm;
                    }
                    float value = exact >= 0f ? exact : (float)(vSum / wSum);
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
