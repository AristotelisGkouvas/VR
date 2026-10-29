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

        /// <summary>A reading older than this many days is flagged "old" (icon ring, card, label).</summary>
        public const int StaleDays = 30;

        /// <summary>
        /// The newest reading is older than <see cref="StaleDays"/>: what the icon shows may no
        /// longer hold. The alert state stays the server's. <paramref name="days"/> is its age;
        /// false with no reading date at all.
        /// </summary>
        public bool IsStale(out int days)
        {
            days = 0;
            if (string.IsNullOrEmpty(latestDate) || !DateTime.TryParse(latestDate, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d)) return false;
            days = (int)(DateTime.UtcNow - d.ToUniversalTime()).TotalDays;
            return days > StaleDays;
        }
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
