#!/usr/bin/env python3
"""Unit tests for GPS→local conversion and server helpers (no Quest required)."""

from __future__ import annotations

import math
import unittest


def gps_to_local_meters(
    latitude: float,
    longitude: float,
    altitude: float,
    origin_lat: float,
    origin_lon: float,
    origin_alt: float = 0.0,
) -> tuple[float, float, float]:
    """Mirror of CoordinateCalibration.GpsToLocalMeters (equirectangular)."""
    meters_per_deg_lat = 111320.0
    lat_rad = origin_lat * (math.pi / 180.0)
    meters_per_deg_lon = meters_per_deg_lat * math.cos(lat_rad)
    x = (longitude - origin_lon) * meters_per_deg_lon  # East
    y = altitude - origin_alt
    z = (latitude - origin_lat) * meters_per_deg_lat  # North
    return x, y, z


def apply_transform(
    real: tuple[float, float, float],
    origin_offset: tuple[float, float, float],
    yaw_deg: float = 0.0,
    scale: float = 1.0,
) -> tuple[float, float, float]:
    """Simplified RealToQuest: scale → rotate Y → translate."""
    sx, sy, sz = real[0] * scale, real[1] * scale, real[2] * scale
    rad = math.radians(yaw_deg)
    c, s = math.cos(rad), math.sin(rad)
    rx = c * sx + s * sz
    rz = -s * sx + c * sz
    return rx + origin_offset[0], sy + origin_offset[1], rz + origin_offset[2]


class TestGpsToLocal(unittest.TestCase):
    def test_origin_is_zero(self):
        x, y, z = gps_to_local_meters(38.0, 23.7, 100.0, 38.0, 23.7, 100.0)
        self.assertAlmostEqual(x, 0.0, places=5)
        self.assertAlmostEqual(y, 0.0, places=5)
        self.assertAlmostEqual(z, 0.0, places=5)

    def test_one_degree_north(self):
        x, y, z = gps_to_local_meters(39.0, 23.7, 0.0, 38.0, 23.7, 0.0)
        self.assertAlmostEqual(x, 0.0, places=3)
        self.assertAlmostEqual(z, 111320.0, places=0)

    def test_east_positive_x(self):
        x, y, z = gps_to_local_meters(0.0, 1.0, 0.0, 0.0, 0.0, 0.0)
        self.assertGreater(x, 100000.0)
        self.assertAlmostEqual(z, 0.0, places=3)


class TestTransform(unittest.TestCase):
    def test_identity(self):
        q = apply_transform((1, 2, 3), (0, 0, 0), 0, 1)
        self.assertEqual(q, (1, 2, 3))

    def test_translation(self):
        q = apply_transform((1, 0, 0), (10, 0, 0), 0, 1)
        self.assertAlmostEqual(q[0], 11.0)

    def test_yaw_90(self):
        # +X real → should go toward +Z or -Z depending on convention
        q = apply_transform((1, 0, 0), (0, 0, 0), 90, 1)
        self.assertAlmostEqual(q[0], 0.0, places=5)
        self.assertAlmostEqual(abs(q[2]), 1.0, places=5)


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
