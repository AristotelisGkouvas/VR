"""Tests for model_prep: matching-point fits, MTL handling, image conversion, and (when pymeshlab
is installed) the whole decimate-and-pack pipeline on a small textured OBJ."""

from __future__ import annotations

import math
import zipfile
from pathlib import Path

import pytest

import model_prep as mp

np = pytest.importorskip("numpy")


def _matches(fn, pts):
    return [mp.Match(tuple(p), tuple(fn(p)), f"P{i}") for i, p in enumerate(pts)]


PTS = [(-43.0, -47.5, 558.7), (24.1, -30.2, 590.0), (10.0, -56.0, 640.3), (-20.0, -26.0, 600.0)]


def _yaw_map(p, deg=37.0, t=(1008.4, 4982.4, -537.4), s=1.0):
    c, sn = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    x, y, z = p
    return (s * (c * x - sn * y) + t[0], s * (sn * x + c * y) + t[1], s * z + t[2])


def test_yaw_fit_recovers_rotation_and_translation():
    fit = mp.fit_transform(_matches(_yaw_map, PTS), "yaw")
    assert fit.yaw_deg == pytest.approx(37.0, abs=1e-6)
    assert fit.rms < 1e-6
    assert fit.apply(PTS[0]) == pytest.approx(_yaw_map(PTS[0]), abs=1e-6)


def test_yaw_fit_with_scale():
    fit = mp.fit_transform(_matches(lambda p: _yaw_map(p, s=1.02), PTS), "yaw", allow_scale=True)
    assert fit.scale == pytest.approx(1.02, abs=1e-9)
    assert fit.rms < 1e-6


def test_yaw_fit_flags_a_bad_point():
    m = _matches(_yaw_map, PTS)
    g = m[2].grid
    m[2] = mp.Match(m[2].model, (g[0] + 0.5, g[1], g[2]), "bad")
    fit = mp.fit_transform(m, "yaw")
    worst = max(range(len(m)), key=lambda i: fit.residuals[i])
    assert worst == 2


def test_3d_fit_recovers_tilt():
    tilt = math.radians(4.0)
    def f(p):
        x, y, z = p
        y2, z2 = math.cos(tilt) * y - math.sin(tilt) * z, math.sin(tilt) * y + math.cos(tilt) * z
        return _yaw_map((x, y2, z2))
    fit = mp.fit_transform(_matches(f, PTS), "3d")
    assert fit.rms < 1e-6
    # The yaw-only fit cannot absorb the tilt.
    assert mp.fit_transform(_matches(f, PTS), "yaw").rms > 0.5


def test_fit_needs_enough_points():
    with pytest.raises(ValueError):
        mp.fit_transform(_matches(_yaw_map, PTS[:1]), "yaw")
    with pytest.raises(ValueError):
        mp.fit_transform(_matches(_yaw_map, PTS[:2]), "3d")


def test_origin_is_rounded_to_100m():
    fit = mp.fit_transform(_matches(_yaw_map, PTS), "yaw")
    o = mp.origin_for(fit, PTS[0])
    assert all(v % 100 == 0 for v in o)
    p = fit.apply(PTS[0])
    assert all(0 <= p[i] - o[i] < 100 for i in range(3))


def test_read_matches(tmp_path: Path):
    f = tmp_path / "m.csv"
    f.write_text("name,model_x,model_y,model_z,E,N,H\nA1,1,2,3,1000,5000,20\nA2,4,5,6,1010,5010,21\n",
                 encoding="utf-8")
    m = mp.read_matches(f)
    assert [x.name for x in m] == ["A1", "A2"]
    assert m[1].model == (4.0, 5.0, 6.0) and m[1].grid == (1010.0, 5010.0, 21.0)
    bad = tmp_path / "b.csv"
    bad.write_text("x,y,z\n1,2,3\n", encoding="utf-8")
    with pytest.raises(ValueError):
        mp.read_matches(bad)


def test_mtl_textures_and_rewrite(tmp_path: Path):
    mtl = tmp_path / "a.mtl"
    mtl.write_text("newmtl m1\nKd 0.5 0.5 0.5\nmap_Kd -s 1 1 1 tex1.png\nmap_Bump n.png\n"
                   "newmtl m2\nmap_Kd tex2.png\n", encoding="utf-8")
    assert mp.mtl_textures(mtl) == ["tex1.png", "tex2.png"]
    mp.rewrite_mtl(mtl, {"tex1.png": "tex1.jpg", "tex2.png": "tex2.jpg"})
    text = mtl.read_text(encoding="utf-8")
    assert "map_Kd tex1.jpg" in text and "map_Kd tex2.jpg" in text
    assert "map_Bump" not in text and "Kd 0.5" in text


def test_convert_image_downscales(tmp_path: Path):
    PIL = pytest.importorskip("PIL.Image")
    src = tmp_path / "big.png"
    PIL.new("RGBA", (3000, 1500), (200, 100, 50, 255)).save(src)
    size = mp.convert_image(src, tmp_path / "big.jpg", 1024)
    assert size == (1024, 512)
    with PIL.open(tmp_path / "big.jpg") as im:
        assert im.format == "JPEG" and im.size == (1024, 512)


def test_prepare_pipeline(tmp_path: Path):
    pytest.importorskip("pymeshlab")
    PIL = pytest.importorskip("PIL.Image")
    # A textured grid of 2 * 30 * 30 triangles in a local frame.
    n = 30
    lines = ["mtllib g.mtl"]
    for j in range(n + 1):
        for i in range(n + 1):
            lines.append(f"v {i * 0.5 - 40} {j * 0.5 - 50} {560 + 0.1 * math.sin(i)}")
    for j in range(n + 1):
        for i in range(n + 1):
            lines.append(f"vt {i / n} {j / n}")
    lines.append("usemtl m1")
    for j in range(n):
        for i in range(n):
            a = j * (n + 1) + i + 1
            b, c, d = a + 1, a + n + 2, a + n + 1
            lines.append(f"f {a}/{a} {b}/{b} {c}/{c}")
            lines.append(f"f {a}/{a} {c}/{c} {d}/{d}")
    src_dir = tmp_path / "src"
    src_dir.mkdir()
    (src_dir / "g.obj").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (src_dir / "g.mtl").write_text("newmtl m1\nKd 0.5 0.5 0.5\nmap_Kd gImage1.png\n", encoding="utf-8")
    PIL.new("RGB", (4096, 4096), (90, 120, 60)).save(src_dir / "gImage1.png")

    # The grid's four corners, on its surface (z = 560 + 0.1 sin(i)).
    zc = 560 + 0.1 * math.sin(n)
    pts = [(-40, -50, 560), (-25, -50, zc), (-25, -35, zc), (-40, -35, 560)]
    matches = _matches(_yaw_map, pts)
    work = tmp_path / "work"
    work.mkdir()
    info = mp.prepare(src_dir / "g.obj", work, name="grid", faces=600, texture_size=512,
                      matches=matches, log=lambda *_: None)
    assert info.faces_in == 2 * n * n
    assert info.faces_out <= 600
    assert info.textures == 1
    assert info.fit is not None and info.fit.rms < 1e-6
    with zipfile.ZipFile(info.package) as z:
        names = set(z.namelist())
        assert {"grid.obj", "gImage1.jpg"} <= names
        mtl_names = [n for n in names if n.endswith(".mtl")]  # MeshLab writes grid.obj.mtl
        assert len(mtl_names) == 1
        objtext = z.read("grid.obj").decode()
        assert f"mtllib {mtl_names[0]}" in objtext or f"mtllib ./{mtl_names[0]}" in objtext
        mtl = z.read(mtl_names[0]).decode()
        assert "map_Kd gImage1.jpg" in mtl
        obj = z.read("grid.obj").decode().splitlines()
    vs = [tuple(map(float, l.split()[1:4])) for l in obj if l.startswith("v ")]
    assert any(l.startswith("vt ") for l in obj)
    # Vertices are in the grid, relative to the 100 m origin: small numbers, around the mapped points.
    expected = [tuple(g - o for g, o in zip(m.grid, info.origin)) for m in matches]
    for e in expected:
        assert min(math.dist(e, v) for v in vs) < 0.6
