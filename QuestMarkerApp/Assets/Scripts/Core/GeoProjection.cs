using System;

namespace QuestMarkerApp.Core
{
    /// <summary>
    /// WGS84 latitude/longitude to a few projected grids, for placing Terraweb sensors on a
    /// model by their lat/lon when the model is in a national or UTM grid and the sensors'
    /// x/y are in another (a surveyor's local grid). No PROJ on the headset, so only
    /// Transverse Mercator grids are covered:
    /// EPSG:2100 (ΕΓΣΑ87, GGRS87 with the 3-parameter shift, ~1 m), 326xx/327xx (WGS84 UTM)
    /// and 258xx (ETRS89 UTM, taken as WGS84: sub-metre in Europe). Good to the metre, which
    /// is far inside the error of a map-picked sensor point.
    /// </summary>
    public static class GeoProjection
    {
        private const double WgsA = 6378137.0, WgsF = 1.0 / 298.257223563;
        private const double Grs80F = 1.0 / 298.257222101;

        /// <summary>Easting/northing in <paramref name="epsg"/>; false when the grid is not supported.</summary>
        public static bool TryProject(int epsg, double lat, double lon, out double e, out double n)
        {
            e = n = 0;
            if (epsg == 2100)
            {
                // WGS84 → GGRS87: GGRS87 + (−199.87, 74.79, 246.62) = WGS84, geocentric.
                Geocentric(lat, lon, 0, WgsA, WgsF, out double x, out double y, out double z);
                Geodetic(x + 199.87, y - 74.79, z - 246.62, WgsA, Grs80F, out double gl, out double gn);
                TransverseMercator(gl, gn, WgsA, Grs80F, 24.0, 0.9996, 500000.0, 0.0, out e, out n);
                return true;
            }
            int zone = epsg % 100;
            bool utm = (epsg / 100 == 326 || epsg / 100 == 327 || epsg / 100 == 258) && zone >= 1 && zone <= 60;
            if (!utm) return false;
            double north = epsg / 100 == 327 ? 10000000.0 : 0.0;
            TransverseMercator(lat, lon, WgsA, epsg / 100 == 258 ? Grs80F : WgsF, zone * 6.0 - 183.0, 0.9996, 500000.0, north, out e, out n);
            return true;
        }

        /// <summary>The grids a model near this point is likely in: ΕΓΣΑ87 in Greece, then the UTM zone.</summary>
        public static int[] Candidates(double lat, double lon)
        {
            int zone = Math.Max(1, Math.Min(60, (int)Math.Floor((lon + 180.0) / 6.0) + 1));
            int utm = (lat >= 0 ? 32600 : 32700) + zone;
            bool greece = lat > 34.0 && lat < 42.5 && lon > 19.0 && lon < 30.0;
            return greece ? new[] { 2100, utm } : new[] { utm };
        }

        // Snyder, Map Projections (USGS 1395), 8-12 to 8-15: millimetres within a few degrees of
        // the central meridian, which every grid above stays within.
        private static void TransverseMercator(double lat, double lon, double a, double f, double lon0, double k0,
            double fe, double fn, out double x, out double y)
        {
            double e2 = f * (2 - f), ep2 = e2 / (1 - e2), e4 = e2 * e2, e6 = e4 * e2;
            double phi = lat * Math.PI / 180.0, lam = (lon - lon0) * Math.PI / 180.0;
            double sin = Math.Sin(phi), cos = Math.Cos(phi), tan = Math.Tan(phi);
            double nu = a / Math.Sqrt(1 - e2 * sin * sin);
            double t = tan * tan, c = ep2 * cos * cos, aa = lam * cos;
            double m = a * ((1 - e2 / 4 - 3 * e4 / 64 - 5 * e6 / 256) * phi
                            - (3 * e2 / 8 + 3 * e4 / 32 + 45 * e6 / 1024) * Math.Sin(2 * phi)
                            + (15 * e4 / 256 + 45 * e6 / 1024) * Math.Sin(4 * phi)
                            - 35 * e6 / 3072 * Math.Sin(6 * phi));
            double a2 = aa * aa, a3 = a2 * aa, a4 = a3 * aa, a5 = a4 * aa, a6 = a5 * aa;
            x = fe + k0 * nu * (aa + (1 - t + c) * a3 / 6 + (5 - 18 * t + t * t + 72 * c - 58 * ep2) * a5 / 120);
            y = fn + k0 * (m + nu * tan * (a2 / 2 + (5 - t + 9 * c + 4 * c * c) * a4 / 24
                                           + (61 - 58 * t + t * t + 600 * c - 330 * ep2) * a6 / 720));
        }

        private static void Geocentric(double lat, double lon, double h, double a, double f,
            out double x, out double y, out double z)
        {
            double e2 = f * (2 - f), p = lat * Math.PI / 180.0, l = lon * Math.PI / 180.0;
            double nu = a / Math.Sqrt(1 - e2 * Math.Sin(p) * Math.Sin(p));
            x = (nu + h) * Math.Cos(p) * Math.Cos(l);
            y = (nu + h) * Math.Cos(p) * Math.Sin(l);
            z = (nu * (1 - e2) + h) * Math.Sin(p);
        }

        private static void Geodetic(double x, double y, double z, double a, double f, out double lat, out double lon)
        {
            double e2 = f * (2 - f), p = Math.Sqrt(x * x + y * y);
            double phi = Math.Atan2(z, p * (1 - e2));
            for (int i = 0; i < 6; i++)
            {
                double nu = a / Math.Sqrt(1 - e2 * Math.Sin(phi) * Math.Sin(phi));
                double h = p / Math.Cos(phi) - nu;
                phi = Math.Atan2(z, p * (1 - e2 * nu / (nu + h)));
            }
            lat = phi * 180.0 / Math.PI;
            lon = Math.Atan2(y, x) * 180.0 / Math.PI;
        }
    }
}
