using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using QuestMarkerApp.API;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Terraweb2 sensors → an alignment project. Colours and icons mirror
    /// Server/terraweb_bridge.py; positions here are x/y/z only (see ToProject), while the PC
    /// bridge still falls back to the WGS84 map points.
    /// </summary>
    public static class TerrawebImport
    {
        // Plan-view families (terraweb2-frontend lib/marker.ts + index.css), matched on type.label
        // because the list API returns type_codename as null.
        private static readonly (Regex pattern, string color)[] Tones =
        {
            (new Regex(@"piezo|water.?level|pressure", RegexOptions.IgnoreCase), "#2bb6d6"),
            (new Regex(@"strain|crack|joint|extenso", RegexOptions.IgnoreCase), "#7c6cd6"),
            (new Regex(@"therm|temp", RegexOptions.IgnoreCase), "#db5a46"),
            (new Regex(@"inclin|incl\b|deflect", RegexOptions.IgnoreCase), "#e4a62b"),
            (new Regex(@"tilt|beam", RegexOptions.IgnoreCase), "#1991ae"),
        };
        private const string SurveyColor = "#5e9c24";
        private const string InactiveColor = "#9aa3a0";

        private static readonly (Regex pattern, string icon)[] Icons =
        {
            (new Regex(@"piezo", RegexOptions.IgnoreCase), "piezometer"),
            (new Regex(@"water.?level", RegexOptions.IgnoreCase), "water-level"),
            (new Regex(@"crack|joint", RegexOptions.IgnoreCase), "crack-meter"),
            (new Regex(@"strain|extenso", RegexOptions.IgnoreCase), "strain-gauge"),
            (new Regex(@"inclin|incl\b|deflect", RegexOptions.IgnoreCase), "inclinometer"),
            (new Regex(@"tilt|beam", RegexOptions.IgnoreCase), "tilt"),
            (new Regex(@"therm|temp", RegexOptions.IgnoreCase), "temperature"),
            (new Regex(@"pressure|load", RegexOptions.IgnoreCase), "pressure"),
            (new Regex(@"vibrat|seism", RegexOptions.IgnoreCase), "vibration"),
            (new Regex(@"borehole|extensometer", RegexOptions.IgnoreCase), "borehole"),
            (new Regex(@"displace", RegexOptions.IgnoreCase), "displacement"),
            (new Regex(@"3d|prism|target|survey|geodet", RegexOptions.IgnoreCase), "target"),
        };

        public static (string icon, string color) Style(string typeLabel, bool active)
        {
            typeLabel = typeLabel ?? "";
            string icon = "sensor-point";
            foreach (var (pattern, name) in Icons)
                if (pattern.IsMatch(typeLabel)) { icon = name; break; }
            if (!active) return (icon, InactiveColor);
            foreach (var (pattern, color) in Tones)
                if (pattern.IsMatch(typeLabel)) return (icon, color);
            return (icon, SurveyColor);
        }

        /// <summary>
        /// Sensors → project points at their x/y/z, in the project's own grid (often a
        /// surveyor's local one). The WGS84 point (sensor_geometry) is not used: it is often
        /// picked on a map (Konitsa: ~10 m off, up to 45 m) and its height is in another datum.
        /// Position, in order: the latest measured x/y/z (SensorHistory, the surveyed truth);
        /// else the sensor's own x/y/z from the list, when those are in the same grid as the
        /// measured ones (or nothing is measured); else the sensor is skipped and counted.
        /// </summary>
        public static ProjectRequest ToProject(string name, JArray sensors, bool includeInactive, out int skipped)
        {
            skipped = 0;
            var points = new List<ProjectPoint>();
            var listed = new List<(double x, double y, double z)?>(); // the sensor's own x/y/z, per point

            foreach (JToken s in sensors)
            {
                // One malformed record (null coordinate, wrong type) must never abort the import.
                try
                {
                    bool active = (bool?)s["is_active"] ?? true;
                    if (!active && !includeInactive) continue;

                    double? sx = Num(s["x"]), sy = Num(s["y"]), sz = Num(s["z"]);
                    string typeLabel = (string)s["type"]?["label"] ?? "";
                    var (icon, color) = Style(typeLabel, active);
                    // Never null: ids key the de-duplication dictionary below (a null key throws
                    // outside this try and aborted the whole import).
                    string sensorName = (string)s["sensor_name"] ?? (string)s["sensor_id"] ?? $"#{points.Count + 1}";
                    var point = new ProjectPoint
                    {
                        id = sensorName,
                        e = double.NaN,
                        n = double.NaN,
                        h = double.NaN,
                        label = sensorName,
                        icon = icon,
                        color = color,
                        sensorId = (string)s["sensor_id"],
                        typeId = (int?)s["type"]?["type_id"] ?? 0,
                        typeLabel = typeLabel,
                        alertState = (string)s["current_alert_state"] ?? "none",
                        latestDate = (string)s["latest_date"],
                        notes = (string)s["notes"],
                        inactive = !active
                    };
                    // Added together so listed[i] always belongs to points[i].
                    listed.Add(sx.HasValue && sy.HasValue && sz.HasValue ? (sx.Value, sy.Value, sz.Value) : ((double, double, double)?)null);
                    points.Add(point);
                }
                catch (Exception ex)
                {
                    skipped++;
                    string id = null;
                    try { id = (string)s?["sensor_id"]; } catch { }
                    UnityEngine.Debug.LogWarning($"[Terraweb] Sensor '{id ?? "?"}' skipped: {ex.Message}");
                }
            }

            int measured = 0, fromList = 0;
            var measuredAt = new SensorHistory.Sample?[points.Count];
            for (int i = 0; i < points.Count; i++)
                if (SensorHistory.TryLatestPosition(points[i].sensorId, out SensorHistory.Sample m) && m.Z.HasValue)
                {
                    measuredAt[i] = m;
                    measured++;
                }
            bool listUsable = ListMatchesMeasured(listed, measuredAt);
            var kept = new List<ProjectPoint>();
            for (int i = 0; i < points.Count; i++)
            {
                ProjectPoint pt = points[i];
                if (measuredAt[i].HasValue)
                {
                    pt.e = measuredAt[i].Value.X.Value;
                    pt.n = measuredAt[i].Value.Y.Value;
                    pt.h = measuredAt[i].Value.Z.Value;
                }
                else if (listUsable && listed[i].HasValue)
                {
                    (pt.e, pt.n, pt.h) = listed[i].Value;
                    fromList++;
                }
                else
                {
                    skipped++;
                    continue;
                }
                kept.Add(pt);
            }
            points = kept;
            UnityEngine.Debug.Log($"[Terraweb] Placed by x/y/z: {measured} measured, {fromList} from the sensor list, " +
                                  $"{skipped} without x/y/z skipped" + (listUsable ? "" : " (list x/y/z in another grid, not used)"));

            // Placements and markers are keyed by id: two sensors with one name would share a
            // control point and a marker. Keep names readable, make ids unique.
            // The suffix is the sensor's own id, so it does not depend on the order the API
            // returned them in (a saved control point must restore onto the same sensor).
            var seen = new Dictionary<string, int>();
            foreach (ProjectPoint pt in points)
            {
                seen.TryGetValue(pt.id, out int count);
                seen[pt.id] = count + 1;
            }
            foreach (ProjectPoint pt in points)
                if (seen[pt.id] > 1) pt.id = $"{pt.id} #{pt.sensorId}";
            points.Sort((a, b) => NaturalCompare(a.id, b.id));
            return new ProjectRequest { name = name, crs = measured > 0 ? "survey (measured)" : "project", points = points.ToArray() };
        }

        private static double? Num(JToken t) =>
            t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer) ? (double?)null : (double)t;

        /// <summary>
        /// Whether the sensors' own x/y/z are in the grid of their measurements. The backend
        /// derives them from the map point through the project's projection (Web Mercator when
        /// none was set), which need not be the surveyor's grid: then they would put the
        /// unmeasured sensors somewhere else entirely. Usable when nothing is measured (they are
        /// all there is), or when they agree with the measured positions to within 25 m (a
        /// map-picked point's error, not a different grid).
        /// </summary>
        private static bool ListMatchesMeasured(List<(double x, double y, double z)?> listed, SensorHistory.Sample?[] measured)
        {
            var gaps = new List<double>();
            for (int i = 0; i < listed.Count; i++)
                if (listed[i].HasValue && measured[i].HasValue)
                {
                    double dx = listed[i].Value.x - measured[i].Value.X.Value;
                    double dy = listed[i].Value.y - measured[i].Value.Y.Value;
                    gaps.Add(Math.Sqrt(dx * dx + dy * dy));
                }
            bool anyMeasured = false;
            foreach (var m in measured) if (m.HasValue) { anyMeasured = true; break; }
            if (!anyMeasured) return true;
            if (gaps.Count == 0) return false;
            gaps.Sort();
            return gaps[gaps.Count / 2] < 25.0;
        }

        // WGS84 ellipsoid → ECEF → local East/North/Up (exact; same as the Python bridge).
        private const double A = 6378137.0;
        private const double F = 1 / 298.257223563;
        private const double E2 = F * (2 - F);

        private static void Ecef(double latDeg, double lonDeg, double h, out double x, out double y, out double z)
        {
            double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
            double n = A / Math.Sqrt(1 - E2 * Math.Sin(lat) * Math.Sin(lat));
            x = (n + h) * Math.Cos(lat) * Math.Cos(lon);
            y = (n + h) * Math.Cos(lat) * Math.Sin(lon);
            z = (n * (1 - E2) + h) * Math.Sin(lat);
        }

        public static void Enu(double lat, double lon, double h, double lat0, double lon0, double h0,
            out double east, out double north)
            => Enu(lat, lon, h, lat0, lon0, h0, out east, out north, out _);

        /// <summary>Exact WGS84 → local East/North/Up (tangent plane at the origin), any latitude / longitude.</summary>
        public static void Enu(double lat, double lon, double h, double lat0, double lon0, double h0,
            out double east, out double north, out double up)
        {
            Ecef(lat, lon, h, out double x, out double y, out double z);
            Ecef(lat0, lon0, h0, out double x0, out double y0, out double z0);
            double dx = x - x0, dy = y - y0, dz = z - z0;
            double la = lat0 * Math.PI / 180, lo = lon0 * Math.PI / 180;
            east = -Math.Sin(lo) * dx + Math.Cos(lo) * dy;
            north = -Math.Sin(la) * Math.Cos(lo) * dx - Math.Sin(la) * Math.Sin(lo) * dy + Math.Cos(la) * dz;
            up = Math.Cos(la) * Math.Cos(lo) * dx + Math.Cos(la) * Math.Sin(lo) * dy + Math.Sin(la) * dz;
        }

        /// <summary>"K2" before "K10".</summary>
        public static int NaturalCompare(string a, string b)
        {
            a = a ?? ""; b = b ?? "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
