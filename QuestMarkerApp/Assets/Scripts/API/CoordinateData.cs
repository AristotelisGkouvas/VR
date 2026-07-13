using System;
using UnityEngine;

namespace QuestMarkerApp.API
{
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
        public float scale;
        public long timestamp;

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }

        public Color GetColor()
        {
            if (string.IsNullOrEmpty(color))
                return Color.white;

            if (ColorUtility.TryParseHtmlString(color, out Color parsedColor))
                return parsedColor;

            return Color.white;
        }
    }

    /// <summary>
    /// Wrapper for receiving multiple coordinates from API
    /// </summary>
    [Serializable]
    public class CoordinateResponse
    {
        public CoordinateData[] coordinates;
        public string referencePointId;
        public bool clearExisting;
    }

    /// <summary>
    /// Request structure for sending coordinate updates
    /// </summary>
    [Serializable]
    public class CoordinateRequest
    {
        public CoordinateData[] coordinates;
    }

    /// <summary>
    /// Calibration request - maps real-world coordinates to Quest position
    /// </summary>
    [Serializable]
    public class CalibrationRequest
    {
        /// <summary>
        /// Real-world X coordinate (meters from reference, or longitude for GPS)
        /// </summary>
        public float realX;
        
        /// <summary>
        /// Real-world Y coordinate (meters height, or altitude for GPS)
        /// </summary>
        public float realY;
        
        /// <summary>
        /// Real-world Z coordinate (meters from reference, or latitude for GPS)
        /// </summary>
        public float realZ;
        
        /// <summary>
        /// Coordinate system type: "local", "gps", "utm"
        /// </summary>
        public string coordinateSystem;
        
        /// <summary>
        /// Optional rotation offset in degrees (Y-axis rotation)
        /// </summary>
        public float rotationY;
        
        /// <summary>
        /// If true, this is a quick single-point calibration
        /// </summary>
        public bool quickCalibrate;
        
        /// <summary>
        /// Optional GPS reference point for GPS coordinate system
        /// </summary>
        public GPSReference gpsReference;

        public Vector3 ToVector3()
        {
            return new Vector3(realX, realY, realZ);
        }
    }

    /// <summary>
    /// GPS reference point for coordinate conversion
    /// </summary>
    [Serializable]
    public class GPSReference
    {
        /// <summary>
        /// Reference latitude (origin point)
        /// </summary>
        public double latitude;
        
        /// <summary>
        /// Reference longitude (origin point)
        /// </summary>
        public double longitude;
        
        /// <summary>
        /// Reference altitude in meters
        /// </summary>
        public double altitude;
    }
}
