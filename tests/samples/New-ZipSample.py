"""Writes the ZIP the Explorer harness's "zip" scenario extracts: two folders, three small text files, fixed dates.

Usage: python New-ZipSample.py photos-3.zip
Deterministic (fixed timestamps, stored order); nothing is third-party.
"""
import sys, zipfile

def main(path):
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for name, text in [("2026/", None), ("2026/a.txt", "one\n"), ("2026/b.txt", "two\n"), ("notes.txt", "three\n")]:
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 25, 12, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o40755 << 16) | 0x10 if text is None else (0o100644 << 16)
            z.writestr(info, b"" if text is None else text.encode("utf-8"))

if __name__ == "__main__":
    main(sys.argv[1])
