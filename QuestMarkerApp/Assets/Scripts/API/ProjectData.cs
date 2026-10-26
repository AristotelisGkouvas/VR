using System;

namespace QuestMarkerApp.API
{
    /// <summary>
    /// One surveyed project point. Kept in double: EGSA87 / UTM eastings and northings
    /// (~476000 / ~4200000) lose centimetres in float.
    /// </summary>
    [Serializable]
    public class ProjectPoint
    {
        public string id;
        /// <summary>Easting (m), e.g. EGSA87 X.</summary>
        public double e;
        /// <summary>Northing (m), e.g. EGSA87 Y.</summary>
        public double n;
        /// <summary>Absolute elevation (m).</summary>
        public double h;
        public string label;
        public string color;
        /// <summary>Brand-kit sensor icon name, e.g. "piezometer"; empty draws a sphere.</summary>
        public string icon;

        // Terraweb2 details, filled when the project came from the backend (headset import or
        // the PC bridge); all optional for plain survey projects.
        public string sensorId;
        public int typeId;
        public string typeLabel;
        /// <summary>none | reference | alert | alarm</summary>
        public string alertState;
        public string latestDate;
        public string notes;
        public bool inactive;
        /// <summary>
        /// Placed from its map-picked WGS84 point fitted onto the survey grid, not from a
        /// measured position: good to metres, not centimetres.
        /// </summary>
        public bool approx;
        /// <summary>
        /// The backend's WGS84 point and elevation (sensor_geometry), kept beside e/n/h, which
        /// may be a surveyor's local grid: the model preview places sensors by these when the
        /// model is in a national or UTM grid. Map-picked, so good to metres. Absent (false)
        /// for projects that did not come from Terraweb.
        /// </summary>
        public bool hasGeo;
        public double lat, lon, alt;
    }

    /// <summary>Body of POST /api/project.</summary>
    [Serializable]
    public class ProjectRequest
    {
        public string name;
        /// <summary>Informational only, e.g. "EGSA87" or "EPSG:2100".</summary>
        public string crs;
        /// <summary>Terraweb project id when loaded from the backend; readings need it selected.</summary>
        public int terrawebProjectId;
        /// <summary>
        /// Angle (degrees, counter-clockwise) from the survey grid's x axis to the points' East
        /// axis — turns measured dx/dy into the points' frame. 0 when the points are the grid.
        /// </summary>
        public float gridYawDeg;
        public ProjectPoint[] points;
    }
}
