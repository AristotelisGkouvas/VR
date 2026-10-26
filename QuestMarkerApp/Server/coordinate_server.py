#!/usr/bin/env python3
"""
Coordinate bridge server for the Quest Marker App (v1.2).

Features:
  - Thread-safe store, API key auth, rate limiting
  - Forward to Quest HTTP API
  - Long-poll /api/events for push-style clients
  - Session export helpers, GPS origin forward
  - UDP discovery listener helper

Usage:
    python coordinate_server.py --quest-ip 192.168.1.100 --quest-key ABCD2345
    QUEST_KEY=ABCD2345 QUEST_API_KEY=secret python coordinate_server.py --api-key secret

Two keys, two directions:
  --quest-key / QUEST_KEY   the headset's own key (shown in its main menu), sent as
                            X-Api-Key on every request this bridge forwards to the Quest.
                            The Quest always requires one.
  --api-key / QUEST_API_KEY what clients of THIS bridge must send (X-Api-Key / Bearer);
                            required when listening beyond localhost. If no --quest-key
                            is given, this key is also forwarded to the Quest (legacy).
"""

from __future__ import annotations

import argparse
import hmac
import json
import logging
import math
import os
import socket
import sys
import threading
import time
from collections import defaultdict, deque
from typing import Any
from urllib.parse import quote, urlsplit

import requests
from flask import Flask, Response, jsonify, request
from flask_cors import CORS
from werkzeug.exceptions import HTTPException

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("quest-marker")

app = Flask(__name__)
# CORS is opt-in (--cors): any web page could otherwise drive this server and, through it, the Quest.
# Bodies are coordinate/session JSON; the headset itself caps JSON bodies at 512 KB.
app.config["MAX_CONTENT_LENGTH"] = 16 * 1024 * 1024

_store_lock = threading.RLock()
_coordinates_store: list[dict[str, Any]] = []
_event_cond = threading.Condition(_store_lock)
_last_event_ts = 0
_rate: dict[str, deque] = defaultdict(deque)
_rate_lock = threading.Lock()
# Long-polls from the headset itself (remote address == QUEST_IP), guarded by _store_lock.
# While it polls, /api/events is how it gets the store, so POSTs are not also forwarded to
# it (it used to receive every batch twice). The grace covers the gap between two polls,
# including the client's back-off of up to 30 s after a failed one.
_quest_polls_in_flight = 0
_quest_last_poll = 0.0
PUSH_GRACE_SECONDS = 35.0

QUEST_IP = os.environ.get("QUEST_IP", "192.168.1.100")
QUEST_PORT = int(os.environ.get("QUEST_PORT", "8080"))
QUEST_TIMEOUT = float(os.environ.get("QUEST_TIMEOUT", "10"))
API_KEY = os.environ.get("QUEST_API_KEY", "")  # what this bridge's clients must send
QUEST_KEY = os.environ.get("QUEST_KEY", "")  # what this bridge sends to the headset
RATE_LIMIT = int(os.environ.get("RATE_LIMIT", "120"))  # per minute
CORS_ENABLED = False  # set by --cors
LOOPBACK_HOSTS = frozenset({"127.0.0.1", "localhost", "::1"})


def quest_base() -> str:
    return f"http://{QUEST_IP}:{QUEST_PORT}"


def quest_key() -> str:
    """The key forwarded to the Quest: --quest-key, else (legacy) the bridge's own --api-key."""
    return QUEST_KEY or API_KEY


def _now_ms() -> int:
    return int(time.time() * 1000)


def _finite_number(value: Any) -> bool:
    """A real JSON number (not bool) that is not NaN/Infinity."""
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _ensure_coord_defaults(coord: dict[str, Any], index: int) -> dict[str, Any]:
    if "id" not in coord or not coord["id"]:
        coord["id"] = f"coord_{_now_ms()}_{index}"
    if "timestamp" not in coord:
        coord["timestamp"] = _now_ms()
    if "scale" not in coord or not coord.get("scale"):
        coord["scale"] = 0.1
    if "markerType" not in coord:
        coord["markerType"] = "sphere"
    if "layer" not in coord:
        coord["layer"] = "default"
    if "crs" not in coord:
        coord["crs"] = "local"
    return coord


def _check_auth() -> tuple[bool, Any]:
    if not API_KEY:
        return True, None
    path = request.path or ""
    if path in ("/api/health", "/health"):
        return True, None
    key = request.headers.get("X-Api-Key") or ""
    auth = request.headers.get("Authorization") or ""
    # Constant-time comparison: response timing must not reveal how much of a guess matched.
    expected = API_KEY.encode("utf-8")
    if key and hmac.compare_digest(key.encode("utf-8"), expected):
        return True, None
    if auth[:7].lower() == "bearer " and hmac.compare_digest(auth[7:].strip().encode("utf-8"), expected):
        return True, None
    return False, (jsonify({"error": "Unauthorized - send X-Api-Key or Authorization: Bearer"}), 401)


def _check_rate() -> tuple[bool, Any]:
    if RATE_LIMIT <= 0:
        return True, None
    ip = request.remote_addr or "unknown"
    now = time.time()
    # Flask serves each request on its own thread: check-and-append must be atomic.
    with _rate_lock:
        q = _rate[ip]
        while q and now - q[0] > 60:
            q.popleft()
        if len(q) >= RATE_LIMIT:
            return False, (jsonify({"error": "Rate limit exceeded"}), 429, {"Retry-After": "60"})
        q.append(now)
        if len(_rate) > 1024:  # forget idle clients so the table cannot grow without bound
            for stale in [k for k, v in _rate.items() if not v or now - v[-1] > 60]:
                del _rate[stale]
    return True, None


def _host_only(value: str) -> str:
    """'host:port' / '[::1]:5000' / 'http://x:1' netloc → lower-case host without port."""
    try:
        return (urlsplit("//" + (value or "")).hostname or "").lower()
    except ValueError:
        return ""


def _check_browser() -> tuple[bool, Any]:
    """
    Without --api-key the bridge trusts whoever can reach it, which by design is only this PC
    (127.0.0.1). A web page the user visits can still reach it: by DNS rebinding (evil.com
    re-resolved to 127.0.0.1, so its script reads every answer, sessions included) or by a
    plain cross-site form POST (/api/clear, /api/calibrate/reset ...), and the bridge
    forwards both to the headset with the headset's key. So a keyless bridge only answers
    requests addressed to a loopback name, and refuses cross-site browser requests unless
    --cors was given. With a key, a browser cannot supply it cross-site anyway.
    """
    if API_KEY:
        return True, None
    if _host_only(request.host) not in LOOPBACK_HOSTS:
        return False, (jsonify({"error": "Host not allowed: a bridge without --api-key answers only "
                                         "http://127.0.0.1 / localhost"}), 403)
    origin = request.headers.get("Origin")
    if origin and not CORS_ENABLED and _host_only(urlsplit(origin).netloc) not in LOOPBACK_HOSTS:
        return False, (jsonify({"error": "Cross-site browser request refused (start with --cors to allow)"}), 403)
    return True, None


@app.errorhandler(HTTPException)
def _http_error_as_json(e: HTTPException):
    """404/405/413/... as JSON like every other answer, not Werkzeug's HTML page."""
    return jsonify({"error": e.name, "detail": e.description}), e.code


@app.before_request
def _security_gate():
    ok, resp = _check_rate()
    if not ok:
        return resp
    ok, resp = _check_browser()
    if not ok:
        return resp
    ok, resp = _check_auth()
    if not ok:
        return resp
    return None


def _quest_request(method: str, path: str, **kwargs) -> dict[str, Any]:
    url = f"{quest_base()}{path}"
    kwargs.setdefault("timeout", QUEST_TIMEOUT)
    headers = kwargs.pop("headers", {})
    key = quest_key()
    if key:
        headers = {**headers, "X-Api-Key": key}
    try:
        response = requests.request(method, url, headers=headers, **kwargs)
        try:
            body = response.json()
        except ValueError:
            body = {"raw": response.text}
        return {"ok": response.ok, "status_code": response.status_code, "body": body}
    except requests.RequestException as e:
        log.warning("Quest request failed %s %s: %s", method, path, e)
        raise


def _notify_events():
    """Call with _store_lock held. Event stamps strictly increase: two changes within one
    millisecond must not share a stamp, or a client polling with since=<first> misses the second."""
    global _last_event_ts
    _last_event_ts = max(_now_ms(), _last_event_ts + 1)
    _event_cond.notify_all()


def _quest_is_polling() -> bool:
    """True while the headset is long-polling /api/events (or did so within the grace period)."""
    with _store_lock:
        return _quest_polls_in_flight > 0 or time.time() - _quest_last_poll < PUSH_GRACE_SECONDS


def _quest_path_id(value: str) -> str:
    """A marker id as one URL path segment (ids may hold spaces, '/', '#', Greek letters)."""
    return quote(str(value), safe="")


# ==================== API ====================

@app.route("/api/health", methods=["GET"])
def health():
    with _store_lock:
        count = len(_coordinates_store)
    return jsonify(
        {
            "status": "healthy",
            "timestamp": _now_ms(),
            "stored": count,
            "quest_ip": QUEST_IP,
            "quest_port": QUEST_PORT,
            "auth": bool(API_KEY),
            "version": "1.2.0",
        }
    )


@app.route("/api/coordinates", methods=["GET"])
def get_coordinates():
    with _store_lock:
        snapshot = list(_coordinates_store)
    return jsonify({"coordinates": snapshot, "clearExisting": False, "count": len(snapshot)})


@app.route("/api/coordinates", methods=["POST"])
def add_coordinates():
    data = request.get_json(silent=True)
    if not isinstance(data, dict) or not data:
        return jsonify({"error": "Body must be a JSON object like {\"coordinates\": [...]}"}), 400

    coords = data.get("coordinates", [])
    if not isinstance(coords, list):
        return jsonify({"error": "'coordinates' must be an array"}), 400
    for i, coord in enumerate(coords):
        if not isinstance(coord, dict):
            return jsonify({"error": f"coordinates[{i}] must be an object"}), 400
        for axis in ("x", "y", "z"):
            if axis in coord and not _finite_number(coord[axis]):
                return jsonify({"error": f"coordinates[{i}].{axis} must be a finite number"}), 400

    clear = bool(data.get("clearExisting", False))
    remove_missing = bool(data.get("removeMissing", False))
    send_to_quest = bool(data.get("sendToQuest", True))

    with _store_lock:
        if clear:
            _coordinates_store.clear()
        prepared = []
        for i, coord in enumerate(coords):
            prepared.append(_ensure_coord_defaults(dict(coord), len(_coordinates_store) + i))
        # Same semantics as the headset: an id that exists is updated in place, not duplicated
        # (duplicates were replayed to push clients as two markers with one id).
        index = {c.get("id"): n for n, c in enumerate(_coordinates_store)}
        for coord in prepared:
            n = index.get(coord["id"])
            if n is None:
                index[coord["id"]] = len(_coordinates_store)
                _coordinates_store.append(coord)
            else:
                _coordinates_store[n] = coord
        if remove_missing and not clear:
            keep = {c["id"] for c in prepared}
            _coordinates_store[:] = [c for c in _coordinates_store if c.get("id") in keep]
        _notify_events()

    if send_to_quest and _quest_is_polling():
        # The headset is long-polling: the event above already delivers this batch.
        return jsonify({"success": True, "stored": len(prepared), "delivery": "events"})

    if send_to_quest:
        # `success` is the Quest's verdict: a 401 from the headset must not read as success.
        try:
            result = _send_to_quest(prepared, clear, remove_missing)
            return jsonify(
                {
                    "success": result.get("ok", False),
                    "stored": len(prepared),
                    "quest_status": result.get("status_code"),
                    "quest_response": result.get("body"),
                }
            )
        except Exception as e:
            return jsonify({"success": False, "stored": len(prepared), "quest_error": str(e)})

    return jsonify({"success": True, "stored": len(prepared)})


@app.route("/api/events", methods=["GET"])
def events_long_poll():
    """Long-poll: wait until store changes after `since`, or timeout."""
    try:
        since = int(request.args.get("since", "0"))
        timeout = float(request.args.get("timeout", "25"))
    except ValueError:
        return jsonify({"error": "since must be an integer and timeout a number"}), 400
    if not math.isfinite(timeout):
        return jsonify({"error": "timeout must be a finite number"}), 400
    timeout = max(0.0, min(timeout, 55.0))
    # removeMissing=1 (current headset builds): the full store as a sync — present markers
    # update in place, only dropped ones go. Without it (older builds): clear and replace.
    sync = request.args.get("removeMissing", "").lower() in ("1", "true", "yes")
    from_quest = request.remote_addr == QUEST_IP

    global _quest_polls_in_flight, _quest_last_poll
    deadline = time.time() + timeout
    with _event_cond:
        if from_quest:
            _quest_polls_in_flight += 1
        try:
            while _last_event_ts <= since and time.time() < deadline:
                remaining = deadline - time.time()
                if remaining <= 0:
                    break
                _event_cond.wait(timeout=min(remaining, 1.0))

            if _last_event_ts <= since:
                return jsonify({"coordinates": [], "timeout": True})

            snapshot = list(_coordinates_store)
            return jsonify(
                {
                    "coordinates": snapshot,
                    "clearExisting": not sync,
                    "removeMissing": sync,
                    "timestamp": _last_event_ts,
                }
            )
        finally:
            if from_quest:
                _quest_polls_in_flight -= 1
                _quest_last_poll = time.time()


# path: converter so ids containing "/" (sent as %2F) still reach this route.
@app.route("/api/coordinates/<path:coord_id>", methods=["DELETE"])
def delete_coordinate(coord_id: str):
    with _store_lock:
        before = len(_coordinates_store)
        _coordinates_store[:] = [c for c in _coordinates_store if c.get("id") != coord_id]
        removed = before - len(_coordinates_store)
        if removed:
            _notify_events()

    quest_result = None
    try:
        quest_result = _quest_request("DELETE", f"/api/coordinates/{_quest_path_id(coord_id)}")
    except Exception as e:
        quest_result = {"ok": False, "error": str(e)}

    return jsonify(
        {"success": quest_result.get("ok", False), "removed_local": removed, "quest_response": quest_result}
    )


@app.route("/api/coordinates/clear", methods=["POST"])
@app.route("/api/clear", methods=["POST"])
def clear_coordinates():
    with _store_lock:
        _coordinates_store.clear()
        _notify_events()
    quest_result = None
    try:
        quest_result = _quest_request("POST", "/api/clear")
    except Exception as e:
        quest_result = {"ok": False, "error": str(e)}
    return jsonify({"success": quest_result.get("ok", False), "quest_response": quest_result})


@app.route("/api/send-to-quest", methods=["POST"])
def send_stored_to_quest():
    with _store_lock:
        snapshot = list(_coordinates_store)
    try:
        result = _send_to_quest(snapshot, clear_existing=True)
        return jsonify(
            {
                "success": result.get("ok", False),
                "sent": len(snapshot),
                "quest_status": result.get("status_code"),
                "quest_response": result.get("body"),
            }
        )
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/quest-status", methods=["GET"])
def quest_status():
    try:
        result = _quest_request("GET", "/api/health", timeout=5)
        return jsonify(
            {
                "connected": result.get("ok", False),
                "quest_ip": QUEST_IP,
                "quest_port": QUEST_PORT,
                "response": result.get("body"),
            }
        )
    except Exception as e:
        return jsonify(
            {"connected": False, "quest_ip": QUEST_IP, "quest_port": QUEST_PORT, "error": str(e)}
        )


@app.route("/api/session/quest", methods=["GET"])
def session_from_quest():
    try:
        result = _quest_request("GET", "/api/session", timeout=10)
        # Pass-through: the Quest's status code travels with its body (e.g. 401).
        return jsonify(result.get("body") or result), result.get("status_code", 200)
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/session/quest", methods=["POST"])
def session_to_quest():
    data = request.get_json(silent=True)
    if not isinstance(data, dict) or not data:
        return jsonify({"error": "Body must be a session JSON object"}), 400
    try:
        result = _quest_request("POST", "/api/session", json=data, timeout=15)
        return jsonify(result.get("body") or result), result.get("status_code", 200)
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/gps-origin", methods=["POST"])
def gps_origin():
    data = request.get_json(silent=True)
    if not isinstance(data, dict) or not data:
        return jsonify({"error": "Body must be a JSON object {latitude, longitude, altitude}"}), 400
    lat, lon, alt = data.get("latitude"), data.get("longitude"), data.get("altitude", 0)
    if not (_finite_number(lat) and _finite_number(lon) and _finite_number(alt)) \
            or abs(lat) > 90 or abs(lon) > 180:
        return jsonify({"error": "latitude (±90), longitude (±180) and altitude must be finite numbers"}), 400
    try:
        result = _quest_request("POST", "/api/gps-origin", json=data)
        return jsonify({"success": result.get("ok", False), "quest_response": result.get("body")})
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/calibrate/start", methods=["POST"])
def start_calibration():
    try:
        result = _quest_request("POST", "/api/calibrate/start", timeout=5)
        return jsonify({"success": result.get("ok", False), "quest_response": result.get("body")})
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/calibrate", methods=["POST"])
def calibrate():
    data = request.get_json(silent=True)
    if not isinstance(data, dict) or not data:
        return jsonify({"error": "Body must be a JSON object"}), 400
    if "realX" not in data or "realZ" not in data:
        return jsonify({"error": "Missing realX or realZ"}), 400
    try:
        calibration_data = {
            "realX": float(data.get("realX", 0)),
            "realY": float(data.get("realY", 0)),
            "realZ": float(data.get("realZ", 0)),
            "rotationY": float(data.get("rotationY", 0)),
            "quickCalibrate": bool(data.get("quickCalibrate", True)),
            "coordinateSystem": data.get("coordinateSystem", "local"),
        }
    except (TypeError, ValueError) as e:
        return jsonify({"error": f"Invalid numeric fields: {e}"}), 400
    # float("nan") parses, but NaN is not JSON: the headset would reject the forwarded body.
    if not all(math.isfinite(calibration_data[k]) for k in ("realX", "realY", "realZ", "rotationY")):
        return jsonify({"error": "realX, realY, realZ and rotationY must be finite"}), 400
    try:
        result = _quest_request("POST", "/api/calibrate", json=calibration_data, timeout=10)
        return jsonify(
            {
                "success": result.get("ok", False),
                "calibration": calibration_data,
                "quest_response": result.get("body"),
            }
        )
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/calibrate/status", methods=["GET"])
def calibration_status():
    try:
        result = _quest_request("GET", "/api/calibrate/status", timeout=5)
        return jsonify({"success": result.get("ok", False), "quest_response": result.get("body")})
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/calibrate/reset", methods=["POST"])
def reset_calibration():
    try:
        result = _quest_request("POST", "/api/calibrate/reset", timeout=5)
        return jsonify({"success": result.get("ok", False), "quest_response": result.get("body")})
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


def _send_to_quest(
    coordinates: list[dict[str, Any]],
    clear_existing: bool = False,
    remove_missing: bool = False,
) -> dict[str, Any]:
    """Forward coordinates; returns the full {ok, status_code, body} result."""
    payload = {
        "coordinates": coordinates,
        "clearExisting": clear_existing,
        "removeMissing": remove_missing,
    }
    return _quest_request("POST", "/api/coordinates", json=payload, timeout=QUEST_TIMEOUT)


def send_to_quest_headset(
    coordinates: list[dict[str, Any]],
    clear_existing: bool = False,
    remove_missing: bool = False,
) -> dict[str, Any]:
    """Forward coordinates and return the Quest's response body (kept for callers/scripts)."""
    result = _send_to_quest(coordinates, clear_existing, remove_missing)
    return result.get("body") or result


def discover_quest(timeout: float = 3.0) -> list[dict[str, Any]]:
    """Listen for UDP beacons from NetworkDiscovery (port 48080)."""
    port = 48080
    found: dict[str, dict[str, Any]] = {}
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(("", port))
        except OSError as e:
            log.error("Cannot listen on UDP %s (%s): another discovery listener running?", port, e)
            return []
        sock.settimeout(0.5)
        deadline = time.time() + timeout
        log.info("Listening for Quest discovery beacons on UDP %s (%.1fs)…", port, timeout)
        while time.time() < deadline:
            try:
                data, addr = sock.recvfrom(4096)
                msg = json.loads(data.decode("utf-8"))
                if isinstance(msg, dict) and msg.get("service") == "quest-marker":
                    key = addr[0]
                    found[key] = {**msg, "ip": addr[0]}
            except socket.timeout:
                continue
            except (ValueError, UnicodeDecodeError):
                continue  # not a beacon
    finally:
        sock.close()
    return list(found.values())


def interactive_mode():
    print("\n" + "=" * 50)
    print("Quest Marker App - Interactive Mode v1.2")
    print("=" * 50)
    print("Commands: add, list, send, clear, del, status, discover,")
    print("          calibrate, startcal, calstatus, quit")
    print("=" * 50 + "\n")

    while True:
        try:
            cmd = input(">>> ").strip()
            if not cmd:
                continue
            parts = cmd.split()
            action = parts[0].lower()

            if action in ("quit", "exit"):
                print("Goodbye!")
                break

            elif action == "discover":
                devices = discover_quest(3.0)
                if not devices:
                    print("No beacons heard. Is the app running on Quest?")
                for d in devices:
                    print(f"  {d.get('ip')}:{d.get('port')} name={d.get('name')}")

            elif action == "add":
                if len(parts) < 4:
                    print("Usage: add x y z [label] [color] [type]")
                    continue
                try:
                    x, y, z = float(parts[1]), float(parts[2]), float(parts[3])
                    label = parts[4] if len(parts) > 4 else None
                    color = parts[5] if len(parts) > 5 else "#00FFFF"
                    mtype = parts[6] if len(parts) > 6 else "sphere"
                    coord = {
                        "id": f"coord_{_now_ms()}",
                        "x": x, "y": y, "z": z,
                        "label": label, "color": color,
                        "markerType": mtype, "scale": 0.1,
                        "timestamp": _now_ms(),
                    }
                    with _store_lock:
                        _coordinates_store.append(coord)
                        _notify_events()
                    print(f"Added {coord['id']} at ({x},{y},{z})")
                    if _quest_is_polling():
                        print("  -> Delivered through the headset's long-poll")
                        continue
                    try:
                        res = _send_to_quest([coord], False)
                        if res.get("ok"):
                            print("  -> Sent to Quest")
                        else:
                            print(f"  -> Quest rejected it: HTTP {res.get('status_code')} {res.get('body')}")
                    except Exception as e:
                        print(f"  -> Quest send failed: {e}")
                except ValueError:
                    print("Invalid numbers")

            elif action == "list":
                with _store_lock:
                    snap = list(_coordinates_store)
                if not snap:
                    print("Empty")
                else:
                    for i, c in enumerate(snap):
                        print(f"  {i+1}. {c.get('id')} ({c.get('x')},{c.get('y')},{c.get('z')}) {c.get('label')}")

            elif action == "send":
                with _store_lock:
                    snap = list(_coordinates_store)
                try:
                    res = _send_to_quest(snap, True)
                    if res.get("ok"):
                        print(f"Sent {len(snap)}")
                    else:
                        print(f"Quest rejected it: HTTP {res.get('status_code')} {res.get('body')}")
                except Exception as e:
                    print(f"Failed: {e}")

            elif action == "clear":
                with _store_lock:
                    _coordinates_store.clear()
                    _notify_events()
                try:
                    res = _quest_request("POST", "/api/clear", timeout=5)
                    if res.get("ok"):
                        print("Cleared (local + Quest)")
                    else:
                        print(f"Local cleared; Quest answered HTTP {res.get('status_code')} {res.get('body')}")
                except Exception as e:
                    print(f"Local cleared; Quest not reachable: {e}")

            elif action == "del":
                if len(parts) < 2:
                    print("Usage: del <id>")
                    continue
                cid = parts[1]
                with _store_lock:
                    _coordinates_store[:] = [c for c in _coordinates_store if c.get("id") != cid]
                    _notify_events()
                try:
                    _quest_request("DELETE", f"/api/coordinates/{_quest_path_id(cid)}", timeout=5)
                except Exception as e:
                    print(f"Quest del failed: {e}")
                print(f"Deleted {cid}")

            elif action == "status":
                try:
                    r = requests.get(f"{quest_base()}/api/health", timeout=5)
                    print(r.json())
                except Exception as e:
                    print(f"Not reachable: {e}")

            elif action == "calibrate":
                if len(parts) < 4:
                    print("Usage: calibrate x y z [rotation]")
                    continue
                try:
                    x, y, z = float(parts[1]), float(parts[2]), float(parts[3])
                    rot = float(parts[4]) if len(parts) > 4 else 0
                    payload = {
                        "realX": x, "realY": y, "realZ": z,
                        "rotationY": rot, "quickCalibrate": True,
                        "coordinateSystem": "local",
                    }
                    r = _quest_request("POST", "/api/calibrate", json=payload, timeout=10)
                    print(r.get("body"))
                except Exception as e:
                    print(e)

            elif action == "startcal":
                try:
                    r = _quest_request("POST", "/api/calibrate/start", timeout=5)
                    print(r.get("body"))
                except Exception as e:
                    print(e)

            elif action == "calstatus":
                try:
                    r = _quest_request("GET", "/api/calibrate/status", timeout=5)
                    print(json.dumps(r.get("body"), indent=2, ensure_ascii=False))
                except Exception as e:
                    print(e)

            else:
                print(f"Unknown: {action}")

        except KeyboardInterrupt:
            print("\nGoodbye!")
            break
        except Exception as e:
            log.exception("Interactive error")
            print(f"Error: {e}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Quest Marker Coordinate Server v1.2")
    parser.add_argument("--port", type=int, default=int(os.environ.get("PORT", "5000")))
    parser.add_argument("--quest-ip", type=str, default=QUEST_IP)
    parser.add_argument("--quest-port", type=int, default=QUEST_PORT)
    parser.add_argument("--api-key", type=str, default=API_KEY,
                        help="Key clients of this bridge must send (env QUEST_API_KEY). "
                             "Required when --host is not localhost")
    parser.add_argument("--quest-key", type=str, default=QUEST_KEY,
                        help="The headset's key from its main menu (env QUEST_KEY), sent as "
                             "X-Api-Key on every request forwarded to the Quest. "
                             "Falls back to --api-key when omitted")
    parser.add_argument("--interactive", action="store_true")
    parser.add_argument("--discover", action="store_true", help="Listen for Quest UDP beacons and exit")
    parser.add_argument("--debug", action="store_true")
    parser.add_argument("--host", default="127.0.0.1",
                        help="Interface to listen on. Default localhost; use 0.0.0.0 only on a trusted LAN")
    parser.add_argument("--cors", action="store_true", help="Allow browser pages from any origin")
    args = parser.parse_args()

    QUEST_IP = args.quest_ip
    QUEST_PORT = args.quest_port
    API_KEY = args.api_key
    QUEST_KEY = args.quest_key
    if args.debug and args.host not in ("127.0.0.1", "localhost"):
        raise SystemExit("--debug exposes the Flask debugger; only allowed with --host 127.0.0.1")
    if args.host not in ("127.0.0.1", "localhost") and not API_KEY:
        raise SystemExit("Listening beyond localhost needs --api-key (a secret this bridge's clients must send)")
    if args.cors:
        CORS(app)
        CORS_ENABLED = True
    if "--api-key" in sys.argv[1:] or "--quest-key" in sys.argv[1:] or \
            any(a.startswith(("--api-key=", "--quest-key=")) for a in sys.argv[1:]):
        log.warning("Keys given on the command line are visible to other users/processes on this PC "
                    "(process list, shell history); prefer the QUEST_API_KEY / QUEST_KEY environment variables.")
    if QUEST_IP and not quest_key():
        log.warning(
            "No --quest-key (env QUEST_KEY) given: the Quest at %s always requires its key "
            "(shown in the headset's main menu), so every forwarded request will be rejected (401).",
            quest_base(),
        )
    elif not QUEST_KEY and API_KEY:
        log.warning("No --quest-key given: forwarding the bridge's --api-key to the Quest (legacy behaviour).")

    if args.discover:
        for d in discover_quest(5.0):
            print(json.dumps(d, ensure_ascii=False))
        raise SystemExit(0)

    print(f"\n{'=' * 50}")
    print("Quest Marker App - Coordinate Server v1.2")
    print(f"{'=' * 50}")
    print(f"Server: http://{args.host}:{args.port}")
    print(f"Quest:  {quest_base()}  (key: {'SET' if quest_key() else 'MISSING — pass --quest-key'})")
    print(f"Auth:   {'ON' if API_KEY else 'OFF'}  (clients of this bridge)")
    print(f"{'=' * 50}\n")

    if args.interactive:
        t = threading.Thread(
            target=lambda: app.run(host=args.host, port=args.port, debug=False, use_reloader=False),
            daemon=True,
        )
        t.start()
        interactive_mode()
    else:
        app.run(host=args.host, port=args.port, debug=args.debug, use_reloader=False)
