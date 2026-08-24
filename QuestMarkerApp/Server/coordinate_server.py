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
    python coordinate_server.py --quest-ip 192.168.1.100
    QUEST_API_KEY=secret python coordinate_server.py --api-key secret
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import socket
import threading
import time
from collections import defaultdict, deque
from typing import Any

import requests
from flask import Flask, Response, jsonify, request
from flask_cors import CORS

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("quest-marker")

app = Flask(__name__)
CORS(app)

_store_lock = threading.RLock()
_coordinates_store: list[dict[str, Any]] = []
_event_cond = threading.Condition(_store_lock)
_last_event_ts = 0
_rate: dict[str, deque] = defaultdict(deque)

QUEST_IP = os.environ.get("QUEST_IP", "192.168.1.100")
QUEST_PORT = int(os.environ.get("QUEST_PORT", "8080"))
QUEST_TIMEOUT = float(os.environ.get("QUEST_TIMEOUT", "10"))
API_KEY = os.environ.get("QUEST_API_KEY", "")
RATE_LIMIT = int(os.environ.get("RATE_LIMIT", "120"))  # per minute


def quest_base() -> str:
    return f"http://{QUEST_IP}:{QUEST_PORT}"


def _now_ms() -> int:
    return int(time.time() * 1000)


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
    if key == API_KEY:
        return True, None
    if auth.startswith("Bearer ") and auth[7:].strip() == API_KEY:
        return True, None
    return False, (jsonify({"error": "Unauthorized"}), 401)


def _check_rate() -> tuple[bool, Any]:
    if RATE_LIMIT <= 0:
        return True, None
    ip = request.remote_addr or "unknown"
    now = time.time()
    q = _rate[ip]
    while q and now - q[0] > 60:
        q.popleft()
    if len(q) >= RATE_LIMIT:
        return False, (jsonify({"error": "Rate limit exceeded"}), 429)
    q.append(now)
    return True, None


@app.before_request
def _security_gate():
    ok, resp = _check_rate()
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
    if API_KEY:
        headers = {**headers, "X-Api-Key": API_KEY}
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
    global _last_event_ts
    _last_event_ts = _now_ms()
    _event_cond.notify_all()


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
    if not data:
        return jsonify({"error": "No JSON data provided"}), 400

    coords = data.get("coordinates", [])
    if not isinstance(coords, list):
        return jsonify({"error": "'coordinates' must be an array"}), 400

    clear = bool(data.get("clearExisting", False))
    remove_missing = bool(data.get("removeMissing", False))
    send_to_quest = bool(data.get("sendToQuest", True))

    with _store_lock:
        if clear:
            _coordinates_store.clear()
        prepared = []
        for i, coord in enumerate(coords):
            if not isinstance(coord, dict):
                continue
            prepared.append(_ensure_coord_defaults(dict(coord), len(_coordinates_store) + i))
            _coordinates_store.append(prepared[-1])
        _notify_events()

    if send_to_quest:
        try:
            response = send_to_quest_headset(prepared, clear, remove_missing)
            return jsonify({"success": True, "stored": len(prepared), "quest_response": response})
        except Exception as e:
            return jsonify({"success": True, "stored": len(prepared), "quest_error": str(e)})

    return jsonify({"success": True, "stored": len(prepared)})


@app.route("/api/events", methods=["GET"])
def events_long_poll():
    """Long-poll: wait until store changes after `since`, or timeout."""
    since = int(request.args.get("since", "0"))
    timeout = min(float(request.args.get("timeout", "25")), 55.0)

    deadline = time.time() + timeout
    with _event_cond:
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
                "clearExisting": True,
                "timestamp": _last_event_ts,
            }
        )


@app.route("/api/coordinates/<coord_id>", methods=["DELETE"])
def delete_coordinate(coord_id: str):
    with _store_lock:
        before = len(_coordinates_store)
        _coordinates_store[:] = [c for c in _coordinates_store if c.get("id") != coord_id]
        removed = before - len(_coordinates_store)
        if removed:
            _notify_events()

    quest_result = None
    try:
        quest_result = _quest_request("DELETE", f"/api/coordinates/{coord_id}")
    except Exception as e:
        quest_result = {"ok": False, "error": str(e)}

    return jsonify({"success": True, "removed_local": removed, "quest_response": quest_result})


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
    return jsonify({"success": True, "quest_response": quest_result})


@app.route("/api/send-to-quest", methods=["POST"])
def send_stored_to_quest():
    with _store_lock:
        snapshot = list(_coordinates_store)
    try:
        response = send_to_quest_headset(snapshot, clear_existing=True)
        return jsonify({"success": True, "sent": len(snapshot), "quest_response": response})
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
        return jsonify(result.get("body") or result)
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/session/quest", methods=["POST"])
def session_to_quest():
    data = request.get_json(silent=True)
    if not data:
        return jsonify({"error": "No JSON"}), 400
    try:
        result = _quest_request("POST", "/api/session", json=data, timeout=15)
        return jsonify(result.get("body") or result)
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/gps-origin", methods=["POST"])
def gps_origin():
    data = request.get_json(silent=True)
    if not data:
        return jsonify({"error": "No JSON"}), 400
    try:
        result = _quest_request("POST", "/api/gps-origin", json=data)
        return jsonify({"success": True, "quest_response": result.get("body")})
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
    if not data:
        return jsonify({"error": "No JSON data provided"}), 400
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
        return jsonify({"success": True, "quest_response": result.get("body")})
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


@app.route("/api/calibrate/reset", methods=["POST"])
def reset_calibration():
    try:
        result = _quest_request("POST", "/api/calibrate/reset", timeout=5)
        return jsonify({"success": True, "quest_response": result.get("body")})
    except Exception as e:
        return jsonify({"success": False, "error": str(e)}), 500


def send_to_quest_headset(
    coordinates: list[dict[str, Any]],
    clear_existing: bool = False,
    remove_missing: bool = False,
) -> dict[str, Any]:
    payload = {
        "coordinates": coordinates,
        "clearExisting": clear_existing,
        "removeMissing": remove_missing,
    }
    result = _quest_request("POST", "/api/coordinates", json=payload, timeout=QUEST_TIMEOUT)
    return result.get("body") or result


def discover_quest(timeout: float = 3.0) -> list[dict[str, Any]]:
    """Listen for UDP beacons from NetworkDiscovery (port 48080)."""
    port = 48080
    found: dict[str, dict[str, Any]] = {}
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("", port))
    sock.settimeout(0.5)
    deadline = time.time() + timeout
    log.info("Listening for Quest discovery beacons on UDP %s (%.1fs)…", port, timeout)
    while time.time() < deadline:
        try:
            data, addr = sock.recvfrom(4096)
            msg = json.loads(data.decode("utf-8"))
            if msg.get("service") == "quest-marker":
                key = addr[0]
                found[key] = {**msg, "ip": addr[0]}
        except socket.timeout:
            continue
        except Exception:
            continue
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
                    try:
                        send_to_quest_headset([coord], False)
                        print("  -> Sent to Quest")
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
                    send_to_quest_headset(snap, True)
                    print(f"Sent {len(snap)}")
                except Exception as e:
                    print(f"Failed: {e}")

            elif action == "clear":
                with _store_lock:
                    _coordinates_store.clear()
                    _notify_events()
                try:
                    _quest_request("POST", "/api/clear", timeout=5)
                    print("Cleared")
                except Exception:
                    print("Local cleared")

            elif action == "del":
                if len(parts) < 2:
                    print("Usage: del <id>")
                    continue
                cid = parts[1]
                with _store_lock:
                    _coordinates_store[:] = [c for c in _coordinates_store if c.get("id") != cid]
                    _notify_events()
                try:
                    _quest_request("DELETE", f"/api/coordinates/{cid}", timeout=5)
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
                    r = requests.post(f"{quest_base()}/api/calibrate", json=payload, timeout=10)
                    print(r.json())
                except Exception as e:
                    print(e)

            elif action == "startcal":
                try:
                    r = requests.post(f"{quest_base()}/api/calibrate/start", timeout=5)
                    print(r.json())
                except Exception as e:
                    print(e)

            elif action == "calstatus":
                try:
                    r = requests.get(f"{quest_base()}/api/calibrate/status", timeout=5)
                    print(json.dumps(r.json(), indent=2, ensure_ascii=False))
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
    parser.add_argument("--api-key", type=str, default=API_KEY)
    parser.add_argument("--interactive", action="store_true")
    parser.add_argument("--discover", action="store_true", help="Listen for Quest UDP beacons and exit")
    parser.add_argument("--debug", action="store_true")
    args = parser.parse_args()

    QUEST_IP = args.quest_ip
    QUEST_PORT = args.quest_port
    API_KEY = args.api_key

    if args.discover:
        for d in discover_quest(5.0):
            print(json.dumps(d, ensure_ascii=False))
        raise SystemExit(0)

    print(f"\n{'=' * 50}")
    print("Quest Marker App - Coordinate Server v1.2")
    print(f"{'=' * 50}")
    print(f"Server: http://0.0.0.0:{args.port}")
    print(f"Quest:  {quest_base()}")
    print(f"Auth:   {'ON' if API_KEY else 'OFF'}")
    print(f"{'=' * 50}\n")

    if args.interactive:
        t = threading.Thread(
            target=lambda: app.run(host="0.0.0.0", port=args.port, debug=False, use_reloader=False),
            daemon=True,
        )
        t.start()
        interactive_mode()
    else:
        app.run(host="0.0.0.0", port=args.port, debug=args.debug, use_reloader=False)
