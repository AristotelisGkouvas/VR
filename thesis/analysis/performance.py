#!/usr/bin/env python3
"""
Ανάλυση της «Απόδοσης γραφικών» (κεφ. 6, ενότητα sec:exp-performance).

Διαβάζει τα CSV που γράφει η εφαρμογή (PerfLog.cs, Μενού › Ρυθμίσεις › Καταγραφή απόδοσης):
μία γραμμή το δευτερόλεπτο, με το φορτίο (σημεία, τρίγωνα μοντέλου, heatmap) και τη ρύθμιση
(Hz, foveation, eye buffer) δίπλα στις μετρήσεις. Παράγει στο figures/generated/:
  perf_by_scenario.tex     πίνακας ανά ρύθμιση και φορτίο
  perf_numbers.tex         μακροεντολές για το κείμενο (\\PerfBatteryPerTenMin, ...)
  fps_vs_triangles.pdf     ρυθμός καρέ ως προς τα τρίγωνα του μοντέλου, μία γραμμή ανά ρύθμιση

Λήψη από τα γυαλιά (κλειδί από Μενού › Ρυθμίσεις, γραμμή «PC · κλειδί …»):
  python performance.py --download --quest-ip 192.168.1.30 --api-key ΚΛΕΙΔΙ
ή, με τα CSV ήδη στο thesis/data/performance/:
  python performance.py

Τμήμα (segment) = συνεχόμενες γραμμές της ίδιας συνεδρίας με ίδια ρύθμιση και ίδιο φορτίο.
Τα πρώτα --warmup δευτερόλεπτα κάθε τμήματος αγνοούνται (φόρτωση, εναλλαγή ρύθμισης).
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import sys
import urllib.request
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

HERE = Path(__file__).resolve().parent
THESIS = HERE.parent
DEFAULT_DATA = THESIS / "data" / "performance"
DEFAULT_OUT = THESIS / "figures" / "generated"
FOVEATION = ["off", "low", "medium", "high"]


def num(s: str) -> float | None:
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


@dataclass
class Sample:
    t: datetime
    session: str
    hz: int
    foveation: int
    eye: float
    markers: int
    tris: int
    heatmap: bool
    fps: float
    worst_ms: float
    dropped: int
    cpu: float | None
    gpu: float | None
    battery: float | None
    temp: float | None
    auto_reduce: bool

    @property
    def setting(self) -> tuple:
        return (self.hz, self.foveation, self.eye, self.auto_reduce)

    @property
    def load(self) -> tuple:
        return (self.markers, self.tris, self.heatmap)


@dataclass
class Segment:
    samples: list[Sample] = field(default_factory=list)

    @property
    def first(self) -> Sample:
        return self.samples[0]


def download(quest_ip: str, api_key: str, folder: Path, port: int = 8080) -> None:
    base = f"http://{quest_ip}:{port}/api/perf"
    headers = {"X-Api-Key": api_key}

    def get(url: str) -> bytes:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=20) as resp:
            return resp.read()

    folder.mkdir(parents=True, exist_ok=True)
    for name in (f["file"] for f in json.loads(get(base).decode("utf-8"))):
        if name.endswith(".csv") and "/" not in name and "\\" not in name:
            (folder / name).write_bytes(get(f"{base}/{urllib.request.quote(name)}"))
            print(f"  κατέβηκε {name}")


def read(folder: Path) -> list[Sample]:
    out: list[Sample] = []
    for path in sorted(folder.glob("perf-*.csv")):
        with path.open(encoding="utf-8-sig", newline="") as f:
            for r in csv.DictReader(f):
                fps = num(r.get("fps"))
                if fps is None:
                    continue
                out.append(Sample(
                    t=datetime.fromisoformat(r["timestamp"]),
                    session=r["session_id"],
                    hz=int(float(r["refresh_req_hz"] or 0)),
                    foveation=int(r["foveation"] or 0),
                    eye=float(r["eye_scale"] or 0),
                    markers=int(r["markers"] or 0),
                    tris=int(r["model_tris"] or 0),
                    heatmap=r["heatmap"] == "1",
                    fps=fps,
                    worst_ms=num(r["worst_frame_ms"]) or 0.0,
                    dropped=int(r["dropped"] or 0),
                    cpu=num(r["cpu_ms"]),
                    gpu=num(r["gpu_ms"]),
                    battery=num(r["battery_pct"]),
                    temp=num(r["battery_temp_c"]),
                    auto_reduce=r.get("auto_reduce", "1") != "0",
                ))
    out.sort(key=lambda s: (s.session, s.t))
    return out


def segments(samples: list[Sample], warmup: int) -> list[Segment]:
    segs: list[Segment] = []
    cur: Segment | None = None
    prev: Sample | None = None
    for s in samples:
        gap = prev is not None and (s.t - prev.t).total_seconds() > 5
        if cur is None or gap or s.session != prev.session or s.setting != prev.setting or s.load != prev.load:
            cur = Segment()
            segs.append(cur)
        cur.samples.append(s)
        prev = s
    kept = []
    for seg in segs:
        seg.samples = seg.samples[warmup:]
        if seg.samples:
            kept.append(seg)
    return kept


def pct(xs: list[float], p: float) -> float | None:
    if not xs:
        return None
    xs = sorted(xs)
    k = (len(xs) - 1) * p
    lo, hi = int(k), min(int(k) + 1, len(xs) - 1)
    return xs[lo] + (xs[hi] - xs[lo]) * (k - lo)


def mean(xs: list[float]) -> float | None:
    return sum(xs) / len(xs) if xs else None


def setting_name(setting: tuple) -> str:
    hz, fov, eye, auto = setting
    fov_name = FOVEATION[fov] if 0 <= fov < len(FOVEATION) else str(fov)
    eye_text = f"{eye:g}".replace(".", ",")
    return f"{hz} Hz · foveation {fov_name} · eye {eye_text}" + ("" if auto else " · χωρίς μείωση")


def fmt(v: float | None, digits: int = 1) -> str:
    return "--" if v is None else f"{v:.{digits}f}".replace(".", ",")


def battery_per_ten_min(segs: list[Segment]) -> dict[tuple, float]:
    """Πτώση μπαταρίας ανά 10 λεπτά ανά ρύθμιση, από τμήματα ≥ 5 λεπτών (κλίση πρώτου–τελευταίου)."""
    drops: dict[tuple, list[float]] = defaultdict(list)
    for seg in segs:
        b = [s for s in seg.samples if s.battery is not None]
        if len(b) < 2:
            continue
        minutes = (b[-1].t - b[0].t).total_seconds() / 60
        if minutes >= 5:
            drops[seg.first.setting].append((b[0].battery - b[-1].battery) / minutes * 10)
    return {k: mean(v) for k, v in drops.items()}


def write_table(segs: list[Segment], out: Path) -> None:
    groups: dict[tuple, list[Sample]] = defaultdict(list)
    for seg in segs:
        groups[(seg.first.setting, seg.first.load)].extend(seg.samples)
    lines = [
        r"\begin{tabular}{lrrrrrrrr}",
        r"\toprule",
        r"Ρύθμιση & Σημεία & Τρίγωνα & Heatmap & s & FPS & 1\,\% χαμ. & Χαμένα/λεπτό & GPU (ms) \\",
        r"\midrule",
    ]
    for (setting, load), xs in sorted(groups.items()):
        markers, tris, heat = load
        fps = [s.fps for s in xs]
        low = pct(fps, 0.01)
        dropped_per_min = sum(s.dropped for s in xs) / len(xs) * 60
        gpu = [s.gpu for s in xs if s.gpu is not None]
        lines.append(
            f"{setting_name(setting)} & {markers} & {f'{tris:,}'.replace(',', '.')}"
            + f" & {'ναι' if heat else 'όχι'} & {len(xs)} & {fmt(mean(fps))} & {fmt(low)}"
            + f" & {fmt(dropped_per_min)} & {fmt(pct(gpu, 0.5), 2)} \\\\"
        )
    lines += [r"\bottomrule", r"\end{tabular}"]
    (out / "perf_by_scenario.tex").write_text("\n".join(lines) + "\n", encoding="utf-8")


def write_numbers(segs: list[Segment], out: Path) -> None:
    drain = battery_per_ten_min(segs)
    all_drain = [v for v in drain.values() if v is not None]
    temps = [s.temp for seg in segs for s in seg.samples if s.temp is not None]
    macros = {
        "PerfSeconds": str(sum(len(seg.samples) for seg in segs)),
        "PerfBatteryPerTenMin": fmt(mean(all_drain)),
        "PerfMaxTempC": fmt(max(temps) if temps else None),
    }
    text = "".join(f"\\newcommand{{\\{k}}}{{{v}}}\n" for k, v in macros.items())
    (out / "perf_numbers.tex").write_text(text, encoding="utf-8")


def plot_fps_vs_triangles(segs: list[Segment], out: Path) -> None:
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        print("  matplotlib λείπει: χωρίς γράφημα (pip install -r requirements.txt)")
        return
    by: dict[tuple, dict[int, list[float]]] = defaultdict(lambda: defaultdict(list))
    for seg in segs:
        if seg.first.heatmap:
            continue  # το heatmap είναι χωριστό σενάριο (δ)
        for s in seg.samples:
            by[s.setting][s.tris].append(s.fps)
    if not by:
        print("  κανένα τμήμα χωρίς heatmap: χωρίς γράφημα")
        return
    fig, ax = plt.subplots(figsize=(6.4, 3.8))
    for setting, per_tris in sorted(by.items()):
        xs = sorted(per_tris)
        ax.plot([x / 1000 for x in xs], [mean(per_tris[x]) for x in xs], marker="o", label=setting_name(setting))
    for hz in sorted({k[0] for k in by}):
        ax.axhline(hz, color="0.6", lw=0.8, ls="--")
    ax.set_xlabel("Τρίγωνα μοντέλου (χιλιάδες)")
    ax.set_ylabel("Μέσος ρυθμός καρέ (fps)")
    ax.grid(alpha=0.3)
    ax.legend(fontsize=8)
    fig.tight_layout()
    fig.savefig(out / "fps_vs_triangles.pdf")
    plt.close(fig)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DEFAULT_DATA, help="φάκελος με τα CSV (προεπιλογή thesis/data/performance)")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help="φάκελος εξόδου (προεπιλογή thesis/figures/generated)")
    ap.add_argument("--download", action="store_true", help="κατέβασε πρώτα τα CSV από τα γυαλιά")
    ap.add_argument("--quest-ip", help="IP των γυαλιών (με --download)")
    ap.add_argument("--api-key", default=os.environ.get("QUEST_KEY"), help="κλειδί API (ή μεταβλητή QUEST_KEY)")
    ap.add_argument("--warmup", type=int, default=5, help="δευτερόλεπτα που αγνοούνται στην αρχή κάθε τμήματος")
    args = ap.parse_args(argv)
    for stream in (sys.stdout, sys.stderr):  # Greek messages on a Windows console
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    if args.download:
        if not args.quest_ip or not args.api_key:
            ap.error("το --download θέλει --quest-ip και --api-key")
        download(args.quest_ip, args.api_key, args.data)

    samples = read(args.data)
    if not samples:
        print(f"Δεν βρέθηκαν μετρήσεις στο {args.data}")
        return 1
    segs = segments(samples, args.warmup)
    args.out.mkdir(parents=True, exist_ok=True)
    write_table(segs, args.out)
    write_numbers(segs, args.out)
    plot_fps_vs_triangles(segs, args.out)
    print(f"{len(samples)} γραμμές, {len(segs)} τμήματα → {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
