"""Writes a minimal PDF of N pages, one text line each, for the Explorer harness's "pdf" scenario.

Usage: python New-PdfSample.py pages-3.pdf 3
Plain PDF 1.4 with an exact cross-reference table; no fonts are embedded and nothing is third-party.
"""
import sys
def pdf(n):
    objs = []
    kids = " ".join(f"{3+i} 0 R" for i in range(n))
    objs.append("<< /Type /Catalog /Pages 2 0 R >>")
    objs.append(f"<< /Type /Pages /Kids [{kids}] /Count {n} >>")
    for i in range(n):
        objs.append(f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents {3+n+i} 0 R >>")
    for i in range(n):
        s = f"BT /F1 24 Tf 72 700 Td (Page {i+1}) Tj ET"
        objs.append(f"<< /Length {len(s)} >>\nstream\n{s}\nendstream")
    out = b"%PDF-1.4\n"
    offs = []
    for i, o in enumerate(objs):
        offs.append(len(out))
        out += f"{i+1} 0 obj\n{o}\nendobj\n".encode()
    x = len(out)
    out += f"xref\n0 {len(objs)+1}\n0000000000 65535 f \n".encode()
    for o in offs:
        out += f"{o:010d} 00000 n \n".encode()
    out += f"trailer\n<< /Size {len(objs)+1} /Root 1 0 R >>\nstartxref\n{x}\n%%EOF\n".encode()
    return out
open(sys.argv[1], "wb").write(pdf(int(sys.argv[2])))
