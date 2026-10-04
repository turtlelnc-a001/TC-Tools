#!/usr/bin/env python3
"""
TC-tools Unlock — 第三方实现交叉验证（Python / CPython stdlib）
---------------------------------------------------------------------------
用途：用**第四个独立实现**复核 tests/unlock/vectors.json 的权威值。
      - HMAC-SHA256 用 CPython 标准库 hashlib/hmac（与 node:crypto、OpenSSL CLI、
        本仓库手工 ipad/opad 实现相互独立）
      - AES-256-GCM 用 `cryptography` 包（若可用；它走 OpenSSL EVP 路径，
        与 node:crypto 的调用面不同）
      - base64url 用 base64.urlsafe_b64encode 去填充

用法：python crosscheck.py
退出码：0 = 全部与期望一致；1 = 有不一致；2 = 期望值缺失
"""
import base64
import hashlib
import hmac
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

# ---- 协议常量（docs/UNLOCK-PROTOCOL.md §8）---------------------------------
PSK = bytes(range(0x00, 0x20))          # 0x00..0x1f
NONCE = bytes(range(0x20, 0x40))        # 0x20..0x3f
HOST_ID = "3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01"
PEER_ID = "8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d"
LABEL_SESSION = b"TCUNLOCK-SESSION-V1"
LABEL_PROOF = b"TCUNLOCK-PROOF-V1"
PLAINTEXT = b'{"type":"unlock"}'
COUNTER = 1

failures = []


def check(name, got, expected):
    ok = got == expected
    print(f"[{'ok ' if ok else 'FAIL'}] {name}")
    print(f"        got      = {got}")
    if not ok:
        print(f"        expected = {expected}")
        failures.append(name)
    return ok


def main():
    print("=== Python 交叉验证（独立于 node:crypto / OpenSSL CLI / 手工实现）===")
    print(f"python     : {sys.version.split()[0]}")
    print(f"openssl    : {__import__('ssl').OPENSSL_VERSION}")
    print()

    # ---- HMAC --------------------------------------------------------------
    k_session = hmac.new(PSK, NONCE + LABEL_SESSION, hashlib.sha256).digest()
    proof = hmac.new(
        PSK, NONCE + LABEL_PROOF + HOST_ID.encode("ascii") + PEER_ID.encode("ascii"),
        hashlib.sha256,
    ).digest()

    print(f"K_session  = {k_session.hex()}")
    print(f"PROOF      = {proof.hex()}")
    print(f"lengths    : K_session={len(k_session)}B PROOF={len(proof)}B")
    print()

    # ---- base64url ---------------------------------------------------------
    psk_b64url = base64.urlsafe_b64encode(PSK).decode("ascii").rstrip("=")
    print(f"psk_b64url = {psk_b64url}   (len={len(psk_b64url)})")
    print()

    # ---- AES-256-GCM -------------------------------------------------------
    iv = COUNTER.to_bytes(8, "big") + b"\x00" * 4
    print(f"IV (12B)   = {iv.hex()}")
    gcm_available = False
    try:
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
        ct_and_tag = AESGCM(k_session).encrypt(iv, PLAINTEXT, None)
        ct, tag = ct_and_tag[:-16], ct_and_tag[-16:]
        gcm_available = True
        print(f"ciphertext = {ct.hex()}")
        print(f"tag        = {tag.hex()}")
        print(f"frame      = {(iv + ct_and_tag).hex()}   ({len(iv + ct_and_tag)}B)")
    except ImportError as exc:
        ct = tag = None
        print(f"[skip] AES-256-GCM: `cryptography` 不可用 ({exc})")
    print()

    # ---- 与 vectors.json 比对 ----------------------------------------------
    vpath = os.path.join(HERE, "vectors.json")
    if not os.path.exists(vpath):
        print(f"[FAIL] 找不到 {vpath}，无法比对")
        return 2
    with open(vpath, "r", encoding="utf-8") as fh:
        vec = json.load(fh)

    print("--- 与 tests/unlock/vectors.json 逐字段比对 ---")
    check("k_session", k_session.hex(), vec["k_session_hex"])
    check("proof", proof.hex(), vec["proof_hex"])
    check("psk_base64url", psk_b64url, vec["psk_base64url"])
    check("iv", iv.hex(), vec["seal"]["iv_hex"])
    if gcm_available:
        check("seal.ciphertext", ct.hex(), vec["seal"]["ciphertext_hex"])
        check("seal.tag", tag.hex(), vec["seal"]["tag_hex"])
    else:
        print("[skip] seal.ciphertext / seal.tag（无 cryptography 包）")

    print()
    if failures:
        print(f"=== PYTHON CROSSCHECK: {len(failures)} MISMATCH(ES): {failures} ===")
        return 1
    print("=== PYTHON CROSSCHECK: ALL MATCH ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
