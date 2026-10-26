#!/usr/bin/env python3
"""
Fake coordinate scenarios for trying the app in a real room (AR phone / Quest).

Coordinate frame (same as the app):
    (0,0,0) = the floor point you tapped as origin
    +Z      = forward (the direction you faced when placing the origin)
    +X      = right,  +Y = up,  units = meters

Usage:
    python fake_scenarios.py --list
    python fake_scenarios.py --scenario room --device-ip 192.168.1.50 --api-key KEY
    python fake_scenarios.py --scenario live --device-ip 192.168.1.50 --api-key KEY  # moving marker, Ctrl+C
    python fake_scenarios.py --scenario grid --dry-run                  # print JSON only
    python fake_scenarios.py --export-all scenarios.json                # every scenario to a file
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
from typing import Any, Callable

import requests


def _c(id_: str, x: float, y: float, z: float, label: str, color: str,
       scale: float = 0.1, **extra: Any) -> dict[str, Any]:
    return {"id": id_, "x": round(x, 3), "y": round(y, 3), "z": round(z, 3),
            "label": label, "color": color, "scale": scale, **extra}


def scenario_room() -> list[dict[str, Any]]:
    """Typical room objects at realistic heights, within ~3 m of the origin."""
    return [
        _c("origin", 0, 0, 0, "Origin (0,0,0)", "#FFFFFF", 0.08),
        _c("table", 0.0, 0.75, 1.2, "Τραπέζι 75cm", "#FF9F0A", 0.12, markerType="pin"),
        _c("switch", -1.0, 1.2, 0.5, "Διακόπτης 1.2m", "#FFD60A", 0.06),
        _c("socket", -1.0, 0.3, 1.5, "Πρίζα 30cm", "#FFD60A", 0.06),
        _c("lamp", 0.0, 2.4, 1.5, "Φωτιστικό 2.4m", "#F2F2F7", 0.15),
        _c("door", 1.5, 1.0, 2.5, "Πόρτα", "#0A84FF", 0.12, markerType="pin"),
        _c("window", -0.5, 1.5, 3.0, "Παράθυρο", "#64D2FF", 0.12),
        _c("leak", 1.0, 0.0, 0.8, "Υγρασία", "#FF3B30", 0.1,
           markerType="zone", radius=0.4, status="warning"),
    ]


def scenario_grid() -> list[dict[str, Any]]:
    """Floor markers every 1 m (x -1..1, z 0..3). Use a tape measure to check accuracy."""
    out = []
    for z in range(0, 4):
        for x in (-1, 0, 1):
            axis = x == 0 or z == 0
            color = "#FF3B30" if z == 0 and x != 0 else "#0A84FF" if x == 0 and z != 0 else "#FFFFFF"
            out.append(_c(f"g_{x}_{z}", x, 0.0, z, f"({x},{z})",
                          color if axis else "#8E8E93", 0.06))
    out.append(_c("g_h1", 0, 1.0, 1, "1m ύψος", "#34C759", 0.06))
    out.append(_c("g_h2", 0, 2.0, 1, "2m ύψος", "#34C759", 0.06))
    return out


def scenario_path() -> list[dict[str, Any]]:
    """A walking route: arrows forward, a turn right, a destination pin."""
    pts = [(0, 0.5), (0, 1.0), (0, 1.5), (0, 2.0), (0.5, 2.3), (1.0, 2.5), (1.5, 2.5)]
    out = [_c(f"path_{i}", x, 0.05, z, f"{i + 1}", "#30D158", 0.08, markerType="arrow")
           for i, (x, z) in enumerate(pts)]
    out.append(_c("dest", 2.0, 0.0, 2.5, "Προορισμός", "#FF375F", 0.15, markerType="pin"))
    return out


def scenario_sensors() -> list[dict[str, Any]]:
    """Sensors with status colors, like an inspection or IoT overlay."""
    data = [
        ("s1", -1.2, 1.0, 1.0, "Θερμ. 22.4°C", "ok"),
        ("s2", 0.8, 1.6, 1.4, "CO2 950ppm", "warning"),
        ("s3", 0.0, 0.4, 2.2, "Υγρασία 81%", "critical"),
        ("s4", 1.4, 2.2, 0.6, "Καπνός OK", "ok"),
        ("s5", -0.6, 0.1, 2.8, "Διαρροή;", "critical"),
    ]
    colors = {"ok": "#34C759", "warning": "#FFD60A", "critical": "#FF3B30"}
    return [_c(i, x, y, z, lbl, colors[st], 0.1, status=st, layer="sensors")
            for i, x, y, z, lbl, st in data]


def live_positions(t: float) -> list[dict[str, Any]]:
    """A marker circling the origin at 1.5 m radius (one lap per 12 s) plus a bobbing one."""
    a = t * 2 * math.pi / 12
    return [
        _c("drone", 1.5 * math.sin(a), 1.2, 1.5 + 1.5 * math.cos(a), "Κινούμενο", "#BF5AF2", 0.12),
        _c("bob", 0, 0.8 + 0.4 * math.sin(t * 2), 1.0, "Πάνω-κάτω", "#FF9F0A", 0.1),
    ]


SCENARIOS: dict[str, tuple[str, Callable[[], list[dict[str, Any]]]]] = {
    "room": ("Αντικείμενα δωματίου σε ρεαλιστικά ύψη", scenario_room),
    "grid": ("Πλέγμα 1m στο πάτωμα για έλεγχο ακρίβειας με μετροταινία", scenario_grid),
    "path": ("Διαδρομή με βέλη και προορισμό", scenario_path),
    "sensors": ("Αισθητήρες με χρώμα κατάστασης (ok/warning/critical)", scenario_sensors),
    "live": ("Κινούμενα markers σε πραγματικό χρόνο (Ctrl+C για stop)", lambda: live_positions(0)),
}


def post(base: str, coords: list[dict[str, Any]], clear: bool, timeout: float = 5,
         api_key: str | None = None) -> dict[str, Any]:
    # The headset always requires its key (X-Api-Key); without it every POST is a 401.
    headers = {"X-Api-Key": api_key} if api_key else {}
    r = requests.post(f"{base}/api/coordinates",
                      json={"coordinates": coords, "clearExisting": clear}, timeout=timeout, headers=headers)
    if r.status_code == 401:
        raise requests.HTTPError("401 Unauthorized: pass --api-key (the key in the headset's main menu)",
                                 response=r)
    r.raise_for_status()
    return r.json() if r.content else {}


def run_live(base: str, hz: float, api_key: str | None = None) -> None:
    print("Live mode — Ctrl+C για stop")
    post(base, live_positions(0), clear=True, api_key=api_key)
    t0 = time.time()
    failures = 0
    try:
        while True:
            try:
                post(base, live_positions(time.time() - t0), clear=False, timeout=2, api_key=api_key)
                failures = 0
                time.sleep(1 / hz)
            except requests.HTTPError as e:
                if e.response is None or e.response.status_code not in (429, 503):
                    raise
                # Rate-limited / headset busy: back off instead of dying mid-demo.
                wait = float(e.response.headers.get("Retry-After", "2") or 2)
                print(f"  HTTP {e.response.status_code}, waiting {wait:.0f}s…", file=sys.stderr)
                time.sleep(min(wait, 60))
            except (requests.ConnectionError, requests.Timeout) as e:
                failures += 1
                if failures >= 10:
                    raise
                print(f"  {type(e).__name__}, retry {failures}/10…", file=sys.stderr)
                time.sleep(min(2 ** failures, 15))
    except KeyboardInterrupt:
        print("\nStopped.")


def main() -> int:
    # Windows consoles default to cp1252 and can't print the Greek labels
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    p =argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--scenario", choices=SCENARIOS.keys())
    p.add_argument("--device-ip", help="IP του κινητού/Quest (φαίνεται στο Info overlay της εφαρμογής)")
    p.add_argument("--port", type=int, default=8080)
    p.add_argument("--api-key", default=os.environ.get("QUEST_KEY"),
                   help="Το κλειδί της συσκευής (κύριο μενού) — env QUEST_KEY")
    p.add_argument("--keep", action="store_true", help="Μην καθαρίσεις τα υπάρχοντα markers")
    p.add_argument("--hz", type=float, default=5, help="Ρυθμός ανανέωσης για live (0.1–20)")
    p.add_argument("--dry-run", action="store_true", help="Τύπωσε το JSON χωρίς αποστολή")
    p.add_argument("--list", action="store_true")
    p.add_argument("--export-all", metavar="FILE")
    args = p.parse_args()

    if args.list:
        for name, (desc, _) in SCENARIOS.items():
            print(f"  {name:8s} {desc}")
        return 0

    if args.export_all:
        data = {name: {"description": desc, "coordinates": fn()} for name, (desc, fn) in SCENARIOS.items()}
        with open(args.export_all, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        print(f"Wrote {args.export_all}")
        return 0

    if not args.scenario:
        p.error("--scenario is required (or use --list)")

    coords = SCENARIOS[args.scenario][1]()
    if args.dry_run:
        print(json.dumps({"coordinates": coords, "clearExisting": not args.keep},
                         ensure_ascii=False, indent=2))
        return 0

    if not args.device_ip:
        p.error("--device-ip is required unless --dry-run")
    if not (math.isfinite(args.hz) and 0.1 <= args.hz <= 20):
        p.error("--hz must be between 0.1 and 20")
    if not args.api_key:
        print("Warning: no --api-key (or QUEST_KEY); the headset will answer 401.", file=sys.stderr)
    base = f"http://{args.device_ip}:{args.port}"

    try:
        if args.scenario == "live":
            run_live(base, args.hz, args.api_key)
        else:
            resp = post(base, coords, clear=not args.keep, api_key=args.api_key)
            print(f"Sent {len(coords)} markers ({args.scenario}) → {base}: {resp}")
    except requests.RequestException as e:
        print(f"Αποτυχία σύνδεσης με {base}: {e}\n"
              "Έλεγξε: ίδιο WiFi, η εφαρμογή ανοιχτή στο προσκήνιο, σωστή IP από το Info overlay.",
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
