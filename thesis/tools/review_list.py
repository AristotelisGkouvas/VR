#!/usr/bin/env python3
"""
Φτιάχνει το thesis/ΕΛΕΓΧΟΣ.md: όλα τα πλαίσια \\review{...} της αναφοράς, με αριθμό,
κεφάλαιο, ενότητα και σελίδα του PDF.

Τρέχει μετά από compile (χρειάζεται το report/build/main.aux για τις σελίδες):
    python thesis/tools/review_list.py
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

THESIS = Path(__file__).resolve().parent.parent
REPORT = THESIS / "report"
OUT = THESIS / "ΕΛΕΓΧΟΣ.md"


def input_order() -> list[Path]:
    """Τα .tex με τη σειρά που τα διαβάζει το main.tex."""
    main = (REPORT / "main.tex").read_text(encoding="utf-8")
    files = []
    for m in re.finditer(r"^\s*\\input\{([^}]+)\}", main, re.M):
        name = m.group(1)
        if name == "setup":
            continue
        p = REPORT / (name if name.endswith(".tex") else name + ".tex")
        if p.exists():
            files.append(p)
    return files


def balanced(text: str, start: int) -> tuple[str, int]:
    """Το περιεχόμενο της αγκύλης που ανοίγει στο text[start] == '{'."""
    depth, i = 0, start
    while i < len(text):
        c = text[i]
        if c == "\\":
            i += 2
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return text[start + 1:i], i + 1
        i += 1
    raise ValueError("μη ισορροπημένες αγκύλες")


def plain(tex: str) -> str:
    """Πρόχειρη μετατροπή LaTeX → απλό κείμενο για τη λίστα."""
    s = re.sub(r"%.*", "", tex)
    s = re.sub(r"\\(textbf|emph|code|en|textenglish)\{([^{}]*)\}", r"\2", s)
    s = re.sub(r"\\(cref|Cref)\{([^{}]*)\}", r"[\2]", s)
    s = re.sub(r"\\cite\{([^{}]*)\}", r"[\1]", s)
    s = re.sub(r"\\SI\{([^{}]*)\}\{([^{}]*)\}", r"\1 \2", s)
    s = re.sub(r"\\fillin\{([^{}]*)\}", r"[\1]", s)
    s = s.replace("\\_", "_").replace("~", " ").replace("\\%", "%").replace("--", "–")
    s = re.sub(r"\\[a-zA-Z]+\*?", "", s).replace("{", "").replace("}", "").replace("$", "")
    return re.sub(r"\s+", " ", s).strip()


def pages() -> dict[int, str]:
    aux = REPORT / "build" / "main.aux"
    if not aux.exists():
        return {}
    found = {}
    for m in re.finditer(r"\\newlabel\{rev:(\d+)\}\{\{[^}]*\}\{([^}]*)\}", aux.read_text(encoding="utf-8", errors="replace")):
        found[int(m.group(1))] = m.group(2)
    return found


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    page_of = pages()
    items = []
    for f in input_order():
        text = f.read_text(encoding="utf-8")
        chapter, section = "", ""
        heads = [(m.start(), m.group(1), m.group(3)) for m in
                 re.finditer(r"\\(chapter|section|subsection)(\*?)\{([^}]*)\}", text)]
        for m in re.finditer(r"\\review(\[!\])?\{", text):
            body, _ = balanced(text, m.end() - 1)
            for pos, kind, title in heads:
                if pos > m.start():
                    break
                if kind == "chapter":
                    chapter, section = title, ""
                elif kind == "section":
                    section = title
            items.append((f.relative_to(REPORT).as_posix(), chapter, section, plain(body), bool(m.group(1))))

    lines = [
        "# Σημεία για έλεγχο και διόρθωση",
        "",
        "Παράγεται αυτόματα από το `thesis/tools/review_list.py` (μετά από compile). Κάθε γραμμή",
        "είναι ένα πλαίσιο «ΕΛΕΓΞΕ #N» στο PDF (κόκκινο = σημαντικό, μωβ = υπόλοιπα). Όταν το διορθώσεις, σβήσε το `\\review{...}`",
        "από το .tex και ξανατρέξε το script.",
        "",
        f"Σύνολο: **{len(items)}**, από τα οποία **{sum(1 for i in items if i[4])}** σημαντικά (κόκκινα στο PDF).",
        "",
    ]
    numbered = list(enumerate(items, 1))
    for title, want in (("## Σημαντικά: ξεκίνα από αυτά", True), ("## Υπόλοιπα", False)):
        lines += [title, "", "| # | Σελ. | Κεφάλαιο / ενότητα | Τι να ελέγξεις | Αρχείο |", "|---|---|---|---|---|"]
        for n, (file, chapter, section, body, high) in numbered:
            if high != want:
                continue
            where = chapter + (f" › {section}" if section else "")
            lines.append(f"| {n} | {page_of.get(n, '?')} | {where} | {body.replace('|', '/')} | `{file}` |")
        lines.append("")
    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"{len(items)} σημεία → {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
