import math

from model_to_local import fit, transform_tiles


def _move(theta, t, dh, p):
    c, s = math.cos(theta), math.sin(theta)
    return (c * p[0] - s * p[1] + t[0], s * p[0] + c * p[1] + t[1], p[2] + dh)


def test_fit_recovers_rotation_translation_and_height():
    model = [(222135.5, 4436556.6, 447.3), (222206.5, 4436630.9, 473.3), (222080.8, 4436530.2, 469.7)]
    theta, t, dh = math.radians(12.5), (-221000.0, -4431600.0), -426.0
    pairs = [(p, _move(theta, t, dh, p)) for p in model]
    f = fit(pairs)
    assert abs(math.degrees(f.theta) - 12.5) < 1e-6
    assert f.scale == 1.0
    assert abs(f.dh + 426.0) < 1e-9
    for m, l in pairs:
        x, y, z = f.apply(*m)
        assert abs(x - l[0]) < 1e-4 and abs(y - l[1]) < 1e-4 and abs(z - l[2]) < 1e-9


def test_transform_tiles_moves_vertices_keeps_uvs_and_offsets_faces(tmp_path):
    src = tmp_path / "in"
    src.mkdir()
    for i, name in enumerate(("a", "b")):
        (src / f"{name}.obj").write_text(
            f"mtllib {name}.mtl\nv 1 0 5\nv 0 1 5\nv 0 0 5\nvt 0.1 0.2\nvn 1 0 0\nusemtl m{name}\nf 1/1/1 2/1/1 3/1/1\n",
            encoding="utf-8")
        (src / f"{name}.mtl").write_text(f"newmtl m{name}\nmap_Kd {name}.jpg\n", encoding="utf-8")
        (src / f"{name}.jpg").write_bytes(b"jpg")
    f = fit([((0, 0, 0), (10, 20, 1)), ((1, 0, 0), (10, 21, 1))])  # 90° turn, shift (10, 20), +1 m
    nv, images = transform_tiles(src, tmp_path / "out", "m", (100.0, 0.0, 0.0), f, (10.0, 20.0, 0.0))
    assert nv == 6 and images == ["a.jpg", "b.jpg"]
    lines = (tmp_path / "out" / "m.obj").read_text(encoding="utf-8").splitlines()
    v = [list(map(float, l.split()[1:])) for l in lines if l.startswith("v ")]
    # Model (101, 0, 5): 90° about the fit centre then shifted, minus the origin.
    x, y, z = f.apply(101.0, 0.0, 5.0)
    assert v[0] == [round(x - 10, 4), round(y - 20, 4), round(z, 4)]
    assert [l for l in lines if l.startswith("vt ")] == ["vt 0.1 0.2", "vt 0.1 0.2"]
    vn = [list(map(float, l.split()[1:])) for l in lines if l.startswith("vn ")]
    assert abs(vn[0][0]) < 1e-6 and abs(vn[0][1] - 1) < 1e-6
    faces = [l for l in lines if l.startswith("f ")]
    assert faces == ["f 1/1/1 2/1/1 3/1/1", "f 4/2/2 5/2/2 6/2/2"]
    assert (tmp_path / "out" / "a.jpg").is_file()
