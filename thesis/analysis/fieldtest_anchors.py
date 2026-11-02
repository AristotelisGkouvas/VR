#!/usr/bin/env python3
"""
Ανάλυση της «Δοκιμής πεδίου anchors» (κεφ. 6, ενότητα «Σταθερότητα και επανεντοπισμός anchors»).

Διαβάζει όλα τα CSV που γράφει η εφαρμογή (FieldTestLog.cs) και παράγει:
  figures/generated/anchors_by_condition.tex       πίνακας LaTeX (booktabs) ανά συνθήκη
  figures/generated/anchors_offset_by_condition.pdf boxplot οριζόντιας μετατόπισης ανά συνθήκη
  figures/generated/anchors_offset_vs_age.pdf       μετατόπιση συναρτήσει ηλικίας anchor
  και μια σύνοψη στην κονσόλα.

Λήψη των CSV από τα γυαλιά (κλειδί από το κύριο μενού):
  python fieldtest_anchors.py --download --quest-ip 192.168.1.30 --api-key ΚΛΕΙΔΙ
ή αντιγραφή με το χέρι στο thesis/data/fieldtest/ και:
  python fieldtest_anchors.py

Εξαρτήσεις: numpy, matplotlib (thesis/analysis/requirements.txt).
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import os
import sys
import urllib.request
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

HERE = Path(__file__).resolve().parent
THESIS = HERE.parent
DEFAULT_DATA = THESIS / "data" / "fieldtest"
DEFAULT_OUT = THESIS / "figures" / "generated"

# Ίδια σειρά και ονόματα με το AnchorFieldTest.Conditions της εφαρμογής.
CONDITIONS = ["sunny", "cloudy", "indoor", "morning", "noon", "evening"]
CONDITION_EL = {
    "sunny": "Ηλιοφάνεια", "cloudy": "Συννεφιά", "indoor": "Εσωτερικός χώρος",
    "morning": "Πρωί", "noon": "Μεσημέρι", "evening": "Βράδυ",
}
# Γραμμές που είναι μέτρηση επανεντοπισμού (όχι δημιουργία/διαγραφή anchor).
MEASURE_PHASES = {"load", "manual"}
TOUCH_PHASES = {"touch"}


@dataclass
class Row:
    file: str
    timestamp: str
    session: str
    project: str
    point: str
    condition: str
    site_changed: bool
    phase: str
    uuid: str
    age_h: float | None
    localized: bool | None
    seconds: float | None
    err_h_touch: float | None
    err_v_touch: float | None
    err_h_solution: float | None
    pair_err_h: float | None


def _num(s: str) -> float | None:
    s = (s or "").strip()
    if not s:
        return None
    try:
        v = float(s)
    except ValueError:
        return None
    return v if math.isfinite(v) else None


def _bool(s: str) -> bool | None:
    s = (s or "").strip().lower()
    return True if s == "yes" else False if s == "no" else None


def read_rows(folder: Path) -> list[Row]:
    rows: list[Row] = []
    for path in sorted(folder.glob("*.csv")):
        # utf-8-sig: η εφαρμογή γράφει BOM στα νέα αρχεία για το Excel.
        with path.open(encoding="utf-8-sig", newline="") as f:
            for r in csv.DictReader(f):
                rows.append(Row(
                    file=path.name,
                    timestamp=r.get("timestamp", ""),
                    session=r.get("session_id", ""),
                    project=r.get("project", ""),
                    point=r.get("point_id", ""),
                    condition=r.get("condition", ""),
                    site_changed=_bool(r.get("site_changed", "")) is True,
                    phase=r.get("phase", ""),
                    uuid=r.get("anchor_uuid", ""),
                    age_h=_num(r.get("anchor_age_h", "")),
                    localized=_bool(r.get("localized", "")),
                    seconds=_num(r.get("seconds", "")),
                    err_h_touch=_num(r.get("errH_cm_touch", "")),
                    err_v_touch=_num(r.get("errV_cm_touch", "")),
                    err_h_solution=_num(r.get("errH_cm_solution", "")),
                    pair_err_h=_num(r.get("pair_errH_cm", "")),
                ))
    return rows


def download(quest_ip: str, api_key: str, folder: Path, port: int = 8080) -> int:
    """Κατεβάζει όλα τα CSV από το GET /api/fieldtest της συσκευής."""
    base = f"http://{quest_ip}:{port}/api/fieldtest"
    headers = {"X-Api-Key": api_key}

    def get(url: str) -> bytes:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=20) as resp:
            return resp.read()

    # Απάντηση: [{"file": "...csv", "bytes": N, "modifiedUtc": "..."}]
    names = [f["file"] for f in json.loads(get(base).decode("utf-8"))]
    folder.mkdir(parents=True, exist_ok=True)
    for name in names:
        if not name.endswith(".csv") or "/" in name or "\\" in name:
            continue
        (folder / name).write_bytes(get(f"{base}/{urllib.request.quote(name)}"))
        print(f"  κατέβηκε {name}")
    return len(names)


# ------------------------------------------------------------------ στατιστικά
def median(xs: list[float]) -> float:
    s = sorted(xs)
    n = len(s)
    return s[n // 2] if n % 2 else 0.5 * (s[n // 2 - 1] + s[n // 2])


def percentile(xs: list[float], p: float) -> float:
    s = sorted(xs)
    if len(s) == 1:
        return s[0]
    k = (len(s) - 1) * p
    lo, hi = math.floor(k), math.ceil(k)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


def fmt(v: float | None, digits: int = 1) -> str:
    # Ελληνική υποδιαστολή, όπως το siunitx του report.
    return "--" if v is None else f"{v:.{digits}f}".replace(".", "{,}")


def summarize(rows: list[Row]) -> list[dict]:
    """Μία γραμμή ανά συνθήκη: επανεντοπισμός (load/manual) και απόκλιση στο νέο άγγιγμα (touch)."""
    by_cond: dict[str, dict[str, list]] = defaultdict(lambda: {"loc": [], "sec": [], "errH": [], "errV": []})
    for r in rows:
        c = by_cond[r.condition or "?"]
        if r.phase in MEASURE_PHASES and r.localized is not None:
            c["loc"].append(r.localized)
            if r.localized and r.seconds is not None:
                c["sec"].append(r.seconds)
        if r.phase in TOUCH_PHASES and r.err_h_touch is not None:
            c["errH"].append(r.err_h_touch)
            if r.err_v_touch is not None:
                c["errV"].append(abs(r.err_v_touch))
    order = [c for c in CONDITIONS if c in by_cond] + sorted(set(by_cond) - set(CONDITIONS))
    out = []
    for cond in order:
        c = by_cond[cond]
        out.append({
            "condition": cond,
            "n_measure": len(c["loc"]),
            "rate": (100.0 * sum(c["loc"]) / len(c["loc"])) if c["loc"] else None,
            "sec_median": median(c["sec"]) if c["sec"] else None,
            "n_touch": len(c["errH"]),
            "errH_median": median(c["errH"]) if c["errH"] else None,
            "errH_p95": percentile(c["errH"], 0.95) if c["errH"] else None,
            "errV_median": median(c["errV"]) if c["errV"] else None,
        })
    return out


def write_table(summary: list[dict], path: Path) -> None:
    lines = [
        r"\begin{tabular}{@{}lrrrrrrr@{}}",
        r"\toprule",
        r"Συνθήκη & $N_\text{μετρ.}$ & Επανεντοπ. (\%) & Χρόνος (s) & $N_\text{αγγ.}$ & "
        r"$e_H$ διάμ. (cm) & $e_H$ 95\% (cm) & $|e_V|$ διάμ. (cm) \\",
        r"\midrule",
    ]
    for s in summary:
        lines.append(" & ".join([
            CONDITION_EL.get(s["condition"], s["condition"]),
            str(s["n_measure"]), fmt(s["rate"], 0), fmt(s["sec_median"]),
            str(s["n_touch"]), fmt(s["errH_median"]), fmt(s["errH_p95"]), fmt(s["errV_median"]),
        ]) + r" \\")
    lines += [r"\bottomrule", r"\end{tabular}", ""]
    path.write_text("\n".join(lines), encoding="utf-8")


# ------------------------------------------------------------------ γραφήματα
def plot(rows: list[Row], out: Path) -> list[Path]:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    plt.rcParams.update({"font.size": 9, "figure.dpi": 150, "savefig.bbox": "tight",
                         "axes.spines.top": False, "axes.spines.right": False})
    made: list[Path] = []
    touches = [r for r in rows if r.phase in TOUCH_PHASES and r.err_h_touch is not None]

    # 1) Boxplot οριζόντιας μετατόπισης ανά συνθήκη.
    conds = [c for c in CONDITIONS if any(r.condition == c for r in touches)]
    if conds:
        data = [[r.err_h_touch for r in touches if r.condition == c] for c in conds]
        fig, ax = plt.subplots(figsize=(4.6, 3.0))
        ax.boxplot(data, showfliers=True)
        ax.set_xticks(range(1, len(conds) + 1), [CONDITION_EL[c] for c in conds], rotation=20)
        ax.set_ylabel("Οριζόντια απόκλιση anchor (cm)")
        ax.grid(axis="y", alpha=0.3)
        p = out / "anchors_offset_by_condition.pdf"
        fig.savefig(p)
        plt.close(fig)
        made.append(p)

    # 2) Μετατόπιση συναρτήσει ηλικίας anchor (λογαριθμικός άξονας: 10 λεπτά έως εβδομάδες).
    aged = [r for r in touches if r.age_h is not None and r.age_h > 0]
    if aged:
        fig, ax = plt.subplots(figsize=(4.6, 3.0))
        for changed, marker, label in ((False, "o", "Χωρίς αλλαγές"), (True, "^", "Αλλαγές εργοταξίου")):
            pts = [r for r in aged if r.site_changed == changed]
            if pts:
                ax.scatter([r.age_h for r in pts], [r.err_h_touch for r in pts],
                           marker=marker, s=18, alpha=0.75, label=label)
        ax.set_xscale("log")
        ax.set_xlabel("Ηλικία anchor (ώρες)")
        ax.set_ylabel("Οριζόντια απόκλιση (cm)")
        ax.grid(alpha=0.3, which="both")
        ax.legend(frameon=False)
        p = out / "anchors_offset_vs_age.pdf"
        fig.savefig(p)
        plt.close(fig)
        made.append(p)
    return made


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DEFAULT_DATA, help="φάκελος με τα CSV (προεπιλογή thesis/data/fieldtest)")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help="φάκελος εξόδου (προεπιλογή thesis/figures/generated)")
    ap.add_argument("--download", action="store_true", help="κατέβασε πρώτα τα CSV από τα γυαλιά")
    ap.add_argument("--quest-ip", help="IP των γυαλιών (με --download)")
    ap.add_argument("--api-key", default=os.environ.get("QUEST_KEY"), help="κλειδί API (ή μεταβλητή QUEST_KEY)")
    args = ap.parse_args(argv)
    # Η κονσόλα των Windows δεν είναι UTF-8 από προεπιλογή: τα ελληνικά θα έριχναν το script.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    if args.download:
        if not args.quest_ip or not args.api_key:
            ap.error("το --download θέλει --quest-ip και --api-key (ή QUEST_KEY)")
        print(f"Λήψη από {args.quest_ip} ...")
        download(args.quest_ip, args.api_key, args.data)

    rows = read_rows(args.data)
    if not rows:
        print(f"Δεν βρέθηκαν CSV στο {args.data}", file=sys.stderr)
        return 1
    args.out.mkdir(parents=True, exist_ok=True)

    summary = summarize(rows)
    table = args.out / "anchors_by_condition.tex"
    write_table(summary, table)
    figs = plot(rows, args.out)

    files = sorted({r.file for r in rows})
    sessions = {r.session for r in rows if r.session}
    print(f"{len(rows)} γραμμές από {len(files)} αρχεία, {len(sessions)} συνεδρίες")
    print(f"{'Συνθήκη':<18}{'Νμετρ':>6}{'Επαν.%':>8}{'s':>6}{'Ναγγ':>6}{'eH διάμ':>9}{'eH 95%':>8}{'|eV| διάμ':>10}")
    for s in summary:
        def f(v, d=1):
            return "-" if v is None else f"{v:.{d}f}"
        print(f"{CONDITION_EL.get(s['condition'], s['condition']):<18}{s['n_measure']:>6}{f(s['rate'], 0):>8}"
              f"{f(s['sec_median']):>6}{s['n_touch']:>6}{f(s['errH_median']):>9}{f(s['errH_p95']):>8}{f(s['errV_median']):>10}")
    print("Έγραψε: " + ", ".join(str(p) for p in [table, *figs]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
