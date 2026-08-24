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

            if (crs == "gps" || crs == "wgs84" || crs == "epsg:4326" || c.epsg == "4326")
            {
                if (gpsOrigin == null)
                {
                    Debug.LogWarning($"[CRS] GPS coordinate '{c.id}' without origin — using x/y/z as-is");
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
            else if (crs == "utm")
            {
                // UTM easting/northing/height already meter-based; treat as local
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
