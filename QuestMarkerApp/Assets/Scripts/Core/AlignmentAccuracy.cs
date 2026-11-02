using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using QuestMarkerApp.API;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Alignment accuracy measurement (thesis ch. 6, «Ακρίβεια ευθυγράμμισης»): with an alignment
    /// in place, every touch of a project point is compared with where the current solution
    /// puts that point, and the difference goes to a CSV. Nothing is placed and the solution
    /// never changes while it is on — that is the point: points that did not take part in the
    /// solution are independent check points, and repeated touches of a control point measure
    /// how well a touch repeats (σ of <see cref="ProjectAlignment.TouchSigma"/>).
    ///
    /// Switching it on freezes the current solution as one "series" (k control points, the
    /// baseline, the RMS); a later re-alignment starts a new series. Session-only: it is off
    /// at every start, so a field visit never begins with touches that do not place points.
    /// Files: persistentDataPath/accuracy, one CSV per project per day, served at
    /// GET /api/accuracy. Analysis: thesis/analysis/alignment_accuracy.py.
    /// </summary>
    public static class AlignmentAccuracy
    {
        public const string FolderName = "accuracy";

        public static readonly string[] Columns =
        {
            "timestamp", "session_id", "project", "practice", "app_version", "condition",
            "series", "k", "control_ids", "baseline_m", "rms_h_cm", "rms_v_cm",
            "meas_id", "point_id", "role", "repeat", "centroid_dist_m",
            "err_h_cm", "err_v_cm", "err_e_cm", "err_n_cm", "predicted_cm",
            "touch_e", "touch_n", "touch_h", "headset_dist_m", "note"
        };

        public static string Folder => Path.Combine(Application.persistentDataPath, FolderName);

        /// <summary>Touches are measured instead of placed. Never persisted.</summary>
        public static bool Active { get; private set; }

        /// <summary>The frozen solution the current touches are measured against.</summary>
        private class Series
        {
            public int Number;
            public string Project;
            public bool Practice;
            public int K;
            public string ControlIds;
            public float Baseline;          // m, project distance of the furthest control pair
            public Vector3 Centroid;        // project-local centroid of the control points
            public float RmsH, RmsV;        // m
            public HashSet<string> Controls = new HashSet<string>();
        }

        private class Measurement
        {
            public int Id;
            public string PointId;
            public float ErrH, ErrV;        // m
        }

        private static Series series;
        private static int seriesCounter, measCounter;
        private static readonly List<Measurement> measured = new List<Measurement>();
        private static readonly Dictionary<string, int> repeats = new Dictionary<string, int>();

        /// <summary>Menu readout: "Ενεργή · 7 μετρήσεις · διάμεσος 2.3 cm".</summary>
        public static string Summary
        {
            get
            {
                if (!Active) return measured.Count > 0
                    ? L.T($"Ανενεργή · {measured.Count} μετρήσεις", $"Off · {measured.Count} measured")
                    : L.T("Ανενεργή", "Off");
                if (measured.Count == 0) return L.T($"Ενεργή · σειρά {series.Number} · k={series.K}", $"On · series {series.Number} · k={series.K}");
                var h = new List<float>();
                foreach (Measurement m in measured) h.Add(m.ErrH);
                h.Sort();
                float median = h.Count % 2 == 1 ? h[h.Count / 2] : 0.5f * (h[h.Count / 2 - 1] + h[h.Count / 2]);
                return L.T($"Ενεργή · {measured.Count} μετρ. · διάμ. {median * 100f:F1} cm",
                    $"On · {measured.Count} measured · median {median * 100f:F1} cm");
            }
        }

        /// <summary>Why it cannot be switched on right now, or null when it can.</summary>
        public static string Unavailable(ProjectAlignment a)
        {
            if (a == null || !a.HasProject) return L.T("Δεν υπάρχει έργο", "No project");
            if (!a.IsAligned) return L.T("Ευθυγράμμισε πρώτα", "Align first");
            if (a.NeedsCheck) return L.T("Επιβεβαίωσε πρώτα τη θέση", "Confirm the position first");
            return null;
        }

        public static void SetActive(bool on, ProjectAlignment a)
        {
            if (on && Unavailable(a) != null) return;
            if (on == Active) return;
            Active = on;
            if (on) Freeze(a);
            Debug.Log($"[Accuracy] {(on ? $"On — series {series.Number}, k={series.K}" : "Off")}");
        }

        /// <summary>
        /// Called after every solve. A different solution while on starts a new series; losing
        /// the alignment switches the measurement off (there is nothing to measure against).
        /// </summary>
        public static void OnSolved(ProjectAlignment a)
        {
            if (!Active) return;
            if (Unavailable(a) != null)
            {
                Active = false;
                Debug.Log("[Accuracy] Off — the alignment is gone");
                return;
            }
            if (series == null || series.K != a.PlacedCount || series.Project != a.ProjectName) Freeze(a);
        }

        private static void Freeze(ProjectAlignment a)
        {
            var s = new Series
            {
                Number = ++seriesCounter,
                Project = a.ProjectName,
                Practice = a.IsPractice,
                K = a.PlacedCount,
                ControlIds = string.Join(";", a.PlacementOrder),
                Baseline = a.Pair.Valid ? a.Pair.Project : 0f,
                Centroid = a.ControlCentroid,
                RmsH = a.RmsHorizontal,
                RmsV = a.RmsVertical
            };
            foreach (string id in a.PlacementOrder) s.Controls.Add(id);
            series = s;
            repeats.Clear();
        }

        /// <summary>
        /// One touch of <paramref name="point"/> at <paramref name="worldTouch"/>: compared with the
        /// frozen solution, logged, and described for the alignment panel.
        /// </summary>
        public static string Record(ProjectAlignment a, ProjectPoint point, Vector3 worldTouch, out bool good)
        {
            good = false;
            if (!Active || series == null || point == null) return null;
            if (!a.TryTouchInProject(point, worldTouch, out Vector3 delta, out Vector3 touchLocal, out double touchE, out double touchN, out double touchH))
                return L.T("Δεν υπάρχει ευθυγράμμιση για σύγκριση.", "No alignment to compare with.");

            Vector3 pointLocal = a.PointLocal(point);
            float errH = new Vector2(delta.x, delta.z).magnitude;
            float errV = delta.y;
            float d = new Vector2(pointLocal.x - series.Centroid.x, pointLocal.z - series.Centroid.z).magnitude;
            float? predicted = series.Baseline > 0.01f ? ProjectAlignment.TouchSigma * 1.414f * d / series.Baseline : (float?)null;
            bool control = series.Controls.Contains(point.id);
            repeats.TryGetValue(point.id, out int rep);
            repeats[point.id] = ++rep;

            var m = new Measurement { Id = ++measCounter, PointId = point.id, ErrH = errH, ErrV = errV };
            measured.Add(m);

            Camera cam = Headset.Camera;
            float? headsetDist = cam != null ? Vector3.Distance(cam.transform.position, worldTouch) : (float?)null;
            Append(new[]
            {
                series.Number.ToString(CultureInfo.InvariantCulture),
                series.K.ToString(CultureInfo.InvariantCulture),
                series.ControlIds,
                Num(series.Baseline, "F3"),
                Num(series.RmsH * 100f, "F1"),
                Num(series.RmsV * 100f, "F1"),
                m.Id.ToString(CultureInfo.InvariantCulture),
                point.id,
                control ? "control" : "check",
                rep.ToString(CultureInfo.InvariantCulture),
                Num(d, "F3"),
                Num(errH * 100f, "F1"),
                Num(errV * 100f, "F1"),
                Num(delta.x * 100f, "F1"),
                Num(delta.z * 100f, "F1"),
                predicted.HasValue ? Num(predicted.Value * 100f, "F1") : null,
                touchE.ToString("F4", CultureInfo.InvariantCulture),
                touchN.ToString("F4", CultureInfo.InvariantCulture),
                touchH.ToString("F4", CultureInfo.InvariantCulture),
                headsetDist.HasValue ? Num(headsetDist.Value, "F2") : null,
                null
            });

            string name = string.IsNullOrEmpty(point.label) ? point.id : point.label;
            string kind = control ? L.T("σημείο ελέγχου", "control point") : L.T("ανεξάρτητο", "independent");
            string again = rep > 1 ? L.T($" · επανάληψη {rep}", $" · repeat {rep}") : "";
            string pred = predicted.HasValue ? L.T($" · πρόβλ. {predicted.Value * 100f:F1}", $" · pred. {predicted.Value * 100f:F1}") : "";
            good = errH <= 0.05f;
            return L.T($"{name} ({kind}{again}): {errH * 100f:F1} cm οριζ., {errV * 100f:+0.0;-0.0} cm κατακ.{pred}",
                $"{name} ({kind}{again}): {errH * 100f:F1} cm horiz., {errV * 100f:+0.0;-0.0} cm vert.{pred}");
        }

        /// <summary>
        /// Withdraws the last measurement: the CSV is append-only, so an "undo" row names the
        /// measurement it cancels and the analysis drops both.
        /// </summary>
        public static string UndoLast()
        {
            if (!Active || measured.Count == 0) return null;
            Measurement m = measured[measured.Count - 1];
            measured.RemoveAt(measured.Count - 1);
            if (repeats.TryGetValue(m.PointId, out int rep)) repeats[m.PointId] = Math.Max(0, rep - 1);
            Append(new[]
            {
                series.Number.ToString(CultureInfo.InvariantCulture), series.K.ToString(CultureInfo.InvariantCulture),
                series.ControlIds, null, null, null,
                m.Id.ToString(CultureInfo.InvariantCulture), m.PointId, "undo", null, null,
                null, null, null, null, null, null, null, null, null,
                $"cancels meas {m.Id}"
            });
            return L.T($"Αναιρέθηκε η μέτρηση του {m.PointId}.", $"Undid the {m.PointId} measurement.");
        }

        // ------------------------------------------------------------------
        //  CSV (same conventions as FieldTestLog: BOM on a new file, quoted fields, invariant numbers)
        // ------------------------------------------------------------------

        // The series-specific values; the common leading columns are added here.
        private static void Append(string[] values)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string file = Path.Combine(Folder, $"{FieldTestLog.ProjectKey(series.Project)}-{DateTime.Now:yyyy-MM-dd}.csv");
                var sb = new StringBuilder(256);
                bool fresh = !File.Exists(file);
                if (fresh) sb.Append(string.Join(",", Columns)).Append('\n');
                Field(sb, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
                Field(sb, FieldTestLog.SessionId);
                Field(sb, series.Project);
                Field(sb, series.Practice ? "yes" : "no");
                Field(sb, Application.version);
                Field(sb, AnchorFieldTest.ConditionTag);
                for (int i = 0; i < values.Length; i++) Field(sb, values[i], i == values.Length - 1);
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(fresh));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Accuracy] CSV write failed: {e.Message}");
            }
        }

        private static string Num(float v, string format) =>
            float.IsNaN(v) || float.IsInfinity(v) ? null : v.ToString(format, CultureInfo.InvariantCulture);

        private static void Field(StringBuilder sb, string value, bool last = false)
        {
            value ??= "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                value = "\"" + value.Replace("\"", "\"\"") + "\"";
            sb.Append(value).Append(last ? '\n' : ',');
        }
    }
}
