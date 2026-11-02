#!/usr/bin/env python3
"""
Prepare a heavy OBJ (photogrammetry / scan) for the Quest before send_model.py sends it.

A Geomagic Wrap / Metashape / RealityCapture export is typically millions of triangles with a
dozen 16k textures — tens of times what the headset can draw. This module:

1. decimates the mesh with MeshLab's texture-preserving quadric edge collapse (pymeshlab) to a
   target face count (default 400 000),
2. optionally moves it into the sensors' survey grid from 3+ matching points (model x y z ->
   grid E N H), by a horizontal rotation + translation (+ optional scale) or a full 3D fit,
3. downsizes every texture to at most --texture-size pixels and stores it as JPEG,
   or as ASTC 6x6 when the `astcenc` tool is on PATH (the headset uploads that directly),
4. packs .obj + .mtl + images (and .astc, stored as-is) into one zip that the headset
   unpacks (X-Model-Format: zip).

The output is always Z-up and in metres; when matching points are given its vertices are
relative to an origin (E, N, H rounded down to 100 m) that travels in the X-Model-Origin header,
so the headset's 32-bit floats keep millimetres (pymeshlab itself works in double). The grid is
any projected CRS in metres (EGSA87, UTM, other national grids), E/N/H in that order; a
geographic (lat/lon) or feet-based grid must be converted to metres first.

Matching points CSV (header required): model_x,model_y,model_z,e,n,h[,name]
"""

from __future__ import annotations

import csv
import math
import re
import shutil
import subprocess
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Sequence

DEFAULT_FACES = 400_000
DEFAULT_TEXTURE = 2048
JPEG_QUALITY = 85
IMAGE_EXTENSIONS = (".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp")
# Already compressed, so the zip stores them. .astc stays out of IMAGE_EXTENSIONS:
# that tuple is the list of source images, and PIL must not be asked to open an ASTC file.
PACKAGE_STORED = IMAGE_EXTENSIONS + (".astc",)
# Minimum horizontal spread of matching points (sum of squared offsets from their centroid, m²).
MIN_SPREAD = 0.25 * 0.25


# ----------------------------------------------------------------------------------------
# Matching points → transform (pure numpy, covered by tests)
# ----------------------------------------------------------------------------------------


@dataclass
class Match:
    model: tuple[float, float, float]
    grid: tuple[float, float, float]
    name: str = ""


@dataclass
class Fit:
    """grid = scale * R @ model + t (R 3x3). residuals in metres, per point, same order as the matches."""

    rotation: "list[list[float]]"
    translation: tuple[float, float, float]
    scale: float
    mode: str
    residuals: list[float]
    yaw_deg: float | None = None

    @property
    def rms(self) -> float:
        return math.sqrt(sum(r * r for r in self.residuals) / len(self.residuals)) if self.residuals else 0.0

    def apply(self, p: Sequence[float]) -> tuple[float, float, float]:
        r = self.rotation
        x, y, z = p
        return (
            self.scale * (r[0][0] * x + r[0][1] * y + r[0][2] * z) + self.translation[0],
            self.scale * (r[1][0] * x + r[1][1] * y + r[1][2] * z) + self.translation[1],
            self.scale * (r[2][0] * x + r[2][1] * y + r[2][2] * z) + self.translation[2],
        )


def read_matches(path: Path) -> list[Match]:
    """CSV with model_x,model_y,model_z,e,n,h[,name] (aliases: mx/my/mz, x/y/z for the grid)."""
    aliases = {
        "mx": ("model_x", "mx"), "my": ("model_y", "my"), "mz": ("model_z", "mz"),
        "e": ("e", "east", "easting", "grid_e"), "n": ("n", "north", "northing", "grid_n"),
        "h": ("h", "elevation", "height", "grid_h"), "name": ("name", "id", "point"),
    }
    matches: list[Match] = []
    with path.open(newline="", encoding="utf-8-sig") as fh:
        reader = csv.DictReader(fh)
        if reader.fieldnames is None:
            raise ValueError(f"{path}: empty file")
        cols = {k.strip().lower(): k for k in reader.fieldnames}

        def col(key: str) -> str | None:
            for a in aliases[key]:
                if a in cols:
                    return cols[a]
            return None

        needed = {k: col(k) for k in ("mx", "my", "mz", "e", "n", "h")}
        missing = [k for k, v in needed.items() if v is None]
        if missing:
            raise ValueError(f"{path}: missing columns {missing}; expected model_x,model_y,model_z,e,n,h")
        name_col = col("name")
        for i, row in enumerate(reader, 1):
            try:
                m = tuple(float(row[needed[k]]) for k in ("mx", "my", "mz"))
                g = tuple(float(row[needed[k]]) for k in ("e", "n", "h"))
            except (TypeError, ValueError):
                raise ValueError(f"{path}: row {i} has a non-numeric value") from None
            matches.append(Match(m, g, (row.get(name_col) or f"P{i}") if name_col else f"P{i}"))
    return matches


def fit_transform(matches: Sequence[Match], mode: str = "yaw", allow_scale: bool = False) -> Fit:
    """
    mode "yaw": rotation about the vertical only (the model is already level, as a survey-based
    photogrammetry export is) — needs 2+ points, 3+ to check the fit.
    mode "3d": full rotation (Kabsch/Umeyama) — needs 3+ non-collinear points.
    """
    import numpy as np

    if len(matches) < 2 or (mode == "3d" and len(matches) < 3):
        raise ValueError(f"{mode} fit needs at least {2 if mode == 'yaw' else 3} matching points")
    M = np.array([m.model for m in matches], dtype=float)
    G = np.array([m.grid for m in matches], dtype=float)
    mc, gc = M.mean(axis=0), G.mean(axis=0)
    Mo, Go = M - mc, G - gc
    yaw = None
    if mode == "yaw":
        a, b = Mo[:, :2], Go[:, :2]
        s_cos = float((a[:, 0] * b[:, 0] + a[:, 1] * b[:, 1]).sum())
        s_sin = float((a[:, 0] * b[:, 1] - a[:, 1] * b[:, 0]).sum())
        # Same 0.25 m guard as the headset's alignment: points a few mm apart would "solve" to
        # whatever heading the noise points at.
        if float((a * a).sum()) < MIN_SPREAD or float((b * b).sum()) < MIN_SPREAD:
            raise ValueError("matching points are too close together horizontally (< ~0.35 m); "
                             "cannot solve the rotation")
        theta = math.atan2(s_sin, s_cos)
        yaw = math.degrees(theta)
        c, s = math.cos(theta), math.sin(theta)
        R = np.array([[c, -s, 0.0], [s, c, 0.0], [0.0, 0.0, 1.0]])
        scale = 1.0
        if allow_scale:
            denom = float((a * a).sum())
            scale = (s_cos * c + s_sin * s) / denom if denom > 1e-12 else 1.0
    elif mode == "3d":
        H = Mo.T @ Go
        U, S, Vt = np.linalg.svd(H)
        # Collinear (or coincident) points leave the rotation about their line free: the SVD
        # then returns an arbitrary roll and the model would be tipped over.
        if S[1] <= 1e-6 * max(S[0], 1e-12) or float((Mo * Mo).sum()) < MIN_SPREAD:
            raise ValueError("3d fit needs 3+ matching points that are not on one line")
        d = np.sign(np.linalg.det(Vt.T @ U.T))
        D = np.diag([1.0, 1.0, d])
        R = Vt.T @ D @ U.T
        scale = float((S * np.diag(D)).sum() / (Mo * Mo).sum()) if allow_scale else 1.0
    else:
        raise ValueError(f"unknown fit mode {mode!r}; use yaw or 3d")
    t = gc - scale * (R @ mc)
    pred = (scale * (R @ M.T)).T + t
    residuals = [float(np.linalg.norm(p - g)) for p, g in zip(pred, G)]
    return Fit(R.tolist(), (float(t[0]), float(t[1]), float(t[2])), float(scale), mode, residuals, yaw)


def origin_for(fit: Fit, sample: Sequence[float]) -> tuple[float, float, float]:
    """Origin (multiples of 100 m) close to the transformed model, so its local floats stay small."""
    p = fit.apply(sample)
    return tuple(math.floor(v / 100.0) * 100.0 for v in p)  # type: ignore[return-value]


# ----------------------------------------------------------------------------------------
# OBJ / MTL helpers
# ----------------------------------------------------------------------------------------


def count_faces(obj: Path) -> int:
    """Fast face count (lines starting with 'f '), to decide whether decimation is needed."""
    n = 0
    with obj.open("rb") as fh:
        for line in fh:
            if line.startswith(b"f "):
                n += 1
    return n


def mtl_textures(mtl: Path) -> list[str]:
    """The map_Kd file names of an MTL (options such as -s 1 1 1 skipped: the last token is the file)."""
    out: list[str] = []
    if not mtl.is_file():
        return out
    for line in mtl.read_text(encoding="utf-8", errors="replace").splitlines():
        s = line.strip()
        if s.lower().startswith("map_kd"):
            tokens = s.split()
            if len(tokens) >= 2:
                out.append(tokens[-1])
    return out


def rewrite_mtl(mtl: Path, renames: dict[str, str]) -> None:
    """Point map_Kd lines at the converted images; drop the other texture maps the headset ignores."""
    lines = []
    for line in mtl.read_text(encoding="utf-8", errors="replace").splitlines():
        s = line.strip()
        low = s.lower()
        if low.startswith("map_kd"):
            tokens = s.split()
            src = tokens[-1]
            lines.append("map_Kd " + renames.get(src, renames.get(Path(src).name, src)))
        elif re.match(r"(map_|bump|disp|decal|refl)", low):
            continue
        else:
            lines.append(line)
    mtl.write_text("\n".join(lines) + "\n", encoding="utf-8")


def convert_image(src: Path, dst: Path, max_size: int) -> tuple[int, int]:
    """Downscale to at most max_size on the long side and store as JPEG. Returns the new size."""
    from PIL import Image

    Image.MAX_IMAGE_PIXELS = None  # photogrammetry textures are legitimately huge (16k x 16k)
    with Image.open(src) as im:
        im.draft("RGB", (max_size, max_size))  # JPEG sources decode at reduced size directly
        im = im.convert("RGB")
        if max(im.size) > max_size:
            im.thumbnail((max_size, max_size), Image.Resampling.LANCZOS, reducing_gap=3.0)
        im.save(dst, "JPEG", quality=JPEG_QUALITY, optimize=True)
        return im.size


def astcenc_path() -> str | None:
    """The ARM astcenc CLI, if it is installed. The Quest build reads its .astc files."""
    for name in ("astcenc", "astcenc-avx2", "astcenc-sse2", "astcenc-neon"):
        found = shutil.which(name)
        if found:
            return found
    return None


def encode_astc(src: Path, dst: Path) -> bool:
    """Compress src (PNG or JPEG) to an astcenc .astc file, 6x6. False when the tool is absent or fails."""
    enc = astcenc_path()
    if enc is None:
        return False
    try:
        subprocess.run([enc, "-cl", str(src), str(dst), "6x6", "-medium"], check=True, capture_output=True)
    except (OSError, subprocess.CalledProcessError):
        if dst.exists():
            dst.unlink()
        return False
    return dst.is_file() and dst.stat().st_size > 16


def package_files(out_dir: Path) -> list[tuple[Path, int]]:
    """Files the headset package keeps, with their zip method.

    OBJ and MTL are deflated. Images and ASTC are stored: they are already compressed,
    and the headset extractor only accepts these suffixes.
    """
    stored = {s.lower() for s in PACKAGE_STORED}
    chosen: list[tuple[Path, int]] = []
    for f in sorted(p for p in out_dir.iterdir() if p.is_file()):
        suf = f.suffix.lower()
        if suf in (".obj", ".mtl") or suf in stored:
            kind = zipfile.ZIP_STORED if suf in stored else zipfile.ZIP_DEFLATED
            chosen.append((f, kind))
    return chosen


# ----------------------------------------------------------------------------------------
# The pipeline
# ----------------------------------------------------------------------------------------


@dataclass
class PrepInfo:
    faces_in: int = 0
    faces_out: int = 0
    textures: int = 0
    package: Path | None = None
    package_bytes: int = 0
    origin: tuple[float, float, float] | None = None
    fit: Fit | None = None
    notes: list[str] = field(default_factory=list)


def _transform_matrix(fit: Fit | None, units: float, up: str, origin: Sequence[float] | None):
    """4x4 applied to the source vertices: units → metres, Y-up → Z-up, fit, minus origin."""
    import numpy as np

    T = np.eye(4)
    T[:3, :3] *= units
    if up == "y":
        # (x, y, z) Y-up right-handed → (x, -z, y) Z-up right-handed
        A = np.eye(4)
        A[:3, :3] = np.array([[1, 0, 0], [0, 0, -1], [0, 1, 0]], dtype=float)
        T = A @ T
    if fit is not None:
        F = np.eye(4)
        F[:3, :3] = fit.scale * np.array(fit.rotation)
        F[:3, 3] = fit.translation
        T = F @ T
    if origin is not None:
        O = np.eye(4)
        O[:3, 3] = [-origin[0], -origin[1], -origin[2]]
        T = O @ T
    return T


def prepare(
    src: Path,
    work: Path,
    *,
    name: str,
    faces: int = DEFAULT_FACES,
    texture_size: int = DEFAULT_TEXTURE,
    units: float = 1.0,
    up: str = "z",
    matches: Sequence[Match] | None = None,
    fit_mode: str = "yaw",
    allow_scale: bool = False,
    log=print,
) -> PrepInfo:
    """Decimate, place and pack src (an OBJ with its MTL and images) into work/<name>.zip."""
    try:
        import pymeshlab  # type: ignore
    except ImportError:
        raise SystemExit("pymeshlab is needed to prepare heavy / textured OBJ files:  pip install pymeshlab") from None

    info = PrepInfo()
    out_dir = work / "pkg"
    if out_dir.exists():
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True)

    # Match points: model units/axes first, so the fit works in the same space the mesh ends up in.
    fit = None
    if matches:
        def to_z_up_metres(p: Sequence[float]) -> tuple[float, float, float]:
            x, y, z = (v * units for v in p)
            return (x, -z, y) if up == "y" else (x, y, z)

        fit = fit_transform([Match(to_z_up_metres(m.model), m.grid, m.name) for m in matches], fit_mode, allow_scale)
        info.fit = fit
        info.origin = origin_for(fit, to_z_up_metres(matches[0].model))
        log(f"  Fit ({fit.mode}{', with scale' if allow_scale else ''}): RMS {fit.rms * 100:.1f} cm"
            + (f", yaw {fit.yaw_deg:.3f} deg" if fit.yaw_deg is not None else "")
            + (f", scale {fit.scale:.5f}" if allow_scale else ""))
        for m, r in zip(matches, fit.residuals):
            flag = "  <-- check" if r > max(0.10, 3 * fit.rms) else ""
            log(f"    {m.name:>8}: {r * 100:6.1f} cm{flag}")
        if len(matches) < 3:
            info.notes.append("Only 2 matching points: the fit cannot be checked. Add a third.")

    info.faces_in = count_faces(src)
    log(f"  Loading {src.name} ({src.stat().st_size / 1e6:.0f} MB, {info.faces_in:,} faces) in MeshLab ...")
    ms = pymeshlab.MeshSet()
    ms.load_new_mesh(str(src))
    m = ms.current_mesh()
    has_tex = m.has_wedge_tex_coord()
    log(f"  {m.vertex_number():,} vertices, {m.face_number():,} faces, "
        f"{'textured' if has_tex else 'no texture coordinates'}")

    if m.face_number() > faces:
        log(f"  Decimating to {faces:,} faces (quadric edge collapse{' with texture' if has_tex else ''}) ...")
        if has_tex:
            ms.meshing_decimation_quadric_edge_collapse_with_texture(
                targetfacenum=faces, qualitythr=0.3, extratcoordw=1.0, preserveboundary=True,
                boundaryweight=1.0, optimalplacement=True, preservenormal=True, planarquadric=False)
        else:
            ms.meshing_decimation_quadric_edge_collapse(
                targetfacenum=faces, qualitythr=0.3, preserveboundary=True, boundaryweight=1.0,
                optimalplacement=True, preservenormal=True, planarquadric=False)
    ms.meshing_remove_unreferenced_vertices()
    info.faces_out = ms.current_mesh().face_number()

    T = _transform_matrix(fit, units, up, info.origin)
    import numpy as np

    if not np.allclose(T, np.eye(4)):
        ms.set_matrix(transformmatrix=T, freeze=True)

    obj_out = out_dir / f"{name}.obj"
    ms.save_current_mesh(str(obj_out), save_textures=False, save_wedge_texcoord=has_tex,
                         save_vertex_color=False, save_face_color=False, save_vertex_normal=False)
    mtl_out = obj_out.with_suffix(".mtl")
    if not mtl_out.is_file():
        candidates = list(out_dir.glob("*.mtl"))
        mtl_out = candidates[0] if candidates else mtl_out

    renames: dict[str, str] = {}
    for tex in dict.fromkeys(mtl_textures(mtl_out)):
        source = (src.parent / tex)
        if not source.is_file():
            source = src.parent / Path(tex).name
        if not source.is_file():
            info.notes.append(f"Texture not found next to the OBJ: {tex}")
            continue
        jpg = Path(tex).with_suffix(".jpg").name
        jpg_path = out_dir / jpg
        size = convert_image(source, jpg_path, texture_size)
        written = jpg
        astc_path = jpg_path.with_suffix(".astc")
        if encode_astc(jpg_path, astc_path):
            jpg_path.unlink()
            written = astc_path.name
        renames[tex] = written
        info.textures += 1
        log(f"    {Path(tex).name}: {source.stat().st_size / 1e6:.0f} MB -> {written} {size[0]}x{size[1]}")
    if mtl_out.is_file():
        rewrite_mtl(mtl_out, renames)

    package = work / f"{name}.zip"
    if package.exists():
        package.unlink()
    with zipfile.ZipFile(package, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for f, kind in package_files(out_dir):
            z.write(f, f.name, compress_type=kind)
    info.package = package
    info.package_bytes = package.stat().st_size
    log(f"  Package {package.name}: {info.faces_out:,} faces, {info.textures} textures, "
        f"{info.package_bytes / 1e6:.1f} MB")
    return info
