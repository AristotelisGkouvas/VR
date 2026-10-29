using System;
using UnityEngine;

namespace QuestMarkerApp.API
{
    /// <summary>Visual / semantic type of a marker.</summary>
    public enum MarkerType
    {
        Sphere = 0,
        Pin = 1,
        Arrow = 2,
        Zone = 3,
        /// <summary>Camera-facing brand-kit sensor icon (Resources/SensorIcons/&lt;icon&gt;).</summary>
        Icon = 4
    }

    /// <summary>
    /// Data class representing a single coordinate point with metadata
    /// </summary>
    [Serializable]
    public class CoordinateData
    {
        public string id;
        public float x;
        public float y;
        public float z;
        public string label;
        public string color;
        public float scale = 0.1f;
        public long timestamp;

        /// <summary>"sphere" | "pin" | "arrow" | "zone" | "icon"</summary>
        public string markerType = "sphere";
        /// <summary>Icon name for markerType "icon", e.g. "piezometer" (Resources/SensorIcons).</summary>
        public string icon;

        /// <summary>Radius for zone markers (meters).</summary>
        public float radius;

        /// <summary>"local" | "gps" | "utm"</summary>
        public string crs = "local";

        /// <summary>EPSG code when known (e.g. 4326, 2100).</summary>
        public string epsg;

        /// <summary>GPS latitude (when crs=gps). Double as string-safe via separate fields.</summary>
        public double latitude;
        public double longitude;
        public double altitude;

        /// <summary>Severity / status free text (e.g. "ok", "warning", "critical").</summary>
        public string status;

        /// <summary>The sensor's newest reading is old (ProjectPoint.IsStale): a grey ring on its icon.</summary>
        public bool stale;

        /// <summary>Logical layer / group name.</summary>
        public string layer = "default";

        /// <summary>Free-form notes.</summary>
        public string notes;

        /// <summary>Optional photo/URL attachment.</summary>
        public string photoUrl;

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }

        public MarkerType GetMarkerType()
        {
            if (string.IsNullOrEmpty(markerType)) return MarkerType.Sphere;
            switch (markerType.Trim().ToLowerInvariant())
            {
                case "pin": return MarkerType.Pin;
                case "arrow": return MarkerType.Arrow;
                case "zone": return MarkerType.Zone;
                case "icon": return MarkerType.Icon;
                default: return MarkerType.Sphere;
            }
        }

        public Color GetColor()
        {
            return TryGetColor(out Color parsedColor) ? parsedColor : Color.white;
        }

        /// <summary>
        /// Parse the color string ("#FF0000", "FF0000" or a named color like "red").
        /// Returns false when no color was specified or it cannot be parsed, so callers
        /// can apply their own default without treating white as a sentinel.
        /// </summary>
        public bool TryGetColor(out Color parsedColor)
        {
            parsedColor = Color.white;
            if (string.IsNullOrEmpty(color))
                return false;

            string s = color.Trim();
            // Only prefix '#' for bare hex — named colors ("red") must stay untouched.
            if (!s.StartsWith("#") && IsHex(s))
                s = "#" + s;
            return ColorUtility.TryParseHtmlString(s, out parsedColor);
        }

        private static bool IsHex(string s)
        {
            if (s.Length != 3 && s.Length != 4 && s.Length != 6 && s.Length != 8)
                return false;
            foreach (char c in s)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        public bool IsValid(float maxAbsCoordinate = 10000f)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z))
                return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
                return false;
            if (Mathf.Abs(x) > maxAbsCoordinate || Mathf.Abs(y) > maxAbsCoordinate || Mathf.Abs(z) > maxAbsCoordinate)
                return false;
            return true;
        }

        public void EnsureDefaults()
        {
            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString("N");
            if (timestamp <= 0)
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (scale <= 0f)
                scale = 0.1f;
            if (string.IsNullOrEmpty(markerType))
                markerType = "sphere";
            if (string.IsNullOrEmpty(crs))
                crs = "local";
            if (string.IsNullOrEmpty(layer))
                layer = "default";
            if (GetMarkerType() == MarkerType.Zone && radius <= 0f)
                radius = Mathf.Max(scale, 0.5f);
        }

        public CoordinateData Clone()
        {
            return new CoordinateData
            {
                id = id,
                x = x, y = y, z = z,
                label = label,
                color = color,
                scale = scale,
                timestamp = timestamp,
                markerType = markerType,
                icon = icon,
                radius = radius,
                crs = crs,
                epsg = epsg,
                latitude = latitude,
                longitude = longitude,
                altitude = altitude,
                status = status,
                layer = layer,
                notes = notes,
                photoUrl = photoUrl
            };
        }
    }

    [Serializable]
    public class CoordinateResponse
    {
        public CoordinateData[] coordinates;
        public string referencePointId;
        public bool clearExisting;
        public bool removeMissing;
        public string sessionId;
        public string sessionName;
    }

    [Serializable]
    public class CoordinateRequest
    {
        public CoordinateData[] coordinates;
    }

    [Serializable]
    public class CalibrationRequest
    {
        public float realX;
        public float realY;
        public float realZ;
        public string coordinateSystem;
        public float rotationY;
        public bool quickCalibrate;
        public GPSReference gpsReference;

        public Vector3 ToVector3()
        {
            return new Vector3(realX, realY, realZ);
        }
    }

    [Serializable]
    public class GPSReference
    {
        public double latitude;
        public double longitude;
        public double altitude;
    }

    /// <summary>Serializable session document for export/import.</summary>
    [Serializable]
    public class MarkerSession
    {
        public string id;
        public string name;
        public string site;
        public long createdAt;
        public long updatedAt;
        public string crs = "local";
        public string epsg;
        public GPSReference gpsOrigin;
        public CoordinateData[] markers;
        public string notes;
    }
}
