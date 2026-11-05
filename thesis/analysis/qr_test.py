#!/usr/bin/env python3
"""
Ανάλυση της «Δοκιμής QR» (data/protocols/δοκιμή_qr.md). Διαβάζει τα CSV της εφαρμογής (Core/QrTest.cs)
από το thesis/data/qrtest/ και τυπώνει, για κάθε κωδικό και ζεύγος κωδικών:

  - πλήθος μετρήσεων και εύρος απόστασης από τα γυαλιά,
  - μετρημένη πλευρά έναντι ονομαστικής (από το όνομα, π.χ. QM-13A = 13 cm),
  - διασπορά του κέντρου όταν το κεφάλι είναι σχεδόν ακίνητο, ανά κλάση απόστασης,
  - απόσταση ζευγών, και με --tape το σφάλμα της ως προς τη μετροταινία.

  python qr_test.py --tape QM-13A,QM-13B=2.345 --tape QM-10A,QM-10B=2.102

Μόνο βιβλιοθήκη της Python. Λήψη από τα γυαλιά:
  adb pull /sdcard/Android/data/com.yourcompany.questmarkerapp/files/qrtest thesis/data/
"""
from __future__ import annotations

import argparse
import csv
import glob
import math
import os
import re
import statistics as st
import sys
from collections import defaultdict
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DIR = os.path.join(HERE, "..", "data", "qrtest")
STILL_M_S = 0.05  # head speed below this counts as still
BINS = [(0, 1), (1, 2), (2, 3), (3, 5), (5, 100)]
PAIR_WINDOW_S = 1.0  # samples of a pair closer in time than this are compared


def load(folder: str) -> list[dict]:
    rows = []
    for path in sorted(glob.glob(os.path.join(folder, "*.csv"))):
        with open(path, encoding="utf-8-sig", newline="") as f:
            for r in csv.DictReader(f):
                r["t"] = datetime.fromisoformat(r["timestamp"]).timestamp()
                if r["tracked"] != "1":
                    continue  # a lost code keeps its last pose: not a measurement
                for k in ("x", "y", "z", "width_m", "head_x", "head_y", "head_z", "head_dist_m", "view_angle_deg"):
                    r[k] = float(r[k])
                rows.append(r)
    return rows


def spread(points: list[tuple[float, float, float]]) -> float:
    """3D standard deviation, metres."""
    if len(points) < 2:
        return float("nan")
    mean = [sum(p[i] for p in points) / len(points) for i in range(3)]
    return math.sqrt(sum(sum((p[i] - mean[i]) ** 2 for i in range(3)) for p in points) / (len(points) - 1))


def mark_still(rows: list[dict]) -> None:
    """A sample is still when the head hardly moved since the same code's previous sample."""
    last: dict[tuple[str, str], dict] = {}
    for r in sorted(rows, key=lambda r: r["t"]):
        key = (r["session_id"], r["payload"])
        p = last.get(key)
        r["still"] = False
        if p and 0 < r["t"] - p["t"] < 2:
            d = math.dist((r["head_x"], r["head_y"], r["head_z"]), (p["head_x"], p["head_y"], p["head_z"]))
            r["still"] = d / (r["t"] - p["t"]) < STILL_M_S
        last[key] = r


def report_codes(rows: list[dict]) -> None:
    by = defaultdict(list)
    for r in rows:
        by[r["payload"]].append(r)
    for name in sorted(by):
        rs = by[name]
        m = re.search(r"-(\d+)", name)
        nominal = f" (ονομαστική {int(m.group(1))} cm)" if m else ""
        w = st.median(r["width_m"] for r in rs) * 100
        print(f"\n{name}: {len(rs)} μετρήσεις, απόσταση {min(r['head_dist_m'] for r in rs):.2f}–"
              f"{max(r['head_dist_m'] for r in rs):.2f} m, πλευρά {w:.1f} cm{nominal}")
        for lo, hi in BINS:
            # Per session: the frame of reference is not comparable across launches.
            sessions = defaultdict(list)
            for r in rs:
                if r["still"] and lo <= r["head_dist_m"] < hi:
                    sessions[r["session_id"]].append((r["x"], r["y"], r["z"]))
            vals = [spread(p) * 1000 for p in sessions.values() if len(p) >= 10]
            n = sum(len(p) for p in sessions.values())
            if vals:
                print(f"  {lo}–{hi} m, ακίνητο κεφάλι: διασπορά {st.median(vals):.1f} mm (n={n})")


def report_pairs(rows: list[dict], tape: dict[frozenset, float]) -> None:
    """Distance of two codes from samples taken within a second of each other, same session."""
    sessions = defaultdict(lambda: defaultdict(list))
    for r in rows:
        sessions[r["session_id"]][r["payload"]].append(r)
    results = defaultdict(list)
    for codes in sessions.values():
        names = sorted(codes)
        for i, a in enumerate(names):
            for b in names[i + 1:]:
                rb = sorted(codes[b], key=lambda r: r["t"])
                j = 0
                for ra in sorted(codes[a], key=lambda r: r["t"]):
                    while j + 1 < len(rb) and abs(rb[j + 1]["t"] - ra["t"]) <= abs(rb[j]["t"] - ra["t"]):
                        j += 1
                    if rb and abs(rb[j]["t"] - ra["t"]) <= PAIR_WINDOW_S:
                        results[frozenset((a, b))].append(
                            math.dist((ra["x"], ra["y"], ra["z"]), (rb[j]["x"], rb[j]["y"], rb[j]["z"])))
    print("\nΖεύγη:")
    if not results:
        print("  κανένα")
    for pair in sorted(results, key=sorted):
        d = results[pair]
        line = f"  {' – '.join(sorted(pair))}: διάμεση {st.median(d):.3f} m, τ.α. {st.pstdev(d) * 1000:.1f} mm (n={len(d)})"
        if pair in tape:
            errs = [abs(x - tape[pair]) * 100 for x in d]
            line += (f", ταινία {tape[pair]:.3f} m → σφάλμα διάμεσης {(st.median(d) - tape[pair]) * 100:+.1f} cm, "
                     f"95% των μετρήσεων εντός {sorted(errs)[int(0.95 * (len(errs) - 1))]:.1f} cm")
        print(line)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dir", default=DEFAULT_DIR)
    ap.add_argument("--tape", action="append", default=[], metavar="A,B=ΜΕΤΡΑ", help="απόσταση κέντρων με μετροταινία")
    args = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    tape = {}
    for t in args.tape:
        names, value = t.split("=")
        tape[frozenset(names.split(","))] = float(value)
    rows = load(args.dir)
    if not rows:
        raise SystemExit(f"Δεν υπάρχουν CSV στο {args.dir}")
    mark_still(rows)
    print(f"{len(rows)} μετρήσεις, {len({r['session_id'] for r in rows})} συνεδρίες")
    report_codes(rows)
    report_pairs(rows, tape)


if __name__ == "__main__":
    main()
