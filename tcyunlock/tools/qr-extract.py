#!/usr/bin/env python3
"""
Independent QR codeword extractor (verification tool).

Reverses ISO/IEC 18004 module placement + masking on a QR matrix and returns
the raw codeword stream (data codewords followed by the interleaved ECC
codewords). Used to compare tcyunlock's encoder against segno's at the
codeword level, which pinpoints differences that a module-level diff only
localises.

Usage:
  python tools/qr-extract.py --ours <payload> [--ecc M] [--mask 0]
  python tools/qr-extract.py --ref  <payload> [--ecc M] [--mask 0]
"""
import argparse
import json
import pathlib
import subprocess
import sys

import segno

_HERE = pathlib.Path(__file__).resolve().parent
# Prefer the self-contained deliverable (runs without a registered .NET runtime).
EXE = _HERE.parent / "dist" / "tctool-unlock.exe"
if not EXE.exists():
    EXE = _HERE.parent / "bin" / "Release" / "net8.0-windows10.0.19041.0" / "tctool-unlock.exe"


def build_function_map(size: int, version: int):
    fn = [[False] * size for _ in range(size)]

    def mark(col, row):
        if 0 <= col < size and 0 <= row < size:
            fn[row][col] = True

    for i in range(size):
        mark(6, i)
        mark(i, 6)

    for (x, y) in ((3, 3), (size - 4, 3), (3, size - 4)):
        for dy in range(-4, 5):
            for dx in range(-4, 5):
                mark(x + dx, y + dy)

    if version > 1:
        num_align = version // 7 + 2
        step = 26 if version == 32 else (version * 4 + num_align * 2 + 1) // (num_align * 2 - 2) * 2
        pos = [6]
        p = size - 7
        tail = []
        while len(pos) + len(tail) < num_align:
            tail.append(p)
            p -= step
        pos += list(reversed(tail))
        for i, cy in enumerate(pos):
            for j, cx in enumerate(pos):
                if (i, j) in ((0, 0), (0, len(pos) - 1), (len(pos) - 1, 0)):
                    continue
                for dy in range(-2, 3):
                    for dx in range(-2, 3):
                        mark(cx + dx, cy + dy)

    if version >= 7:
        for i in range(18):
            a = size - 11 + i % 3
            b = i // 3
            mark(a, b)
            mark(b, a)

    for i in range(6):
        mark(8, i)
    mark(8, 7)
    mark(8, 8)
    mark(7, 8)
    for i in range(9, 15):
        mark(14 - i, 8)
    for i in range(8):
        mark(size - 1 - i, 8)
    for i in range(8, 15):
        mark(8, size - 15 + i)
    mark(8, size - 8)
    return fn


def mask_bit(mask: int, row: int, col: int) -> bool:
    return [
        lambda r, c: (r + c) % 2 == 0,
        lambda r, c: r % 2 == 0,
        lambda r, c: c % 3 == 0,
        lambda r, c: (r + c) % 3 == 0,
        lambda r, c: (r // 2 + c // 3) % 2 == 0,
        lambda r, c: (r * c) % 2 + (r * c) % 3 == 0,
        lambda r, c: ((r * c) % 2 + (r * c) % 3) % 2 == 0,
        lambda r, c: ((r + c) % 2 + (r * c) % 3) % 2 == 0,
    ][mask](row, col)


def extract(rows, mask, version, size):
    fn = build_function_map(size, version)
    bits = []
    right = size - 1
    while right >= 1:
        if right == 6:
            right = 5
        for vert in range(size):
            for j in range(2):
                x = right - j
                upward = ((right + 1) & 2) == 0
                y = size - 1 - vert if upward else vert
                if fn[y][x]:
                    continue
                bit = rows[y][x] == "1"
                if mask_bit(mask, y, x):
                    bit = not bit
                bits.append(1 if bit else 0)
        right -= 2
    out = bytearray()
    for i in range(0, len(bits) - 7, 8):
        v = 0
        for b in bits[i:i + 8]:
            v = (v << 1) | b
        out.append(v)
    return bytes(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ours", metavar="PAYLOAD")
    ap.add_argument("--ref", metavar="PAYLOAD")
    ap.add_argument("--ecc", default="M")
    ap.add_argument("--mask", type=int, default=0)
    args = ap.parse_args()

    if bool(args.ours) == bool(args.ref):
        print("give exactly one of --ours / --ref", file=sys.stderr)
        return 2

    text = args.ours or args.ref
    if args.ours is not None:
        p = subprocess.run([str(EXE), "qrdump", "--json", "--text", text,
                            "--ecc", args.ecc, "--mask", str(args.mask)],
                           capture_output=True, text=True, encoding="utf-8")
        if p.returncode != 0:
            print(p.stderr, file=sys.stderr)
            return 2
        doc = json.loads(p.stdout)
        rows, version, size = doc["rows"], doc["version"], doc["size"]
        label = "ours"
    else:
        qr = segno.make(text, error=args.ecc.lower(), mask=args.mask, boost_error=False,
                        mode="byte", encoding="utf-8", micro=False)
        rows = ["".join("1" if v else "0" for v in row) for row in qr.matrix]
        version, size = qr.version, len(rows)
        label = "segno"

    code = extract(rows, args.mask, version, size)
    print(f"{label}: version={version} size={size} ecc={args.ecc} mask={args.mask} "
          f"codewords={len(code)}")
    print(code.hex())
    return 0


if __name__ == "__main__":
    sys.exit(main())
