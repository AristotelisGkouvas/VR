#!/usr/bin/env python3
"""
Φύλλο QR για τη «Δοκιμή QR» (data/protocols/δοκιμή_qr.md): δύο κωδικοί 13 cm (ό,τι χωρά σε A4 με το
απαραίτητο λευκό περιθώριο), δύο 10 cm, δύο 5 cm, ζευγάρια ώστε να μετριέται η απόσταση μεταξύ τους με μετροταινία.

Το περιεχόμενο κάθε κωδικού είναι μόνο ένα αναγνωριστικό (π.χ. QM-13A), και το μέγεθος είναι η πλευρά
του μαύρου τετραγώνου, χωρίς το λευκό περιθώριο. Τα σημάδια γύρω από τον κωδικό δείχνουν το κέντρο του.

  python qr_stickers.py [έξοδος.pdf]
  python qr_stickers.py --six έξοδος.pdf     έξι κωδικοί 13 cm, ένας ανά σελίδα (QM-13C έως QM-13H)
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


def modules(payload: str):
    code = qrcode.QRCode(error_correction=qrcode.constants.ERROR_CORRECT_M, border=0)
    code.add_data(payload)
    code.make(fit=True)
    return code.get_matrix()


def draw_code(c: canvas.Canvas, payload: str, cx: float, cy: float, side: float) -> None:
    """cx, cy: the code's centre; side: the black square's side, in points."""
    m = modules(payload)
    n = len(m)
    cell = side / n
    x0, y0 = cx - side / 2, cy - side / 2
    c.setFillGray(0)
    for row, cells in enumerate(m):
        for col, dark in enumerate(cells):
            if dark:
                c.rect(x0 + col * cell, y0 + (n - 1 - row) * cell, cell, cell, stroke=0, fill=1)
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


def main() -> None:
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
