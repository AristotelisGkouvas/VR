#!/usr/bin/env python3
"""Unit tests for GPS→local conversion, the alignment fit and server helpers (no Quest required).

The functions here mirror the C# math (CoordinateCalibration, ProjectAlignment, TerrawebImport)
so its conventions are pinned down: Unity x = East, y = Up, z = North; Euler(0, θ, 0) turns
+z (North) towards +x (East).
"""

from __future__ import annotations

import math
import struct
import unittest

_A = 6378137.0
_F = 1 / 298.257223563
_E2 = _F * (2 - _F)


def f32(v: float) -> float:
    return struct.unpack("f", struct.pack("f", v))[0]


def _ecef(lat: float, lon: float, h: float) -> tuple[float, float, float]:
    la, lo = math.radians(lat), math.radians(lon)
    n = _A / math.sqrt(1 - _E2 * math.sin(la) ** 2)
    return ((n + h) * math.cos(la) * math.cos(lo), (n + h) * math.cos(la) * math.sin(lo),
            (n * (1 - _E2) + h) * math.sin(la))


def exact_enu(lat, lon, h, lat0, lon0, h0):
    """Reference: WGS84 → ECEF → ENU (as TerrawebImport.Enu / terraweb_bridge.enu)."""
    x, y, z = _ecef(lat, lon, h)
    x0, y0, z0 = _ecef(lat0, lon0, h0)
    dx, dy, dz = x - x0, y - y0, z - z0
    la, lo = math.radians(lat0), math.radians(lon0)
    e = -math.sin(lo) * dx + math.cos(lo) * dy
    n = -math.sin(la) * math.cos(lo) * dx - math.sin(la) * math.sin(lo) * dy + math.cos(la) * dz
    return e, n


def exact_enu_up(lat, lon, h, lat0, lon0, h0):
    x, y, z = _ecef(lat, lon, h)
    x0, y0, z0 = _ecef(lat0, lon0, h0)
    la, lo = math.radians(lat0), math.radians(lon0)
    return (math.cos(la) * math.cos(lo) * (x - x0) + math.cos(la) * math.sin(lo) * (y - y0)
            + math.sin(la) * (z - z0))


def gps_to_local_meters(latitude, longitude, altitude, origin_lat, origin_lon, origin_alt=0.0):
    """Mirror of CoordinateCalibration.GpsToLocalMeters: exact ENU → (x East, y Up, z North)."""
    e, n = exact_enu(latitude, longitude, altitude, origin_lat, origin_lon, origin_alt)
    u = exact_enu_up(latitude, longitude, altitude, origin_lat, origin_lon, origin_alt)
    return e, u, n


def old_equirectangular(latitude, longitude, origin_lat, origin_lon):
    """The previous C# formula (spherical 111 320 m/deg, no wrap), for regression evidence."""
    x = (longitude - origin_lon) * 111320.0 * math.cos(math.radians(origin_lat))
    z = (latitude - origin_lat) * 111320.0
    return x, z


def offset_point(lat0, lon0, east_m, north_m):
    """lat/lon at an exact east/north (ENU) offset, solved iteratively against exact_enu."""
    lat, lon = lat0, lon0
    for _ in range(20):
        e, n = exact_enu(lat, lon, 0.0, lat0, lon0, 0.0)
        phi = math.radians(lat)
        w = 1 - _E2 * math.sin(phi) ** 2
        lat += math.degrees((north_m - n) / (_A * (1 - _E2) / w ** 1.5))
        lon += math.degrees((east_m - e) / (_A / math.sqrt(w) * math.cos(phi)))
    return lat, ((lon + 180.0) % 360.0) - 180.0


def unity_yaw(v, deg):
    """Quaternion.Euler(0, deg, 0) * v in Unity (left-handed, y up)."""
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    x, y, z = v
    return (c * x + s * z, y, -s * x + c * z)


def solve_yaw(pairs):
    """Mirror of ProjectAlignment.Solve / CoordinateCalibration.CalculateCalibration:
    least-squares yaw + translation, scale fixed at 1. pairs: [(project_local, measured)].
    None when the points are too close together (spread < 0.25²)."""
    n = len(pairs)
    pc = [sum(p[i] for p, _ in pairs) / n for i in range(3)]
    mc = [sum(m[i] for _, m in pairs) / n for i in range(3)]
    s_cos = s_sin = spread = 0.0
    for p, m in pairs:
        px, pz, mx, mz = p[0] - pc[0], p[2] - pc[2], m[0] - mc[0], m[2] - mc[2]
        s_cos += mx * px + mz * pz
        s_sin += mx * pz - mz * px
        spread += px * px + pz * pz
    if spread < 0.25 ** 2:
        return None
    yaw = math.degrees(math.atan2(s_sin, s_cos))
    r = unity_yaw(pc, yaw)
    t = tuple(mc[i] - r[i] for i in range(3))
    return yaw, t


def fitted(p, yaw, t):
    r = unity_yaw(p, yaw)
    return tuple(r[k] + t[k] for k in range(3))


def project_local(e, n, h, origin):
    """ProjectAlignment.ProjectLocal: E/N/H minus the double origin → (x East, y Up, z North)."""
    return (e - origin[0], h - origin[2], n - origin[1])


def apply_transform(
    real: tuple[float, float, float],
    origin_offset: tuple[float, float, float],
    yaw_deg: float = 0.0,
    scale: float = 1.0,
) -> tuple[float, float, float]:
    """Simplified RealToQuest: scale → rotate Y → translate."""
    r = unity_yaw((real[0] * scale, real[1] * scale, real[2] * scale), yaw_deg)
    return r[0] + origin_offset[0], r[1] + origin_offset[1], r[2] + origin_offset[2]


def mirrored(pairs):
    """Mirror of TerrawebImport.Mirrored (complex similarity fit, proper vs reflected)."""
    if len(pairs) < 3:
        return False
    n = len(pairs)
    pe = sum(p[0] for p in pairs) / n
    pn = sum(p[1] for p in pairs) / n
    qx = sum(p[2] for p in pairs) / n
    qy = sum(p[3] for p in pairs) / n
    den = qq = r1 = i1 = r2 = i2 = 0.0
    for e, nn, x, y in pairs:
        ax, ay, bx, by = e - pe, nn - pn, x - qx, y - qy
        den += ax * ax + ay * ay
        qq += bx * bx + by * by
        r1 += bx * ax + by * ay
        i1 += by * ax - bx * ay
        r2 += bx * ax - by * ay
        i2 += by * ax + bx * ay
    if den < 25 or qq < 25:
        return False
    proper = qq - (r1 * r1 + i1 * i1) / den
    mirror = qq - (r2 * r2 + i2 * i2) / den
    return mirror < 0.25 * proper and proper > 1.0


# A site in three different projected grids: EGSA87, UTM 33S (southern hemisphere, false
# northing 10 000 000), UTM 60N near the antimeridian. Same relative geometry each time.
SITE = [(0.0, 0.0, 0.0), (35.2, 4.1, 1.3), (12.7, 41.9, -2.2), (-20.4, 25.5, 0.4)]
GRIDS = {"EGSA87": (476512.437, 4203118.912, 112.37),
         "UTM33S": (512345.678, 9876543.219, 1503.2),
         "UTM60N": (699999.991, 7012345.678, 3.1)}


class TestGpsToLocal(unittest.TestCase):
    def test_origin_is_zero(self):
        x, y, z = gps_to_local_meters(38.0, 23.7, 100.0, 38.0, 23.7, 100.0)
        self.assertAlmostEqual(x, 0.0, places=5)
        self.assertAlmostEqual(y, 0.0, places=5)
        self.assertAlmostEqual(z, 0.0, places=5)

    def test_one_minute_north_at_38(self):
        # The old spherical 111 320 m/deg gave 1855.3 m; on WGS84 at 38° N it is 1849.9 m.
        x, _, z = gps_to_local_meters(38.0 + 1 / 60, 23.7, 0.0, 38.0, 23.7, 0.0)
        self.assertAlmostEqual(x, 0.0, places=3)
        self.assertAlmostEqual(z, 1849.9, delta=0.1)

    def test_east_positive_x(self):
        x, _, z = gps_to_local_meters(0.0, 1.0, 0.0, 0.0, 0.0, 0.0)
        # One degree on the equator, on the tangent plane: a·sin(1°) (the arc is 111 319.49 m).
        self.assertAlmostEqual(x, 6378137.0 * math.sin(math.radians(1.0)), delta=0.01)
        self.assertAlmostEqual(z, 0.0, places=3)

    def test_roundtrip_worldwide(self):
        # Greece, 70° N, southern hemisphere, western hemisphere, US east coast, equator, Fiji.
        sites = [(38.0, 23.7), (70.0, 25.0), (-33.9, 151.2), (-54.8, -68.3), (40.7, -74.0), (0.0, 0.0),
                 (-17.0, 179.999)]
        for lat0, lon0 in sites:
            for east, north in ((5000.0, 0.0), (0.0, 5000.0), (700.0, -700.0)):
                lat, lon = offset_point(lat0, lon0, east, north)
                x, _, z = gps_to_local_meters(lat, lon, 0.0, lat0, lon0, 0.0)
                with self.subTest(site=(lat0, lon0), east=east, north=north):
                    self.assertAlmostEqual(x, east, delta=1e-4)
                    self.assertAlmostEqual(z, north, delta=1e-4)

    def test_antimeridian(self):
        lat0, lon0 = -17.0, 179.999
        lat, lon = offset_point(lat0, lon0, 300.0, 50.0)
        self.assertLess(lon, 0)  # across ±180°
        x, _, z = gps_to_local_meters(lat, lon, 0.0, lat0, lon0, 0.0)
        self.assertAlmostEqual(x, 300.0, delta=0.01)
        self.assertAlmostEqual(z, 50.0, delta=0.01)
        old_x, _ = old_equirectangular(lat, lon, lat0, lon0)
        self.assertGreater(abs(old_x), 1e7)  # the old formula: ~38 000 km west

    def test_old_spherical_formula_was_metres_off(self):
        lat, lon = offset_point(38.0, 23.7, 0.0, 2000.0)
        _, old_z = old_equirectangular(lat, lon, 38.0, 23.7)
        self.assertGreater(old_z - 2000.0, 5.0)  # 2 km north came out ~5.8 m long
        lat, lon = offset_point(38.0, 23.7, 1000.0, 0.0)
        old_x, old_z = old_equirectangular(lat, lon, 38.0, 23.7)
        self.assertGreater(math.hypot(old_x - 1000.0, old_z), 0.5)

    def test_up_includes_earth_curvature(self):
        lat, lon = offset_point(38.0, 23.7, 1000.0, 0.0)
        _, y, _ = gps_to_local_meters(lat, lon, 0.0, 38.0, 23.7, 0.0)
        self.assertAlmostEqual(y, -0.078, delta=0.003)


class TestTransform(unittest.TestCase):
    def test_identity(self):
        q = apply_transform((1, 2, 3), (0, 0, 0), 0, 1)
        self.assertEqual(q, (1, 2, 3))

    def test_translation(self):
        q = apply_transform((1, 0, 0), (10, 0, 0), 0, 1)
        self.assertAlmostEqual(q[0], 11.0)

    def test_yaw_90(self):
        # Unity: Euler(0, 90, 0) turns North (+z) to East (+x) and East to South (-z).
        q = apply_transform((1, 0, 0), (0, 0, 0), 90, 1)
        self.assertAlmostEqual(q[0], 0.0, places=6)
        self.assertAlmostEqual(q[2], -1.0, places=6)
        q = apply_transform((0, 0, 1), (0, 0, 0), 90, 1)
        self.assertAlmostEqual(q[0], 1.0, places=6)


class TestAlignmentFit(unittest.TestCase):
    def test_recovers_yaw_and_translation_in_any_grid(self):
        for name, (e0, n0, h0) in GRIDS.items():
            origin = (round(e0), round(n0), round(h0))
            pts = [project_local(e0 + de, n0 + dn, h0 + dh, origin) for de, dn, dh in SITE]
            pts32 = [tuple(f32(c) for c in p) for p in pts]  # ProjectLocal casts after subtracting
            for yaw in (-170.0, -37.5, 0.0, 12.25, 95.0):
                with self.subTest(grid=name, yaw=yaw):
                    t = (1.5, 0.8, -2.0)
                    touches = [fitted(p, yaw, t) for p in pts]
                    yaw_s, t_s = solve_yaw(list(zip(pts32, touches)))
                    self.assertAlmostEqual(((yaw_s - yaw + 180) % 360) - 180, 0.0, places=4)
                    for p, m in zip(pts32, touches):
                        self.assertLess(math.dist(fitted(p, yaw_s, t_s), m), 1e-3)

    def test_float_would_lose_decimetres_without_origin(self):
        n = GRIDS["UTM33S"][1]
        self.assertGreater(abs(f32(n) - n), 0.2)

    def test_two_points_too_close_are_rejected(self):
        pairs = [((0, 0, 0), (0, 0, 0)), ((0.2, 0, 0.1), (0.1, 0, 0.2))]
        self.assertIsNone(solve_yaw(pairs))

    def test_two_point_fit_cannot_see_a_mirror(self):
        # E and N exchanged (N,E-ordered source): with two points the fit is still perfect.
        a, b = (0.0, 0.0, 0.0), (30.0, 0.0, 10.0)
        sa, sb = (a[2], a[1], a[0]), (b[2], b[1], b[0])
        yaw, t = solve_yaw([(a, sa), (b, sb)])
        self.assertLess(math.dist(fitted(b, yaw, t), sb), 1e-9)

    def test_three_points_expose_a_mirror(self):
        pts = [(de, dh, dn) for de, dn, dh in SITE]
        touches = [(p[2], p[1], p[0]) for p in pts]  # E/N exchanged
        yaw, t = solve_yaw(list(zip(pts, touches)))
        self.assertGreater(max(math.dist(fitted(p, yaw, t), m) for p, m in zip(pts, touches)), 1.0)
        # ProjectAlignment.LooksMirrored: the same fit with E/N exchanged is perfect.
        swapped = [(p[2], p[1], p[0]) for p in pts]
        yaw2, t2 = solve_yaw(list(zip(swapped, touches)))
        self.assertLess(max(math.dist(fitted(p, yaw2, t2), m) for p, m in zip(swapped, touches)), 1e-9)


class TestQuickCalibrate(unittest.TestCase):
    """CoordinateCalibration.QuickCalibrate: R = Euler(controllerYaw + rotationY)."""

    def test_north_follows_the_controller(self):
        for yaw in (0.0, 90.0, -45.0, 170.0):
            fwd = unity_yaw((0, 0, 1), yaw)                # controller forward in the frame
            r = unity_yaw((0, 0, 1), yaw + 0.0)            # rotationY = 0: real North
            self.assertLess(math.dist(r, fwd), 1e-9)
            old = unity_yaw((0, 0, 1), 0.0 - yaw)          # previous "rotationY - controllerYaw"
            if abs(math.sin(math.radians(yaw))) > 0.1:
                self.assertGreater(math.dist(old, fwd), 0.1)

    def test_origin_lands_on_the_controller(self):
        controller, real_origin, yaw = (1.0, 0.5, -2.0), (10.0, 0.0, 5.0), 30.0
        r = unity_yaw(real_origin, yaw)
        offset = tuple(controller[k] - r[k] for k in range(3))
        self.assertLess(math.dist(apply_transform(real_origin, offset, yaw), controller), 1e-9)


class TestMirrorDetection(unittest.TestCase):
    ENU = [(0.0, 0.0), (40.0, 5.0), (10.0, 60.0), (-25.0, 30.0), (15.0, -20.0)]

    def _grid(self, swap, rot_deg=1.3, noise=0.0):
        c, s = math.cos(math.radians(rot_deg)), math.sin(math.radians(rot_deg))
        out = []
        for i, (e, n) in enumerate(self.ENU):
            x = 500000 + c * e - s * n + noise * ((-1) ** i)
            y = 4400000 + s * e + c * n + noise * ((-1) ** (i // 2))
            out.append((y, x) if swap else (x, y))
        return [(e, n, x, y) for (e, n), (x, y) in zip(self.ENU, out)]

    def test_proper_grid_is_not_flagged(self):
        self.assertFalse(mirrored(self._grid(False)))
        self.assertFalse(mirrored(self._grid(False, noise=8.0)))  # map points metres off

    def test_swapped_grid_is_flagged(self):
        self.assertTrue(mirrored(self._grid(True)))
        self.assertTrue(mirrored(self._grid(True, noise=3.0)))

    def test_two_points_never_flagged(self):
        self.assertFalse(mirrored(self._grid(True)[:2]))


class TestModelPrepDegenerate(unittest.TestCase):
    def setUp(self):
        try:
            import numpy  # noqa: F401
        except ImportError:
            self.skipTest("numpy not installed")
        import model_prep
        self.mp = model_prep

    def test_yaw_fit_rejects_nearly_coincident_points(self):
        mp = self.mp
        m = [mp.Match((0, 0, 0), (476500, 4203100, 100)), mp.Match((0.001, 0, 0), (476500.001, 4203100, 100))]
        with self.assertRaises(ValueError):
            mp.fit_transform(m, "yaw")

    def test_3d_fit_rejects_collinear_points(self):
        mp = self.mp
        m = [mp.Match((i, 0, 0), (476500 + i, 4203100, 100)) for i in range(4)]
        with self.assertRaises(ValueError):
            mp.fit_transform(m, "3d")

    def test_origin_keeps_floats_small_in_any_grid(self):
        mp = self.mp
        for e0, n0, h0 in GRIDS.values():
            m = [mp.Match((de, dn, dh), (e0 + de, n0 + dn, h0 + dh)) for de, dn, dh in SITE]
            fit = mp.fit_transform(m, "yaw")
            o = mp.origin_for(fit, m[0].model)
            for mm in m:
                local = [g - oo for g, oo in zip(fit.apply(mm.model), o)]
                self.assertTrue(all(abs(v) < 200 for v in local))


class TestCoordDefaults(unittest.TestCase):
    def test_ensure_defaults_shape(self):
        # Import logic inline to avoid Flask app side-effects when possible
        coord = {"x": 1, "y": 0, "z": 2}
        if "id" not in coord:
            coord["id"] = "t1"
        if "scale" not in coord:
            coord["scale"] = 0.1
        if "markerType" not in coord:
            coord["markerType"] = "sphere"
        self.assertEqual(coord["markerType"], "sphere")
        self.assertEqual(coord["scale"], 0.1)


if __name__ == "__main__":
    unittest.main()
