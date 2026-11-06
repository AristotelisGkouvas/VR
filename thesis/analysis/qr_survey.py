#!/usr/bin/env python3
"""
Ακρίβεια των κωδικών QR έναντι γεωδαιτικού σταθμού. Συγκρίνει τα κέντρα που μέτρησαν τα γυαλιά
(CSV της «Δοκιμής QR») με τις συντεταγμένες E, N, H των ίδιων κωδικών.

Ένα μηδένισμα του συστήματος συντεταγμένων των γυαλιών (το κεφάλι πηδά πάνω από 2 m μέσα σε 2 s)
χωρίζει τη δοκιμή σε ομάδες. Συγκρίσεις γίνονται μόνο μέσα σε κάθε ομάδα. Για κάθε ομάδα τυπώνει:

  - τις αποστάσεις των ζευγών, σταθμός έναντι γυαλιών,
  - την προσαρμογή όλης της ομάδας (γωνία και μετάθεση, όπως η ευθυγράμμιση της εφαρμογής),
  - την πρόβλεψη κάθε κωδικού από τους άλλους δύο ή περισσότερους, που δεν τον χρησιμοποιούν.

  python qr_survey.py
  python qr_survey.py --csv ../data/qrtest/qrtest-2026-11-06.csv --survey ../data/qrtest/survey-2026-11-06.csv

Μόνο βιβλιοθήκη της Python.
"""
from __future__ import annotations

import argparse
import csv
import itertools
import math
import os
import statistics as st
import sys
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "..", "data", "qrtest")
JUMP_METRES, JUMP_SECONDS = 2.0, 2.0
WIDTH_TOLERANCE = 0.25


def read_survey(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        # Unity axes: x = E, y = H, z = N.
        return {r["code"]: (float(r["e"]), float(r["h"]), float(r["n"])) for r in csv.DictReader(f)}


def read_rows(path, codes):
    rows = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        for r in csv.DictReader(f):
            r["t"] = datetime.fromisoformat(r["timestamp"]).timestamp()
            r["head"] = (float(r["head_x"]), float(r["head_y"]), float(r["head_z"]))
            r["centre"] = (float(r["x"]), float(r["y"]), float(r["z"]))
            rows.append(r)
    rows.sort(key=lambda r: r["t"])
    first = next(r["t"] for r in rows if r["payload"] in codes)
    return [r for r in rows if r["t"] >= first]


def split_frames(rows):
    """Frame number for each row, and the local time of each jump."""
    frame, jumps, last, jumped_at = 0, [], None, -math.inf
    for r in rows:
        if last is not None:
            dt = r["t"] - last["t"]
            if dt <= JUMP_SECONDS and math.dist(r["head"], last["head"]) > JUMP_METRES:
                # One reset can take two samples: horizontal first, then height.
                if r["t"] - jumped_at > JUMP_SECONDS:
                    frame += 1
                    jumps.append(r["timestamp"][11:19])
                jumped_at = r["t"]
        r["frame"] = frame
        last = r
    return jumps


def centres(rows, codes):
    """(frame, code) -> median centre of the tracked readings, misreads dropped by width."""
    grouped = {}
    for r in rows:
        if r["tracked"] == "1" and r["payload"] in codes:
            grouped.setdefault((r["frame"], r["payload"]), []).append(r)
    out = {}
    for key, readings in grouped.items():
        width = st.median(float(r["width_m"]) for r in readings)
        kept = [r for r in readings if abs(float(r["width_m"]) - width) <= WIDTH_TOLERANCE * width]
        out[key] = {
            "centre": tuple(st.median(r["centre"][i] for r in kept) for i in range(3)),
            "n": len(kept),
            "width": width,
            "dist": st.median(float(r["head_dist_m"]) for r in kept),
        }
    return out


def solve(pairs):
    """Yaw and translation, least squares on the horizontal plane, as ProjectAlignment.Solve."""
    n = len(pairs)
    pc = [sum(p[i] for p, _ in pairs) / n for i in range(3)]
    mc = [sum(m[i] for _, m in pairs) / n for i in range(3)]
    s_cos = s_sin = 0.0
    for p, m in pairs:
        px, pz, mx, mz = p[0] - pc[0], p[2] - pc[2], m[0] - mc[0], m[2] - mc[2]
        s_cos += mx * px + mz * pz
        s_sin += mx * pz - mz * px
    yaw = math.atan2(s_sin, s_cos)

    def apply(p):
        c, s = math.cos(yaw), math.sin(yaw)
        x, y, z = p[0] - pc[0], p[1] - pc[1], p[2] - pc[2]
        return (c * x + s * z + mc[0], y + mc[1], -s * x + c * z + mc[2])

    return math.degrees(yaw), apply


def offsets(predicted, measured):
    return math.hypot(predicted[0] - measured[0], predicted[2] - measured[2]), predicted[1] - measured[1]


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", default=os.path.join(DATA, "qrtest-2026-11-06.csv"))
    ap.add_argument("--survey", default=os.path.join(DATA, "survey-2026-11-06.csv"))
    args = ap.parse_args()

    survey = read_survey(args.survey)
    rows = read_rows(args.csv, survey)
    jumps = split_frames(rows)
    read = centres(rows, survey)
    print(f"Σημεία σταθμού: {len(survey)}. Μηδενισμοί: {', '.join(jumps) or 'κανένας'}.")
    missing = sorted(set(survey) - {code for _, code in read})
    print(f"Δεν διαβάστηκαν: {', '.join(missing) or 'κανένας'}.")

    for number, frame in enumerate(sorted({f for f, _ in read}), 1):
        ids = sorted(code for f, code in read if f == frame)
        m = {code: read[(frame, code)]["centre"] for code in ids}
        print(f"\n## Ομάδα {number}: {', '.join(ids)}\n")
        print("| Κωδικός | Αναγνώσεις | Πλευρά (cm) | Απόσταση από το κεφάλι (m) |\n|---|---|---|---|")
        for code in ids:
            c = read[(frame, code)]
            print(f"| {code} | {c['n']} | {c['width'] * 100:.1f} | {c['dist']:.2f} |")
        if len(ids) < 2:
            continue
        print("\n| Ζεύγος | Σταθμός (m) | Γυαλιά (m) | Διαφορά (cm) |\n|---|---|---|---|")
        for a, b in itertools.combinations(ids, 2):
            s, g = math.dist(survey[a], survey[b]), math.dist(m[a], m[b])
            print(f"| {a} – {b} | {s:.3f} | {g:.3f} | {(g - s) * 100:+.1f} |")
        pairs = [(survey[c], m[c]) for c in ids]
        yaw, apply = solve(pairs)
        fits = [offsets(apply(survey[c]), m[c]) for c in ids]
        rms_flat = math.sqrt(sum(f ** 2 for f, _ in fits) / len(fits))
        rms_up = math.sqrt(sum(u ** 2 for _, u in fits) / len(fits))
        print(f"\nΠροσαρμογή όλης της ομάδας: γωνία {yaw:.3f}°, RMS οριζόντια {rms_flat * 100:.1f} cm, κατακόρυφα {rms_up * 100:.1f} cm.")
        if len(ids) < 3:
            continue
        print("\n| Πρόβλεψη | Από | Οριζόντια (cm) | Κατακόρυφα (cm) | Γωνία (°) |\n|---|---|---|---|---|")
        for code in ids:
            others = [c for c in ids if c != code]
            yaw, apply = solve([(survey[c], m[c]) for c in others])
            flat, up = offsets(apply(survey[code]), m[code])
            print(f"| {code} | {', '.join(others)} | {flat * 100:.1f} | {up * 100:+.1f} | {yaw:.2f} |")


if __name__ == "__main__":
    main()
