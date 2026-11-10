using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using QuestMarkerApp.Markers;
using QuestMarkerApp.Models;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Frame rate log for the performance experiment (thesis §exp-performance), switched on
    /// from Μενού › Ρυθμίσεις. One CSV row a second with the frame rate, the worst frame, frames
    /// over budget, CPU and GPU frame times, battery and its temperature, and the load that
    /// produced them: refresh rate, foveation, eye buffer, markers, model triangles, heatmap.
    /// OVR Metrics Tool measures the same on the headset, but cannot say which scenario was up.
    /// The menu row cycles off → on → on without the heavy-model cut (DisplayPerformance.AutoReduceOff)
    /// → off, so the 150 000-triangle threshold can be measured too; column auto_reduce says which.
    /// Files: persistentDataPath/perf/perf-yyyy-MM-dd.csv, also at GET /api/perf.
    /// </summary>
    public class PerfLog : MonoBehaviour
    {
        public const string FolderName = "perf";
        private const float Window = 1f;
        // A frame later than 1.5 periods missed its slot: the compositor showed the old one again.
        private const float DroppedFactor = 1.5f;

        public static bool Active { get; private set; }

        /// <summary>One line for the menu.</summary>
        public static string Status { get; private set; } = "";

        /// <summary>The menu row: off → on → on, no heavy-model cut → off.</summary>
        public static void Cycle()
        {
            if (!Active) SetActive(true);
            else if (!DisplayPerformance.AutoReduceOff) DisplayPerformance.AutoReduceOff = true;
            else SetActive(false);
        }

        /// <summary>The menu row's value.</summary>
        public static string MenuValue => !Active ? L.T("Όχι", "Off")
            : DisplayPerformance.AutoReduceOff ? Status + L.T(" · χωρίς μείωση", " · no cut") : Status;

        private static PerfLog instance;

        private readonly FrameTiming[] timings = new FrameTiming[1];
        private MarkerManager markers;
        private ModelOverlay model;
        private float windowStart;
        private int frames, dropped, rows;
        private float worst;
        private double cpuSum, gpuSum;
        private int cpuCount, gpuCount;
        private string file;

        public static void SetActive(bool on)
        {
            if (on && instance == null)
            {
                var go = new GameObject("PerfLog");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<PerfLog>();
            }
            if (instance == null) return;
            Active = on;
            if (!on) DisplayPerformance.AutoReduceOff = false;
            instance.enabled = on;
            if (on) instance.Begin();
            else Status = "";
        }

        private void Begin()
        {
            if (markers == null) markers = FindAnyObjectByType<MarkerManager>();
            if (model == null) model = FindAnyObjectByType<ModelOverlay>();
            ResetWindow();
            rows = 0;
            try
            {
                string folder = Path.Combine(Application.persistentDataPath, FolderName);
                Directory.CreateDirectory(folder);
                file = Path.Combine(folder, $"perf-{DateTime.Now:yyyy-MM-dd}.csv");
                if (!File.Exists(file))
                    File.WriteAllText(file,
                        "timestamp,session_id,app_version,mode,refresh_req_hz,refresh_hz,foveation,eye_scale,markers,model_tris,heatmap," +
                        "fps,worst_frame_ms,dropped,cpu_ms,gpu_ms,battery_pct,battery_temp_c,auto_reduce\n", new UTF8Encoding(true));
                Status = L.T("Καταγράφει…", "Recording…");
                Debug.Log($"[Perf] Logging to {file}");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Perf] Cannot write the log: " + e.Message);
                Status = L.T("Δεν γράφεται το αρχείο", "Cannot write the file");
                Active = false;
                enabled = false;
            }
        }

        private void ResetWindow()
        {
            windowStart = Time.unscaledTime;
            frames = dropped = 0;
            worst = 0f;
            cpuSum = gpuSum = 0;
            cpuCount = gpuCount = 0;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float rate = DisplayPerformance.ActualRate > 0f ? DisplayPerformance.ActualRate : DisplayPerformance.AppliedRate;
            frames++;
            worst = Mathf.Max(worst, dt);
            if (rate > 0f && dt > DroppedFactor / rate) dropped++;

            // Needs Player Settings › Frame Timing Stats (on); zeros where the platform has none.
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
            {
                if (timings[0].cpuFrameTime > 0) { cpuSum += timings[0].cpuFrameTime; cpuCount++; }
                if (timings[0].gpuFrameTime > 0) { gpuSum += timings[0].gpuFrameTime; gpuCount++; }
            }

            float elapsed = Time.unscaledTime - windowStart;
            if (elapsed < Window) return;
            float fps = frames / elapsed;
            Write(fps, rate);
            Status = L.En ? $"Recording · {fps:F0} fps" : $"Καταγράφει · {fps:F0} fps";
            ResetWindow();
        }

        private void Write(float fps, float rate)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(200);
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", inv)).Append(',');
            sb.Append(FieldTestLog.SessionId).Append(',').Append(Application.version).Append(',');
            sb.Append(WorkMode.Current).Append(',');
            sb.Append(DisplayPerformance.AppliedRate.ToString("0", inv)).Append(',');
            sb.Append(rate.ToString("0.#", inv)).Append(',');
            sb.Append(DisplayPerformance.Foveation).Append(',');
            sb.Append(UnityEngine.XR.XRSettings.eyeTextureResolutionScale.ToString("0.##", inv)).Append(',');
            sb.Append(markers != null ? markers.ActiveMarkerCount : 0).Append(',');
            sb.Append(model != null && model.IsLoaded && model.Model != null && model.Root != null && model.Root.gameObject.activeInHierarchy
                ? model.Model.TriangleCount : 0).Append(',');
            sb.Append(ModelHeatmap.Enabled ? '1' : '0').Append(',');
            sb.Append(fps.ToString("0.0", inv)).Append(',');
            sb.Append((worst * 1000f).ToString("0.0", inv)).Append(',');
            sb.Append(dropped).Append(',');
            sb.Append(cpuCount > 0 ? (cpuSum / cpuCount).ToString("0.00", inv) : "").Append(',');
            sb.Append(gpuCount > 0 ? (gpuSum / gpuCount).ToString("0.00", inv) : "").Append(',');
            float battery = SystemInfo.batteryLevel;
            sb.Append(battery >= 0f ? (battery * 100f).ToString("0", inv) : "").Append(',');
#if !MOBILE_PREVIEW && !UNITY_EDITOR
            sb.Append(OVRManager.batteryTemperature.ToString("0.0", inv));
#endif
            sb.Append(',').Append(DisplayPerformance.AutoReduceOff ? '0' : '1');
            sb.Append('\n');
            try { File.AppendAllText(file, sb.ToString()); }
            catch (Exception e)
            {
                Debug.LogWarning("[Perf] Write failed: " + e.Message);
                return;
            }
            rows++;
        }

        private void OnDisable()
        {
            if (rows > 0) Debug.Log($"[Perf] Stopped after {rows} rows");
        }
    }
}
