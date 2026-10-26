#!/usr/bin/env python3
"""
Send a survey project (EGSA87 / UTM + absolute elevation) to the Quest.

CSV columns (header required, extra columns ignored):
    id,e,n,h[,label][,color][,icon]
Common aliases are accepted: x/easting for e, y/northing for n, z/elevation for h.
Decimal commas (Greek Excel exports, ';'-separated) are accepted.

The Quest's key (headset main menu) can come from --api-key or the QUEST_KEY env var.
Exit code 0 only when the headset accepted the project.

Usage:
    python send_project.py points.csv --quest-ip 192.168.50.108
    python send_project.py points.csv --quest-ip 192.168.50.108 --name "Site A" --api-key secret
    python send_project.py --status --quest-ip 192.168.50.108
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import sys
from pathlib import Path

import requests

ALIASES = {
    "e": ("e", "x", "easting", "east"),
    "n": ("n", "y", "northing", "north"),
    "h": ("h", "z", "elevation", "height", "alt"),
    "id": ("id", "name", "point", "pt"),
}

# LocalAPIServer rejects JSON bodies above 512 KB (413); check before sending.
MAX_BODY_BYTES = 512 * 1024


def _number(text: str | None) -> float:
    """A finite float from a CSV cell, accepting a decimal comma."""
    if text is None or not text.strip():
        raise ValueError("empty value")
    value = float(text.strip().replace(",", "."))
    if not math.isfinite(value):
        raise ValueError(f"not a finite number: {text!r}")
    return value


def _column(header: list[str], key: str) -> str | None:
    lowered = {h.strip().lower(): h for h in header}
    for alias in ALIASES.get(key, (key,)):
        if alias in lowered:
            return lowered[alias]
    return None


def _delimiter(sample: str) -> str:
    """Tab, ';' or ',' from the header line. csv.Sniffer guesses wrong (or gives up) on
    ';'-separated files whose values use decimal commas, the usual Greek Excel export."""
    header = next((line for line in sample.splitlines() if line.strip()), "")
    for d in ("\t", ";", ","):
        if d in header:
            return d
    return ","


def read_points(path: Path) -> list[dict]:
    try:
        f = path.open(newline="", encoding="utf-8-sig")
    except OSError as exc:
        sys.exit(f"Cannot open {path}: {exc}")
    with f:
        sample = f.read(2048)
        f.seek(0)
        reader = csv.DictReader(f, delimiter=_delimiter(sample))
        header = reader.fieldnames or []
        cols = {k: _column(header, k) for k in ("id", "e", "n", "h", "label", "color", "icon")}
        missing = [k for k in ("e", "n", "h") if cols[k] is None]
        if missing:
            sys.exit(f"CSV is missing column(s) {missing}; header was {header}")

        points = []
        for i, row in enumerate(reader, start=1):
            if not any((v or "").strip() for v in row.values() if isinstance(v, str)):
                continue  # blank line
            try:
                point = {
                    "id": ((row.get(cols["id"]) or "").strip() or str(i)) if cols["id"] else str(i),
                    "e": _number(row.get(cols["e"])),
                    "n": _number(row.get(cols["n"])),
                    "h": _number(row.get(cols["h"])),
                }
            except (TypeError, ValueError, AttributeError) as exc:
                sys.exit(f"Row {i}: cannot parse coordinates ({exc}): {row}")
            for extra in ("label", "color", "icon"):  # header case does not matter ("Label")
                value = row.get(cols[extra]) if cols[extra] else None
                if value and value.strip():
                    point[extra] = value.strip()
            points.append(point)
        return points


def post_project(url: str, body: dict, headers: dict, timeout: float = 10) -> int:
    """POST the project; print the outcome. Returns the process exit code."""
    payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
    if len(payload) > MAX_BODY_BYTES:
        print(f"Project is {len(payload) // 1024} KB; the headset accepts at most {MAX_BODY_BYTES // 1024} KB. "
              "Send fewer points or shorter labels.", file=sys.stderr)
        return 2
    try:
        r = requests.post(url, data=payload, timeout=timeout,
                          headers={**headers, "Content-Type": "application/json; charset=utf-8"})
    except requests.RequestException as exc:
        print(f"Could not reach the headset at {url}: {exc}\n"
              "Check: same Wi-Fi, app open on the Quest, correct --quest-ip.", file=sys.stderr)
        return 1
    print(r.status_code, r.text)
    if r.status_code == 401:
        print("The headset rejected the key: pass --api-key with the key shown in its main menu.",
              file=sys.stderr)
    return 0 if r.ok else 1


def main() -> None:
    # Windows consoles default to cp1252: Greek names in the headset's answer would not print.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
        except (AttributeError, ValueError):
            pass
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("csv", nargs="?", type=Path)
    parser.add_argument("--quest-ip", required=True)
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--name", help="Project name (defaults to the CSV file name)")
    parser.add_argument("--crs", default="EGSA87")
    parser.add_argument("--api-key", default=os.environ.get("QUEST_KEY"),
                        help="The Quest's key, shown in its main menu (env QUEST_KEY)")
    parser.add_argument("--scale", type=float, default=1.0,
                        help="Grid-to-ground factor applied to E/N around their centroid. EGSA87 grid "
                             "distances are ~0.9996 x ground (plus a height term); e.g. --scale 1.00048 "
                             "near 500 m elevation. Default 1 (no correction).")
    parser.add_argument("--status", action="store_true", help="Only print the alignment status")
    args = parser.parse_args()

    base = f"http://{args.quest_ip}:{args.port}/api/project"
    headers = {"X-Api-Key": args.api_key} if args.api_key else {}

    if not args.api_key:
        print("Warning: no --api-key (or QUEST_KEY); the headset always requires its key.", file=sys.stderr)

    if args.status:
        try:
            r = requests.get(base, headers=headers, timeout=5)
        except requests.RequestException as exc:
            sys.exit(f"Could not reach the headset at {base}: {exc}")
        try:
            print(r.status_code, json.dumps(r.json(), indent=2, ensure_ascii=False))
        except ValueError:
            print(r.status_code, r.text)
        sys.exit(0 if r.ok else 1)
    if args.csv is None:
        parser.error("csv is required unless --status is given")
    if not math.isfinite(args.scale) or args.scale <= 0:
        parser.error("--scale must be a positive number")

    points = read_points(args.csv)
    if not points:
        sys.exit(f"{args.csv} has no data rows.")
    if args.scale != 1.0 and points:
        # Scale about the centroid so absolute coordinates stay meaningful in the listing.
        ce = sum(p["e"] for p in points) / len(points)
        cn = sum(p["n"] for p in points) / len(points)
        for p in points:
            p["e"] = ce + (p["e"] - ce) * args.scale
            p["n"] = cn + (p["n"] - cn) * args.scale
    body = {"name": args.name or args.csv.stem, "crs": args.crs, "points": points}
    code = post_project(base, body, headers)
    if code == 0:
        print(f"Sent {len(points)} points. In the headset: L-stick select, R-trigger place, X close.")
    sys.exit(code)


if __name__ == "__main__":
    main()
