# Writes the committed 7z samples (ADR-0030) with py7zr 1.1.4 (LGPL-2.1, liblzma and PyCryptodome inside), an encoder
# independent of SharpCompress, the reader under test. A developer-machine tool only: nothing in the build or CI runs it.
# Run with a Python that has py7zr==1.1.4 (for example in a virtual environment):  python -I New-SevenZipSamples.py <out-folder>
import os
import shutil
import sys
import tempfile

import multivolumefile
import py7zr

out = sys.argv[1]
work = tempfile.mkdtemp(prefix="wintools-7z-")
FIXED = 1767225600  # 2026-01-01 00:00:00 UTC, so the listing is the same on every run


def tree(root):
    os.makedirs(os.path.join(root, "sub"))
    files = {
        "a.txt": b"hello 7z world\n",
        "sub/big.bin": b"WinTools sample line " * 5000,
        "tiny.txt": b"x",
        "\u0645\u0644\u0627\u062d\u0638\u0627\u062a.txt": "\u0645\u0644\u0627\u062d\u0638\u0629".encode("utf-8"),
    }
    for name, data in files.items():
        path = os.path.join(root, *name.split("/"))
        with open(path, "wb") as f:
            f.write(data)
        os.utime(path, (FIXED, FIXED))
    return list(files)


def make(name, password=None, header_encryption=False):
    path = os.path.join(out, name)
    if os.path.exists(path):
        os.remove(path)
    with py7zr.SevenZipFile(path, "w", password=password, header_encryption=header_encryption) as z:
        for rel in names:
            z.write(os.path.join(src, *rel.split("/")), rel)
    print(name)


try:
    src = os.path.join(work, "src")
    names = tree(src)
    make("7z-solid.7z")  # py7zr writes one solid block
    make("7z-locked.7z", password="right")  # AES-256, names visible
    make("7z-locked-names.7z", password="right", header_encryption=True)  # names encrypted too
    make("7z-locked-arabic.7z", password="\u0633\u0631\u0651\u00e91")  # "سرّé1"

    # A link entry: a Unix mode of lrwxrwxrwx in the high 16 bits, as p7zip and 7-Zip on Linux write it.
    link = os.path.join(out, "7z-link.7z")
    with py7zr.SevenZipFile(link, "w") as z:
        z.writestr(b"fine", "ok.txt")
        z.writestr(b"C:/Windows", "link")
        z.header.files_info.files[-1]["attributes"] = 0x8000 | 0x20 | (0xA1FF << 16)
    print("7z-link.7z")

    # 300 MB of zeros: an honest 7z whose declared size is far beyond its own (the whole-archive bomb rule).
    zeros = os.path.join(out, "7z-zeros.7z")
    with py7zr.SevenZipFile(zeros, "w") as z:
        z.writestr(bytes(300 << 20), "zeros.bin")
    print("7z-zeros.7z")

    data = os.path.join(work, "random.bin")
    with open(data, "wb") as f:
        f.write(bytes((i * 7919 + 13) % 251 for i in range(3000)))

    # Stored, not compressed: a changed byte still decodes, so only the CRC-32 can catch it.
    with py7zr.SevenZipFile(os.path.join(out, "7z-stored.7z"), "w", filters=[{"id": py7zr.FILTER_COPY}]) as z:
        z.write(data, "random.bin")
    print("7z-stored.7z")

    # The first file of an archive split into 2 KB parts (".7z.001"): only the first is kept.
    with multivolumefile.open(os.path.join(work, "split.7z"), mode="wb", volume=2048) as vol:
        with py7zr.SevenZipFile(vol, "w", filters=[{"id": py7zr.FILTER_COPY}]) as z:
            z.write(data, "random.bin")
    shutil.copyfile(os.path.join(work, "split.7z.0001"), os.path.join(out, "7z-split.7z.001"))
    print("7z-split.7z.001")
finally:
    shutil.rmtree(work)
