using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Files of the anchor field test (<see cref="AnchorFieldTest"/>), all under
    /// persistentDataPath/fieldtest: one CSV per project per day (one row per anchor per
    /// measurement) and one JSON per project with its control-point anchor UUIDs. Plain C#,
    /// no anchor APIs, so it compiles for the mobile preview too.
    /// </summary>
    public static class FieldTestLog
    {
        public const string FolderName = "fieldtest";

        /// <summary>Column order of every CSV. A column is left empty when the value is not known.</summary>
        public static readonly string[] Columns =
        {
            "timestamp", "session_id", "project", "point_id", "condition", "site_changed", "app_version",
            "phase", "anchor_uuid", "anchor_age_h", "localized", "seconds", "tracked", "dist_from_original_m",
            "errH_cm_solution", "errV_cm_solution", "errH_cm_touch", "errV_cm_touch",
            "pair_errH_cm", "pair_errV_cm", "headset_dist_m", "note"
        };

        /// <summary>One launch of the app: rows of the same visit share it.</summary>
        public static readonly string SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);

        public static string Folder => Path.Combine(Application.persistentDataPath, FolderName);

        /// <summary>One CSV row. Null fields are written empty.</summary>
        public class Row
        {
            public string Project, PointId, Phase, Uuid, Note;
            public double? AgeHours, Seconds, DistFromOriginal, HeadsetDist;
            public double? ErrHSolution, ErrVSolution, ErrHTouch, ErrVTouch, PairErrH, PairErrV;
            public bool? Localized, Tracked;
        }

        // ASCII letters/digits only plus a stable hash: the file name is safe on every file
        // system and in a URL, and Greek project names still get their own files.
        public static string ProjectKey(string projectName)
        {
            var sb = new StringBuilder();
            foreach (char c in projectName ?? "project")
                sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '_');
            uint h = 2166136261u;
            foreach (char c in projectName ?? "")
            {
                h = (h ^ (byte)(c & 0xFF)) * 16777619u;
                h = (h ^ (byte)(c >> 8)) * 16777619u;
            }
            string name = sb.Length > 40 ? sb.ToString(0, 40) : sb.ToString();
            return name + "_" + h.ToString("x8");
        }

        public static void Append(Row r)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string file = Path.Combine(Folder, $"{ProjectKey(r.Project)}-{DateTime.Now:yyyy-MM-dd}.csv");
                var sb = new StringBuilder(256);
                // BOM on a new file so Excel reads the Greek project names as UTF-8.
                bool fresh = !File.Exists(file);
                if (fresh) sb.Append(string.Join(",", Columns)).Append('\n');
                Field(sb, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
                Field(sb, SessionId);
                Field(sb, r.Project);
                Field(sb, r.PointId);
                Field(sb, AnchorFieldTest.ConditionTag);
                Field(sb, AnchorFieldTest.SiteChanged ? "yes" : "no");
                Field(sb, Application.version);
                Field(sb, r.Phase);
                Field(sb, r.Uuid);
                Field(sb, Num(r.AgeHours, "F2"));
                Field(sb, r.Localized.HasValue ? (r.Localized.Value ? "yes" : "no") : null);
                Field(sb, Num(r.Seconds, "F2"));
                Field(sb, r.Tracked.HasValue ? (r.Tracked.Value ? "yes" : "no") : null);
                Field(sb, Num(r.DistFromOriginal, "F3"));
                Field(sb, Num(r.ErrHSolution, "F1"));
                Field(sb, Num(r.ErrVSolution, "F1"));
                Field(sb, Num(r.ErrHTouch, "F1"));
                Field(sb, Num(r.ErrVTouch, "F1"));
                Field(sb, Num(r.PairErrH, "F1"));
                Field(sb, Num(r.PairErrV, "F1"));
                Field(sb, Num(r.HeadsetDist, "F2"));
                Field(sb, r.Note, true);
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(fresh));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FieldTest] CSV write failed: {e.Message}");
            }
        }

        private static string Num(double? v, string format) =>
            v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)
                ? v.Value.ToString(format, CultureInfo.InvariantCulture) : null;

        private static void Field(StringBuilder sb, string value, bool last = false)
        {
            value ??= "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                value = "\"" + value.Replace("\"", "\"\"") + "\"";
            sb.Append(value).Append(last ? '\n' : ',');
        }

        // ------------------------------------------------------------------
        //  Per-project anchor store
        // ------------------------------------------------------------------

        [Serializable]
        public class Entry
        {
            public string pointId;
            public string uuid;
            public string createdUtc;
            // Where it was made, relative to the room's reference anchor, so a later session
            // can tell how far it moved when that same reference anchor relocalised too.
            public string referenceUuid;
            public Vector3 referenceLocal;
        }

        [Serializable]
        public class Store
        {
            public string project;
            public List<Entry> anchors = new List<Entry>();
            // Replaced or undone anchors still to erase from the device (survives a crash).
            public List<string> pendingErase = new List<string>();

            public Entry Find(string pointId) => anchors.Find(a => a.pointId == pointId);
        }

        private static string StorePath(string projectName) =>
            Path.Combine(Folder, ProjectKey(projectName) + "-anchors.json");

        public static Store LoadStore(string projectName)
        {
            try
            {
                string path = StorePath(projectName);
                if (File.Exists(path))
                {
                    var s = JsonUtility.FromJson<Store>(File.ReadAllText(path));
                    if (s != null)
                    {
                        s.anchors ??= new List<Entry>();
                        s.pendingErase ??= new List<string>();
                        return s;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FieldTest] Anchor list unreadable: {e.Message}");
            }
            return new Store { project = projectName };
        }

        public static void SaveStore(Store store)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(StorePath(store.project), JsonUtility.ToJson(store, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FieldTest] Anchor list not saved: {e.Message}");
            }
        }
    }
}
