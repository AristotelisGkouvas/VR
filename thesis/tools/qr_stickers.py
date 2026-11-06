#!/usr/bin/env python3
"""
Φύλλο QR για τη «Δοκιμή QR» (data/protocols/δοκιμή_qr.md): δύο κωδικοί 13 cm (ό,τι χωρά σε A4 με το
απαραίτητο λευκό περιθώριο), δύο 10 cm, δύο 5 cm, ζευγάρια ώστε να μετριέται η απόσταση μεταξύ τους με μετροταινία.

Το περιεχόμενο κάθε κωδικού είναι μόνο ένα αναγνωριστικό (π.χ. QM-13A), και το μέγεθος είναι η πλευρά
του μαύρου τετραγώνου, χωρίς το λευκό περιθώριο. Τα σημάδια γύρω από τον κωδικό δείχνουν το κέντρο του.

  python qr_stickers.py [έξοδος.pdf]
  python qr_stickers.py --six έξοδος.pdf     έξι κωδικοί 13 cm, ένας ανά σελίδα (QM-13C έως QM-13H)
  python qr_stickers.py --target έξοδος.pdf  το ίδιο με στόχο γεωδαιτικού σταθμού στο κέντρο (QM-13J έως QM-13P)
  python qr_stickers.py --small έξοδος.pdf   δύο μικρότεροι με στόχο σε ένα A4 (QM-10R, QM-07S)
  python qr_stickers.py --set έξοδος.pdf     τρεις 13 cm, δύο 10 cm, δύο 7 cm, δύο 5 cm, όλοι με στόχο
Απαιτεί: qrcode, reportlab
"""
from __future__ import annotations

import os
import sys

import qrcode
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import cm
from reportlab.pdfgen import canvas

DEFAULT_OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "data", "protocols", "qr_δοκιμή_φύλλο.pdf")


def modules(payload: str, level=qrcode.constants.ERROR_CORRECT_M):
    code = qrcode.QRCode(error_correction=level, border=0)
    code.add_data(payload)
    code.make(fit=True)
    return code.get_matrix()


def survey_target(c: canvas.Canvas, cx: float, cy: float, side: float) -> None:
    """A checkered target for the total station on the code's centre, in a white ring, in step with the code."""
    scale = side / (13 * cm)
    outer, inner = 0.85 * cm * scale, 0.65 * cm * scale
    c.setFillGray(1)
    c.circle(cx, cy, outer, stroke=0, fill=1)
    c.setFillGray(0)
    c.wedge(cx - inner, cy - inner, cx + inner, cy + inner, 0, 90, stroke=0, fill=1)
    c.wedge(cx - inner, cy - inner, cx + inner, cy + inner, 180, 90, stroke=0, fill=1)
    c.setLineWidth(0.5)
    c.setStrokeGray(0)
    c.circle(cx, cy, inner, stroke=1, fill=0)


def draw_code(c: canvas.Canvas, payload: str, cx: float, cy: float, side: float, target: bool = False) -> None:
    """cx, cy: the code's centre; side: the black square's side, in points."""
    # With a target on it the code leans on the strongest error correction (up to 30 % of it).
    m = modules(payload, qrcode.constants.ERROR_CORRECT_H) if target else modules(payload)
    n = len(m)
    cell = side / n
    x0, y0 = cx - side / 2, cy - side / 2
    c.setFillGray(0)
    for row, cells in enumerate(m):
        for col, dark in enumerate(cells):
            if dark:
                c.rect(x0 + col * cell, y0 + (n - 1 - row) * cell, cell, cell, stroke=0, fill=1)
    if target:
        survey_target(c, cx, cy, side)
    # Centre ticks outside the quiet zone (4 modules): the tape goes from centre to centre.
    q = 4 * cell
    tick = 0.6 * cm
    c.setLineWidth(0.6)
    half = side / 2 + q
    c.line(cx, cy + half, cx, cy + half + tick)
    c.line(cx, cy - half, cx, cy - half - tick)
    c.line(cx - half, cy, cx - half - tick, cy)
    c.line(cx + half, cy, cx + half + tick, cy)
    c.setFont("Helvetica-Bold", 11)
    c.drawCentredString(cx, cy - half - tick - 0.45 * cm, f"{payload}   ({side / cm:.0f} cm)")


def scale_bar(c: canvas.Canvas) -> None:
    """Check with a ruler: a printer set to 'fit to page' shrinks everything."""
    x, y = 1.5 * cm, 1.3 * cm
    c.setLineWidth(1.5)
    c.line(x, y, x + 10 * cm, y)
    c.line(x, y - 0.15 * cm, x, y + 0.15 * cm)
    c.line(x + 10 * cm, y - 0.15 * cm, x + 10 * cm, y + 0.15 * cm)
    c.setFont("Helvetica", 9)
    c.drawString(x, y + 0.25 * cm, "This bar must measure exactly 10 cm. Print at 100 % (actual size), no fit to page.")


def six_of_a_size(out: str) -> None:
    """Six codes of the largest size, one per A4: QM-13C to QM-13H (A and B are on the first sheet)."""
    w, h = A4
    c = canvas.Canvas(out, pagesize=A4)
    c.setTitle("QR test sheet, six codes")
    for side in "CDEFGH":
        draw_code(c, f"QM-13{side}", w / 2, h / 2 + 1 * cm, 13 * cm)
        scale_bar(c)
        c.showPage()
    c.save()
    sys.stdout.reconfigure(encoding="utf-8")
    print(out)


def six_with_target(out: str) -> None:
    """Six codes with a survey target on the centre, one per A4: QM-13J to QM-13P."""
    w, h = A4
    c = canvas.Canvas(out, pagesize=A4)
    c.setTitle("QR test sheet, six codes with a survey target")
    for side in "JKLMNP":
        draw_code(c, f"QM-13{side}", w / 2, h / 2 + 1 * cm, 13 * cm, target=True)
        scale_bar(c)
        c.showPage()
    c.save()
    sys.stdout.reconfigure(encoding="utf-8")
    print(out)


def small_with_target(out: str) -> None:
    """Two smaller codes with the target, on one A4: QM-10R and QM-07S."""
    w, h = A4
    c = canvas.Canvas(out, pagesize=A4)
    c.setTitle("QR test sheet, smaller codes with a survey target")
    draw_code(c, "QM-10R", w / 2, 20.5 * cm, 10 * cm, target=True)
    draw_code(c, "QM-07S", w / 2, 8.2 * cm, 7 * cm, target=True)
    scale_bar(c)
    c.showPage()
    c.save()
    sys.stdout.reconfigure(encoding="utf-8")
    print(out)


def field_set(out: str) -> None:
    """The set for the survey test, all with the target: three 13 cm, two 10 cm, two 7 cm, two 5 cm, on six A4."""
    w, h = A4
    c = canvas.Canvas(out, pagesize=A4)
    c.setTitle("QR test set with survey targets")
    for side in "TUV":
        draw_code(c, f"QM-13{side}", w / 2, h / 2 + 1 * cm, 13 * cm, target=True)
        scale_bar(c)
        c.showPage()
    for big, small in (("W", "Y"), ("X", "Z")):
        draw_code(c, f"QM-10{big}", w / 2, 20.5 * cm, 10 * cm, target=True)
        draw_code(c, f"QM-07{small}", w / 2, 8.2 * cm, 7 * cm, target=True)
        scale_bar(c)
        c.showPage()
    draw_code(c, "QM-05Q", w / 2, 20.5 * cm, 5 * cm, target=True)
    draw_code(c, "QM-05I", w / 2, 9 * cm, 5 * cm, target=True)
    scale_bar(c)
    c.showPage()
    c.save()
    sys.stdout.reconfigure(encoding="utf-8")
    print(out)


def main() -> None:
    if len(sys.argv) > 2 and sys.argv[1] == "--set":
        os.makedirs(os.path.dirname(os.path.abspath(sys.argv[2])), exist_ok=True)
        field_set(sys.argv[2])
        return
    if len(sys.argv) > 2 and sys.argv[1] == "--small":
        os.makedirs(os.path.dirname(os.path.abspath(sys.argv[2])), exist_ok=True)
        small_with_target(sys.argv[2])
        return
    if len(sys.argv) > 2 and sys.argv[1] == "--target":
        os.makedirs(os.path.dirname(os.path.abspath(sys.argv[2])), exist_ok=True)
        six_with_target(sys.argv[2])
        return
    if len(sys.argv) > 2 and sys.argv[1] == "--six":
        os.makedirs(os.path.dirname(os.path.abspath(sys.argv[2])), exist_ok=True)
        six_of_a_size(sys.argv[2])
        return
    out = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUT
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    w, h = A4
    c = canvas.Canvas(out, pagesize=A4)
    c.setTitle("QR test sheet")
    for payload in ("QM-13A", "QM-13B"):
        draw_code(c, payload, w / 2, h / 2 + 1 * cm, 13 * cm)
        scale_bar(c)
        c.showPage()
    for side in ("A", "B"):
        draw_code(c, f"QM-10{side}", w / 2, 20.5 * cm, 10 * cm)
        draw_code(c, f"QM-05{side}", w / 2, 7.5 * cm, 5 * cm)
        scale_bar(c)
        c.showPage()
    c.save()
    sys.stdout.reconfigure(encoding="utf-8")
    print(out)


if __name__ == "__main__":
    main()
