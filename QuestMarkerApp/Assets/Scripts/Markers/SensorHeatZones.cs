using System;
using System.Collections.Generic;
using UnityEngine;
using QuestMarkerApp.API;
using QuestMarkerApp.Core;
using QuestMarkerApp.Models;
using QuestMarkerApp.UI;

namespace QuestMarkerApp.Markers
{
    /// <summary>
    /// The heatmap for projects with no 3D model: a zone around each aligned sensor in the colour
    /// of its movement, same measures, scale and legend as ModelHeatmap, following the time
    /// machine's date. Zones are never joined, so they show only what was really measured:
    /// through walls and floors a surface between sensors would invent values.
    /// Near the user the colour lands on the real surface through the Quest depth map
    /// (SensorZone shader); further away, or with no depth, a zone is a soft ball.
    /// With this project's model loaded, ModelHeatmap paints the model and this stays off; a
    /// model of another site, with none of the sensors near it, does not count.
    /// </summary>
    public class SensorHeatZones : MonoBehaviour
    {
        private const float Radius = 1.5f;
        /// <summary>Beyond this the depth map is not trusted enough to paint on, metres.</summary>
        private const float PaintWithin = 6f;
        /// <summary>The ray walk is the costly part: only this many, the nearest.</summary>
        private const int MaxPainted = 12;
        private const float RecomputeSeconds = 0.25f;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int PaintId = Shader.PropertyToID("_Paint");

        /// <summary>Zones are on show: ModelHeatmap keeps its legend up for them.</summary>
        public static bool Showing { get; private set; }

        /// <summary>Sensors with a zone.</summary>
        public static int Count { get; private set; }

        private sealed class Zone
        {
            public Marker Marker;
            public string SensorId;
            public MeshRenderer Renderer;
            public bool HasValue;
            public Color Color;
            public float SqrDist;
        }

        private readonly Dictionary<string, Zone> zones = new Dictionary<string, Zone>();
        private readonly List<Zone> byDistance = new List<Zone>();
        private readonly List<string> stale = new List<string>();
        private MarkerManager markers;
        private ProjectAlignment project;
        private ModelOverlay overlay;
        private ModelSensors modelSensors;
        private Material material;
        private Mesh sphere;
        private MaterialPropertyBlock block;
        private float nextCompute;
        private string key;
        private bool visible;

        public void Init(MarkerManager markerManager, ProjectAlignment alignment, ModelOverlay modelOverlay, ModelSensors sensorsOnModel)
        {
            markers = markerManager;
            project = alignment;
            overlay = modelOverlay;
            modelSensors = sensorsOnModel;
        }

        private bool Wanted =>
            ModelHeatmap.Enabled && markers != null && project != null
            && (overlay == null || !overlay.IsLoaded || (modelSensors != null && modelSensors.ModelElsewhere))
            && project.HasProject && !project.IsPractice && project.IsAligned && !project.NeedsCheck && !project.IsModeActive
            && !WorkMode.IsOffice && !markers.HideProjectLayer;

        private void LateUpdate()
        {
            bool on = Wanted;
            DepthOcclusion.ZonesWantDepth = on;
            if (!on)
            {
                if (visible) SetVisible(false);
                Showing = false;
                Count = 0;
                key = null;
                return;
            }
            Camera cam = Headset.Camera;
            if (cam == null || !EnsureAssets()) return;

            if (Time.unscaledTime >= nextCompute)
            {
                nextCompute = Time.unscaledTime + RecomputeSeconds;
                Sync();
                DateTime at = DisplacementView.IsActive && DisplacementView.ShownAt.HasValue ? DisplacementView.ShownAt.Value : DateTime.MaxValue;
                DateTime? from = DisplacementView.IsActive ? DisplacementView.ComparedFrom : null;
                string k = $"{zones.Count}|{SensorHistory.Count}|{SensorHistory.Last.Ticks}|{at.Ticks}|{from?.Ticks}|{(int)ModelHeatmap.Shown}|{ModelHeatmap.ReachSetting}";
                if (k != key)
                {
                    key = k;
                    Compute(at, from);
                }
            }
            Place(cam.transform.position);
            visible = true;
            Showing = true;
        }

        // A zone per project marker; markers come and go with reloads and the alarms-only filter.
        private void Sync()
        {
            foreach (KeyValuePair<string, Marker> kv in markers.ActiveMarkers)
            {
                if (zones.ContainsKey(kv.Key) || kv.Value == null) continue;
                if (!project.TryGetPoint(kv.Key, out ProjectPoint p) || string.IsNullOrEmpty(p.sensorId)) continue;
                zones[kv.Key] = new Zone { Marker = kv.Value, SensorId = p.sensorId, Renderer = BuildRenderer(p.id) };
            }
            stale.Clear();
            foreach (KeyValuePair<string, Zone> kv in zones)
                if (kv.Value.Marker == null || !markers.ActiveMarkers.ContainsKey(kv.Key)) stale.Add(kv.Key);
            foreach (string id in stale)
            {
                if (zones[id].Renderer != null) Destroy(zones[id].Renderer.gameObject);
                zones.Remove(id);
            }
        }

        private void Compute(DateTime at, DateTime? from)
        {
            ModelHeatmap.Measure measure = ModelHeatmap.Shown;
            int count = 0;
            foreach (Zone z in zones.Values)
            {
                z.HasValue = ModelHeatmap.TryValue(z.SensorId, at, from, measure, out float value);
                if (!z.HasValue) continue;
                z.Color = ModelHeatmap.Ramp(value, measure);
                count++;
            }
            if (count != Count) Debug.Log($"[HeatZones] {count} of {zones.Count} sensors with a value · history {SensorHistory.Count} · depth {DepthOcclusion.DepthReady}");
            Count = count;
        }

        private void Place(Vector3 eye)
        {
            float diameter = 2f * Radius * ModelHeatmap.ReachFactor;
            bool depth = DepthOcclusion.DepthReady;
            byDistance.Clear();
            foreach (Zone z in zones.Values)
            {
                bool show = z.HasValue && z.Marker != null && z.Marker.gameObject.activeInHierarchy;
                if (z.Renderer.gameObject.activeSelf != show) z.Renderer.gameObject.SetActive(show);
                if (!show) continue;
                Transform t = z.Renderer.transform;
                t.position = z.Marker.transform.position;
                t.localScale = Vector3.one * diameter;
                z.SqrDist = (t.position - eye).sqrMagnitude;
                byDistance.Add(z);
            }
            byDistance.Sort((a, b) => a.SqrDist.CompareTo(b.SqrDist));
            float reachSqr = (PaintWithin + diameter * 0.5f) * (PaintWithin + diameter * 0.5f);
            for (int i = 0; i < byDistance.Count; i++)
            {
                Zone z = byDistance[i];
                block.SetColor(ColorId, z.Color);
                block.SetFloat(PaintId, depth && i < MaxPainted && z.SqrDist <= reachSqr ? 1f : 0f);
                z.Renderer.SetPropertyBlock(block);
            }
        }

        private void SetVisible(bool on)
        {
            visible = on;
            foreach (Zone z in zones.Values)
                if (z.Renderer != null && z.Renderer.gameObject.activeSelf != on) z.Renderer.gameObject.SetActive(on);
        }

        private bool EnsureAssets()
        {
            if (material == null)
            {
                Shader shader = Resources.Load<Shader>("Shaders/SensorZone");
                if (shader == null)
                {
                    Debug.LogWarning("[HeatZones] Resources/Shaders/SensorZone missing");
                    return false;
                }
                material = new Material(shader) { name = "SensorZone" };
                block = new MaterialPropertyBlock();
                GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere = primitive.GetComponent<MeshFilter>().sharedMesh;
                Destroy(primitive);
            }
            return true;
        }

        private MeshRenderer BuildRenderer(string id)
        {
            var go = new GameObject("HeatZone_" + id);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = sphere;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return r;
        }
    }
}
