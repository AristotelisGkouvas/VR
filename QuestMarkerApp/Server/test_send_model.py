#!/usr/bin/env python3
"""send_model.py unit tests (no Quest, no network)."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch
from urllib.parse import unquote

import pytest

SCRIPT = Path(__file__).resolve().parent / "send_model.py"
spec = importlib.util.spec_from_file_location("send_model", SCRIPT)
sm = importlib.util.module_from_spec(spec)
sys.modules["send_model"] = sm
spec.loader.exec_module(sm)


@pytest.fixture(autouse=True)
def _key_ok(request, monkeypatch):
    """main() checks the key with a GET before uploading; tests of the upload skip that."""
    if not request.node.name.startswith(("test_wrong_key_stops", "test_check_key")):
        monkeypatch.setattr(sm, "check_key", lambda *a, **k: None)


# ---- units / format / name ---------------------------------------------------------------


@pytest.mark.parametrize("value,expected", [
    (None, 1.0), ("m", 1.0), ("MM", 0.001), ("cm", 0.01), ("ft", 0.3048), ("in", 0.0254),
    ("0.5", 0.5), (2, 2.0),
])
def test_parse_units(value, expected):
    assert sm.parse_units(value) == pytest.approx(expected)


@pytest.mark.parametrize("bad", ["", "yards", "0", "-1", "nan"])
def test_parse_units_rejects(bad):
    with pytest.raises(ValueError):
        sm.parse_units(bad)


@pytest.mark.parametrize("filename,fmt", [
    ("site.obj", "obj"), ("Site.OBJ", "obj"), ("plan.DXF", "dxf"), ("building.Ifc", "ifc"),
])
def test_infer_format(filename, fmt):
    assert sm.infer_format(Path(filename)) == fmt


def test_infer_format_rejects_unknown():
    with pytest.raises(ValueError):
        sm.infer_format(Path("model.stl"))


def test_default_name_is_stem():
    assert sm.default_name(Path("C:/data/Κτήριο Α.obj")) == "Κτήριο Α"


# ---- headers -----------------------------------------------------------------------------


def test_build_headers_defaults():
    h = sm.build_headers("KEY123", "site", "obj")
    assert h["X-Api-Key"] == "KEY123"
    assert h["X-Model-Name"] == "site"
    assert h["X-Model-Format"] == "obj"
    assert float(h["X-Model-Units"]) == 1.0
    assert h["X-Model-Up"] == "z"
    assert h["X-Model-Georeferenced"] == "true"
    assert "X-Model-Origin" not in h
    assert "X-Model-Project" not in h
    assert "X-Model-Source" not in h
    # every header value must survive the Latin-1 restriction of HTTP headers
    for v in h.values():
        v.encode("latin-1")


def test_build_headers_full():
    h = sm.build_headers("K", "Κτήριο Α", "dxf", units=sm.parse_units("mm"), up="y",
                         georeferenced=False, origin=(476512.3, 4203118.9, 112.4), project=12,
                         source="Κτήριο Α.dxf")
    assert unquote(h["X-Model-Name"]) == "Κτήριο Α"
    assert unquote(h["X-Model-Source"]) == "Κτήριο Α.dxf"
    h["X-Model-Name"].encode("latin-1")  # percent-encoded, so header-safe
    assert float(h["X-Model-Units"]) == pytest.approx(0.001)
    assert h["X-Model-Up"] == "y"
    assert h["X-Model-Georeferenced"] == "false"
    e, n, z = (float(v) for v in h["X-Model-Origin"].split(","))
    assert (e, n, z) == pytest.approx((476512.3, 4203118.9, 112.4))
    assert h["X-Model-Project"] == "12"
    assert h["X-Model-Format"] == "dxf"


def test_build_headers_rejects_bad_input():
    with pytest.raises(ValueError):
        sm.build_headers("K", "x", "ifc")  # IFC never reaches the headset
    with pytest.raises(ValueError):
        sm.build_headers("K", "x", "obj", up="x")
    with pytest.raises(ValueError):
        sm.build_headers("K", "x", "obj", origin=(1, 2))
    with pytest.raises(ValueError):
        sm.build_headers("K", "   ", "obj")


# ---- OBJ writer --------------------------------------------------------------------------


def _triangle(storey: str, ifc_type: str, name: str, z: float) -> "sm.Mesh":
    return sm.Mesh(storey, ifc_type, name,
                   verts=[(0.0, 0.0, z), (1.0, 0.0, z), (0.0, 1.0, z)],
                   faces=[(0, 1, 2)])


def test_write_obj_groups_and_materials(tmp_path):
    meshes = [
        _triangle("Ground floor", "IfcWall", "W1", 0.0),
        _triangle("Ground floor", "IfcSlab", "S1", 0.0),
        _triangle("Level 1", "IfcWall", "W2", 3.0),
        _triangle("Ground floor", "IfcWall", "W3", 0.0),  # back to the first storey: same g
    ]
    dst = tmp_path / "out.obj"
    n_verts, n_tri = sm.write_obj(dst, meshes, mtl_file="out.mtl")
    assert (n_verts, n_tri) == (12, 4)

    lines = dst.read_text(encoding="utf-8").splitlines()
    assert "mtllib out.mtl" in lines
    g_lines = [l for l in lines if l.startswith("g ")]
    assert g_lines == ["g Ground_floor", "g Level_1"]
    usemtl = [l for l in lines if l.startswith("usemtl ")]
    assert usemtl == ["usemtl IfcWall", "usemtl IfcSlab", "usemtl IfcWall", "usemtl IfcWall"]
    v_lines = [l for l in lines if l.startswith("v ")]
    assert len(v_lines) == 12
    assert v_lines[0] == "v 0.000000 0.000000 0.000000"
    f_lines = [l for l in lines if l.startswith("f ")]
    # 1-based, offset by the vertices of the previous meshes
    assert f_lines == ["f 1 2 3", "f 4 5 6", "f 7 8 9", "f 10 11 12"]
    # Level 1 geometry lives in the second group
    level1_start = lines.index("g Level_1")
    assert "v 0.000000 0.000000 3.000000" in lines[level1_start:]


def test_write_obj_skips_empty_meshes(tmp_path):
    dst = tmp_path / "e.obj"
    n_verts, n_tri = sm.write_obj(dst, [sm.Mesh("S", "IfcWall", "empty", [], [])])
    assert (n_verts, n_tri) == (0, 0)
    text = dst.read_text(encoding="utf-8")
    assert "g S" in text and "usemtl" not in text and "\nv " not in text


def test_write_mtl_one_material_per_type(tmp_path):
    dst = tmp_path / "m.mtl"
    count = sm.write_mtl(dst, ["IfcWall", "IfcSlab", "IfcWall", "IfcSomethingNew"])
    assert count == 3
    text = dst.read_text(encoding="utf-8")
    assert text.count("newmtl ") == 3
    assert "newmtl IfcWall\nKd 0.850 0.820 0.760" in text
    assert "newmtl IfcSomethingNew\nKd 0.650 0.650 0.650" in text  # default colour


def test_dms_to_degrees():
    assert sm._dms_to_degrees((37, 58, 30)) == pytest.approx(37.975)
    assert sm._dms_to_degrees((-23, 30, 0)) == pytest.approx(-23.5)
    assert sm._dms_to_degrees((37, 58, 30, 500000)) == pytest.approx(37.975 + 0.5 / 3600)
    assert sm._dms_to_degrees(None) is None


# ---- HTTP (requests mocked) --------------------------------------------------------------


def _ok_response(payload: dict) -> MagicMock:
    r = MagicMock()
    r.ok = True
    r.status_code = 200
    r.json.return_value = payload
    r.text = str(payload)
    return r


def test_send_model_streams_file(tmp_path, capsys):
    model = tmp_path / "site.obj"
    model.write_bytes(b"v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n")
    headers = sm.build_headers("KEY", "site", "obj")
    seen = {}

    def fake_post(url, data=None, headers=None, timeout=None):
        seen["url"] = url
        seen["headers"] = headers
        seen["has_read"] = hasattr(data, "read")
        seen["len"] = len(data)
        seen["body"] = b"".join(iter(data))  # consume like http.client would
        return _ok_response({"success": True, "name": "site", "bytes": seen["len"]})

    with patch("requests.post", side_effect=fake_post) as post:
        r = sm.send_model(model, "192.168.1.30", 8080, headers)

    assert post.call_count == 1
    assert r.ok
    assert seen["url"] == "http://192.168.1.30:8080/api/model"
    assert seen["headers"]["X-Api-Key"] == "KEY"
    assert seen["headers"]["X-Model-Format"] == "obj"
    assert seen["has_read"], "body must be a readable file object so requests streams it"
    assert seen["len"] == model.stat().st_size
    assert seen["body"] == model.read_bytes()
    out = capsys.readouterr().out
    assert "Uploading site.obj" in out and "100.0%" in out


def test_main_sends_obj_with_cli_options(tmp_path):
    model = tmp_path / "Κτήριο Α.obj"
    model.write_bytes(b"v 0 0 0\n")
    with patch("requests.post", return_value=_ok_response({"success": True})) as post:
        sm.main([str(model), "--quest-ip", "10.0.0.5", "--port", "9000", "--api-key", "ABC",
                 "--units", "mm", "--up", "y", "--origin", "1.5", "2.5", "3.5", "--project", "7"])
    args, kwargs = post.call_args
    assert args[0] == "http://10.0.0.5:9000/api/model"
    h = kwargs["headers"]
    assert unquote(h["X-Model-Name"]) == "Κτήριο Α"
    assert unquote(h["X-Model-Source"]) == "Κτήριο Α.obj"
    assert float(h["X-Model-Units"]) == pytest.approx(0.001)
    assert h["X-Model-Up"] == "y"
    assert h["X-Model-Georeferenced"] == "true"
    assert h["X-Model-Origin"] == "1.5,2.5,3.5"
    assert h["X-Model-Project"] == "7"
    assert hasattr(kwargs["data"], "read")


def test_main_local_flag(tmp_path):
    model = tmp_path / "plan.dxf"
    model.write_bytes(b"0\nSECTION\n0\nEOF\n")
    with patch("requests.post", return_value=_ok_response({"success": True})) as post:
        sm.main([str(model), "--quest-ip", "10.0.0.5", "--api-key", "ABC", "--local", "--name", "Plan"])
    h = post.call_args.kwargs["headers"]
    assert h["X-Model-Georeferenced"] == "false"
    assert h["X-Model-Format"] == "dxf"
    assert h["X-Model-Name"] == "Plan"


def test_list_and_delete_urls():
    with patch("requests.get", return_value=_ok_response([{"name": "a"}])) as get:
        assert sm.list_models("1.2.3.4", 8080, "K") == [{"name": "a"}]
    assert get.call_args.args[0] == "http://1.2.3.4:8080/api/models"
    assert get.call_args.kwargs["headers"] == {"X-Api-Key": "K"}

    with patch("requests.delete", return_value=_ok_response({"success": True})) as delete:
        sm.delete_model("1.2.3.4", 8080, "K", "Κτήριο Α")
    assert delete.call_args.args[0] == "http://1.2.3.4:8080/api/model/" + "%CE%9A%CF%84%CE%AE%CF%81%CE%B9%CE%BF%20%CE%91"
    assert delete.call_args.kwargs["headers"] == {"X-Api-Key": "K"}


def test_main_exits_without_model(capsys):
    with pytest.raises(SystemExit):
        sm.main(["--quest-ip", "10.0.0.5", "--api-key", "ABC"])


# ---- IFC (only when ifcopenshell is installed) --------------------------------------------


def test_convert_ifc_to_obj_roundtrip(tmp_path):
    ifcopenshell = pytest.importorskip("ifcopenshell")
    pytest.importorskip("ifcopenshell.api")
    import ifcopenshell.api  # noqa: F401

    run = ifcopenshell.api.run
    f = run("project.create_file", version="IFC4")
    project = run("root.create_entity", f, ifc_class="IfcProject", name="Test")
    run("unit.assign_unit", f, length={"is_metric": True, "raw": "MILLIMETERS"})
    ctx = run("context.add_context", f, context_type="Model")
    body = run("context.add_context", f, context_type="Model", context_identifier="Body",
               target_view="MODEL_VIEW", parent=ctx)
    site = run("root.create_entity", f, ifc_class="IfcSite", name="Site")
    building = run("root.create_entity", f, ifc_class="IfcBuilding", name="B")
    storey = run("root.create_entity", f, ifc_class="IfcBuildingStorey", name="Ground floor")
    run("aggregate.assign_object", f, relating_object=project, products=[site])
    run("aggregate.assign_object", f, relating_object=site, products=[building])
    run("aggregate.assign_object", f, relating_object=building, products=[storey])
    wall = run("root.create_entity", f, ifc_class="IfcWall", name="W1")
    run("geometry.edit_object_placement", f, product=wall)
    rep = run("geometry.add_wall_representation", f, context=body, length=5000, height=3000, thickness=200)
    run("geometry.assign_representation", f, product=wall, representation=rep)
    run("spatial.assign_container", f, relating_structure=storey, products=[wall])
    src = tmp_path / "t.ifc"
    f.write(str(src))

    dst = tmp_path / "t.obj"
    info = sm.convert_ifc_to_obj(src, dst)
    assert info.unit_scale == pytest.approx(0.001)
    assert info.georeferenced is False  # no IfcMapConversion in this file
    assert info.products == 1 and info.triangles >= 12 and info.storeys == 1
    text = dst.read_text(encoding="utf-8")
    assert "g Ground_floor" in text and "usemtl IfcWall" in text
    assert (tmp_path / "t.mtl").exists()
    # 5 m long wall written in metres (not millimetres)
    xs = [float(l.split()[1]) for l in text.splitlines() if l.startswith("v ")]
    assert max(xs) - min(xs) == pytest.approx(5.0, abs=1e-3)


# ---- validation / error handling ----------------------------------------------------------


@pytest.mark.parametrize("origin", [(float("nan"), 0, 0), (0, float("inf"), 0)])
def test_build_headers_rejects_non_finite_origin(origin):
    # The headset answers 400 for these; catch them before a 300 MB upload.
    with pytest.raises(ValueError):
        sm.build_headers("K", "site", "obj", origin=origin)


def test_main_requires_api_key(monkeypatch, tmp_path):
    monkeypatch.delenv("QUEST_KEY", raising=False)
    model = tmp_path / "a.obj"
    model.write_bytes(b"v 0 0 0\n")
    with pytest.raises(SystemExit) as exc:
        sm.main([str(model), "--quest-ip", "10.0.0.5"])
    assert exc.value.code == 2


def test_main_api_key_from_env(monkeypatch, tmp_path):
    monkeypatch.setenv("QUEST_KEY", "ENVKEY")
    model = tmp_path / "a.obj"
    model.write_bytes(b"v 0 0 0\n")
    with patch("requests.post", return_value=_ok_response({"success": True})) as post:
        sm.main([str(model), "--quest-ip", "10.0.0.5"])
    assert post.call_args.kwargs["headers"]["X-Api-Key"] == "ENVKEY"


def test_main_list_unreachable_is_a_clean_exit():
    with patch("requests.get", side_effect=sm.requests.ConnectionError("refused")):
        with pytest.raises(SystemExit) as exc:
            sm.main(["--list", "--quest-ip", "10.0.0.5", "--api-key", "K"])
    assert "Could not reach the headset" in str(exc.value.code)


def test_wrong_key_stops_before_upload(tmp_path):
    # The headset closes a wrong-key upload unread; the key is checked first so the user
    # gets a clear message instead of a reset connection after sending megabytes.
    model = tmp_path / "a.obj"
    model.write_bytes(b"v 0 0 0\n")
    with patch("requests.get", return_value=MagicMock(status_code=401)) as get, \
            patch("requests.post") as post:
        with pytest.raises(SystemExit) as exc:
            sm.main([str(model), "--quest-ip", "10.0.0.5", "--api-key", "bad"])
    assert "rejected the key" in str(exc.value.code)
    assert get.call_args.args[0] == "http://10.0.0.5:8080/api/status"
    assert get.call_args.kwargs["headers"] == {"X-Api-Key": "bad"}
    post.assert_not_called()


def test_check_key_lockout_and_ok():
    with patch("requests.get", return_value=MagicMock(status_code=429)):
        with pytest.raises(PermissionError):
            sm.check_key("1.2.3.4", 8080, "K")
    with patch("requests.get", return_value=MagicMock(status_code=200)):
        sm.check_key("1.2.3.4", 8080, "K")


def test_list_models_wrong_key_message():
    r = MagicMock(status_code=401)
    with patch("requests.get", return_value=r):
        with pytest.raises(PermissionError):
            sm.list_models("1.2.3.4", 8080, "bad")
