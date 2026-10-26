using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using QuestMarkerApp.API;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// Terraweb2 sensors → an alignment project. Mirrors Server/terraweb_bridge.py so the
    /// headset and the PC bridge place, colour and label sensors identically.
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
        /// Sensors with a 3D WGS84 point → project points in local East/North metres around
        /// the first sensor, with absolute elevation. Returns how many were skipped.
        /// </summary>
        public static ProjectRequest ToProject(string name, JArray sensors, bool includeInactive, out int skipped)
        {
            skipped = 0;
            var points = new List<ProjectPoint>();
            var survey = new List<(double x, double y)?>(); // the project-CRS pair, per point
            double lat0 = 0, lon0 = 0, h0 = 0;
            bool haveOrigin = false;

            foreach (JToken s in sensors)
            {
                // One malformed record (null coordinate, wrong type) must never abort the import.
                try
                {
                    bool active = (bool?)s["is_active"] ?? true;
                    if (!active && !includeInactive) continue;

                    JArray coords = s["sensor_geometry"]?["coordinates"] as JArray;
                    if ((string)s["sensor_geometry"]?["type"] != "Point" || coords == null || coords.Count < 3
                        || coords[0] == null || coords[0].Type == JTokenType.Null
                        || coords[1] == null || coords[1].Type == JTokenType.Null
                        || coords[2] == null || coords[2].Type == JTokenType.Null)
                    {
                        skipped++;
                        continue;
                    }

                    double lon = (double)coords[0], lat = (double)coords[1], h = (double)coords[2];
                    double? sx = (double?)s["x"], sy = (double?)s["y"];
                    if (!haveOrigin) { lat0 = lat; lon0 = lon; h0 = h; haveOrigin = true; }
                    Enu(lat, lon, h, lat0, lon0, h0, out double e, out double n);

                    string typeLabel = (string)s["type"]?["label"] ?? "";
                    var (icon, color) = Style(typeLabel, active);
                    // Never null: ids key the de-duplication dictionary below (a null key throws
                    // outside this try and aborted the whole import).
                    string sensorName = (string)s["sensor_name"] ?? (string)s["sensor_id"] ?? $"#{points.Count + 1}";
                    var point = new ProjectPoint
                    {
                        id = sensorName,
                        e = e,
                        n = n,
                        h = h,
                        label = sensorName,
                        icon = icon,
                        color = color,
                        sensorId = (string)s["sensor_id"],
                        typeId = (int?)s["type"]?["type_id"] ?? 0,
                        typeLabel = typeLabel,
                        alertState = (string)s["current_alert_state"] ?? "none",
                        latestDate = (string)s["latest_date"],
                        notes = (string)s["notes"],
                        inactive = !active,
                        hasGeo = true,
                        lat = lat,
                        lon = lon,
                        alt = h
                    };
                    // Added together so survey[i] always belongs to points[i].
                    survey.Add(sx.HasValue && sy.HasValue ? (sx.Value, sy.Value) : ((double, double)?)null);
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

            string crs = "WGS84->ENU";
            if (UseMeasuredPositions(points))
            {
                crs = "survey (measured)";
            }
            else if (UseSurveyCoordinates(points, survey))
            {
                // Same frame the surveyors measure control points in; z is already that height.
                for (int i = 0; i < points.Count; i++)
                {
                    points[i].e = survey[i].Value.x;
                    points[i].n = survey[i].Value.y;
                }
                crs = "project";
            }

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
            return new ProjectRequest { name = name, crs = crs, points = points.ToArray() };
        }

        /// <summary>
        /// Place sensors at their latest measured x/y/z (SensorHistory) — the surveyed truth,
        /// in the same grid the control points are measured in. The WGS84 points are often
        /// picked on a map (Konitsa: ~12 m off). Sensors without a measured position are put
        /// on the grid through a best-fit of the others' WGS84 → grid, and flagged approximate.
        /// </summary>
        private static bool UseMeasuredPositions(List<ProjectPoint> points)
        {
            var measured = new SensorHistory.Sample?[points.Count];
            int count = 0;
            for (int i = 0; i < points.Count; i++)
                if (SensorHistory.TryLatestPosition(points[i].sensorId, out SensorHistory.Sample s))
                {
                    measured[i] = s;
                    count++;
                }
            if (count < 2) return false;

            // A grid with X = North (or N,E column order) is the WGS84 layout mirrored; a
            // rotation-only alignment cannot undo that, so exchange X and Y here.
            var check = new List<(double, double, double, double)>();
            for (int i = 0; i < points.Count; i++)
                if (measured[i].HasValue)
                    check.Add((points[i].e, points[i].n, measured[i].Value.X.Value, measured[i].Value.Y.Value));
            if (Mirrored(check))
            {
                UnityEngine.Debug.LogWarning("[Terraweb] Measured X/Y are mirrored against the map points — treating X as North, Y as East");
                for (int i = 0; i < points.Count; i++)
                    if (measured[i].HasValue)
                    {
                        SensorHistory.Sample m = measured[i].Value;
                        (m.X, m.Y) = (m.Y, m.X);
                        measured[i] = m;
                    }
            }

            // Similarity fit (complex numbers: grid = a·(enu − pc) + qc) for the unmeasured ones.
            double pcx = 0, pcy = 0, qcx = 0, qcy = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (!measured[i].HasValue) continue;
                pcx += points[i].e; pcy += points[i].n;
                qcx += measured[i].Value.X.Value; qcy += measured[i].Value.Y.Value;
            }
            pcx /= count; pcy /= count; qcx /= count; qcy /= count;
            double nr = 0, ni = 0, den = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (!measured[i].HasValue) continue;
                double px = points[i].e - pcx, py = points[i].n - pcy;
                double qx = measured[i].Value.X.Value - qcx, qy = measured[i].Value.Y.Value - qcy;
                nr += qx * px + qy * py; // (q)·conj(p)
                ni += qy * px - qx * py;
                den += px * px + py * py;
            }
            double ar = den > 0 ? nr / den : 1, ai = den > 0 ? ni / den : 0;

            // The WGS84 geometry height and the surveyed Z are different datums (ellipsoidal vs
            // orthometric / local): lift the unmeasured ones by the median offset between the
            // two over the measured sensors, so they sit level with their neighbours.
            var dh = new List<double>();
            for (int i = 0; i < points.Count; i++)
                if (measured[i].HasValue && measured[i].Value.Z.HasValue)
                    dh.Add(measured[i].Value.Z.Value - points[i].h);
            double hOffset = 0;
            if (dh.Count >= 1)
            {
                dh.Sort();
                hOffset = dh.Count % 2 == 1 ? dh[dh.Count / 2] : 0.5 * (dh[dh.Count / 2 - 1] + dh[dh.Count / 2]);
            }

            int approx = 0;
            for (int i = 0; i < points.Count; i++)
            {
                ProjectPoint pt = points[i];
                if (measured[i].HasValue)
                {
                    pt.e = measured[i].Value.X.Value;
                    pt.n = measured[i].Value.Y.Value;
                    pt.h = measured[i].Value.Z.Value;
                    continue;
                }
                double px = pt.e - pcx, py = pt.n - pcy;
                pt.e = qcx + ar * px - ai * py;
                pt.n = qcy + ai * px + ar * py;
                pt.h += hOffset;
                pt.approx = true;
                approx++;
            }
            UnityEngine.Debug.Log($"[Terraweb] Placed by measured survey position: {count}, approximate: {approx} " +
                                  $"(map points fit the grid at scale {Math.Sqrt(ar * ar + ai * ai):F3}, height offset {hOffset:F2} m)");
            return true;
        }

        /// <summary>
        /// Whether to place sensors by the project's own survey x/y instead of WGS84.
        /// The API doesn't say which CRS a project has, so the data decides: every sensor
        /// must carry x/y, and distances in x/y must match true ground distances (a real
        /// survey CRS such as EGSA87 is within ~0.1 %). That rules out a project with no
        /// projection set, whose x/y are the backend's Web Mercator default — stretched by
        /// 1/cos(latitude), ~30 % in Greece — and a custom local proj4 that is off in scale.
        /// </summary>
        private static bool UseSurveyCoordinates(List<ProjectPoint> points, List<(double x, double y)?> survey)
        {
            if (points.Count < 2) return false;
            foreach (var xy in survey) if (!xy.HasValue) return false;

            // Mirrored layout: X/Y in N,E order (a grid with X = North). Distances cannot tell,
            // so compare against the map geometry and exchange them (see Mirrored).
            var check = new List<(double, double, double, double)>();
            for (int i = 0; i < points.Count; i++)
                check.Add((points[i].e, points[i].n, survey[i].Value.x, survey[i].Value.y));
            if (Mirrored(check))
            {
                UnityEngine.Debug.LogWarning("[Terraweb] Survey x/y are mirrored against the map points — treating x as North, y as East");
                for (int i = 0; i < survey.Count; i++) survey[i] = (survey[i].Value.y, survey[i].Value.x);
            }

            var ratios = new List<double>();
            for (int i = 1; i < points.Count; i++)
            {
                double ground = Math.Sqrt(Sq(points[i].e - points[0].e) + Sq(points[i].n - points[0].n));
                if (ground < 5) continue; // too short to judge against coordinate rounding
                double grid = Math.Sqrt(Sq(survey[i].Value.x - survey[0].Value.x) + Sq(survey[i].Value.y - survey[0].Value.y));
                ratios.Add(grid / ground);
            }
            if (ratios.Count == 0) return false;
            ratios.Sort();
            double scale = ratios[ratios.Count / 2];
            bool real = Math.Abs(scale - 1) < 0.003;
            UnityEngine.Debug.Log($"[Terraweb] survey x/y scale {scale:F5} → {(real ? "using project CRS" : "using WGS84")}");
            return real;
        }

        private static double Sq(double v) => v * v;

        /// <summary>
        /// True when grid ≈ similarity(mirror(enu)) fits clearly better than grid ≈ similarity(enu):
        /// the grid's two axes are in the opposite order (N,E) to the East/North the app expects.
        /// Needs 3+ points spread over 5 m+ (map points are only good to metres); with two
        /// points a mirror is indistinguishable from a rotation.
        /// </summary>
        internal static bool Mirrored(List<(double e, double n, double x, double y)> pairs)
        {
            if (pairs.Count < 3) return false;
            double pe = 0, pn = 0, qx = 0, qy = 0;
            foreach (var p in pairs) { pe += p.e; pn += p.n; qx += p.x; qy += p.y; }
            pe /= pairs.Count; pn /= pairs.Count; qx /= pairs.Count; qy /= pairs.Count;
            double den = 0, qq = 0, r1 = 0, i1 = 0, r2 = 0, i2 = 0;
            foreach (var p in pairs)
            {
                double ax = p.e - pe, ay = p.n - pn, bx = p.x - qx, by = p.y - qy;
                den += ax * ax + ay * ay;
                qq += bx * bx + by * by;
                r1 += bx * ax + by * ay; i1 += by * ax - bx * ay;     // b·conj(a)
                r2 += bx * ax - by * ay; i2 += by * ax + bx * ay;     // b·a  (= b·conj(conj a))
            }
            if (den < 25 || qq < 25) return false;
            double proper = qq - (r1 * r1 + i1 * i1) / den;   // residual Σ|b − k·a|² at the best k
            double mirror = qq - (r2 * r2 + i2 * i2) / den;
            return mirror < 0.25 * proper && proper > 1.0;
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
