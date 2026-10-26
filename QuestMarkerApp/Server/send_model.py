#!/usr/bin/env python3
"""
Send a design model (OBJ / DXF / IFC) to the Quest, or list / delete the models stored on it.

The headset keeps models in its models directory next to a sidecar JSON that records the
display name, format, unit scale, up axis and (optionally) the map-coordinate origin.
OBJ and DXF files are streamed as-is; IFC is converted to OBJ on the PC first
(ifcopenshell, see --help) because the headset has no IFC parser.

Usage:
    python send_model.py model.obj --quest-ip 192.168.1.30 --api-key KEY
    python send_model.py model.obj --quest-ip ... --api-key ... --name "Κτήριο Α" --units mm --up y
    python send_model.py model.dxf --quest-ip ... --api-key ... --origin 476512.3 4203118.9 112.4
    python send_model.py model.ifc --quest-ip ... --api-key ...           (converted to OBJ, then sent)
    python send_model.py model.obj --quest-ip ... --api-key ... --local   (not georeferenced: place by hand)
    python send_model.py --list   --quest-ip ... --api-key ...
    python send_model.py --delete "Κτήριο Α" --quest-ip ... --api-key ...

Units: --units m|mm|cm|ft|in or a float (metres per source unit). Default metres for OBJ/DXF;
the headset may override it from the DXF $INSUNITS header. Converted IFC is always written in
metres (the IfcProject length unit is read and applied), so --units stays 1 for it.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import sys
import tempfile
import time
from collections import OrderedDict
from dataclasses import dataclass, field
from pathlib import Path
from typing import BinaryIO, Iterable, Iterator, Sequence
from urllib.parse import quote

import requests

# model_prep sits next to this script; make it importable however the script is started.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from model_prep import (  # noqa: E402
    DEFAULT_FACES,
    DEFAULT_TEXTURE,
    count_faces,
    mtl_textures,
    prepare,
    read_matches,
)

UNIT_SCALES: dict[str, float] = {
    "m": 1.0,
    "mm": 0.001,
    "cm": 0.01,
    "ft": 0.3048,
    "in": 0.0254,
}

MODEL_FORMATS = ("obj", "dxf", "ifc")

# Colour per IfcProduct type for the MTL written beside a converted IFC (diffuse r g b 0..1).
TYPE_COLOURS: dict[str, tuple[float, float, float]] = {
    "IfcWall": (0.85, 0.82, 0.76),
    "IfcWallStandardCase": (0.85, 0.82, 0.76),
    "IfcSlab": (0.62, 0.62, 0.64),
    "IfcRoof": (0.55, 0.30, 0.25),
    "IfcColumn": (0.45, 0.47, 0.52),
    "IfcBeam": (0.50, 0.42, 0.34),
    "IfcFooting": (0.40, 0.40, 0.42),
    "IfcPile": (0.38, 0.38, 0.40),
    "IfcStair": (0.70, 0.66, 0.60),
    "IfcStairFlight": (0.70, 0.66, 0.60),
    "IfcRamp": (0.68, 0.64, 0.58),
    "IfcRailing": (0.30, 0.30, 0.32),
    "IfcDoor": (0.60, 0.40, 0.20),
    "IfcWindow": (0.55, 0.75, 0.90),
    "IfcCurtainWall": (0.55, 0.75, 0.90),
    "IfcPlate": (0.60, 0.62, 0.66),
    "IfcMember": (0.52, 0.48, 0.42),
    "IfcPipeSegment": (0.20, 0.55, 0.85),
    "IfcPipeFitting": (0.20, 0.50, 0.80),
    "IfcDuctSegment": (0.75, 0.75, 0.55),
    "IfcDuctFitting": (0.70, 0.70, 0.50),
    "IfcCableCarrierSegment": (0.85, 0.55, 0.20),
    "IfcCableSegment": (0.85, 0.45, 0.15),
    "IfcFlowTerminal": (0.35, 0.65, 0.45),
    "IfcFlowSegment": (0.25, 0.55, 0.80),
    "IfcFlowFitting": (0.25, 0.50, 0.75),
    "IfcDistributionElement": (0.45, 0.60, 0.55),
    "IfcFurnishingElement": (0.72, 0.58, 0.40),
    "IfcSite": (0.40, 0.55, 0.30),
    "IfcBuildingElementProxy": (0.70, 0.70, 0.70),
    "IfcCovering": (0.80, 0.80, 0.78),
}
DEFAULT_COLOUR = (0.65, 0.65, 0.65)


# ----------------------------------------------------------------------------------------
# Argument helpers (pure functions, covered by test_send_model.py)
# ----------------------------------------------------------------------------------------


def parse_units(value: str | float | None) -> float:
    """m|mm|cm|ft|in or a positive float → metres per source unit (default 1)."""
    if value is None:
        return 1.0
    if isinstance(value, (int, float)):
        scale = float(value)
    else:
        key = value.strip().lower()
        if key in UNIT_SCALES:
            return UNIT_SCALES[key]
        try:
            scale = float(key)
        except ValueError:
            raise ValueError(f"--units must be one of {sorted(UNIT_SCALES)} or a number, not {value!r}") from None
    if not math.isfinite(scale) or scale <= 0:
        raise ValueError(f"--units must be a positive number, not {value!r}")
    return scale


def infer_format(path: Path) -> str:
    """obj|dxf|ifc from the file extension (case-insensitive)."""
    ext = path.suffix.lower().lstrip(".")
    if ext not in MODEL_FORMATS:
        raise ValueError(f"Unsupported model type {path.suffix!r}; expected one of {MODEL_FORMATS}")
    return ext


def default_name(path: Path) -> str:
    return path.stem


def encode_header_value(text: str) -> str:
    """HTTP headers carry Latin-1 only: UTF-8 percent-encode (Greek names) — the headset unescapes."""
    return quote(text, safe=" ()[]!*,.-_~")


def build_headers(
    api_key: str,
    name: str,
    fmt: str,
    *,
    units: float = 1.0,
    up: str = "z",
    georeferenced: bool = True,
    origin: Sequence[float] | None = None,
    project: int | None = None,
    source: str | None = None,
) -> dict[str, str]:
    """Headers for POST /api/model (metadata travels in headers; the body is the raw file)."""
    if fmt not in ("obj", "dxf", "zip"):
        raise ValueError(f"the headset accepts obj, dxf or zip (prepared package), not {fmt!r} (convert IFC first)")
    if up not in ("z", "y"):
        raise ValueError(f"--up must be z or y, not {up!r}")
    if not name.strip():
        raise ValueError("model name must not be empty")
    headers = {
        "X-Api-Key": api_key,
        "Content-Type": "application/octet-stream",
        "X-Model-Name": encode_header_value(name.strip()),
        "X-Model-Format": fmt,
        "X-Model-Units": repr(float(units)),
        "X-Model-Up": up,
        "X-Model-Georeferenced": "true" if georeferenced else "false",
    }
    if origin is not None:
        if len(origin) != 3:
            raise ValueError("--origin needs three numbers: E N H")
        values = [float(v) for v in origin]
        if not all(math.isfinite(v) for v in values):
            raise ValueError(f"--origin must be finite numbers, not {list(origin)!r}")
        headers["X-Model-Origin"] = ",".join(repr(v) for v in values)
    if project is not None:
        headers["X-Model-Project"] = str(int(project))
    if source:
        headers["X-Model-Source"] = encode_header_value(source)
    return headers


def model_url(quest_ip: str, port: int, suffix: str = "") -> str:
    return f"http://{quest_ip}:{port}/api/model{suffix}"


# ----------------------------------------------------------------------------------------
# HTTP
# ----------------------------------------------------------------------------------------


class ProgressFile:
    """A readable wrapper that reports upload progress; keeps __len__ so requests sends Content-Length."""

    def __init__(self, fh: BinaryIO, size: int, label: str = "", out=None) -> None:
        self._fh = fh
        self._size = size
        self._sent = 0
        self._label = label
        self._out = out or sys.stdout
        self._last = 0.0

    def __len__(self) -> int:
        return self._size

    def read(self, n: int = -1) -> bytes:
        chunk = self._fh.read(n)
        self._sent += len(chunk)
        now = time.monotonic()
        if chunk and (now - self._last > 0.5 or self._sent >= self._size):
            self._last = now
            pct = 100.0 * self._sent / self._size if self._size else 100.0
            print(f"\r{self._label}{self._sent / 1e6:8.1f} / {self._size / 1e6:.1f} MB ({pct:5.1f}%)",
                  end="", file=self._out, flush=True)
        return chunk

    def __iter__(self) -> Iterator[bytes]:
        while True:
            chunk = self.read(64 * 1024)
            if not chunk:
                return
            yield chunk


def send_model(path: Path, quest_ip: str, port: int, headers: dict[str, str], timeout: float = 300.0) -> requests.Response:
    """Stream the file body to POST /api/model and return the response."""
    size = path.stat().st_size
    url = model_url(quest_ip, port)
    print(f"Uploading {path.name} ({size / 1e6:.1f} MB) to {url}")
    with path.open("rb") as fh:
        body = ProgressFile(fh, size, label="  ")
        r = requests.post(url, data=body, headers=headers, timeout=timeout)
    print()
    return r


def list_models(quest_ip: str, port: int, api_key: str) -> list[dict]:
    r = requests.get(model_url(quest_ip, port, "s"), headers={"X-Api-Key": api_key}, timeout=10)
    if r.status_code == 401:
        raise PermissionError("the headset rejected the key: pass --api-key with the key from its main menu")
    r.raise_for_status()
    data = r.json()
    if not isinstance(data, list):
        raise ValueError(f"unexpected /api/models answer: {str(data)[:200]}")
    return data


def check_key(quest_ip: str, port: int, api_key: str) -> None:
    """
    A cheap authenticated GET before a long upload. The headset answers a keyless or wrong-key
    upload at once and closes the connection without reading the body (so a stranger cannot
    tie it up), which the upload would only see as a reset connection.
    """
    r = requests.get(f"http://{quest_ip}:{port}/api/status", headers={"X-Api-Key": api_key}, timeout=10)
    if r.status_code == 401:
        raise PermissionError("the headset rejected the key: pass --api-key with the key from its main menu")
    if r.status_code == 429:
        raise PermissionError("the headset is refusing this PC after too many wrong keys: wait a minute, "
                              "then retry with the key from its main menu")


def delete_model(quest_ip: str, port: int, api_key: str, name: str) -> requests.Response:
    return requests.delete(model_url(quest_ip, port, "/" + quote(name, safe="")),
                           headers={"X-Api-Key": api_key}, timeout=10)


def print_response(r: requests.Response) -> None:
    try:
        print(r.status_code, json.dumps(r.json(), indent=2, ensure_ascii=False))
    except ValueError:
        print(r.status_code, r.text)


# ----------------------------------------------------------------------------------------
# IFC → OBJ
# ----------------------------------------------------------------------------------------


@dataclass
class Mesh:
    """One triangulated product: vertices in metres (Z up), faces as 0-based triangles."""

    storey: str
    ifc_type: str
    name: str
    verts: list[tuple[float, float, float]]
    faces: list[tuple[int, int, int]]


@dataclass
class IfcInfo:
    """What the conversion found out about the file's placement."""

    georeferenced: bool = False
    origin: tuple[float, float, float] | None = None  # E, N, H of the OBJ's (0,0,0) in map coordinates
    crs: str | None = None
    unit_scale: float = 1.0  # project length unit → metres (already applied to the OBJ)
    latitude: float | None = None
    longitude: float | None = None
    elevation: float | None = None
    storeys: int = 0
    products: int = 0
    triangles: int = 0
    notes: list[str] = field(default_factory=list)


def _obj_safe(text: str) -> str:
    cleaned = "".join(c if (c.isalnum() or c in "._-") else "_" for c in text.strip())
    return cleaned or "unnamed"


def mtl_name_for(ifc_type: str) -> str:
    return _obj_safe(ifc_type)


def write_mtl(dst: Path, ifc_types: Iterable[str]) -> int:
    """Write one material per product type; returns how many were written."""
    lines: list[str] = ["# QuestMarkerApp IFC export"]
    count = 0
    for ifc_type in OrderedDict.fromkeys(ifc_types):
        r, g, b = TYPE_COLOURS.get(ifc_type, DEFAULT_COLOUR)
        lines.append(f"newmtl {mtl_name_for(ifc_type)}")
        lines.append(f"Kd {r:.3f} {g:.3f} {b:.3f}")
        lines.append(f"Ka {r * 0.2:.3f} {g * 0.2:.3f} {b * 0.2:.3f}")
        lines.append("Ks 0.050 0.050 0.050")
        lines.append("d 1.0")
        count += 1
    dst.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return count


def write_obj(dst: Path, meshes: Iterable[Mesh], mtl_file: str | None = None) -> tuple[int, int]:
    """
    Write an OBJ with one `g` per storey (meshes grouped, first-seen order), a `usemtl`
    per product type and `o` per product. Returns (vertex_count, triangle_count).
    """
    by_storey: "OrderedDict[str, list[Mesh]]" = OrderedDict()
    for m in meshes:
        by_storey.setdefault(m.storey or "Unassigned", []).append(m)

    vertex_base = 0
    n_tri = 0
    with dst.open("w", encoding="utf-8", newline="\n") as out:
        out.write("# QuestMarkerApp IFC export (metres, Z up)\n")
        if mtl_file:
            out.write(f"mtllib {mtl_file}\n")
        for storey, group in by_storey.items():
            out.write(f"g {_obj_safe(storey)}\n")
            current_mtl = None
            for m in group:
                if not m.faces or not m.verts:
                    continue
                mtl = mtl_name_for(m.ifc_type)
                if mtl != current_mtl:
                    out.write(f"usemtl {mtl}\n")
                    current_mtl = mtl
                out.write(f"o {_obj_safe(m.ifc_type + '_' + m.name)}\n")
                for x, y, z in m.verts:
                    out.write(f"v {x:.6f} {y:.6f} {z:.6f}\n")
                for a, b, c in m.faces:
                    out.write(f"f {a + 1 + vertex_base} {b + 1 + vertex_base} {c + 1 + vertex_base}\n")
                    n_tri += 1
                vertex_base += len(m.verts)
    return vertex_base, n_tri


def _ifc_length_scale(ifc_file) -> float:
    """Metres per project length unit, from IfcProject.UnitsInContext (SI prefix or conversion unit)."""
    try:
        from ifcopenshell.util.unit import calculate_unit_scale  # type: ignore

        return float(calculate_unit_scale(ifc_file))
    except Exception:
        pass
    prefixes = {"EXA": 1e18, "PETA": 1e15, "TERA": 1e12, "GIGA": 1e9, "MEGA": 1e6, "KILO": 1e3, "HECTO": 1e2,
                "DECA": 1e1, "DECI": 1e-1, "CENTI": 1e-2, "MILLI": 1e-3, "MICRO": 1e-6, "NANO": 1e-9}
    for project in ifc_file.by_type("IfcProject"):
        units = getattr(project, "UnitsInContext", None)
        if not units:
            continue
        for unit in units.Units or []:
            if getattr(unit, "UnitType", None) != "LENGTHUNIT":
                continue
            if unit.is_a("IfcSIUnit"):
                return prefixes.get(unit.Prefix or "", 1.0)
            if unit.is_a("IfcConversionBasedUnit"):
                measure = unit.ConversionFactor
                factor = float(measure.ValueComponent.wrappedValue)
                base = measure.UnitComponent
                base_scale = prefixes.get(getattr(base, "Prefix", None) or "", 1.0) if base.is_a("IfcSIUnit") else 1.0
                return factor * base_scale
    return 1.0


def _ifc_map_conversion(ifc_file, info: IfcInfo):
    """
    Returns (a, b, E0, N0, H0) for E = a*x - b*y + E0, N = b*x + a*y + N0, H = z + H0
    with x, y, z already in metres, or None when the file has no IfcMapConversion (IFC4+).
    """
    try:
        conversions = ifc_file.by_type("IfcMapConversion")
    except Exception:
        conversions = []
    if not conversions:
        return None
    mc = conversions[0]
    east = float(mc.Eastings or 0.0)
    north = float(mc.Northings or 0.0)
    height = float(mc.OrthogonalHeight or 0.0)
    xa = float(mc.XAxisAbscissa) if mc.XAxisAbscissa is not None else 1.0
    xo = float(mc.XAxisOrdinate) if mc.XAxisOrdinate is not None else 0.0
    scale = float(mc.Scale) if mc.Scale is not None else 1.0
    norm = math.hypot(xa, xo) or 1.0
    # Map units: the target CRS may be in a different unit than the project (rare but legal).
    map_scale = 1.0
    target = getattr(mc, "TargetCRS", None)
    if target is not None:
        info.crs = getattr(target, "Name", None) or None
        map_unit = getattr(target, "MapUnit", None)
        if map_unit is not None and map_unit.is_a("IfcSIUnit"):
            prefixes = {"KILO": 1e3, "CENTI": 1e-2, "MILLI": 1e-3}
            map_scale = prefixes.get(map_unit.Prefix or "", 1.0)
    # Eastings/Northings are in map units; our geometry is in metres → express the offset in metres.
    return (scale * xa / norm, scale * xo / norm, east * map_scale, north * map_scale, height * map_scale)


def _dms_to_degrees(value) -> float | None:
    """IfcCompoundPlaneAngleMeasure (deg, min, sec[, millionth-sec]) → decimal degrees."""
    if value is None:
        return None
    try:
        parts = [float(v) for v in value]
    except TypeError:
        return None
    if not parts:
        return None
    sign = -1.0 if parts[0] < 0 or (parts[0] == 0 and any(p < 0 for p in parts[1:])) else 1.0
    parts = [abs(p) for p in parts]
    deg = parts[0] + (parts[1] if len(parts) > 1 else 0) / 60 + (parts[2] if len(parts) > 2 else 0) / 3600
    if len(parts) > 3:
        deg += parts[3] / 3600e6
    return sign * deg


def _storey_name(ifc_file, product) -> str:
    try:
        from ifcopenshell.util.element import get_container  # type: ignore

        container = get_container(product)
        while container is not None and not container.is_a("IfcBuildingStorey"):
            container = get_container(container)
        if container is not None:
            return container.Name or f"Storey_{container.id()}"
    except Exception:
        pass
    return "Unassigned"


def _iter_shapes(ifc_file, settings, ifcopenshell_geom):
    """Yield (product, verts_flat, faces_flat) for every product that has a Representation."""
    try:
        import multiprocessing

        threads = max(1, multiprocessing.cpu_count())
        iterator = ifcopenshell_geom.iterator(settings, ifc_file, threads)
        if iterator.initialize():
            while True:
                shape = iterator.get()
                product = ifc_file.by_id(shape.id)
                yield product, shape.geometry.verts, shape.geometry.faces
                if not iterator.next():
                    break
            return
    except Exception as exc:  # fall back to per-element shapes
        print(f"  iterator unavailable ({exc}); converting element by element", file=sys.stderr)
    for product in ifc_file.by_type("IfcProduct"):
        if not getattr(product, "Representation", None):
            continue
        try:
            shape = ifcopenshell_geom.create_shape(settings, product)
        except Exception:
            continue
        yield product, shape.geometry.verts, shape.geometry.faces


def convert_ifc_to_obj(src: Path, dst: Path) -> IfcInfo:
    """
    Convert an IFC file to an OBJ (+ MTL beside it) in metres, Z up, world coordinates.
    Geometry is written relative to the IfcMapConversion origin when the file has one
    (then `info.georeferenced` is True and `info.origin` is the E,N,H of OBJ (0,0,0)).
    """
    try:
        import ifcopenshell  # type: ignore
        import ifcopenshell.geom  # type: ignore
    except ImportError:
        sys.exit(
            "IFC conversion needs the ifcopenshell package:\n"
            "    pip install ifcopenshell\n"
            "Alternative: IfcConvert from the same project, then send the OBJ:\n"
            f"    IfcConvert {src.name} {src.stem}.obj --use-world-coords\n"
            f"    python send_model.py {src.stem}.obj ..."
        )

    info = IfcInfo()
    ifc_file = ifcopenshell.open(str(src))
    info.unit_scale = _ifc_length_scale(ifc_file)

    settings = ifcopenshell.geom.settings()
    try:  # ifcopenshell ≥ 0.8
        settings.set("use-world-coords", True)
    except Exception:
        settings.set(settings.USE_WORLD_COORDS, True)

    for site in ifc_file.by_type("IfcSite"):
        info.latitude = _dms_to_degrees(getattr(site, "RefLatitude", None))
        info.longitude = _dms_to_degrees(getattr(site, "RefLongitude", None))
        elev = getattr(site, "RefElevation", None)
        info.elevation = float(elev) if elev is not None else None
        break

    mapping = _ifc_map_conversion(ifc_file, info)
    if mapping:
        a, b, e0, n0, h0 = mapping
        info.georeferenced = True
        info.origin = (e0, n0, h0)
        info.notes.append(
            f"IfcMapConversion found{(' (' + info.crs + ')') if info.crs else ''}: geometry rotated/scaled to map "
            f"axes, origin E={e0:.3f} N={n0:.3f} H={h0:.3f} sent as the model origin."
        )
    else:
        a, b = 1.0, 0.0
        if info.latitude is not None and info.longitude is not None:
            info.notes.append(
                f"No IfcMapConversion. IfcSite gives lat/lon {info.latitude:.6f}, {info.longitude:.6f}"
                f"{f', elev {info.elevation:.2f}' if info.elevation is not None else ''} but no projected "
                "coordinates: sending as NOT georeferenced (place it in the headset, or pass --origin E N H)."
            )
        else:
            info.notes.append("No IfcMapConversion and no IfcSite lat/lon: sending as NOT georeferenced "
                              "(place it in the headset, or pass --origin E N H).")

    s = info.unit_scale
    skip_types = ("IfcOpeningElement", "IfcSpace", "IfcAnnotation", "IfcGrid")
    meshes: list[Mesh] = []
    ifc_types: list[str] = []
    storeys: set[str] = set()
    for product, verts_flat, faces_flat in _iter_shapes(ifc_file, settings, ifcopenshell.geom):
        if product is None or any(product.is_a(t) for t in skip_types):
            continue
        verts: list[tuple[float, float, float]] = []
        for i in range(0, len(verts_flat), 3):
            x, y, z = verts_flat[i] * s, verts_flat[i + 1] * s, verts_flat[i + 2] * s
            verts.append((a * x - b * y, b * x + a * y, z))
        faces = [(faces_flat[i], faces_flat[i + 1], faces_flat[i + 2]) for i in range(0, len(faces_flat), 3)]
        if not faces:
            continue
        storey = _storey_name(ifc_file, product)
        storeys.add(storey)
        ifc_type = product.is_a()
        ifc_types.append(ifc_type)
        meshes.append(Mesh(storey, ifc_type, product.Name or product.GlobalId, verts, faces))

    mtl_path = dst.with_suffix(".mtl")
    write_mtl(mtl_path, ifc_types)
    n_verts, n_tri = write_obj(dst, meshes, mtl_file=mtl_path.name)
    info.storeys = len(storeys)
    info.products = len(meshes)
    info.triangles = n_tri
    if n_tri == 0:
        info.notes.append("Warning: no triangulated geometry was produced from this IFC.")
    return info


# ----------------------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------------------


def _safe_stem(name: str) -> str:
    """File-system-safe version of the display name for the package files."""
    cleaned = "".join(ch if ch.isalnum() or ch in "._- " else "_" for ch in name).strip()
    return cleaned or "model"


def main(argv: Sequence[str] | None = None) -> None:
    # The Windows console defaults to cp1252, which cannot print the Greek names in the help
    # text or in model names: switch the streams to UTF-8 where the runtime allows it.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
        except (AttributeError, ValueError):
            pass
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("model", nargs="?", type=Path, help="model.obj | model.dxf | model.ifc")
    parser.add_argument("--quest-ip", required=True)
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--api-key", default=os.environ.get("QUEST_KEY"),
                        help="The Quest's key, shown in its main menu (env QUEST_KEY)")
    parser.add_argument("--name", help="Display name on the headset (default: file name without extension)")
    parser.add_argument("--units", default=None, help="m|mm|cm|ft|in or metres per source unit (default m)")
    parser.add_argument("--up", default="z", choices=("z", "y"), help="Up axis of the file (default z)")
    parser.add_argument("--local", action="store_true",
                        help="Not georeferenced: the model is placed by hand in the headset")
    parser.add_argument("--origin", nargs=3, type=float, metavar=("E", "N", "H"),
                        help="Map coordinates (any projected CRS in metres: E N H) of the model's (0,0,0)")
    parser.add_argument("--project", type=int, help="Project id the model belongs to")
    parser.add_argument("--keep-obj", action="store_true", help="IFC: keep the converted OBJ next to the IFC")
    prep = parser.add_argument_group("heavy / textured OBJ (photogrammetry)", "Needs pymeshlab and Pillow.")
    prep.add_argument("--faces", type=int, default=None,
                      help=f"Decimate to this many triangles (default {DEFAULT_FACES:,} when the OBJ has more)")
    prep.add_argument("--texture-size", type=int, default=DEFAULT_TEXTURE,
                      help=f"Longest texture side in pixels on the headset (default {DEFAULT_TEXTURE})")
    prep.add_argument("--match", type=Path, metavar="CSV",
                      help="Matching points model_x,model_y,model_z,e,n,h[,name]: moves the model into the "
                           "sensors' grid (3+ points recommended)")
    prep.add_argument("--fit", choices=("yaw", "3d"), default="yaw",
                      help="yaw: level model, rotate about the vertical only (default); 3d: full rotation")
    prep.add_argument("--allow-scale", action="store_true", help="Also solve a scale factor from --match")
    prep.add_argument("--no-prepare", action="store_true", help="Send the OBJ as it is (no decimation / packing)")
    prep.add_argument("--keep-package", action="store_true", help="Keep the prepared zip next to the OBJ")
    parser.add_argument("--list", action="store_true", help="List the models stored on the headset")
    parser.add_argument("--delete", metavar="NAME", help="Delete a model (by display name) from the headset")
    args = parser.parse_args(argv)
    if not args.api_key:
        parser.error("--api-key is required (or set QUEST_KEY): the headset always asks for its key")

    try:
        _run(parser, args)
    except PermissionError as exc:
        sys.exit(f"Error: {exc}")
    except requests.ConnectionError as exc:
        sys.exit(f"Could not reach the headset at {args.quest_ip}:{args.port} ({exc}).\n"
                 "Check: same Wi-Fi, app open on the Quest, correct --quest-ip.")
    except requests.Timeout:
        sys.exit("The headset did not answer in time (asleep, or the upload stalled). Try again.")
    except requests.RequestException as exc:
        sys.exit(f"Request failed: {exc}")


def _run(parser: argparse.ArgumentParser, args: argparse.Namespace) -> None:
    if args.list:
        models = list_models(args.quest_ip, args.port, args.api_key)
        if not models:
            print("No models on the headset.")
        for m in models:
            size = m.get("bytes", 0) / 1e6
            geo = "georef" if m.get("georeferenced") else "local"
            origin = ""
            if "originE" in m:
                origin = f"  origin E={m['originE']} N={m['originN']} H={m.get('originH', 0)}"
            print(f"- {m.get('name')}  [{m.get('format')}, {size:.1f} MB, {geo}, units x{m.get('unitScale', 1)}, "
                  f"up {m.get('up', 'z')}]{origin}  ({m.get('file')})")
        return

    if args.delete:
        r = delete_model(args.quest_ip, args.port, args.api_key, args.delete)
        print_response(r)
        sys.exit(0 if r.ok else 1)

    if args.model is None:
        parser.error("model file is required unless --list or --delete is given")
    src: Path = args.model
    if not src.is_file():
        sys.exit(f"File not found: {src}")

    try:
        fmt = infer_format(src)
        units = parse_units(args.units)
    except ValueError as exc:
        parser.error(str(exc))
    check_key(args.quest_ip, args.port, args.api_key)  # before any conversion or upload

    name = args.name or default_name(src)
    georeferenced = not args.local
    origin: tuple[float, float, float] | None = tuple(args.origin) if args.origin else None
    up = args.up
    send_path = src
    tmp_dir: tempfile.TemporaryDirectory | None = None

    if fmt == "ifc":
        if args.keep_obj:
            send_path = src.with_suffix(".obj")
        else:
            tmp_dir = tempfile.TemporaryDirectory(prefix="send_model_")
            send_path = Path(tmp_dir.name) / (src.stem + ".obj")
        print(f"Converting {src.name} with ifcopenshell ...")
        info = convert_ifc_to_obj(src, send_path)
        print(f"  {info.products} products, {info.triangles} triangles, {info.storeys} storeys; "
              f"project unit x{info.unit_scale:g} → metres")
        for note in info.notes:
            print("  " + note)
        fmt = "obj"
        up = "z"
        if args.units is not None and units != 1.0:
            print("  Note: --units ignored for IFC; the converted OBJ is already in metres.")
        units = 1.0
        if origin is None and info.origin is not None and not args.local:
            origin = info.origin
        if not info.georeferenced and origin is None:
            georeferenced = False
        if args.local:
            georeferenced = False

    # Heavy or textured OBJ (photogrammetry): decimate, place from matching points, shrink the
    # textures and pack everything into one zip the headset unpacks.
    if fmt == "obj" and not args.no_prepare:
        # Look at the OBJ actually being sent: for an IFC that is the converted file (and its
        # MTL) in the temp folder, not the .ifc and whatever .mtl lies beside it.
        mtl_images = []
        for mtl in send_path.parent.glob("*.mtl"):
            mtl_images += mtl_textures(mtl)
        target = args.faces or DEFAULT_FACES
        needs = (args.match is not None or args.faces is not None or bool(mtl_images)
                 or send_path.stat().st_size > 60_000_000 or count_faces(send_path) > target)
        if needs:
            if tmp_dir is None:
                tmp_dir = tempfile.TemporaryDirectory(prefix="send_model_")
            matches = read_matches(args.match) if args.match else None
            print(f"Preparing {src.name} for the headset ...")
            info = prepare(send_path, Path(tmp_dir.name), name=_safe_stem(name), faces=target,
                           texture_size=args.texture_size, units=units, up=up, matches=matches,
                           fit_mode=args.fit, allow_scale=args.allow_scale)
            for note in info.notes:
                print("  Note: " + note)
            send_path = info.package
            fmt, units, up = "zip", 1.0, "z"
            if info.origin is not None:
                origin = info.origin
                georeferenced = not args.local
            elif origin is None and not args.local:
                print("  Note: no --match / --origin: the model keeps its own coordinates. The headset shows it "
                      "only if those are the sensors' grid (E N H).")
            if args.keep_package:
                kept = src.with_name(f"{_safe_stem(name)}_quest.zip")
                shutil.copyfile(info.package, kept)
                print(f"  Kept {kept}")

    if not georeferenced and origin is not None:
        print("  Note: --origin given together with --local; the origin is stored but the model is placed by hand.")

    try:
        headers = build_headers(args.api_key, name, fmt, units=units, up=up, georeferenced=georeferenced,
                                origin=origin, project=args.project, source=src.name)
    except ValueError as exc:
        parser.error(str(exc))
    try:
        r = send_model(send_path, args.quest_ip, args.port, headers)
    finally:
        if tmp_dir is not None:
            tmp_dir.cleanup()
    print_response(r)
    if r.ok:
        where = "georeferenced" if georeferenced else "placed by hand"
        print(f"Sent '{name}' ({fmt}, {where}). Open Models in the headset menu to show it.")
    else:
        sys.exit(1)


if __name__ == "__main__":
    main()
