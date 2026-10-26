using System;
using UnityEngine;
using QuestMarkerApp.API;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// CRS / coordinate system conversions used when ingesting markers.
    /// </summary>
    public static class CoordinateTransform
    {
        /// <summary>
        /// Convert a coordinate to local meters relative to optional GPS origin,
        /// then optionally through calibration.
        /// Mutates a clone — does not modify the original.
        /// </summary>
        public static CoordinateData ToLocalMeters(
            CoordinateData src,
            GPSReference gpsOrigin,
            CoordinateCalibration calibration,
            bool applyCalibration)
        {
            if (src == null) return null;
            var c = src.Clone();
            c.EnsureDefaults();

            string crs = (c.crs ?? "local").Trim().ToLowerInvariant();
            string epsg = (c.epsg ?? "").Trim().ToLowerInvariant();
            if (epsg.StartsWith("epsg:")) epsg = epsg.Substring(5);

            if (IsGeographic(crs, epsg))
            {
                if (gpsOrigin == null)
                {
                    Debug.LogWarning($"[CRS] GPS coordinate '{c.id}' without origin — using x/y/z as-is");
                }
                else if (!GpsInRange(c))
                {
                    // Degrees used as metres (or projected E/N labelled as GPS) would land the
                    // marker hundreds of km away; refuse rather than place it wrongly.
                    Debug.LogWarning($"[CRS] GPS coordinate '{c.id}' out of range (lat {c.latitude}, lon {c.longitude}, " +
                                     $"x {c.x}, z {c.z}) — expected degrees, latitude ±90, longitude ±180; left as-is");
                }
                else
                {
                    double lat = c.latitude != 0 || c.longitude != 0 ? c.latitude : c.z; // allow z=lat fallback
                    double lon = c.latitude != 0 || c.longitude != 0 ? c.longitude : c.x;
                    double alt = c.latitude != 0 || c.longitude != 0 ? c.altitude : c.y;

                    // Prefer explicit lat/lon fields
                    if (Math.Abs(c.latitude) > 1e-12 || Math.Abs(c.longitude) > 1e-12)
                    {
                        lat = c.latitude;
                        lon = c.longitude;
                        alt = c.altitude;
                    }

                    var origin = new GPSReferenceOrigin
                    {
                        latitude = gpsOrigin.latitude,
                        longitude = gpsOrigin.longitude,
                        altitude = gpsOrigin.altitude
                    };
                    Vector3 local = CoordinateCalibration.GpsToLocalMeters(lat, lon, alt, origin);
                    c.x = local.x;
                    c.y = local.y;
                    c.z = local.z;
                    c.crs = "local";
                }
            }
            else if (crs != "local")
            {
                // Any projected metric CRS (UTM, EGSA87, other national grids): x = East,
                // y = Up, z = North in metres, used as-is. CoordinateData is float, so absolute
                // grid values (E ~10^5, N up to 10^7) keep only 0.03–1 m: such points must be
                // sent relative to a site origin, or as a project (/api/project, double).
                const float limit = 20000f;
                if (Mathf.Abs(c.x) > limit || Mathf.Abs(c.z) > limit || Mathf.Abs(c.y) > limit)
                    Debug.LogWarning($"[CRS] '{c.id}' ({c.crs}{(string.IsNullOrEmpty(c.epsg) ? "" : " EPSG:" + epsg)}) has " +
                                     $"absolute grid values ({c.x}, {c.y}, {c.z}): float precision is lost and they are " +
                                     "metres from the reference point. Subtract a site origin, or send them as a project.");
                c.crs = "local";
            }

            if (applyCalibration && calibration != null && calibration.IsCalibrated)
            {
                Vector3 quest = calibration.RealToQuest(c.ToVector3());
                c.x = quest.x;
                c.y = quest.y;
                c.z = quest.z;
            }

            return c;
        }

        /// <summary>
        /// Geographic lat/lon in degrees (WGS84 / EPSG:4326 and the like), as opposed to a
        /// projected CRS in metres. EPSG:4326's official axis order is lat, lon: the explicit
        /// latitude/longitude fields avoid that ambiguity (x = lon, z = lat in the fallback).
        /// </summary>
        public static bool IsGeographic(string crs, string epsg)
        {
            crs = (crs ?? "").Trim().ToLowerInvariant();
            epsg = (epsg ?? "").Trim().ToLowerInvariant();
            if (epsg.StartsWith("epsg:")) epsg = epsg.Substring(5);
            if (crs.StartsWith("epsg:")) { if (epsg.Length == 0) epsg = crs.Substring(5); }
            return crs == "gps" || crs == "wgs84" || crs == "latlon" || crs == "lonlat" || crs == "geographic"
                   || epsg == "4326" || epsg == "4979" || epsg == "4258"; // WGS84 2D/3D, ETRS89
        }

        // Same choice as ToLocalMeters: explicit lat/lon when either is set, else x = lon, z = lat.
        private static bool GpsInRange(CoordinateData c)
        {
            bool explicitLatLon = Math.Abs(c.latitude) > 1e-12 || Math.Abs(c.longitude) > 1e-12;
            double lat = explicitLatLon ? c.latitude : c.z;
            double lon = explicitLatLon ? c.longitude : c.x;
            return !double.IsNaN(lat) && !double.IsNaN(lon) && Math.Abs(lat) <= 90.0 && Math.Abs(lon) <= 180.0;
        }

        /// <summary>
        /// Set / update global GPS origin used for GPS → local conversion.
        /// </summary>
        public static GPSReference MakeOrigin(double lat, double lon, double alt = 0)
        {
            return new GPSReference
            {
                latitude = lat,
                longitude = lon,
                altitude = alt
            };
        }
    }
}
