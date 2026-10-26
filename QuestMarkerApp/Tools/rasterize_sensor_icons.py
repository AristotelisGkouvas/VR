#!/usr/bin/env python3
"""
Rasterize the Terraweb brand-kit sensor icons to white-on-transparent PNGs for Unity.

The Quest tints them per sensor family at runtime, so they are drawn white. Source of
truth stays the frontend kit (docs/design/brand-assets/ui-icons/svg) — re-run this after
the kit changes instead of editing the PNGs.

Usage:
    python Tools/rasterize_sensor_icons.py --kit "C:/.../terraweb2-frontend-main/docs/design/brand-assets/ui-icons/svg"
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

SENSOR_ICONS = [
    "inclinometer", "piezometer", "crack-meter", "tilt", "strain-gauge", "displacement",
    "pressure", "water-level", "temperature", "borehole", "target", "vibration", "sensor-point",
]
# Action glyphs for the controller button badges and panel buttons.
UI_ICONS = [
    "menu", "close", "check", "chevron-right", "sort", "pin", "history", "list", "info", "compass",
    "edit", "location", "trash", "eye", "search", "play", "pause", "clock", "displacement", "zoom-in", "time-series",
]
CHROME_CANDIDATES = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
]


def white_svg(src: str, size: int, stroke_width: float) -> str:
    svg = src.replace('stroke="currentColor"', 'stroke="#FFFFFF"')
    svg = re.sub(r'stroke-width="[\d.]+"', f'stroke-width="{stroke_width}"', svg)
    return re.sub(r'width="24" height="24"', f'width="{size}" height="{size}"', svg, count=1)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--kit", required=True, type=Path)
    parser.add_argument("--set", choices=["sensor", "ui"], default="sensor")
    parser.add_argument("--out", type=Path, help="Defaults to Resources/SensorIcons or Resources/UiIcons")
    parser.add_argument("--size", type=int, default=256)
    # Slightly heavier than the kit's 1.7 — hairlines shimmer at headset resolution.
    parser.add_argument("--stroke-width", type=float, default=2.0)
    args = parser.parse_args()
    icons = SENSOR_ICONS if args.set == "sensor" else UI_ICONS
    if args.out is None:
        args.out = (Path(__file__).resolve().parent.parent / "Assets" / "Resources"
                    / ("SensorIcons" if args.set == "sensor" else "UiIcons"))

    chrome = next((c for c in CHROME_CANDIDATES if Path(c).exists()), None) or shutil.which("chrome")
    if not chrome:
        raise SystemExit("Chrome or Edge is required for headless rasterization")

    args.out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        for name in icons:
            src = args.kit / f"{name}.svg"
            if not src.exists():
                print(f"  missing {src.name}, skipped")
                continue
            html = Path(tmp) / f"{name}.html"
            html.write_text(
                "<html><body style='margin:0;background:transparent'>"
                + white_svg(src.read_text(encoding="utf-8"), args.size, args.stroke_width)
                + "</body></html>",
                encoding="utf-8",
            )
            png = args.out / f"{name}.png"
            subprocess.run(
                [chrome, "--headless=new", "--disable-gpu", "--hide-scrollbars",
                 "--default-background-color=00000000", f"--window-size={args.size},{args.size}",
                 f"--screenshot={png}", html.as_uri()],
                check=True, capture_output=True, timeout=60,
            )
            print(f"  {png.name}")


if __name__ == "__main__":
    main()
