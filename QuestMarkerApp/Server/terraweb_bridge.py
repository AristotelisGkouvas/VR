#!/usr/bin/env python3
"""
Terraweb2 → Quest bridge: log in to the Terraweb2 backend, pick a project, fetch all
of its sensors and send them to the Quest as an alignment project (POST /api/project).

Terraweb2 facts this relies on (terraweb2-backend):
  - POST /api/auth/login/            {username, password} → {access, refresh} (JWT, 15 min)
  - GET  /api/project/               projects visible to the user
  - GET/PATCH /api/auth/profile/     the sensor API is scoped to profile.current_project
  - GET  /api/sensors/sensors/       full list when no ?page is sent
  - sensor_geometry is GeoJSON in WGS84 (lon, lat, elevation). The survey x/y/z columns
    exist on the model but the list endpoint does not return them, so positions are
    converted here from WGS84 to a local East/North/Up frame (exact, via ECEF).

Selecting a project means PATCHing the user's current_project, which also changes what
their web session shows; the original project is restored afterwards.

Usage:
    python terraweb_bridge.py --quest-ip 192.168.1.30 -u myuser          # default backend demo.iqsoft.xyz
    python terraweb_bridge.py --backend https://89.116.27.151 --cert-sha256 <hex> --quest-ip 192.168.1.30 -u myuser
        (self-signed server: run once with --insecure to see its SHA-256, check it, then pin it)
    python terraweb_bridge.py ... --project 12            # skip the menu
    python terraweb_bridge.py ... --list-only             # print sensors, do not send
Password: prompted, or TERRAWEB_PASSWORD env var.
"""

from __future__ import annotations

import argparse
import getpass
import json
import math
import os
import re
import sys

import requests
from requests.adapters import HTTPAdapter

# Same families, order and colours as the plan view (terraweb2-frontend
# src/features/sensors/lib/marker.ts + app/index.css --color-sensor-*). Matched against
# type.label: the list API returns type_codename as null.
TONES = [
    (re.compile(r"piezo|water.?level|pressure", re.I), "#2bb6d6"),
    (re.compile(r"strain|crack|joint|extenso", re.I), "#7c6cd6"),
    (re.compile(r"therm|temp", re.I), "#db5a46"),
    (re.compile(r"inclin|incl\b|deflect", re.I), "#e4a62b"),
    (re.compile(r"tilt|beam", re.I), "#1991ae"),
]
SURVEY_COLOR = "#5e9c24"
INACTIVE_COLOR = "#9aa3a0"

# Brand-kit icon per type (Assets/Resources/SensorIcons). First match wins.
ICONS = [
    (re.compile(r"piezo", re.I), "piezometer"),
    (re.compile(r"water.?level", re.I), "water-level"),
    (re.compile(r"crack|joint", re.I), "crack-meter"),
    (re.compile(r"strain|extenso", re.I), "strain-gauge"),
    (re.compile(r"inclin|incl\b|deflect", re.I), "inclinometer"),
    (re.compile(r"tilt|beam", re.I), "tilt"),
    (re.compile(r"therm|temp", re.I), "temperature"),
    (re.compile(r"pressure|load", re.I), "pressure"),
    (re.compile(r"vibrat|seism", re.I), "vibration"),
    (re.compile(r"borehole|extensometer", re.I), "borehole"),
    (re.compile(r"displace", re.I), "displacement"),
    (re.compile(r"3d|prism|target|survey|geodet", re.I), "target"),
]
DEFAULT_ICON = "sensor-point"

# LocalAPIServer rejects JSON bodies above 512 KB (413); check before sending.
MAX_BODY_BYTES = 512 * 1024


def sensor_style(type_label: str, is_active: bool) -> tuple[str, str]:
    """(icon, colour) for a sensor type, the way the plan view colours it."""
    icon = next((name for pattern, name in ICONS if pattern.search(type_label)), DEFAULT_ICON)
    if not is_active:
        return icon, INACTIVE_COLOR
    color = next((c for pattern, c in TONES if pattern.search(type_label)), SURVEY_COLOR)
    return icon, color

# WGS84 ellipsoid
_A = 6378137.0
_F = 1 / 298.257223563
_E2 = _F * (2 - _F)


def _ecef(lat_deg: float, lon_deg: float, h: float) -> tuple[float, float, float]:
    lat, lon = math.radians(lat_deg), math.radians(lon_deg)
    n = _A / math.sqrt(1 - _E2 * math.sin(lat) ** 2)
    return ((n + h) * math.cos(lat) * math.cos(lon),
            (n + h) * math.cos(lat) * math.sin(lon),
            (n * (1 - _E2) + h) * math.sin(lat))


def enu(lat: float, lon: float, h: float, lat0: float, lon0: float, h0: float) -> tuple[float, float, float]:
    """WGS84 → East/North/Up metres around (lat0, lon0, h0)."""
    x, y, z = _ecef(lat, lon, h)
    x0, y0, z0 = _ecef(lat0, lon0, h0)
    dx, dy, dz = x - x0, y - y0, z - z0
    la, lo = math.radians(lat0), math.radians(lon0)
    e = -math.sin(lo) * dx + math.cos(lo) * dy
    n = -math.sin(la) * math.cos(lo) * dx - math.sin(la) * math.sin(lo) * dy + math.cos(la) * dz
    u = math.cos(la) * math.cos(lo) * dx + math.cos(la) * math.sin(lo) * dy + math.sin(la) * dz
    return e, n, u


_LOOPBACK = {"127.0.0.1", "localhost", "::1"}


def normalize_fingerprint(value: str) -> str:
    """'AB:CD:…' / 'ab cd …' / 'abcd…' → 64 lower-case hex chars of a SHA-256 certificate hash."""
    hexed = re.sub(r"[\s:]", "", value or "").lower()
    if not re.fullmatch(r"[0-9a-f]{64}", hexed):
        raise ValueError(f"--cert-sha256 must be the 64 hex digits of a SHA-256 hash, not {value!r}")
    return hexed


def server_fingerprint(base: str, timeout: float = 10) -> str:
    """SHA-256 of the certificate the backend presents (unverified: shown so the user can pin it)."""
    import hashlib
    import ssl
    from urllib.parse import urlsplit

    u = urlsplit(base)
    pem = ssl.get_server_certificate((u.hostname, u.port or 443), timeout=timeout)
    return hashlib.sha256(ssl.PEM_cert_to_DER_cert(pem)).hexdigest()


class _PinnedAdapter(HTTPAdapter):
    """Accept exactly one certificate (by SHA-256), whatever its issuer or host name: a site
    server's self-signed certificate, without trusting every certificate like --insecure."""

    def __init__(self, fingerprint: str):
        self._fingerprint = fingerprint
        super().__init__()

    def init_poolmanager(self, *args, **kwargs):
        kwargs["assert_fingerprint"] = self._fingerprint
        return super().init_poolmanager(*args, **kwargs)


def check_backend_url(base: str) -> None:
    """The password must never cross the network in clear text (loopback is fine for testing)."""
    from urllib.parse import urlsplit

    u = urlsplit(base)
    if u.scheme == "https" and u.hostname:
        return
    if u.scheme == "http" and (u.hostname or "").lower() in _LOOPBACK:
        return
    raise ValueError(f"--backend must be an https:// URL (the password is sent to it), not {base!r}")


class Terraweb:
    def __init__(self, base: str, verify_tls: bool = True, pin_sha256: str | None = None):
        self.base = base.rstrip("/")
        self.session = requests.Session()
        self.session.verify = verify_tls
        if pin_sha256:
            # A pinned certificate is checked by hash; the CA / host-name check would refuse a
            # self-signed one, so it is replaced (not added to) by the pin.
            self.session.verify = False
            self.session.mount("https://", _PinnedAdapter(normalize_fingerprint(pin_sha256)))
        if not self.session.verify:
            import urllib3
            urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

    def _url(self, path: str) -> str:
        return f"{self.base}/api/{path.lstrip('/')}"

    def login(self, username: str, password: str) -> None:
        r = self.session.post(self._url("auth/login/"), json={"username": username, "password": password}, timeout=15)
        if r.status_code != 200:
            sys.exit(f"Login failed ({r.status_code}): {r.text[:300]}")
        try:
            token = r.json()["access"]
        except (ValueError, KeyError, TypeError):
            sys.exit(f"Login answered without an access token: {r.text[:300]}")
        self.session.headers["Authorization"] = f"Bearer {token}"

    def get(self, path: str, **params):
        r = self.session.get(self._url(path), params=params, timeout=60)
        r.raise_for_status()
        return r.json()

    def patch(self, path: str, body: dict):
        r = self.session.patch(self._url(path), json=body, timeout=15)
        r.raise_for_status()
        return r.json()


def _results(payload):
    return payload["results"] if isinstance(payload, dict) and "results" in payload else payload


def choose_project(projects: list[dict], wanted: str | None) -> dict:
    if wanted:
        for p in projects:
            if str(p["id"]) == wanted or (p.get("name") or "").lower() == wanted.lower():
                return p
        sys.exit(f"Project '{wanted}' not found. Available: {[(p['id'], p['name']) for p in projects]}")

    print("\nProjects:")
    for i, p in enumerate(projects, 1):
        print(f"  {i:>3}. {p['name']}  (id {p['id']})")
    while True:
        choice = input("Select project number: ").strip()
        if choice.isdigit() and 1 <= int(choice) <= len(projects):
            return projects[int(choice) - 1]
        print("Invalid choice.")


def choose_area(areas: list[dict], wanted: str | None) -> dict | None:
    """An area by id or name substring, or from a menu; None means the whole project."""
    if wanted:
        if wanted.lower() == "all":
            return None
        # area_id may come back as a number: compare as text.
        hits = [a for a in areas
                if wanted == str(a.get("area_id")) or wanted.lower() in (a.get("name") or "").lower()]
        if len(hits) == 1:
            return hits[0]
        sys.exit(f"Area '{wanted}' matched {len(hits)} areas: {[a['name'] for a in hits][:20]}")

    print("\nAreas:")
    print("    0. (whole project)")
    for i, a in enumerate(areas, 1):
        print(f"  {i:>3}. {a['name']}")
    while True:
        choice = input("Select area number: ").strip()
        if choice.isdigit() and 0 <= int(choice) <= len(areas):
            return None if choice == "0" else areas[int(choice) - 1]
        print("Invalid choice.")


def sensor_points(sensors: list[dict], active_only: bool,
                  types: list[str] | None = None) -> tuple[list[dict], list[str]]:
    """Sensors with a 3D point → Quest project points (local ENU metres, absolute H)."""
    usable, skipped = [], []
    for s in sensors:
        name = s.get("sensor_name") or s.get("sensor_id")
        if active_only and not s.get("is_active", True):
            continue
        type_label = ((s.get("type") or {}).get("label") or "").lower()
        if types and not any(t in type_label for t in types):
            continue
        geom = s.get("sensor_geometry") or {}
        coords = geom.get("coordinates")
        if geom.get("type") != "Point" or not coords:
            skipped.append(f"{name}: no point geometry ({geom.get('type')})")
            continue
        if len(coords) < 3 or coords[2] is None:
            skipped.append(f"{name}: no elevation")
            continue
        try:
            lon, lat, h = float(coords[0]), float(coords[1]), float(coords[2])
        except (TypeError, ValueError):
            skipped.append(f"{name}: non-numeric coordinates {coords[:3]}")
            continue
        if not all(math.isfinite(v) for v in (lon, lat, h)) or abs(lat) > 90 or abs(lon) > 180:
            skipped.append(f"{name}: coordinates out of range {coords[:3]}")
            continue
        icon, color = sensor_style(type_label, s.get("is_active", True))
        usable.append({"name": name, "lon": lon, "lat": lat, "h": h,
                       "icon": icon, "color": color})

    if not usable:
        return [], skipped

    lat0, lon0, h0 = usable[0]["lat"], usable[0]["lon"], usable[0]["h"]
    points = []
    for u in usable:
        e, n, _ = enu(u["lat"], u["lon"], u["h"], lat0, lon0, h0)
        points.append({"id": u["name"], "e": round(e, 4), "n": round(n, 4), "h": u["h"],
                       "label": u["name"], "icon": u["icon"], "color": u["color"]})
    return points, skipped


def send_project(quest_ip: str, port: int, api_key: str | None, body: dict, timeout: float = 15) -> int:
    """POST /api/project to the headset; prints the outcome and returns the exit code."""
    payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
    if len(payload) > MAX_BODY_BYTES:
        print(f"Project is {len(payload) // 1024} KB; the headset accepts at most {MAX_BODY_BYTES // 1024} KB. "
              "Narrow it with --area / --type.", file=sys.stderr)
        return 2
    headers = {"Content-Type": "application/json; charset=utf-8"}
    if api_key:
        headers["X-Api-Key"] = api_key
    url = f"http://{quest_ip}:{port}/api/project"
    try:
        r = requests.post(url, data=payload, headers=headers, timeout=timeout)
    except requests.RequestException as exc:
        print(f"Could not reach the headset at {url}: {exc}", file=sys.stderr)
        return 1
    print(r.status_code, r.text)
    if r.status_code == 401:
        print("The headset rejected the key: pass --quest-api-key with the key from its main menu.",
              file=sys.stderr)
    return 0 if r.ok else 1


def main() -> None:
    # Windows consoles default to cp1252: the "→" in --help and Greek project/sensor names
    # would raise UnicodeEncodeError on print.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
        except (AttributeError, ValueError):
            pass
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--backend", default="https://demo.iqsoft.xyz", help="Terraweb2 base URL")
    parser.add_argument("--quest-ip")
    parser.add_argument("--quest-port", type=int, default=8080)
    parser.add_argument("--quest-api-key", default=os.environ.get("QUEST_KEY"),
                        help="The headset's key from its main menu (env QUEST_KEY)")
    parser.add_argument("-u", "--username", required=True)
    parser.add_argument("--project", help="Project id or exact name (skips the menu)")
    parser.add_argument("--area", help="Area id, name substring, or 'all' (skips the menu)")
    parser.add_argument("--type", help="Comma-separated sensor type filter, e.g. '3d point,inclinometer'")
    parser.add_argument("--max", type=int, default=1000, help="Refuse to send more points than this")
    parser.add_argument("--include-inactive", action="store_true")
    parser.add_argument("--list-only", action="store_true", help="Print the sensors instead of sending them")
    parser.add_argument("--cert-sha256", metavar="HEX",
                        help="Trust exactly this backend certificate (SHA-256, as printed by --insecure): "
                             "the safe way to use a self-signed server")
    parser.add_argument("--insecure", action="store_true",
                        help="Skip TLS certificate checks (self-signed backend certificate). Anyone on the "
                             "network path can then read the password; prefer --cert-sha256")
    args = parser.parse_args()

    try:
        check_backend_url(args.backend)
        if args.cert_sha256:
            normalize_fingerprint(args.cert_sha256)
    except ValueError as exc:
        parser.error(str(exc))
    if "--quest-api-key" in sys.argv[1:] or any(a.startswith("--quest-api-key=") for a in sys.argv[1:]):
        print("Note: a key on the command line is visible in the process list and shell history; "
              "prefer the QUEST_KEY environment variable.", file=sys.stderr)
    if args.insecure and not args.cert_sha256:
        try:
            fp = server_fingerprint(args.backend)
            print(f"WARNING: --insecure accepts any certificate. The backend presented SHA-256 {fp}.\n"
                  f"         If that is your server's certificate, use --cert-sha256 {fp} instead.",
                  file=sys.stderr)
        except (OSError, ValueError) as exc:
            print(f"WARNING: --insecure accepts any certificate ({exc})", file=sys.stderr)

    if not args.list_only and not args.quest_ip:
        parser.error("--quest-ip is required unless --list-only")
    if not args.list_only and not args.quest_api_key:
        print("Warning: no --quest-api-key (or QUEST_KEY); the headset always requires its key.",
              file=sys.stderr)

    password = os.environ.get("TERRAWEB_PASSWORD") or getpass.getpass(f"Password for {args.username}: ")
    tw = Terraweb(args.backend, verify_tls=not args.insecure, pin_sha256=args.cert_sha256)
    tw.login(args.username, password)

    projects = _results(tw.get("project/"))
    if not projects:
        sys.exit("No projects visible to this user.")
    project = choose_project(projects, args.project)

    profile = tw.get("auth/profile/")
    original = profile.get("current_project_id") or (profile.get("current_project") or {}).get("id")
    switched = original != project["id"]
    try:
        if switched:
            tw.patch("auth/profile/", {"current_project_id": project["id"]})
        areas = _results(tw.get("sensors/areas/"))
        area = choose_area(areas, args.area) if areas else None
        sensors = _results(tw.get("sensors/sensors/", **({"area": area["area_id"]} if area else {})))
    finally:
        if switched and original is not None:
            tw.patch("auth/profile/", {"current_project_id": original})

    types = [t.strip().lower() for t in args.type.split(",")] if args.type else None
    points, skipped = sensor_points(sensors, active_only=not args.include_inactive, types=types)
    scope = f"{project['name']} / {area['name']}" if area else project["name"]
    print(f"\n{scope}: {len(sensors)} sensors, {len(points)} with a 3D point after filters")
    for s in skipped:
        print(f"  skipped {s}")

    if args.list_only or not points:
        for p in points:
            print(f"  {p['id']:<24} E{p['e']:>10.3f} N{p['n']:>10.3f} H{p['h']:>9.3f}  {p['icon']:<13} {p['color']}")
        return

    if len(points) > args.max:
        sys.exit(f"{len(points)} points is more than --max {args.max}; narrow it with --area / --type.")

    body = {"name": scope, "crs": "WGS84->ENU", "points": points}
    code = send_project(args.quest_ip, args.quest_port, args.quest_api_key, body)
    if code == 0:
        print("In the headset: L-stick select, R-trigger place at tip, B unplace, X close.")
    sys.exit(code)


if __name__ == "__main__":
    try:
        main()
    except requests.HTTPError as exc:
        resp = exc.response
        sys.exit(f"Terraweb request failed: HTTP {resp.status_code if resp is not None else '?'} "
                 f"{exc.request.url if exc.request is not None else ''}: "
                 f"{(resp.text[:300] if resp is not None else exc)}")
    except requests.RequestException as exc:
        sys.exit(f"Network error: {exc}")
    except KeyboardInterrupt:
        sys.exit(130)
