#!/usr/bin/env python3
"""
Move an exported model (OBJ tiles + MTL + textures, e.g. Metashape's ΕΓΣΑ87 export) into the
project's local grid, the one Terraweb's sensor x/y/z are in, so the headset can show model
and sensors together without any lat/lon.

The transform comes from common points: the same sensors with coordinates in the model's
system (--model-points, e.g. the surveyor's EGSA_all_points.csv) and in the local grid
(--local-points CSV, or straight from Terraweb with -u: each sensor's latest measured x/y/z).
It is a rotation about the vertical plus a translation (add --allow-scale for a scale factor),
fitted by least squares on E/N, and one vertical shift (the mean height difference).
Textures are untouched: a rigid move does not change UVs.

Output (next to the input folder, "<input> local"): one merged OBJ + MTL + the images, a
fit report CSV and a zip in the package format the headset takes, plus the send command.

Usage:
    python model_to_local.py "…/konitsa v.3 quest egsa87 4x4k" --shift 222000 4436000 0 \\
        --model-points EGSA_all_points.csv -u myuser --project Konitsa
    python model_to_local.py "…/4x4k" --shift 222000 4436000 0 --model-points EGSA_all_points.csv \\
        --local-points local.csv          # name,x,y,z  (no Terraweb login)
    ... --send --quest-ip 192.168.1.30 --api-key KEY       # and send it to the headset
Password: prompted, or TERRAWEB_PASSWORD env var.
"""

from __future__ import annotations

import argparse
import csv
import getpass
import math
import os
import re
import shutil
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path


@dataclass
class Fit:
    """local = s·R(θ)·(model − model_c) + local_c on E/N; local_h = model_h + dh."""
    theta: float
    scale: float
    model_c: tuple[float, float]
    local_c: tuple[float, float]
    dh: float

    def apply(self, e: float, n: float, h: float) -> tuple[float, float, float]:
        c, s = math.cos(self.theta) * self.scale, math.sin(self.theta) * self.scale
        x, y = e - self.model_c[0], n - self.model_c[1]
        return (self.local_c[0] + c * x - s * y, self.local_c[1] + s * x + c * y, h + self.dh)

    def rotate(self, x: float, y: float) -> tuple[float, float]:
        """A direction (normal): rotation only."""
        c, s = math.cos(self.theta), math.sin(self.theta)
        return (c * x - s * y, s * x + c * y)


def fit(pairs: list[tuple[tuple[float, float, float], tuple[float, float, float]]], allow_scale: bool = False) -> Fit:
    """Least-squares rotation (+ scale) and translation from (model, local) point pairs."""
    if len(pairs) < 2:
        raise ValueError("at least 2 common points are needed")
    n = len(pairs)
    mc = (sum(p[0][0] for p in pairs) / n, sum(p[0][1] for p in pairs) / n)
    lc = (sum(p[1][0] for p in pairs) / n, sum(p[1][1] for p in pairs) / n)
    # Complex form: q = a·p with a = Σ q·conj(p) / Σ |p|²; θ = arg(a), s = |a|.
    re_, im_, den = 0.0, 0.0, 0.0
    for (me, mn, _), (le, ln, _) in pairs:
        px, py = me - mc[0], mn - mc[1]
        qx, qy = le - lc[0], ln - lc[1]
        re_ += qx * px + qy * py
        im_ += qy * px - qx * py
        den += px * px + py * py
    if den < 1e-6:
        raise ValueError("the common points are all in one place")
    theta = math.atan2(im_, re_)
    scale = math.hypot(re_, im_) / den if allow_scale else 1.0
    dh = sum(p[1][2] - p[0][2] for p in pairs) / n
    return Fit(theta, scale, mc, lc, dh)


def read_points(path: Path) -> dict[str, tuple[float, float, float]]:
    """name → (e, n, h) from a CSV with a name column and three coordinate columns."""
    names = ("name", "id", "point", "sensor", "sensor_name")
    cols_e = ("e", "x", "e_egsa87", "east", "easting")
    cols_n = ("n", "y", "n_egsa87", "north", "northing")
    cols_h = ("h", "z", "elevation", "height")
    with path.open(newline="", encoding="utf-8-sig") as fh:
        reader = csv.DictReader(fh)
        lower = {k.strip().lower(): k for k in reader.fieldnames or []}

        def pick(options):
            for o in options:
                if o in lower:
                    return lower[o]
            raise ValueError(f"{path}: none of the columns {options} (has {list(lower)})")

        cn, ce, cnn, ch = pick(names), pick(cols_e), pick(cols_n), pick(cols_h)
        out = {}
        for row in reader:
            try:
                out[row[cn].strip()] = (float(row[ce]), float(row[cnn]), float(row[ch]))
            except (TypeError, ValueError):
                continue
        return out


def terraweb_points(backend: str, username: str, project_wanted: str | None, insecure: bool,
                    pin: str | None) -> dict[str, tuple[float, float, float]]:
    """Each sensor's latest enabled measured x/y/z (what the headset places it by)."""
    from terraweb_bridge import Terraweb, _results, check_backend_url, choose_project

    check_backend_url(backend)
    password = os.environ.get("TERRAWEB_PASSWORD") or getpass.getpass(f"Password for {username}: ")
    tw = Terraweb(backend, verify_tls=not insecure, pin_sha256=pin)
    tw.login(username, password)
    project = choose_project(_results(tw.get("project/")), project_wanted)
    profile = tw.get("auth/profile/")
    original = profile.get("current_project_id") or (profile.get("current_project") or {}).get("id")
    switched = original != project["id"]
    out: dict[str, tuple[float, float, float]] = {}
    try:
        if switched:
            tw.patch("auth/profile/", {"current_project_id": project["id"]})
        for s in _results(tw.get("sensors/sensors/")):
            sid, name = s.get("sensor_id"), s.get("sensor_name") or s.get("sensor_id")
            rows = _results(tw.get("sensors/measurements/", sensor=sid, ordering="-meas_date", page_size=5))
            for m in rows or []:
                if m.get("is_enabled") is False:
                    continue
                v = m.get("values") or {}
                if all(isinstance(v.get(k), (int, float)) for k in ("x", "y", "z")):
                    out[name] = (float(v["x"]), float(v["y"]), float(v["z"]))
                    break
    finally:
        if switched and original is not None:
            tw.patch("auth/profile/", {"current_project_id": original})
    print(f"Terraweb {project['name']}: {len(out)} sensors with a measured x/y/z")
    return out


_FACE_INDEX = re.compile(r"-?\d+")


def transform_tiles(src: Path, out: Path, name: str, shift: tuple[float, float, float], f: Fit,
                    origin: tuple[float, float, float]) -> tuple[int, list[str]]:
    """Merge the folder's OBJ tiles into out/name.obj, moved by f and relative to origin."""
    out.mkdir(parents=True, exist_ok=True)
    tiles = sorted(p for p in src.glob("*.obj"))
    if not tiles:
        raise SystemExit(f"No .obj files in {src}")
    nv = nvt = nvn = 0
    mtl_blocks, seen, images = [], set(), []
    with open(out / f"{name}.obj", "w", encoding="utf-8", newline="\n") as o:
        o.write(f"mtllib {name}.mtl\n")
        for t in tiles:
            cv = cvt = cvn = 0
            o.write(f"g {t.stem}\n")
            for line in open(t, encoding="utf-8", errors="replace"):
                if line.startswith("v "):
                    p = line.split()
                    x, y, z = f.apply(float(p[1]) + shift[0], float(p[2]) + shift[1], float(p[3]) + shift[2])
                    o.write(f"v {x - origin[0]:.4f} {y - origin[1]:.4f} {z - origin[2]:.4f}\n")
                    cv += 1
                elif line.startswith("vn "):
                    p = line.split()
                    x, y = f.rotate(float(p[1]), float(p[2]))
                    o.write(f"vn {x:.6f} {y:.6f} {p[3]}\n")
                    cvn += 1
                elif line.startswith("vt "):
                    o.write(line)
                    cvt += 1
                elif line.startswith("f "):
                    parts = []
                    for tok in line.split()[1:]:
                        idx = tok.split("/")
                        if any(i.startswith("-") for i in idx if i):
                            raise SystemExit(f"{t.name}: negative OBJ indices are not handled")
                        offs = (nv, nvt, nvn)
                        parts.append("/".join(str(int(i) + offs[k]) if i else "" for k, i in enumerate(idx)))
                    o.write("f " + " ".join(parts) + "\n")
                elif line.startswith("usemtl "):
                    o.write(line)
            nv, nvt, nvn = nv + cv, nvt + cvt, nvn + cvn
            mtl = t.with_suffix(".mtl")
            if mtl.is_file():
                for block in re.split(r"(?m)^(?=newmtl )", mtl.read_text(encoding="utf-8", errors="replace")):
                    m = re.match(r"newmtl (\S+)", block)
                    if not m or m.group(1) in seen:
                        continue
                    seen.add(m.group(1))
                    mtl_blocks.append(block.strip() + "\n")
                    images += [i.strip() for i in re.findall(r"(?m)^map_Kd (.+)$", block)]
    (out / f"{name}.mtl").write_text("\n".join(mtl_blocks), encoding="utf-8")
    for img in dict.fromkeys(images):
        if (src / img).is_file():
            shutil.copyfile(src / img, out / Path(img).name)
        else:
            print(f"  Note: texture not found: {img}")
    return nv, list(dict.fromkeys(images))


def package(folder: Path, zip_path: Path) -> None:
    with zipfile.ZipFile(zip_path, "w") as z:
        for f in sorted(folder.iterdir()):
            # .astc is already compressed (astcenc). The headset extractor accepts it next to jpg/png.
            if f.suffix.lower() in (".obj", ".mtl", ".jpg", ".jpeg", ".png", ".astc"):
                kind = zipfile.ZIP_STORED if f.suffix.lower() in (".jpg", ".jpeg", ".png", ".astc") else zipfile.ZIP_DEFLATED
                z.write(f, f.name, compress_type=kind)


def main(argv: list[str] | None = None) -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
        except (AttributeError, ValueError):
            pass
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folder", type=Path, help="Folder with the exported OBJ tiles, MTL and textures")
    ap.add_argument("--shift", nargs=3, type=float, required=True, metavar=("E", "N", "H"),
                    help="What the export subtracted (Metashape shift; 0 0 0 for absolute vertices)")
    ap.add_argument("--model-points", type=Path, required=True,
                    help="CSV name,E,N,H of the sensors in the model's system (e.g. EGSA_all_points.csv)")
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--local-points", type=Path, help="CSV name,x,y,z of the same sensors in the local grid")
    src.add_argument("-u", "--username", help="Read the local x/y/z from Terraweb (latest measurement)")
    ap.add_argument("--backend", default="https://demo.iqsoft.xyz")
    ap.add_argument("--project", help="Terraweb project id or exact name")
    ap.add_argument("--cert-sha256", metavar="HEX")
    ap.add_argument("--insecure", action="store_true")
    ap.add_argument("--allow-scale", action="store_true", help="Also fit a scale factor")
    ap.add_argument("--exclude", default="", help="Comma-separated point names to leave out of the fit")
    ap.add_argument("--name", help="Model name (default: folder name + ' local')")
    ap.add_argument("--max-residual", type=float, default=0.5,
                    help="Refuse when a common point is further off than this after the fit (m)")
    ap.add_argument("--send", action="store_true", help="Send the package to the headset")
    ap.add_argument("--quest-ip")
    ap.add_argument("--port", type=int, default=8080)
    ap.add_argument("--api-key", default=os.environ.get("QUEST_KEY"))
    args = ap.parse_args(argv)

    model_pts = read_points(args.model_points)
    local_pts = (read_points(args.local_points) if args.local_points
                 else terraweb_points(args.backend, args.username, args.project, args.insecure, args.cert_sha256))
    excluded = {s.strip() for s in args.exclude.split(",") if s.strip()}
    common = sorted(k for k in model_pts if k in local_pts and k not in excluded)
    print(f"Common points: {len(common)} (model {len(model_pts)}, local {len(local_pts)})")
    if len(common) < 2:
        sys.exit("Need at least 2 points with the same name in both lists.")

    f = fit([(model_pts[k], local_pts[k]) for k in common], args.allow_scale)
    print(f"Rotation {math.degrees(f.theta):+.4f}°, scale {f.scale:.6f}, height shift {f.dh:+.3f} m")
    worst, rows = 0.0, []
    for k in common:
        x, y, z = f.apply(*model_pts[k])
        lx, ly, lz = local_pts[k]
        dxy, dz = math.hypot(x - lx, y - ly), z - lz
        worst = max(worst, dxy, abs(dz))
        rows.append((k, dxy, dz))
        print(f"  {k:<8} ΔEN {dxy * 100:6.1f} cm   ΔH {dz * 100:+6.1f} cm")
    rms = math.sqrt(sum(r[1] ** 2 for r in rows) / len(rows))
    print(f"RMS horizontal {rms * 100:.1f} cm, worst {worst * 100:.1f} cm")

    name = args.name or f"{args.folder.name} local"
    out = args.folder.parent / f"{args.folder.name} local"
    with open(out.parent / f"{out.name} fit.csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["point", "residual_en_m", "residual_h_m"])
        w.writerows((k, f"{a:.4f}", f"{b:.4f}") for k, a, b in rows)
    if worst > args.max_residual:
        sys.exit(f"A common point is {worst:.2f} m off after the fit (limit --max-residual {args.max_residual}): "
                 "check the names, or leave it out with --exclude. Nothing written.")

    lc = f.local_c
    origin = (math.floor(lc[0] / 10) * 10.0, math.floor(lc[1] / 10) * 10.0, 0.0)
    vertices, images = transform_tiles(args.folder, out, re.sub(r"[^\w.-]+", "_", name), tuple(args.shift), f, origin)
    zip_path = out.parent / f"{out.name}.zip"
    package(out, zip_path)
    print(f"\n{vertices} vertices, {len(images)} textures → {out}")
    print(f"Package {zip_path} ({zip_path.stat().st_size / 1e6:.1f} MB), origin E{origin[0]:.0f} N{origin[1]:.0f} H0")

    if not args.send:
        print("Send it with --send --quest-ip … --api-key … (or rerun with those).")
        return
    if not args.quest_ip:
        sys.exit("--send needs --quest-ip")
    import send_model as sm
    sm.check_key(args.quest_ip, args.port, args.api_key)
    headers = sm.build_headers(args.api_key, name, "zip", units=1.0, up="z", georeferenced=True,
                               origin=origin, project=None, source=zip_path.name)
    r = sm.send_model(zip_path, args.quest_ip, args.port, headers)
    sm.print_response(r)
    if not r.ok:
        sys.exit(1)


if __name__ == "__main__":
    main()
