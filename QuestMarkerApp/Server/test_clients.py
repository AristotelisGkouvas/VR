#!/usr/bin/env python3
"""send_project / terraweb_bridge / fake_scenarios client tests (no Quest, no network)."""

from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest
import requests

HERE = Path(__file__).resolve().parent


def _load(name: str):
    spec = importlib.util.spec_from_file_location(name, HERE / f"{name}.py")
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    return mod


sp = _load("send_project")
tb = _load("terraweb_bridge")
fs = _load("fake_scenarios")


def _response(status: int, body=None, headers=None) -> MagicMock:
    r = MagicMock()
    r.status_code = status
    r.ok = 200 <= status < 300
    r.text = json.dumps(body) if body is not None else ""
    r.content = r.text.encode()
    r.json.return_value = body
    r.headers = headers or {}
    if not r.ok:
        r.raise_for_status.side_effect = requests.HTTPError(f"HTTP {status}", response=r)
    return r


# ---- send_project -------------------------------------------------------------------------


def test_read_points_semicolon_decimal_comma_and_case_insensitive_extras(tmp_path):
    csv_path = tmp_path / "p.csv"
    csv_path.write_text("ID;Easting;Northing;Elevation;Label;Icon\n"
                        "P1;476500,5;4205000,25;152,1;Κολόνα;piezometer\n"
                        "\n"
                        "P2;476501;4205001;153;;\n", encoding="utf-8")
    pts = sp.read_points(csv_path)
    assert [p["id"] for p in pts] == ["P1", "P2"]
    assert pts[0]["e"] == pytest.approx(476500.5) and pts[0]["n"] == pytest.approx(4205000.25)
    assert pts[0]["label"] == "Κολόνα" and pts[0]["icon"] == "piezometer"
    assert "label" not in pts[1]


@pytest.mark.parametrize("row", ["P1,nan,1,1", "P1,1,inf,1", "P1,1,2", "P1,,2,3"])
def test_read_points_rejects_bad_rows(tmp_path, row):
    csv_path = tmp_path / "p.csv"
    csv_path.write_text("id,e,n,h\n" + row + "\n", encoding="utf-8")
    with pytest.raises(SystemExit):
        sp.read_points(csv_path)


def test_read_points_sample_file():
    pts = sp.read_points(HERE / "sample_project.csv")
    assert len(pts) >= 2 and all({"id", "e", "n", "h"} <= p.keys() for p in pts)


def test_post_project_sends_utf8_json_and_reports_status(capsys):
    body = {"name": "Έργο", "points": [{"id": "1", "e": 1.0, "n": 2.0, "h": 3.0}]}
    with patch("requests.post", return_value=_response(200, {"success": True})) as post:
        assert sp.post_project("http://q/api/project", body, {"X-Api-Key": "K"}) == 0
    kwargs = post.call_args.kwargs
    assert kwargs["headers"]["X-Api-Key"] == "K"
    assert "charset=utf-8" in kwargs["headers"]["Content-Type"]
    assert json.loads(kwargs["data"].decode("utf-8")) == body
    with patch("requests.post", return_value=_response(401, {"error": "Unauthorized"})):
        assert sp.post_project("http://q/api/project", body, {}) == 1
    assert "--api-key" in capsys.readouterr().err


def test_post_project_network_error_and_size_cap(capsys):
    body = {"points": [{"id": "1", "e": 1, "n": 2, "h": 3}]}
    with patch("requests.post", side_effect=requests.ConnectionError("refused")):
        assert sp.post_project("http://q/api/project", body, {}) == 1
    big = {"points": [{"id": str(i), "label": "x" * 200, "e": 1, "n": 2, "h": 3} for i in range(3000)]}
    with patch("requests.post") as post:
        assert sp.post_project("http://q/api/project", big, {}) == 2
    post.assert_not_called()


def test_send_project_main_exit_code_follows_headset(tmp_path, monkeypatch):
    csv_path = tmp_path / "p.csv"
    csv_path.write_text("id,e,n,h\nA,1,2,3\n", encoding="utf-8")
    monkeypatch.setattr(sys, "argv", ["send_project.py", str(csv_path), "--quest-ip", "1.2.3.4", "--api-key", "K"])
    with patch("requests.post", return_value=_response(400, {"error": "bad"})):
        with pytest.raises(SystemExit) as exc:
            sp.main()
    assert exc.value.code == 1
    with patch("requests.post", return_value=_response(200, {"success": True})):
        with pytest.raises(SystemExit) as exc:
            sp.main()
    assert exc.value.code == 0


# ---- terraweb_bridge ----------------------------------------------------------------------


def test_choose_area_matches_numeric_area_id():
    areas = [{"area_id": 7, "name": "Γέφυρα"}, {"area_id": 8, "name": "Σήραγγα"}]
    assert tb.choose_area(areas, "7")["name"] == "Γέφυρα"
    assert tb.choose_area(areas, "all") is None


def test_sensor_points_skips_bad_geometry():
    def sensor(name, coords, label="Piezometer"):
        return {"sensor_name": name, "is_active": True, "type": {"label": label},
                "sensor_geometry": {"type": "Point", "coordinates": coords}}

    pts, skipped = tb.sensor_points([
        sensor("ok1", [23.7, 37.9, 100.0]),
        sensor("ok2", [23.7001, 37.9, 101.0]),
        sensor("str", ["x", 37.9, 100.0]),
        sensor("range", [23.7, 137.9, 100.0]),
        sensor("noz", [23.7, 37.9]),
    ], active_only=True)
    assert [p["id"] for p in pts] == ["ok1", "ok2"]
    assert pts[0]["e"] == 0 and pts[1]["e"] > 5  # ~8.8 m east
    assert pts[0]["icon"] == "piezometer"
    assert len(skipped) == 3


def test_bridge_send_project_exit_codes():
    body = {"name": "n", "points": [{"id": "a", "e": 0, "n": 0, "h": 0}]}
    with patch("requests.post", return_value=_response(200, {"success": True})) as post:
        assert tb.send_project("1.2.3.4", 8080, "K", body) == 0
    assert post.call_args.args[0] == "http://1.2.3.4:8080/api/project"
    assert post.call_args.kwargs["headers"]["X-Api-Key"] == "K"
    with patch("requests.post", return_value=_response(401, {"error": "Unauthorized"})):
        assert tb.send_project("1.2.3.4", 8080, None, body) == 1
    with patch("requests.post", side_effect=requests.Timeout("slow")):
        assert tb.send_project("1.2.3.4", 8080, "K", body) == 1


@pytest.mark.parametrize("base", ["http://demo.iqsoft.xyz", "http://89.116.27.151", "ftp://x", "demo.iqsoft.xyz"])
def test_bridge_refuses_cleartext_backend(base):
    with pytest.raises(ValueError):
        tb.check_backend_url(base)


@pytest.mark.parametrize("base", ["https://demo.iqsoft.xyz", "https://89.116.27.151:8443", "http://127.0.0.1:8000"])
def test_bridge_accepts_https_or_loopback_backend(base):
    tb.check_backend_url(base)


def test_fingerprint_normalization():
    fp = "AB:" * 31 + "CD"
    assert tb.normalize_fingerprint(fp) == "ab" * 31 + "cd"
    for bad in ("", "abc", "zz" * 32, "ab" * 20):
        with pytest.raises(ValueError):
            tb.normalize_fingerprint(bad)


def test_pinned_session_checks_the_certificate_hash():
    fp = "0f" * 32
    tw = tb.Terraweb("https://89.116.27.151", pin_sha256=fp.upper())
    adapter = tw.session.get_adapter("https://89.116.27.151/api/auth/login/")
    assert isinstance(adapter, tb._PinnedAdapter)
    # urllib3 compares the peer certificate with this hash on every new TLS connection.
    assert adapter.poolmanager.connection_pool_kw["assert_fingerprint"] == fp
    plain = tb.Terraweb("https://demo.iqsoft.xyz")
    assert plain.session.verify is True
    assert not isinstance(plain.session.get_adapter("https://demo.iqsoft.xyz/"), tb._PinnedAdapter)


def test_bridge_main_rejects_http_backend(monkeypatch):
    monkeypatch.setattr(sys, "argv", ["terraweb_bridge.py", "--backend", "http://demo.iqsoft.xyz",
                                      "--list-only", "-u", "me"])
    with patch("getpass.getpass") as ask, patch("requests.Session.post") as post:
        with pytest.raises(SystemExit) as exc:
            tb.main()
    assert exc.value.code == 2
    ask.assert_not_called()
    post.assert_not_called()


# ---- fake_scenarios -----------------------------------------------------------------------


def test_fake_scenarios_post_sends_key():
    with patch("requests.post", return_value=_response(200, {"success": True})) as post:
        fs.post("http://q:8080", [{"id": "a"}], clear=True, api_key="K")
    assert post.call_args.kwargs["headers"] == {"X-Api-Key": "K"}
    assert post.call_args.kwargs["json"]["clearExisting"] is True


def test_fake_scenarios_live_backs_off_on_429():
    responses = iter([_response(200, {}), _response(429, {"error": "Rate limit"}, {"Retry-After": "1"}),
                      _response(200, {})])

    def fake_post(*args, **kwargs):
        try:
            return next(responses)
        except StopIteration:
            raise KeyboardInterrupt

    with patch("requests.post", side_effect=fake_post), patch.object(fs.time, "sleep") as sleep:
        fs.run_live("http://q:8080", hz=5, api_key="K")  # ends via KeyboardInterrupt → "Stopped."
    assert any(call.args[0] == 1.0 for call in sleep.call_args_list)
