using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Alert limits of the loaded project's sensors and how close each sensor's latest reading
    /// is to them (feature 10), fetched with the sensors. The arithmetic is in
    /// <see cref="LimitMath"/>; this class only loads, parses and keeps the results.
    ///
    /// A load costs: 1 request for every current limit (GET sensors/limits/?is_active=true),
    /// 0 for 3D points whose limited fields the survey history already holds, 1 for the latest
    /// readings of the rest of the area (measurements/?area=…&amp;date_from=…, newest first), and one
    /// small request per sensor the window missed, at most <see cref="MaxFallback"/>.
    ///
    /// The tier shown is never computed here: it stays the server's (ProjectPoint.alertState,
    /// kept fresh by LiveAlerts). In memory only, like <see cref="SensorHistory"/>.
    /// </summary>
    public static class SensorLimits
    {
        public enum Status { NotLoaded, Loading, Ready, NoPermission, Failed }

        /// <summary>Readings older than this before the area's newest one are fetched one by one.</summary>
        private const int WindowDays = 14;
        private const int PageSize = 5000;
        private const int MaxFallback = 40;
        private const int Parallel = 6;

        // Fields SensorHistory keeps for 3D points: limits on these need no extra request.
        private static readonly HashSet<string> HistoryFields = new HashSet<string> { "x", "y", "z", "dx", "dy", "dz" };

        public sealed class Entry
        {
            public string SensorId;
            public int TypeId;
            public LimitMath.LimitSet Limits;
            public DateTime? ValidFrom;
            /// <summary>Null until a reading of a limited field was found.</summary>
            public LimitMath.SensorResult Result;
            public DateTime? ReadingDate;
            /// <summary>
            /// A newer reading was rejected (is_enabled false) and skipped here. The backend's
            /// classifier does not skip rejected readings, so its tier may come from that one.
            /// </summary>
            public bool NewerRejected;
        }

        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        // Field units per sensor type (from the type the sensor import already fetched).
        private static readonly Dictionary<int, Dictionary<string, string>> units = new Dictionary<int, Dictionary<string, string>>();
        // Sensors whose disagreement with the server was already logged in this load.
        private static readonly HashSet<string> loggedDisagreements = new HashSet<string>();

        public static Status State { get; private set; } = Status.NotLoaded;
        /// <summary>Terraweb project the limits belong to (0 = none).</summary>
        public static int ProjectId { get; private set; }
        /// <summary>Bumped on every change, for views that rebuild on it.</summary>
        public static int Version { get; private set; }
        public static string Error { get; private set; }
        /// <summary>Sensors with a current limit.</summary>
        public static int LimitCount => entries.Count;
        /// <summary>Sensors with a limit and a measurable U.</summary>
        public static int MeasuredCount { get; private set; }
        /// <summary>Requests the last load made (limits + readings), for the log.</summary>
        public static int Requests { get; private set; }

        public static void Clear(int projectId)
        {
            entries.Clear();
            units.Clear();
            loggedDisagreements.Clear();
            ProjectId = projectId;
            State = Status.NotLoaded;
            Error = null;
            MeasuredCount = 0;
            Version++;
        }

        public static bool TryGet(string sensorId, out Entry entry)
        {
            entry = null;
            return sensorId != null && entries.TryGetValue(sensorId, out entry);
        }

        /// <summary>The field's unit from its sensor type ("" when unknown — never assume mm).</summary>
        public static string Unit(int typeId, string field) =>
            field != null && units.TryGetValue(typeId, out var map) && map.TryGetValue(field, out string u) ? u ?? "" : "";

        /// <summary>
        /// Why limits can't be shown for <paramref name="projectId"/>, or null when they can.
        /// </summary>
        public static string Unavailable(int projectId)
        {
            if (projectId == 0) return L.T("Χωρίς έργο", "No project");
            if (ProjectId != projectId || State == Status.NotLoaded) return L.T("Φόρτωσε ξανά το έργο", "Reload the project");
            switch (State)
            {
                case Status.Loading: return L.T("Φόρτωση…", "Loading…");
                case Status.NoPermission: return L.T("Χωρίς δικαίωμα ορίων", "No permission for limits");
                case Status.Failed: return L.T("Σφάλμα ορίων", "Limits failed");
            }
            return entries.Count == 0 ? L.T("Χωρίς όρια", "No limits") : null;
        }

        /// <summary>
        /// The headset finds U ≥ 100 % but the server's tier is lower: shown as "?", logged once.
        /// The server's tier is never overridden.
        /// </summary>
        public static bool Disagrees(Entry e, string serverState)
        {
            LimitMath.SensorResult r = e?.Result;
            if (r?.U == null || r.U.Value < 1.0 || LimitMath.Rank(serverState) >= r.Reached) return false;
            // The server judged a different reading or limit: not a disagreement. It classifies
            // rejected readings too, and by the limit in force at the reading's date, while the
            // headset scores the newest enabled reading against the current limit.
            if (e.NewerRejected) return false;
            if (e.ValidFrom.HasValue && e.ReadingDate.HasValue && e.ValidFrom.Value > e.ReadingDate.Value) return false;
            if (loggedDisagreements.Add(e.SensorId))
                Debug.LogWarning($"[Limits] Sensor {e.SensorId}: {r.Driving.Field} = {r.Driving.Value} is {r.U.Value:P0} of its " +
                                 $"{LimitMath.TierNames[r.Driving.TopTier]} limit, but the server says '{serverState ?? "none"}' " +
                                 "(other zero reading, stale reading or pending evaluation?)");
            return true;
        }

        /// <summary>The highest-U sensors first, at most <paramref name="max"/>.</summary>
        public static void Ranked(List<Entry> into, int max)
        {
            into.Clear();
            foreach (Entry e in entries.Values)
                if (e.Result?.U != null) into.Add(e);
            into.Sort((a, b) => b.Result.U.Value.CompareTo(a.Result.U.Value));
            if (into.Count > max) into.RemoveRange(max, into.Count - max);
        }

        // ------------------------------------------------------------------
        //  Load
        // ------------------------------------------------------------------

        private struct SensorInfo
        {
            public int TypeId;
            public DateTime? Latest;
        }

        /// <summary>
        /// Limits and latest values for the sensors just fetched. Never fails the project load:
        /// no permission or an error leaves <see cref="State"/> saying so and the views off.
        /// <paramref name="live"/> turns false when the user moved on; the load then stops.
        /// </summary>
        public static IEnumerator Load(TerrawebClient client, MonoBehaviour runner, int projectId, JArray sensors,
            string areaId, Func<bool> live)
        {
            Clear(projectId);
            State = Status.Loading;
            Requests = 1;

            var info = new Dictionary<string, SensorInfo>();
            foreach (JToken s in sensors)
            {
                if (!(s is JObject) || !((bool?)s["is_active"] ?? true)) continue;
                string id = (string)s["sensor_id"];
                if (string.IsNullOrEmpty(id)) continue;
                info[id] = new SensorInfo { TypeId = (int?)s["type"]?["type_id"] ?? 0, Latest = Date(s["latest_date"]) };
            }

            JArray limits = null;
            bool denied = false;
            string error = null;
            yield return client.GetLimits(a => limits = a, () => denied = true, e => error = e);
            if (!live()) yield break;
            if (denied)
            {
                State = Status.NoPermission;
                Version++;
                Debug.Log("[Limits] No CanViewLimits permission: limit views stay off");
                yield break;
            }
            if (error != null)
            {
                State = Status.Failed;
                Error = error;
                Version++;
                Debug.LogWarning("[Limits] Load failed: " + error);
                yield break;
            }

            foreach (JToken t in limits)
            {
                var l = t as JObject;
                if (l == null || !((bool?)l["is_current"] ?? false)) continue;
                string id = (string)l["sensor"];
                if (id == null || !info.TryGetValue(id, out SensorInfo si)) continue;
                LimitMath.LimitSet set = Parse(l["limit_data"] as JObject);
                if (set.IsEmpty) continue;
                entries[id] = new Entry { SensorId = id, TypeId = si.TypeId, Limits = set, ValidFrom = Date(l["valid_from"]) };
            }

            // Units of the limited fields: the import fetched these types already (cached).
            foreach (Entry e in entries.Values)
            {
                if (e.TypeId <= 0 || units.ContainsKey(e.TypeId)) continue;
                var map = new Dictionary<string, string>();
                units[e.TypeId] = map;
                yield return client.GetSensorType(e.TypeId, type =>
                {
                    if (type?["fields"] is JArray fields)
                        foreach (JToken f in fields)
                            if ((string)f["codename"] is string code) map[code] = (string)f["unit"] ?? "";
                }, _ => { });
                if (!live()) yield break;
            }

            // Latest values: the survey history first, then one area request, then one by one.
            var need = new List<Entry>();
            foreach (Entry e in entries.Values)
                if (!FromHistory(e, projectId) && info[e.SensorId].Latest.HasValue) need.Add(e);

            if (need.Count > 0)
            {
                DateTime newest = DateTime.MinValue, oldest = DateTime.MaxValue;
                foreach (Entry e in need)
                {
                    DateTime d = info[e.SensorId].Latest.Value;
                    if (d > newest) newest = d;
                    if (d < oldest) oldest = d;
                }
                DateTime from = oldest > newest.AddDays(-WindowDays) ? oldest : newest.AddDays(-WindowDays);
                JArray rows = null;
                Requests++;
                yield return client.GetRecentMeasurements(areaId,
                    from.AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), PageSize,
                    (list, count) => rows = list, e => Debug.LogWarning("[Limits] Latest readings failed: " + e));
                if (!live()) yield break;
                if (rows != null) Apply(rows, need);
            }

            var missing = need.FindAll(e => e.Result == null);
            if (missing.Count > MaxFallback)
            {
                Debug.Log($"[Limits] {missing.Count} sensors outside the readings window — only {MaxFallback} fetched one by one");
                missing.RemoveRange(MaxFallback, missing.Count - MaxFallback);
            }
            int next = 0, done = 0, running = 0;
            while (done < missing.Count)
            {
                while (running < Parallel && next < missing.Count)
                {
                    Entry e = missing[next++];
                    running++;
                    Requests++;
                    // A survey's depths are several rows of one date: ask for enough of them.
                    int depth = e.Limits.Bands.Count > 0 ? 100 : 3;
                    runner.StartCoroutine(client.GetMeasurements(e.SensorId, depth,
                        list =>
                        {
                            try { if (live()) Apply(list, new List<Entry> { e }); }
                            finally { running--; done++; }
                        },
                        _ => { running--; done++; }));
                }
                yield return null;
                if (!live()) yield break;
            }

            int measured = 0;
            foreach (Entry e in entries.Values)
                if (e.Result?.U != null) measured++;
            MeasuredCount = measured;
            State = Status.Ready;
            Version++;
            Debug.Log($"[Limits] {entries.Count} sensors with a current limit, {measured} measurable, {Requests} requests");
        }

        // A 3D point's history holds x/y/z/dx/dy/dz of its newest reading: enough when the limit
        // names nothing else (and has no depth bands).
        private static bool FromHistory(Entry e, int projectId)
        {
            if (SensorHistory.ProjectId != projectId || e.Limits.Bands.Count > 0) return false;
            foreach (string field in e.Limits.Fields.Keys)
                if (!HistoryFields.Contains(field)) return false;
            IReadOnlyList<SensorHistory.Sample> series = SensorHistory.Of(e.SensorId);
            if (series == null || series.Count == 0) return false;
            SensorHistory.Sample s = series[series.Count - 1];
            var r = new LimitMath.Reading();
            void Put(string k, double? v) { if (v.HasValue) r.Values[k] = v.Value; }
            Put("x", s.X); Put("y", s.Y); Put("z", s.Z); Put("dx", s.Dx); Put("dy", s.Dy); Put("dz", s.Dz);
            e.Result = LimitMath.Evaluate(e.Limits, new List<LimitMath.Reading> { r });
            e.ReadingDate = s.Date;
            return true;
        }

        // Newest-first rows of many sensors: each wanted sensor takes its first enabled row,
        // or every row of that row's survey (a vertical sensor's depths).
        private static void Apply(JArray rows, List<Entry> wanted)
        {
            var want = new Dictionary<string, Entry>();
            foreach (Entry e in wanted) want[e.SensorId] = e;
            var picked = new Dictionary<string, (string group, DateTime? date, List<LimitMath.Reading> readings)>();
            var rejectedFirst = new HashSet<string>();
            foreach (JToken t in rows)
            {
                var m = t as JObject;
                string id = (string)m?["sensor"];
                if (id == null || !want.ContainsKey(id)) continue;
                if (!IsEnabled(m))
                {
                    if (!picked.ContainsKey(id)) rejectedFirst.Add(id);
                    continue;
                }
                string group = m["group"] == null || m["group"].Type == JTokenType.Null ? null : m["group"].ToString();
                if (picked.TryGetValue(id, out var have))
                {
                    if (have.group != null && have.group == group) have.readings.Add(ToReading(m, true));
                    continue;
                }
                picked[id] = (group, Date(m["meas_date"]), new List<LimitMath.Reading> { ToReading(m, group != null) });
            }
            foreach (var kv in picked)
            {
                Entry e = want[kv.Key];
                e.Result = LimitMath.Evaluate(e.Limits, kv.Value.readings);
                e.ReadingDate = kv.Value.date;
                e.NewerRejected = rejectedFirst.Contains(kv.Key);
            }
        }

        // ------------------------------------------------------------------
        //  Parsing
        // ------------------------------------------------------------------

        private static readonly string[] TierKeys = { null, "reference", "alert", "alarm" };

        /// <summary>
        /// limit_data as sensors/serializers/limit.py validates it:
        /// <c>{codename: {tier: {min, max}}, "bands": [{from, to | values, codename: {...}}]}</c>.
        /// </summary>
        public static LimitMath.LimitSet Parse(JObject data)
        {
            var set = new LimitMath.LimitSet();
            if (data == null) return set;
            foreach (var kv in data)
            {
                if (kv.Key == "bands")
                {
                    if (kv.Value is JArray bands)
                        foreach (JToken b in bands)
                            if (b is JObject band) set.Bands.Add(ParseBand(band));
                    continue;
                }
                if (kv.Value is JObject tiers) set.Fields[kv.Key] = ParseField(tiers);
            }
            return set;
        }

        private static LimitMath.FieldLimit ParseField(JObject tiers)
        {
            var f = new LimitMath.FieldLimit();
            for (int t = LimitMath.Reference; t <= LimitMath.Alarm; t++)
            {
                if (!(tiers[TierKeys[t]] is JObject bounds)) continue;
                f.Tiers[t].Min = Number(bounds["min"], false);
                f.Tiers[t].Max = Number(bounds["max"], false);
            }
            return f;
        }

        private static LimitMath.Band ParseBand(JObject b)
        {
            var band = new LimitMath.Band { From = Number(b["from"], false), To = Number(b["to"], false) };
            if (b.ContainsKey("values"))
            {
                band.Values = new List<double>();
                if (b["values"] is JArray values)
                    foreach (JToken v in values)
                        if (Number(v, false) is double d) band.Values.Add(d);
            }
            foreach (var kv in b)
                if (kv.Key != "from" && kv.Key != "to" && kv.Key != "values" && kv.Value is JObject tiers)
                    band.Fields[kv.Key] = ParseField(tiers);
            return band;
        }

        /// <summary>
        /// The readings of the newest enabled measurement in a newest-first list of one sensor:
        /// one row, or every depth of its survey. Shared with the sensor card.
        /// </summary>
        public static List<LimitMath.Reading> LatestReadings(JArray newestFirst, out DateTime? date)
        {
            var readings = new List<LimitMath.Reading>();
            date = null;
            string group = null;
            bool first = true;
            if (newestFirst == null) return readings;
            foreach (JToken t in newestFirst)
            {
                var m = t as JObject;
                if (m == null || !IsEnabled(m)) continue;
                string g = m["group"] == null || m["group"].Type == JTokenType.Null ? null : m["group"].ToString();
                if (first)
                {
                    first = false;
                    group = g;
                    date = Date(m["meas_date"]);
                    readings.Add(ToReading(m, g != null));
                    if (g == null) break;
                }
                else if (g == group) readings.Add(ToReading(m, true));
            }
            return readings;
        }

        // A rejected reading (or a depth of a rejected survey) is not part of the record.
        private static bool IsEnabled(JObject m) =>
            ((bool?)m["is_enabled"] ?? true) && ((bool?)m["group_enabled"] ?? true);

        // Only a survey's depths carry the band index (the backend's index_field is None otherwise).
        private static LimitMath.Reading ToReading(JObject m, bool vertical)
        {
            var r = new LimitMath.Reading();
            if (m["values"] is JObject values)
                foreach (var kv in values)
                    if (Number(kv.Value, true) is double d) r.Values[kv.Key] = d;
            if (vertical) r.Index = Number(m["vertical"], true);
            return r;
        }

        // The backend's _is_number: int or float, never bool. Readings may also arrive as numeric
        // strings (as SensorHistory accepts); thresholds may not — the evaluator would not compare them.
        private static double? Number(JToken t, bool allowString)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    double d = (double)t;
                    return double.IsNaN(d) || double.IsInfinity(d) ? (double?)null : d;
                case JTokenType.String when allowString:
                    return double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                        ? p : (double?)null;
                default:
                    return null;
            }
        }

        private static DateTime? Date(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            // JToken.Parse turns ISO strings into dates already, in local time when they carry an offset.
            if (t.Type == JTokenType.Date)
            {
                var d0 = (DateTime)t;
                return d0.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d0, DateTimeKind.Utc) : d0.ToUniversalTime();
            }
            string s = (string)t;
            return !string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime d) ? d : (DateTime?)null;
        }
    }
}
