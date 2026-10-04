#!/usr/bin/env python3
"""Throwaway diagnostic: WHERE does our QR matrix differ from segno's?"""
import json
import pathlib
import subprocess
import sys

import segno

EXE = pathlib.Path(__file__).resolve().parents[1] / "bin" / "Release" / \
    "net8.0-windows10.0.19041.0" / "tctool-unlock.exe"


def ours(text, ecc="M", mask=0):
    p = subprocess.run([str(EXE), "qrdump", "--json", "--text", text, "--ecc", ecc, "--mask", str(mask)],
                       capture_output=True, text=True, encoding="utf-8")
    if p.returncode != 0:
        raise RuntimeError(p.stderr)
    return json.loads(p.stdout)


def theirs(text, ecc="M", mask=0):
    qr = segno.make(text, error=ecc.lower(), mask=mask, boost_error=False, mode="byte", encoding="utf-8")
    return qr.version, ["".join("1" if v else "0" for v in row) for row in qr.matrix]


def compare(text, label, ecc="M", mask=0, show_map=True):
    got = ours(text, ecc, mask)
    version, ref = theirs(text, ecc, mask)
    mine = got["rows"]
    if got["version"] != version:
        print(f"{label}: VERSION differs ours={got['version']} segno={version}")
        return False
    size = got["size"]
    diffs = [(r, c) for r in range(size) for c in range(size) if mine[r][c] != ref[r][c]]
    same = len(diffs) == 0
    print(f"{label}: version={version} size={size} bytes={len(text)} differing modules={len(diffs)}"
          f"{'  MATCH' if same else ''}")
    if not same and show_map:
        marks = set(diffs)
        for r in range(size):
            print("   " + "".join("X" if (r, c) in marks else "." for c in range(size)))
    return same


if __name__ == "__main__":
    print("=== bisect by payload size (ECC M, mask 0) ===")
    for n in [1, 5, 10, 14, 15, 20, 30, 40, 60, 100, 150]:
        text = "A" * n
        try:
            ok = compare(text, f"len={n:>4}", show_map=False)
        except Exception as ex:  # noqa: BLE001
            print(f"len={n}: ERROR {ex}")
            ok = False
        if not ok and n <= 20:
            compare(text, f"len={n:>4} map")
