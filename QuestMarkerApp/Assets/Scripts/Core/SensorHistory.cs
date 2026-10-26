using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Survey history of the loaded project's 3D-point sensors, fetched once at load: each
    /// reading's measured x/y/z (in the project's survey grid) and its dx/dy/dz (mm, against
    /// the sensor's zero reading). Placement uses the latest x/y/z — survey-grade, unlike the
    /// map-picked WGS84 point — and the displacement arrows / time machine replay dx/dy/dz.
    /// In memory only: a project restored from disk has no history until it is re-loaded.
    /// </summary>
    public static class SensorHistory
    {
        public struct Sample
        {
            public DateTime Date;
            public double? X, Y, Z;
            /// <summary>Displacement from the zero reading, millimetres, survey-grid axes.</summary>
            public double? Dx, Dy, Dz;
            public bool HasDisplacement => Dx.HasValue && Dy.HasValue;
            public bool HasPosition => X.HasValue && Y.HasValue && Z.HasValue;
        }

        private static readonly Dictionary<string, List<Sample>> series = new Dictionary<string, List<Sample>>();

        /// <summary>Terraweb project the history belongs to (0 = none).</summary>
        public static int ProjectId { get; private set; }
        public static int Count => series.Count;
        public static DateTime First { get; private set; }
        public static DateTime Last { get; private set; }
        /// <summary>Any sensor with at least one dx/dy reading — arrows and time machine possible.</summary>
        public static bool HasDisplacements { get; private set; }

        public static void Clear(int projectId)
        {
            series.Clear();
            ProjectId = projectId;
            HasDisplacements = false;
            First = DateTime.MaxValue;
            Last = DateTime.MinValue;
        }

        /// <summary>Oldest first; null when the sensor has no history.</summary>
        public static IReadOnlyList<Sample> Of(string sensorId) =>
            sensorId != null && series.TryGetValue(sensorId, out List<Sample> s) ? s : null;

        /// <summary>The newest reading that carries a full measured position.</summary>
        public static bool TryLatestPosition(string sensorId, out Sample sample)
        {
            sample = default;
            var s = Of(sensorId);
            if (s == null) return false;
            for (int i = s.Count - 1; i >= 0; i--)
                if (s[i].HasPosition) { sample = s[i]; return true; }
            return false;
        }

        /// <summary>
        /// The reading in force at <paramref name="at"/> (the newest one not after it) that has a
        /// displacement; false before the first one.
        /// </summary>
        public static bool TryDisplacementAt(string sensorId, DateTime at, out Sample sample)
        {
            sample = default;
            var s = Of(sensorId);
            if (s == null) return false;
            int lo = 0, hi = s.Count - 1, found = -1;
            while (lo <= hi) // last index with Date <= at
            {
                int mid = (lo + hi) / 2;
                if (s[mid].Date <= at) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            for (int i = found; i >= 0; i--)
                if (s[i].HasDisplacement) { sample = s[i]; return true; }
            return false;
        }

        /// <summary>Store one sensor's measurements as the API returned them (any order).</summary>
        public static void Add(string sensorId, JArray measurements)
        {
            if (string.IsNullOrEmpty(sensorId) || measurements == null) return;
            var list = new List<Sample>();
            foreach (JToken m in measurements)
            {
                // Indexing a non-object token (null, number) throws and would lose the whole sensor.
                if (!(m is JObject)) continue;
                // A rejected reading is not part of the record.
                if (!((bool?)m["is_enabled"] ?? true)) continue;
                if (!DateTime.TryParse((string)m["meas_date"], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime date)) continue;
                var v = m["values"] as JObject;
                if (v == null) continue;
                var sample = new Sample
                {
                    Date = date,
                    X = Num(v["x"]), Y = Num(v["y"]), Z = Num(v["z"]),
                    Dx = Num(v["dx"]), Dy = Num(v["dy"]), Dz = Num(v["dz"])
                };
                if (!sample.HasPosition && !sample.HasDisplacement) continue;
                list.Add(sample);
            }
            if (list.Count == 0) return;
            list.Sort((a, b) => a.Date.CompareTo(b.Date));
            series[sensorId] = list;
            foreach (Sample s in list)
            {
                if (!s.HasDisplacement) continue;
                HasDisplacements = true;
                if (s.Date < First) First = s.Date;
                if (s.Date > Last) Last = s.Date;
            }
        }

        private static double? Num(JToken t)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    double d = (double)t;
                    return double.IsNaN(d) || double.IsInfinity(d) ? (double?)null : d;
                case JTokenType.String:
                    return double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                        ? p : (double?)null;
                default:
                    return null;
            }
        }
    }
}
