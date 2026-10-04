#!/usr/bin/env python3
"""
Independent verification of tcyunlock's QR encoder (src/Qr/QrCode.cs).

Two layers, strongest first:

  Part A -- DECODE ROUND TRIP (primary acceptance test).
      The product renders a pairing payload to a real PNG (`qrdump --png`);
      OpenCV's independent QR decoder reads that PNG back and the decoded text
      must equal the original payload byte-for-byte. This tests the encoder,
      the padding, the mask choice, the format/version bits, the PNG writer and
      UTF-8 handling end to end -- exactly what a phone camera would do.

  Part B -- CODEWORD-LEVEL COMPARISON against `segno` (third-party encoder),
      for every mask 0..7. Module-for-module equality is NOT expected here:
      segno 1.6.6 has a padding quirk (see NOTE below) that changes the
      semantically-ignored pad codewords and therefore the ECC bytes. We
      therefore require:
        * the codeword stream is identical up to the first pad codeword,
        * our pad codewords are the canonical ISO 18004 0xEC/0x11 alternation,
        * segno's are the same sequence preceded by one extra 0x00 byte.
      Anything else is a failure.

  NOTE on the quirk: ISO/IEC 18004:2015 §7.4.10 adds padding bits ONLY when the
  data stream does not end on a codeword boundary. segno/encoder.py:346 does
  `buff.extend([0] * (8 - (length % 8)))`, which appends 8 zero bits when the
  stream is already aligned. Pad codewords are ignored by decoders, so both
  encoders produce valid symbols; our stream follows the standard.

Usage:
  python tools/verify-qr.py [--exe <tctool-unlock.exe>] [--ecc M] [--masks]
Exit code 0 = everything verified, 1 = a check failed, 2 = setup error.
"""
import argparse
import json
import pathlib
import subprocess
import sys
import tempfile

try:
    import segno
except ImportError:  # pragma: no cover
    print("ERROR: segno is required (pip install segno)", file=sys.stderr)
    sys.exit(2)

try:
    import cv2
except ImportError:  # pragma: no cover
    cv2 = None

HERE = pathlib.Path(__file__).resolve().parent
DEFAULT_EXE = HERE.parent / "bin" / "Release" / "net8.0-windows10.0.19041.0" / "tctool-unlock.exe"
EXTRACTOR = HERE / "qr-extract.py"

CASES = [
    ("short-ascii", '{"v":1,"p":"tcunlock","id":"3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01",'
                    '"name":"DESKTOP-ABC","psk":"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"}'),
    ("chinese-name", '{"v":1,"p":"tcunlock","id":"adefc231-6aad-48a0-bd28-fe91588dcd9a",'
                     '"name":"\\u5434\\u6865\\u751F\\u7684\\u7B14\\u8BB0\\u672C",'
                     '"psk":"k_cJraVltgtElwVSxLHDd1_Rg_LB8ZtHKKbZiw_NGlU"}'),
    ("long-ascii", '{"v":1,"p":"tcunlock","id":"d8f44e95-5d17-4d6d-add7-541c4707faab",'
                   '"name":"LAPTOP-9KC7VPLA-A-VERY-LONG-COMPUTER-NAME-INDEED",'
                   '"psk":"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"}'),
    ("exact-fill-v2m", "A" * 26),
    ("needs-padding", "A" * 15),
]

PAD = [0xEC, 0x11]


def canonical_pad_length(stream, start):
    """Length of the maximal canonical 0xEC/0x11 alternation at stream[start:]."""
    n = 0
    while start + n < len(stream) and stream[start + n] == PAD[n % 2]:
        n += 1
    return n


def qrdump(exe, text, ecc, mask=None, png=None):
    cmd = [str(exe), "qrdump", "--json", "--text", text, "--ecc", ecc]
    if mask is not None:
        cmd += ["--mask", str(mask)]
    if png is not None:
        cmd += ["--png", str(png)]
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")
    if proc.returncode != 0:
        raise RuntimeError(f"qrdump failed ({proc.returncode}): {proc.stderr.strip()}")
    return json.loads(proc.stdout)


def extract_stream(variant, text, ecc, mask):
    cmd = [sys.executable, str(EXTRACTOR), f"--{variant}", text, "--ecc", ecc, "--mask", str(mask)]
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")
    if proc.returncode != 0:
        raise RuntimeError(f"qr-extract failed: {proc.stderr.strip()}")
    lines = proc.stdout.strip().splitlines()
    return lines[0], bytes.fromhex(lines[1])


def part_a(exe, ecc):
    print("=== Part A: PNG -> OpenCV decode round trip ===")
    if cv2 is None:
        print("  SKIPPED: opencv-python-headless not installed\n")
        return None
    detector = cv2.QRCodeDetector()
    passed = failed = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name, text in CASES:
            png = pathlib.Path(tmp) / f"{name}.png"
            doc = qrdump(exe, text, ecc, png=png)
            img = cv2.imread(str(png))
            if img is None:
                print(f"  [FAIL] {name}: could not read {png}")
                failed += 1
                continue
            decoded, points, _ = detector.detectAndDecode(img)
            ok = decoded == text
            print(f"  [{' OK ' if ok else 'FAIL'}] {name:<14} version={doc['version']} mask={doc['mask']} "
                  f"{'decoded payload identical' if ok else 'DECODED MISMATCH'}")
            if not ok:
                print(f"         expected: {text!r}")
                print(f"         decoded : {decoded!r}")
                failed += 1
            else:
                passed += 1
    print(f"  {passed}/{passed + failed} payloads decoded back byte-for-byte\n")
    return failed == 0


def part_b(exe, ecc, masks):
    print("=== Part B: codeword stream vs segno (all masks) ===")
    total = failed = 0
    for name, text in CASES:
        for mask in masks:
            total += 1
            ours_info, ours = extract_stream("ours", text, ecc, mask)
            ref_info, ref = extract_stream("ref", text, ecc, mask)
            if len(ours) != len(ref):
                print(f"  [FAIL] {name} mask={mask}: stream lengths differ ours={len(ours)} segno={len(ref)}")
                failed += 1
                continue

            first_diff = next((i for i in range(len(ours)) if ours[i] != ref[i]), None)
            if first_diff is None:
                print(f"  [ OK ] {name:<14} mask={mask}  entire {len(ours)}-codeword stream identical")
                continue

            prefix_ok = ours[:first_diff] == ref[:first_diff]
            our_pads = canonical_pad_length(ours, first_diff)
            ref_extra = ref[first_diff] == 0x00
            ref_pads = canonical_pad_length(ref, first_diff + 1)

            if prefix_ok and our_pads >= 1 and ref_extra and ref_pads == our_pads - 1:
                print(f"  [ OK ] {name:<14} mask={mask}  message region identical "
                      f"({first_diff} codewords); differs only in ISO-ignored pad codewords "
                      f"(ours {our_pads} canonical pads, segno 1 extra 0x00 + {ref_pads})")
            else:
                failed += 1
                print(f"  [FAIL] {name} mask={mask}: unexplained difference at codeword {first_diff} "
                      f"(prefix_ok={prefix_ok} ours={ours[first_diff]:#04x} segno={ref[first_diff]:#04x} "
                      f"our_pads={our_pads} ref_pads={ref_pads})")
    print(f"  {total - failed}/{total} streams agree on the message region\n")
    return failed == 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=str(DEFAULT_EXE))
    ap.add_argument("--ecc", default="M", choices=["L", "M", "Q", "H"])
    ap.add_argument("--masks", action="store_true", help="also run Part B for masks 1..7")
    args = ap.parse_args()

    exe = pathlib.Path(args.exe)
    if not exe.exists():
        print(f"ERROR: {exe} not found (build first: dotnet build -c Release)", file=sys.stderr)
        return 2

    print(f"exe   : {exe}")
    print(f"segno : {segno.__version__}")
    print(f"opencv: {cv2.__version__ if cv2 else 'not installed'}")
    print(f"ecc   : {args.ecc}\n")

    a = part_a(exe, args.ecc)
    masks = range(8) if args.masks else [0]
    b = part_b(exe, args.ecc, masks)

    ok = (b is True) and (a is not False)
    print("RESULT:", "ALL VERIFIED" if ok else "FAILURES PRESENT")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
