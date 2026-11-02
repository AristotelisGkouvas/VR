#!/usr/bin/env python3
"""
Ανάλυση της «Μέτρησης ακρίβειας ευθυγράμμισης» (κεφ. 6, ενότητα «Ακρίβεια ευθυγράμμισης»).

Διαβάζει τα CSV που γράφει η εφαρμογή (AlignmentAccuracy.cs) και παράγει στο figures/generated/:
  alignment_by_k.tex                 πίνακας ανά πλήθος σημείων ελέγχου k
  alignment_numbers.tex              μακροεντολές με τους βασικούς αριθμούς για το κείμενο
                                     (\\AccSigmaHcm, \\AccMedianCheckHcm, ...)
  alignment_error_vs_distance.pdf    οριζόντιο σφάλμα ανεξάρτητων σημείων ως προς την απόσταση
                                     από το κέντρο των σημείων ελέγχου, με την πρόβλεψη
  alignment_pred_vs_meas.pdf         μετρημένο έναντι προβλεπόμενου σφάλματος

Λήψη από τα γυαλιά (κλειδί από το κύριο μενού):
  python alignment_accuracy.py --download --quest-ip 192.168.1.30 --api-key ΚΛΕΙΔΙ
ή, με τα CSV ήδη στο thesis/data/accuracy/:
  python alignment_accuracy.py

Ορολογία (στήλη role):
  check    ανεξάρτητο σημείο: δεν συμμετείχε στη λύση → μετρά την ακρίβεια
  control  σημείο ελέγχου της λύσης, ξανά-αγγιγμένο → μετρά την επαναληψιμότητα του αγγίγματος (σ)
  undo     ακυρώνει τη μέτρηση meas_id της ίδιας συνεδρίας (αναίρεση με B)
Οι γραμμές του έργου εξάσκησης (practice = yes) αγνοούνται, εκτός με --include-practice.
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
DEFAULT_DATA = THESIS / "data" / "accuracy"
DEFAULT_OUT = THESIS / "figures" / "generated"
ASSUMED_SIGMA_CM = 2.0   # ProjectAlignment.TouchSigma


@dataclass
class Row:
    session: str
    project: str
    practice: bool
    series: str
    k: int
    baseline: float | None
    rms_h: float | None
    meas_id: str
    point: str
    role: str
    repeat: int
    dist: float | None
    err_h: float | None
    err_v: float | None
    predicted: float | None
    touch_e: float | None
    touch_n: float | None
    touch_h: float | None


def _num(s: str | None) -> float | None:
    s = (s or "").strip()
    if not s:
        return None
    try:
        v = float(s)
    except ValueError:
        return None
    return v if math.isfinite(v) else None


def _int(s: str | None) -> int:
    v = _num(s)
    return int(v) if v is not None else 0


def read_rows(folder: Path, include_practice: bool) -> list[Row]:
    rows: list[Row] = []
    for path in sorted(folder.glob("*.csv")):
        with path.open(encoding="utf-8-sig", newline="") as f:
            for r in csv.DictReader(f):
                rows.append(Row(
                    session=r.get("session_id", ""), project=r.get("project", ""),
                    practice=(r.get("practice", "") == "yes"), series=r.get("series", ""),
                    k=_int(r.get("k")), baseline=_num(r.get("baseline_m")), rms_h=_num(r.get("rms_h_cm")),
                    meas_id=r.get("meas_id", ""), point=r.get("point_id", ""), role=r.get("role", ""),
                    repeat=_int(r.get("repeat")), dist=_num(r.get("centroid_dist_m")),
                    err_h=_num(r.get("err_h_cm")), err_v=_num(r.get("err_v_cm")),
                    predicted=_num(r.get("predicted_cm")),
                    touch_e=_num(r.get("touch_e")), touch_n=_num(r.get("touch_n")), touch_h=_num(r.get("touch_h")),
                ))
    # Undo rows cancel a measurement of the same app session (meas_id restarts every launch).
    cancelled = {(r.session, r.meas_id) for r in rows if r.role == "undo"}
    kept = [r for r in rows if r.role in ("check", "control") and (r.session, r.meas_id) not in cancelled]
    return [r for r in kept if include_practice or not r.practice]


def download(quest_ip: str, api_key: str, folder: Path, port: int = 8080) -> None:
    base = f"http://{quest_ip}:{port}/api/accuracy"
    headers = {"X-Api-Key": api_key}

    def get(url: str) -> bytes:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=20) as resp:
            return resp.read()

    folder.mkdir(parents=True, exist_ok=True)
    # Απάντηση: [{"file": "...csv", "bytes": N, "modifiedUtc": "..."}]
    for name in (f["file"] for f in json.loads(get(base).decode("utf-8"))):
        if name.endswith(".csv") and "/" not in name and "\\" not in name:
            (folder / name).write_bytes(get(f"{base}/{urllib.request.quote(name)}"))
            print(f"  κατέβηκε {name}")


# ------------------------------------------------------------------ στατιστικά
def median(xs: list[float]) -> float | None:
    if not xs:
        return None
    s = sorted(xs)
    n = len(s)
    return s[n // 2] if n % 2 else 0.5 * (s[n // 2 - 1] + s[n // 2])


def percentile(xs: list[float], p: float) -> float | None:
    if not xs:
        return None
    s = sorted(xs)
    k = (len(s) - 1) * p
    lo, hi = math.floor(k), math.ceil(k)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


def rms(xs: list[float]) -> float | None:
    return math.sqrt(sum(x * x for x in xs) / len(xs)) if xs else None


def touch_sigma(rows: list[Row]) -> tuple[float | None, float | None, int]:
    """
    Επαναληψιμότητα αγγίγματος: για κάθε σημείο που αγγίχτηκε ≥ 2 φορές στην ίδια σειρά, οι
    αποκλίσεις των αγγιγμάτων από τον μέσο τους (σε E, N, H). Συγκεντρωτική τυπική απόκλιση
    ανά άξονα με n_i − 1 βαθμούς ελευθερίας. Επιστρέφει (σ οριζόντια ανά άξονα, σ κατακόρυφη) σε cm.
    """
    groups: dict[tuple, list[Row]] = defaultdict(list)
    for r in rows:
        if r.touch_e is not None and r.touch_n is not None and r.touch_h is not None:
            groups[(r.session, r.series, r.point)].append(r)
    ss_e = ss_n = ss_h = 0.0
    dof = 0
    for g in groups.values():
        if len(g) < 2:
            continue
        me = sum(r.touch_e for r in g) / len(g)
        mn = sum(r.touch_n for r in g) / len(g)
        mh = sum(r.touch_h for r in g) / len(g)
        ss_e += sum((r.touch_e - me) ** 2 for r in g)
        ss_n += sum((r.touch_n - mn) ** 2 for r in g)
        ss_h += sum((r.touch_h - mh) ** 2 for r in g)
        dof += len(g) - 1
    if dof == 0:
        return None, None, 0
    sigma_h = math.sqrt((ss_e + ss_n) / (2 * dof)) * 100.0
    sigma_v = math.sqrt(ss_h / dof) * 100.0
    return sigma_h, sigma_v, dof


def fmt(v: float | None, digits: int = 1) -> str:
    return "--" if v is None else f"{v:.{digits}f}".replace(".", "{,}")


def by_k(rows: list[Row]) -> list[dict]:
    out = []
    for k in sorted({r.k for r in rows}):
        checks = [r for r in rows if r.k == k and r.role == "check" and r.err_h is not None]
        eh = [r.err_h for r in checks]
        ev = [abs(r.err_v) for r in checks if r.err_v is not None]
        ratio = [r.err_h / r.predicted for r in checks if r.predicted and r.predicted > 0]
        series = {(r.session, r.series) for r in rows if r.k == k}
        out.append({
            "k": k, "series": len(series), "n": len(checks),
            "rms_fit": median([r.rms_h for r in rows if r.k == k and r.rms_h is not None]),
            "eh_median": median(eh), "eh_p95": percentile(eh, 0.95), "ev_median": median(ev),
            "ratio": median(ratio),
        })
    return out


def write_table(summary: list[dict], path: Path) -> None:
    lines = [
        r"\begin{tabular}{@{}rrrrrrrr@{}}",
        r"\toprule",
        r"$k$ & Σειρές & $N$ & RMS λύσης (cm) & $e_H$ διάμ. (cm) & $e_H$ 95\% (cm) & $|e_V|$ διάμ. (cm) & μετρ./προβλ. \\",
        r"\midrule",
    ]
    for s in summary:
        lines.append(" & ".join([str(s["k"]), str(s["series"]), str(s["n"]), fmt(s["rms_fit"]),
                                 fmt(s["eh_median"]), fmt(s["eh_p95"]), fmt(s["ev_median"]), fmt(s["ratio"], 2)]) + r" \\")
    lines += [r"\bottomrule", r"\end{tabular}", ""]
    path.write_text("\n".join(lines), encoding="utf-8")


def write_numbers(rows: list[Row], sigma_h, sigma_v, dof, path: Path) -> None:
    checks = [r.err_h for r in rows if r.role == "check" and r.err_h is not None]
    macros = {
        "AccSigmaHcm": fmt(sigma_h), "AccSigmaVcm": fmt(sigma_v), "AccSigmaDof": str(dof),
        "AccChecks": str(len(checks)), "AccMedianCheckHcm": fmt(median(checks)),
        "AccPNinetyFiveCheckHcm": fmt(percentile(checks, 0.95)),
    }
    text = "% Παράγεται από το analysis/alignment_accuracy.py — μην το διορθώνεις με το χέρι.\n"
    text += "".join(f"\\providecommand{{\\{k}}}{{{v}}}\n" for k, v in macros.items())
    path.write_text(text, encoding="utf-8")


# ------------------------------------------------------------------ γραφήματα
def plot(rows: list[Row], out: Path) -> list[Path]:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    plt.rcParams.update({"font.size": 9, "savefig.bbox": "tight",
                         "axes.spines.top": False, "axes.spines.right": False})
    made: list[Path] = []
    checks = [r for r in rows if r.role == "check" and r.err_h is not None and r.dist is not None]
    if not checks:
        return made
    ks = sorted({r.k for r in checks})
    markers = {2: "o", 3: "s", 4: "^"}

    # 1) σφάλμα ως προς απόσταση, με την πρόβλεψη σ√2·D/b (b = διάμεσος βάσης της ομάδας k)
    fig, ax = plt.subplots(figsize=(4.8, 3.2))
    dmax = max(r.dist for r in checks) * 1.05
    for k in ks:
        pts = [r for r in checks if r.k == k]
        line = ax.scatter([r.dist for r in pts], [r.err_h for r in pts], s=18, alpha=0.8,
                          marker=markers.get(k, "D"), label=f"k = {k}")
        b = median([r.baseline for r in pts if r.baseline])
        if b:
            ax.plot([0, dmax], [0, ASSUMED_SIGMA_CM * math.sqrt(2) * dmax / b], "--",
                    color=line.get_facecolor()[0], linewidth=1)
    ax.set_xlabel("Απόσταση από το κέντρο των σημείων ελέγχου D (m)")
    ax.set_ylabel("Οριζόντιο σφάλμα $e_H$ (cm)")
    ax.set_xlim(0, dmax)
    ax.set_ylim(bottom=0)
    ax.grid(alpha=0.3)
    ax.legend(frameon=False, title="διακεκομμένη: πρόβλεψη", title_fontsize=7)
    p = out / "alignment_error_vs_distance.pdf"
    fig.savefig(p)
    plt.close(fig)
    made.append(p)

    # 2) μετρημένο έναντι προβλεπόμενου
    pred = [r for r in checks if r.predicted is not None]
    if pred:
        fig, ax = plt.subplots(figsize=(3.6, 3.4))
        for k in ks:
            pts = [r for r in pred if r.k == k]
            ax.scatter([r.predicted for r in pts], [r.err_h for r in pts], s=18, alpha=0.8,
                       marker=markers.get(k, "D"), label=f"k = {k}")
        top = max(max(r.predicted for r in pred), max(r.err_h for r in pred)) * 1.1
        ax.plot([0, top], [0, top], "k:", linewidth=1)
        ax.set_xlim(0, top)
        ax.set_ylim(0, top)
        ax.set_aspect("equal")
        ax.set_xlabel("Προβλεπόμενο σφάλμα (cm)")
        ax.set_ylabel("Μετρημένο $e_H$ (cm)")
        ax.grid(alpha=0.3)
        ax.legend(frameon=False)
        p = out / "alignment_pred_vs_meas.pdf"
        fig.savefig(p)
        plt.close(fig)
        made.append(p)
    return made


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DEFAULT_DATA, help="φάκελος με τα CSV (προεπιλογή thesis/data/accuracy)")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help="φάκελος εξόδου (προεπιλογή thesis/figures/generated)")
    ap.add_argument("--download", action="store_true", help="κατέβασε πρώτα τα CSV από τα γυαλιά")
    ap.add_argument("--quest-ip", help="IP των γυαλιών (με --download)")
    ap.add_argument("--api-key", default=os.environ.get("QUEST_KEY"), help="κλειδί API (ή μεταβλητή QUEST_KEY)")
    ap.add_argument("--include-practice", action="store_true", help="κράτα και τις μετρήσεις του έργου εξάσκησης")
    args = ap.parse_args(argv)
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    if args.download:
        if not args.quest_ip or not args.api_key:
            ap.error("το --download θέλει --quest-ip και --api-key (ή QUEST_KEY)")
        print(f"Λήψη από {args.quest_ip} ...")
        download(args.quest_ip, args.api_key, args.data)

    rows = read_rows(args.data, args.include_practice)
    if not rows:
        print(f"Δεν βρέθηκαν μετρήσεις στο {args.data}", file=sys.stderr)
        return 1
    args.out.mkdir(parents=True, exist_ok=True)

    summary = by_k(rows)
    sigma_h, sigma_v, dof = touch_sigma(rows)
    write_table(summary, args.out / "alignment_by_k.tex")
    write_numbers(rows, sigma_h, sigma_v, dof, args.out / "alignment_numbers.tex")
    figs = plot(rows, args.out)

    print(f"{len(rows)} μετρήσεις ({sum(r.role == 'check' for r in rows)} ανεξάρτητα, "
          f"{sum(r.role == 'control' for r in rows)} σε σημεία ελέγχου)")
    print(f"{'k':>2}{'σειρές':>8}{'N':>5}{'RMS λύσης':>11}{'eH διάμ':>9}{'eH 95%':>8}{'|eV| διάμ':>11}{'μετρ/προβλ':>12}")
    for s in summary:
        def f(v, d=1):
            return "-" if v is None else f"{v:.{d}f}"
        print(f"{s['k']:>2}{s['series']:>8}{s['n']:>5}{f(s['rms_fit']):>11}{f(s['eh_median']):>9}"
              f"{f(s['eh_p95']):>8}{f(s['ev_median']):>11}{f(s['ratio'], 2):>12}")
    if dof:
        print(f"σ αγγίγματος: οριζ. {sigma_h:.2f} cm/άξονα, κατακ. {sigma_v:.2f} cm "
              f"({dof} βαθμοί ελευθερίας· η εφαρμογή υποθέτει {ASSUMED_SIGMA_CM:.1f} cm)")
    else:
        print("σ αγγίγματος: χρειάζονται επαναλήψεις στο ίδιο σημείο (≥ 2 αγγίγματα)")
    print("Έγραψε: " + ", ".join(str(p) for p in
                                 [args.out / "alignment_by_k.tex", args.out / "alignment_numbers.tex", *figs]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
