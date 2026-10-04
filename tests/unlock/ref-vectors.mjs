#!/usr/bin/env node
/**
 * TC-tools Unlock — 独立参考实现（权威向量生成器）
 * ---------------------------------------------------------------------------
 * 目的：作为 Android 端与 Windows 端之外的**第三种实现**，计算
 *       docs/UNLOCK-PROTOCOL.md 第 8 节的权威向量，供两端对齐。
 *
 * 依赖：仅 Node.js 内置模块 `node:crypto` / `node:fs` / `node:process`。
 *       不读取、不引用任何一端的产品代码（tests/unlock/ 之外的依赖为零）。
 *
 * 独立性强化：本文件对每个关键量都做**双路计算**并断言一致——
 *   1) HMAC-SHA256：node:crypto 的 createHmac  vs  手工 ipad/opad 实现（sha256 原语）
 *   2) base64url  ：Buffer 的 'base64url' 编码   vs  标准 base64 去填充 + 字符表替换
 *   3) AES-256-GCM：node:crypto 的 aes-256-gcm   vs
 *                   手工实现（aes-256-ecb 做 CTR 密钥流 + 纯 BigInt GHASH + 手工 tag）
 *   任一路不一致都会抛错，绝不产出"看起来对"的结果。
 *
 * 用法：
 *   node ref-vectors.mjs                     # 打印人类可读的权威向量
 *   node ref-vectors.mjs --json             # 打印确定性 JSON（stdout，可直接落盘）
 *   node ref-vectors.mjs --json > vectors.json
 *   node ref-vectors.mjs --compare out.json  # 与另一端自有输出逐字节比对
 *   node ref-vectors.mjs --selftest          # 只跑独立性自检
 */

import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

// ===========================================================================
// 0. 规范常量 —— 全部来自 docs/UNLOCK-PROTOCOL.md（此处为协议文本的机械转录）
// ===========================================================================

/** §2: PSK = 0x00..0x1f */
const PSK = Buffer.from(Array.from({ length: 32 }, (_, i) => i));
/** §8.2: NONCE = 0x20..0x3f */
const NONCE = Buffer.from(Array.from({ length: 32 }, (_, i) => 0x20 + i));
/** §8.3 */
const HOST_ID = '3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01';
const PEER_ID = '8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d';
/** §2: 域分隔标签，ASCII 原始字节，不含结尾 \0 */
const LABEL_SESSION = 'TCUNLOCK-SESSION-V1';
const LABEL_PROOF = 'TCUNLOCK-PROOF-V1';
/** §8: SEAL 明文载荷 */
const UNLOCK_PLAINTEXT = '{"type":"unlock"}';
/** §2.1: counter 从 1 开始 */
const COUNTER = 1;

const assert = (cond, msg) => {
  if (!cond) throw new Error(`SELF-CHECK FAILED: ${msg}`);
};

// ===========================================================================
// 1. SHA-256 / HMAC —— 手工实现，用于交叉验证 node:crypto
// ===========================================================================

/** 手工 HMAC-SHA256（RFC 2104），只用 sha256 原语 */
function hmacSha256Manual(key, msg) {
  const B = 64;
  let k = Buffer.from(key);
  if (k.length > B) k = crypto.createHash('sha256').update(k).digest();
  if (k.length < B) k = Buffer.concat([k, Buffer.alloc(B - k.length, 0x00)]);
  const inner = Buffer.allocUnsafe(B);
  const outer = Buffer.allocUnsafe(B);
  for (let i = 0; i < B; i++) {
    inner[i] = k[i] ^ 0x36;
    outer[i] = k[i] ^ 0x5c;
  }
  const innerHash = crypto.createHash('sha256')
    .update(Buffer.concat([inner, msg]))
    .digest();
  return crypto.createHash('sha256')
    .update(Buffer.concat([outer, innerHash]))
    .digest();
}

/** 参考实现使用的 HMAC：node:crypto，并断言与手工实现逐字节一致 */
function hmacSha256(key, msg) {
  const viaNode = crypto.createHmac('sha256', key).update(msg).digest();
  const viaManual = hmacSha256Manual(key, msg);
  assert(
    viaNode.equals(viaManual),
    `HMAC 双路不一致: node=${viaNode.toString('hex')} manual=${viaManual.toString('hex')}`,
  );
  return viaNode;
}

// ===========================================================================
// 2. AES-256-GCM —— 手工实现（ECB 做 CTR + BigInt GHASH），交叉验证 node:crypto
// ===========================================================================

/** 单块 AES-256 加密（ECB，无填充） */
function aesBlockEncrypt(key, block16) {
  const c = crypto.createCipheriv('aes-256-ecb', key, null);
  c.setAutoPadding(false);
  return Buffer.concat([c.update(block16), c.final()]);
}

/** GF(2^128) 乘法，GCM 约定（MSB-first，R = 0xe1 || 0^120） */
function gfMul(x, y) {
  const R = 0xe1n << 120n;
  let z = 0n;
  let v = y;
  for (let i = 0; i < 128; i++) {
    if ((x >> BigInt(127 - i)) & 1n) z ^= v;
    if (v & 1n) v = (v >> 1n) ^ R;
    else v >>= 1n;
  }
  return z;
}

const bufToBig = (b) => (b.length === 0 ? 0n : BigInt('0x' + b.toString('hex')));
const bigToBuf = (n) => {
  const h = n.toString(16).padStart(32, '0');
  return Buffer.from(h, 'hex');
};

/** GHASH_H(data) —— 逐 16 字节块，末块零填充 */
function ghashBlocks(y, H, data) {
  for (let off = 0; off < data.length; off += 16) {
    const chunk = Buffer.alloc(16, 0x00);
    data.copy(chunk, 0, off, Math.min(off + 16, data.length));
    y = gfMul(y ^ bufToBig(chunk), H);
  }
  return y;
}

/**
 * 手工 AES-256-GCM 加密。
 * IV 为 12 字节时 J0 = IV || 0x00000001；数据计数器从 J0 的低 32 位 +1 开始。
 * 返回 { ciphertext, tag }，tag 为 16 字节，plaintext 长度不限（支持多块）。
 */
function gcmEncryptManual(key, iv, plaintext, aad = Buffer.alloc(0)) {
  assert(iv.length === 12, `手工 GCM 仅实现 12 字节 IV，收到 ${iv.length}`);
  const H = bufToBig(aesBlockEncrypt(key, Buffer.alloc(16, 0x00)));
  const J0 = Buffer.concat([iv, Buffer.from([0x00, 0x00, 0x00, 0x01])]);

  // CTR 密钥流
  const ct = Buffer.allocUnsafe(plaintext.length);
  const blocks = Math.ceil(plaintext.length / 16);
  const ctr = Buffer.from(J0);
  for (let i = 0; i < blocks; i++) {
    // 低 32 位自增（GCM inc32）
    let c = ctr.readUInt32BE(12) + 1;
    c = c >>> 0;
    ctr.writeUInt32BE(c, 12);
    const ks = aesBlockEncrypt(key, ctr);
    const start = i * 16;
    const end = Math.min(start + 16, plaintext.length);
    for (let j = start; j < end; j++) ct[j] = plaintext[j] ^ ks[j - start];
  }

  // GHASH: A || pad || C || pad || len(A)bits || len(C)bits
  let y = ghashBlocks(0n, H, aad);
  y = ghashBlocks(y, H, ct);
  const lenBlock = Buffer.alloc(16, 0x00);
  lenBlock.writeBigUInt64BE(BigInt(aad.length) * 8n, 0);
  lenBlock.writeBigUInt64BE(BigInt(ct.length) * 8n, 8);
  y = gfMul(y ^ bufToBig(lenBlock), H);
  const tag = bigToBuf(y ^ bufToBig(aesBlockEncrypt(key, J0)));
  return { ciphertext: ct, tag };
}

/** 参考实现使用的 GCM：node:crypto，并断言与手工实现逐字节一致 */
function gcmEncrypt(key, iv, plaintext, aad = Buffer.alloc(0)) {
  const c = crypto.createCipheriv('aes-256-gcm', key, iv, { authTagLength: 16 });
  if (aad.length) c.setAAD(aad);
  const ct = Buffer.concat([c.update(plaintext), c.final()]);
  const tag = c.getAuthTag();

  const manual = gcmEncryptManual(key, iv, plaintext, aad);
  assert(
    ct.equals(manual.ciphertext),
    `GCM 密文双路不一致: node=${ct.toString('hex')} manual=${manual.ciphertext.toString('hex')}`,
  );
  assert(
    tag.equals(manual.tag),
    `GCM tag 双路不一致: node=${tag.toString('hex')} manual=${manual.tag.toString('hex')}`,
  );

  // 反向自检：能解开且 tag 校验通过
  const d = crypto.createDecipheriv('aes-256-gcm', key, iv, { authTagLength: 16 });
  if (aad.length) d.setAAD(aad);
  d.setAuthTag(tag);
  const round = Buffer.concat([d.update(ct), d.final()]);
  assert(round.equals(plaintext), 'GCM 往返解密结果与原文不一致');

  return { ciphertext: ct, tag };
}

// ===========================================================================
// 3. base64url（无填充）—— 双路实现
// ===========================================================================

function base64url(byteBuf) {
  const viaNode = byteBuf.toString('base64url');
  const viaStd = byteBuf.toString('base64')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');
  assert(
    viaNode === viaStd,
    `base64url 双路不一致: node=${viaNode} std-derived=${viaStd}`,
  );
  assert(!viaNode.includes('='), 'base64url 不得含填充 =');
  return viaNode;
}

// ===========================================================================
// 4. 协议原语（第 2 节）
// ===========================================================================

/** K_session = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-SESSION-V1") */
function deriveKSession(psk, nonce) {
  return hmacSha256(psk, Buffer.concat([nonce, Buffer.from(LABEL_SESSION, 'ascii')]));
}

/** PROOF = HMAC-SHA256(PSK, NONCE || "TCUNLOCK-PROOF-V1" || HOST_ID || PEER_ID) */
function deriveProof(psk, nonce, hostId, peerId) {
  return hmacSha256(psk, Buffer.concat([
    nonce,
    Buffer.from(LABEL_PROOF, 'ascii'),
    Buffer.from(hostId, 'ascii'),
    Buffer.from(peerId, 'ascii'),
  ]));
}

/** §2.1 SEAL: IV = counter(8B BE) || 4B 0x00，输出线格式 IV || ct || tag */
function seal(key, counter, plaintextUtf8) {
  const iv = Buffer.alloc(12, 0x00);
  // counter 语义为 64 位大端：写入前 8 字节，后 4 字节保持 0x00
  iv.writeBigUInt64BE(BigInt(counter), 0);
  const { ciphertext, tag } = gcmEncrypt(key, iv, Buffer.from(plaintextUtf8, 'utf8'));
  return { iv, ciphertext, tag, frame: Buffer.concat([iv, ciphertext, tag]) };
}

// ===========================================================================
// 5. 计算权威向量
// ===========================================================================

export function computeVectors() {
  assert(PSK.length === 32 && PSK[0] === 0x00 && PSK[31] === 0x1f, 'PSK 常量错误');
  assert(NONCE.length === 32 && NONCE[0] === 0x20 && NONCE[31] === 0x3f, 'NONCE 常量错误');
  assert(Buffer.byteLength(HOST_ID, 'ascii') === 36, 'HOST_ID 必须是 36 字节 ASCII');
  assert(Buffer.byteLength(PEER_ID, 'ascii') === 36, 'PEER_ID 必须是 36 字节 ASCII');

  const kSession = deriveKSession(PSK, NONCE);
  const proof = deriveProof(PSK, NONCE, HOST_ID, PEER_ID);

  assert(kSession.length === 32, 'K_session 必须 32 字节');
  assert(proof.length === 32, 'PROOF 必须 32 字节');

  const plaintextBuf = Buffer.from(UNLOCK_PLAINTEXT, 'utf8');
  const sealed = seal(kSession, COUNTER, UNLOCK_PLAINTEXT);

  assert(
    sealed.frame.length === 12 + plaintextBuf.length + 16,
    'SEAL 长度必须为 12 + len(plaintext) + 16',
  );
  // IV 布局自检：counter=1 大端 || 4 字节 0x00
  assert(
    sealed.iv.toString('hex') === '0000000000000001' + '00000000',
    `IV 布局错误: ${sealed.iv.toString('hex')}`,
  );

  return {
    k_session_hex: kSession.toString('hex'),
    proof_hex: proof.toString('hex'),
    seal: {
      plaintext: UNLOCK_PLAINTEXT,
      plaintext_utf8_hex: plaintextBuf.toString('hex'),
      counter: COUNTER,
      iv_hex: sealed.iv.toString('hex'),
      ciphertext_hex: sealed.ciphertext.toString('hex'),
      tag_hex: sealed.tag.toString('hex'),
      ciphertext_and_tag_hex: Buffer.concat([sealed.ciphertext, sealed.tag]).toString('hex'),
      frame_hex: sealed.frame.toString('hex'),
      frame_len: sealed.frame.length,
    },
    psk_base64url: base64url(PSK),
  };
}

function buildVectorsJson() {
  const v = computeVectors();
  // 确定性输出：不含时间戳 / 版本号，重复运行必须字节一致
  return {
    _comment: 'TC-tools Unlock protocol v1.0 §8 authoritative loopback vectors. Generated by tests/unlock/ref-vectors.mjs — independent Node.js implementation (node:crypto only). Do not hand-edit; regenerate with: node tests/unlock/ref-vectors.mjs --json',
    protocol_version: 1,
    inputs: {
      psk_hex: PSK.toString('hex'),
      psk_len: PSK.length,
      nonce_hex: NONCE.toString('hex'),
      nonce_len: NONCE.length,
      host_id: HOST_ID,
      peer_id: PEER_ID,
      label_session_ascii: LABEL_SESSION,
      label_proof_ascii: LABEL_PROOF,
    },
    k_session_hex: v.k_session_hex,
    proof_hex: v.proof_hex,
    seal: v.seal,
    psk_base64url: v.psk_base64url,
    length_identities: {
      k_session_bytes: 32,
      proof_bytes: 32,
      proof_frame_bytes: 32,
      seal_frame_bytes: 12 + Buffer.byteLength(UNLOCK_PLAINTEXT, 'utf8') + 16,
    },
  };
}

// ===========================================================================
// 6. --compare：与另一端自测输出逐字节比对
// ===========================================================================

const KEY_ALIASES = {
  k_session: ['k_session_hex', 'ksession_hex', 'k_session', 'ksession', 'session_key_hex',
    'sessionkeyhex', 'ksessionhex', 'sessionkey'],
  proof: ['proof_hex', 'proof', 'proofhex'],
  seal_frame: ['seal_hex', 'sealframehex', 'seal_frame_hex', 'sealed_hex', 'frame_hex',
    'seal', 'sealedframehex'],
  sealtag: ['sealtaghex', 'tag_hex', 'tag', 'authtaghex', 'authtag', 'gcmtaghex'],
  sealct: ['sealcthex', 'ciphertext_hex', 'ct_hex', 'ciphertext', 'ct'],
  base64url: ['psk_base64url', 'pskbase64url', 'base64url', 'psk_b64', 'pskb64',
    'psk_base64', 'pskbase64', 'psk'],
};

const normalizeKey = (s) => s.toLowerCase().replace(/[^a-z0-9]/g, '');

/** 把任意字符串规整为纯小写十六进制（容忍空格/冒号/0x/连字符/换行） */
function normalizeHexish(s) {
  let t = String(s).trim().replace(/^0x/i, '');
  t = t.replace(/[\s:_-]/g, '').toLowerCase();
  return /^[0-9a-f]*$/.test(t) && t.length % 2 === 0 ? t : null;
}

/** 递归收集所有 "key -> string value"（含数组下标路径） */
function collectLeaves(node, out = [], pathStr = '$') {
  if (node === null || node === undefined) return out;
  if (typeof node === 'string' || typeof node === 'number') {
    out.push({ path: pathStr, key: normalizeKey(pathStr.split('.').pop().replace(/\[\d+\]$/, '')), value: String(node) });
    return out;
  }
  if (Array.isArray(node)) {
    node.forEach((it, i) => collectLeaves(it, out, `${pathStr}[${i}]`));
    return out;
  }
  if (typeof node === 'object') {
    for (const [k, v] of Object.entries(node)) collectLeaves(v, out, `${pathStr}.${k}`);
  }
  return out;
}

function pick(leaves, aliases) {
  for (const alias of aliases) {
    const na = normalizeKey(alias);
    const hit = leaves.find((l) => l.key === na);
    if (hit) return hit;
  }
  return null;
}

function firstDiffByte(a, b) {
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) {
    if (a[i] !== b[i]) return i;
  }
  return a.length === b.length ? -1 : n;
}

function compareWith(candidatePath) {
  const expected = buildVectorsJson();
  const raw = fs.readFileSync(candidatePath, 'utf8');
  const doc = JSON.parse(raw);
  const leaves = collectLeaves(doc);

  const checks = [
    { name: 'K_session', aliases: KEY_ALIASES.k_session, expectedHex: expected.k_session_hex },
    { name: 'PROOF', aliases: KEY_ALIASES.proof, expectedHex: expected.proof_hex },
    { name: 'SEAL frame (IV||ct||tag)', aliases: KEY_ALIASES.seal_frame, expectedHex: expected.seal.frame_hex },
    { name: 'SEAL tag', aliases: KEY_ALIASES.sealtag, expectedHex: expected.seal.tag_hex },
    { name: 'SEAL ciphertext', aliases: KEY_ALIASES.sealct, expectedHex: expected.seal.ciphertext_hex },
  ];

  const results = [];
  for (const c of checks) {
    const hit = pick(leaves, c.aliases);
    if (!hit) {
      results.push({ field: c.name, status: 'MISSING', detail: `未在 ${candidatePath} 中找到（别名: ${c.aliases.join(', ')}）` });
      continue;
    }
    const gotHex = normalizeHexish(hit.value);
    if (gotHex === null) {
      results.push({ field: c.name, status: 'UNPARSEABLE', where: hit.path, got: hit.value });
      continue;
    }
    if (gotHex === c.expectedHex) {
      results.push({ field: c.name, status: 'MATCH', where: hit.path, bytes: gotHex.length / 2 });
    } else {
      const idx = firstDiffByte(Buffer.from(gotHex, 'hex'), Buffer.from(c.expectedHex, 'hex'));
      const lenMismatch = gotHex.length !== c.expectedHex.length;
      results.push({
        field: c.name,
        status: lenMismatch ? 'LENGTH-MISMATCH' : `MISMATCH@byte${idx}`,
        where: hit.path,
        expected: c.expectedHex,
        got: gotHex,
        expected_len: c.expectedHex.length / 2,
        got_len: gotHex.length / 2,
        first_diff_byte: idx,
        got_byte: lenMismatch && idx >= gotHex.length / 2 ? null : gotHex.slice(idx * 2, idx * 2 + 2),
        expected_byte: lenMismatch && idx >= c.expectedHex.length / 2 ? null : c.expectedHex.slice(idx * 2, idx * 2 + 2),
      });
    }
  }

  // base64url：大小写敏感，单独比对字符串
  const b64hit = pick(leaves, KEY_ALIASES.base64url);
  if (!b64hit) {
    results.push({ field: 'base64url(PSK)', status: 'MISSING', detail: '未找到 base64url 字段' });
  } else if (b64hit.value.trim() === expected.psk_base64url) {
    results.push({ field: 'base64url(PSK)', status: 'MATCH', where: b64hit.path });
  } else {
    results.push({
      field: 'base64url(PSK)',
      status: 'MISMATCH',
      where: b64hit.path,
      expected: expected.psk_base64url,
      got: b64hit.value.trim(),
      got_has_padding: b64hit.value.includes('='),
      got_uses_std_charset: /[+/]/.test(b64hit.value),
    });
  }

  const failed = results.filter((r) => r.status !== 'MATCH');
  return { candidate: path.resolve(candidatePath), allMatch: failed.length === 0, results };
}

// ===========================================================================
// 7. CLI
// ===========================================================================

function main() {
  const argv = process.argv.slice(2);
  const has = (f) => argv.includes(f);
  const valueOf = (f) => {
    const i = argv.indexOf(f);
    return i >= 0 ? argv[i + 1] : null;
  };

  if (has('--selftest')) {
    computeVectors();
    console.log('SELFTEST OK — 双路实现（HMAC / base64url / AES-256-GCM）逐字节一致');
    return 0;
  }

  if (has('--json')) {
    const out = valueOf('--out');
    const text = JSON.stringify(buildVectorsJson(), null, 2) + '\n';
    if (out) {
      fs.writeFileSync(out, text, { encoding: 'utf8' });
      // 落盘后再读回校验，确保磁盘内容与内存一致（无 BOM、无编码改写）
      const back = fs.readFileSync(out, 'utf8');
      assert(back === text, `写入 ${out} 后读回不一致`);
      console.log(`WROTE ${path.resolve(out)} (${Buffer.byteLength(text, 'utf8')} bytes, utf8 no BOM, verified by read-back)`);
    } else {
      process.stdout.write(text);
    }
    return 0;
  }

  const emit = valueOf('--emit-hmac-inputs');
  if (emit) {
    // 供 OpenSSL / Python 等第三方实现交叉验证用的**原始 HMAC 输入字节流**
    fs.mkdirSync(emit, { recursive: true });
    const ksInput = Buffer.concat([NONCE, Buffer.from(LABEL_SESSION, 'ascii')]);
    const proofInput = Buffer.concat([
      NONCE,
      Buffer.from(LABEL_PROOF, 'ascii'),
      Buffer.from(HOST_ID, 'ascii'),
      Buffer.from(PEER_ID, 'ascii'),
    ]);
    const files = {
      'psk.hex': PSK.toString('hex') + '\n',
      'nonce.hex': NONCE.toString('hex') + '\n',
      'ksession-input.bin': ksInput,
      'proof-input.bin': proofInput,
      'seal-plaintext.bin': Buffer.from(UNLOCK_PLAINTEXT, 'utf8'),
    };
    for (const [name, data] of Object.entries(files)) {
      fs.writeFileSync(path.join(emit, name), data);
      console.log(`WROTE ${path.join(path.resolve(emit), name)} (${Buffer.isBuffer(data) ? data.length : Buffer.byteLength(data)} bytes)`);
    }
    return 0;
  }

  const cmp = valueOf('--compare');
  if (cmp) {
    const r = compareWith(cmp);
    console.log(JSON.stringify(r, null, 2));
    return r.allMatch ? 0 : 1;
  }

  const v = computeVectors();
  console.log('=== TC-tools Unlock §8 权威向量（独立 Node.js 参考实现）===');
  console.log(`PSK            = ${PSK.toString('hex')}  (32B)`);
  console.log(`NONCE          = ${NONCE.toString('hex')}  (32B)`);
  console.log(`HOST_ID        = ${HOST_ID}`);
  console.log(`PEER_ID        = ${PEER_ID}`);
  console.log(`K_session      = ${v.k_session_hex}`);
  console.log(`PROOF          = ${v.proof_hex}`);
  console.log(`psk_base64url  = ${v.psk_base64url}`);
  console.log(`SEAL plaintext = ${v.seal.plaintext} (counter=${v.seal.counter})`);
  console.log(`  IV (12B)     = ${v.seal.iv_hex}`);
  console.log(`  ciphertext   = ${v.seal.ciphertext_hex}`);
  console.log(`  tag (16B)    = ${v.seal.tag_hex}`);
  console.log(`  frame ct||tag= ${v.seal.ciphertext_and_tag_hex}`);
  console.log(`  FRAME(${v.seal.frame_len}B)   = ${v.seal.frame_hex}`);
  return 0;
}

const isMain = process.argv[1]
  && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url));
if (isMain) {
  try {
    process.exitCode = main();
  } catch (err) {
    console.error('ERROR: ' + err.message);
    process.exitCode = 2;
  }
}
