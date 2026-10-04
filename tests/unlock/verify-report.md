# TC-tools Unlock — 独立验证报告（verify / task-4）

- 版本：协议 v1.0（`docs/UNLOCK-PROTOCOL.md`，冻结）
- 验证人：teammate `verify`（独立于两端实现者）
- 日期：2026-10-04
- 结论：**协议字节级、跨端互通、两端可编译可运行、交付物自包含、APK 打包、构建清单绑定 = 全部实测通过；真机 BLE 空口 / 真实生物识别 / 真实锁屏注入 / Win10 1709 = 未验证（环境固有限制，非缺陷）。**
- **交付物：v0.2.0-rc2 冻结版 ｜ 终局复核已完成 → 以 §12 为准**（含全部参与哈希 + 三段式验收口径 + 构建清单核验）

> **校验和警告**：本报告所验证的代码版本见 §9「被验证代码的 SHA256」。产品代码在本报告生成期间仍在被两端作者修改；
> **任何在此哈希之后发生的改动都会使对应结论失效**，必须重跑 §7 的命令。重跑全套命令约 60 秒。

---

## 1. 验证方法与独立性声明

本报告的证据全部来自**我亲自执行的命令**，不接受"看起来对"。三条独立性保障：

1. **权威向量来自第四方实现**：`tests/unlock/ref-vectors.mjs` 只用 Node 内置 `crypto`，且对每个关键量做**双路实现交叉验证**——
   HMAC 用 `createHmac` 与手工 ipad/opad 两路；base64url 用 `Buffer('base64url')` 与标准 base64 去填充两路；
   AES-256-GCM 用 `createCipheriv('aes-256-gcm')` 与**手工 CTR（aes-256-ecb 密钥流）+ 纯 BigInt GHASH + 手工 tag** 两路。任一路不一致立即抛错，绝不产出结果。
2. **不读、不引用任何一端产品代码**：`ref-vectors.mjs` 与两端零依赖。
3. **两端产品代码由我在隔离宿主中直接编译运行**，不是复制改写、也不是读它们自己打印的结论：
   - Windows：`winproto-harness.csproj` 用 `<Compile Include="..\..\..\tcyunlock\src\Protocol.cs" />` **直接编译产品源文件**。
   - Android：`run-android-selftest.ps1` 用独立 kotlinc **直接编译产品源文件**（绕开 gradle/AGP，与 mobile 的构建互不干扰）。

环境：Windows 11 专业版 10.0.26200；node v24.19.0；.NET 8.0.425（`toolchain/dotnet8`）；
OpenJDK 17.0.13（`toolchain/jdk17`）；CPython 3.12.14；OpenSSL 3.5.7（Git 自带）；shell 为 **Windows PowerShell 5.1 / .NET Framework**（无 `AesGcm`，故 .NET GCM 交叉验证走 `dotnet8`）。

---

## 2. 权威向量的确定（§8）

### 2.1 五条独立实现路径的一致性

`K_session` / `PROOF` 由 **5 条互不相同的实现**计算并逐字节比对，全部一致：

| # | 实现路径 | 命令 | 结果 |
|---|---|---|---|
| 1 | node:crypto（OpenSSL 后端）+ 手工 ipad/opad | `node tests/unlock/ref-vectors.mjs --selftest` | `SELFTEST OK` |
| 2 | 纯 BigInt GHASH + aes-256-ecb CTR（我手写） | 同上（内建断言） | 与 #1 一致 |
| 3 | CPython stdlib `hmac`/`hashlib` | `python tests/unlock/crosscheck.py` | `=== PYTHON CROSSCHECK: ALL MATCH ===` |
| 4 | OpenSSL 3.5.7 CLI | `openssl mac -digest SHA256 -macopt hexkey:<psk> -in evidence/ksession-input.bin HMAC` | `21F2A1A3…AF47`（大写形式，值一致） |
| 5 | .NET 8 `HMACSHA256` + `AesGcm`（CNG） | `dotnet run --project tests/unlock/dotnet-crosscheck` | `=== .NET CROSSCHECK: ALL MATCH ===` |

原始输出：

```
# 路径 3（CPython 3.12.14）
K_session  = 21f2a1a3890968e1da28553de67b49cab6ab2ffb7f3ecbaec72d284852b9af47
PROOF      = 353d6fdd6e73a63620389b806711e6c2c726407045d4c7ddae29264a9e8bab10
psk_b64url = AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8   (len=43)
IV (12B)   = 000000000000000100000000
[ok ] k_session / proof / psk_base64url / iv
=== PYTHON CROSSCHECK: ALL MATCH ===

# 路径 5（.NET 8.0.31）
ciphertext = 678def935e80e0eeb8ca5a9ee9bbef97f6   (17B)
tag        = a2c4302b392deeb76b81f496665496c6
frame      = 000000000000000100000000678def935e80e0eeb8ca5a9ee9bbef97f6a2c4302b392deeb76b81f496665496c6   (45B)
[ok ] k_session / proof / psk_base64url / seal.iv / seal.ciphertext / seal.tag / seal.frame / seal.frame_len
=== .NET CROSSCHECK: ALL MATCH ===
```

### 2.2 冻结的权威值

写入 `tests/unlock/vectors.json`（1617 B，UTF-8 无 BOM，SHA256 `984DC2D00881A90AAA5F01B070583FA07267845E2EEB4A9041E3BECC435DCBB7`；**重跑字节一致**，已实测确定性）：

```
PSK              = 000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f
NONCE            = 202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f
HOST_ID          = 3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01   (36B ASCII)
PEER_ID          = 8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d   (36B ASCII)
K_session        = 21f2a1a3890968e1da28553de67b49cab6ab2ffb7f3ecbaec72d284852b9af47
PROOF            = 353d6fdd6e73a63620389b806711e6c2c726407045d4c7ddae29264a9e8bab10
psk_base64url    = AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8   (43 字符，无 '=')
SEAL(K_session,1,'{"type":"unlock"}')   明文 17B = 7b2274797065223a22756e6c6f636b227d
  IV (12B)       = 000000000000000100000000
  ciphertext     = 678def935e80e0eeb8ca5a9ee9bbef97f6        (17B)
  tag (16B)      = a2c4302b392deeb76b81f496665496c6
  整帧(45B)      = 000000000000000100000000678def935e80e0eeb8ca5a9ee9bbef97f6a2c4302b392deeb76b81f496665496c6
```

长度恒等式：`45 = 12(IV) + 17(明文) + 16(tag)`，与协议 §8 末行 `12 + len(plaintext) + 16` 一致。

### 2.3 回填协议文档

`docs/UNLOCK-PROTOCOL.md` 第 206–207 行由 `<PENDING-VERIFIER>` 替换为真实值，**仅改这两行**：

```
205: 4. 期望 `K_session`（十六进制）与 `PROOF`（十六进制）：**待填充**
206:    `K_session = 21f2a1a3890968e1da28553de67b49cab6ab2ffb7f3ecbaec72d284852b9af47`
207:    `PROOF     = 353d6fdd6e73a63620389b806711e6c2c726407045d4c7ddae29264a9e8bab10`
```

复核命令与结果（全文仅第 209 行仍在*说明文字*中提到 `PENDING-VERIFIER`，非期望值）：

```powershell
Select-String -Path docs\UNLOCK-PROTOCOL.md -Pattern "PENDING-VERIFIER"
# line 209: > ⚠️ 这两个期望值最初为 `PENDING-VERIFIER`。**验证负责人**用第三种独立实现（Node.js，
(Get-Content docs\UNLOCK-PROTOCOL.md).Count   # 220（与原文件总行数一致，未被截断）
```

---

## 3. 两端自测输出与权威值的逐字节核对

比对工具：`node tests/unlock/ref-vectors.mjs --compare <候选 JSON>`（报告首个不一致字节的下标）。

### 3.1 Windows 端

```powershell
toolchain\dotnet8\dotnet.exe run --project TC-tools\tests\unlock\winproto-harness
node TC-tools\tests\unlock\ref-vectors.mjs --compare TC-tools\tests\unlock\evidence\win-proto-raw.json
```

```
win compare exit=0 (0=MATCH)
  K_session                    MATCH
  PROOF                        MATCH
  SEAL frame (IV||ct||tag)     MATCH
  SEAL tag                     MATCH
  SEAL ciphertext              MATCH
  base64url(PSK)               MATCH
```

同一宿主对产品代码的附加断言（`_diagnostics` 原文）：

```
tryopen_ok=True / tryopen_counter=1 / tryopen_plaintext_matches=True
tampered_tag_rejected=True / tampered_tag_error=decrypt-failed
nonzero_iv_padding_rejected=True / short_frame_rejected=True
base64url_roundtrip=True / base64url_has_padding=False / base64url_len=43
uuid_service=7a1c9e40-2f3d-4b6c-9a11-6c5d3e8f2b01
uuid_challenge=7a1c9e41-2f3d-4b6c-9a11-6c5d3e8f2b01
uuid_command=7a1c9e42-2f3d-4b6c-9a11-6c5d3e8f2b01
fixedtime_equal=True / fixedtime_unequal=True
```

### 3.2 Android 端

```powershell
powershell -ExecutionPolicy Bypass -File TC-tools\tests\unlock\run-android-selftest.ps1
node TC-tools\tests\unlock\ref-vectors.mjs --compare TC-tools\tests\unlock\evidence\android-selftest-raw.json
```

```
SELFTEST_RESULT=PASS
SELFTEST exit code = 0 (0=全部通过)
android compare exit=0 (0=MATCH)
```

26 项断言全部 PASS（节选，原始输出见 `evidence/android-selftest-console.txt`）：

```
[PASS] K_session == 权威值
[PASS] PROOF == 权威值
[PASS] base64url(PSK) == 权威值   / 不含 '=' 填充 / 往返一致
[PASS] IV = 8 字节大端 counter + 4 字节 0x00
[PASS] SEALED 整帧 == 权威值      / 长度 12+len(pt)+16 = 45
[PASS] ciphertext == 权威值       / GCM tag == 权威值（密文||tag 顺序）
[PASS] OPEN(SEAL(...)) 往返明文一致
[PASS] counter 不匹配时 OPEN 失败（IV 参与认证）
[PASS] ct||tag 主体长度 = 1+16 = 17
[PASS] 短明文(1 字节)的 ct||tag 主体可解（MIN_BODY_LEN=17，修正 verify 发现的潜在缺陷）
[PASS] IV 填充非 0x00 被判非法   / IV 填充非 0 的帧被组装器拒绝（bad-frame）
[PASS] 密文被篡改时 OPEN 失败（GCM tag 校验）
[PASS] 分片重组：45B 帧切 3 片后完整解出（counter=1）
[PASS] 长度前缀分片格式可解出
[PASS] 重放：counter 未严格递增时被拒
[PASS] counter 严格递增的消息被接受
[PASS] 配对二维码文本可解析且往返一致
[PASS] 非法 pairing 文本被拒（PSK 长度错 / 产品标识错）
```

#### 3.2.1 Android 端**自己的 gradle 构建**产出的自测 JSON（本轮新增）

mobile 用真实 `gradle :app:selfTest`（`JavaExec` 直跑纯 JVM 类，不经过 Android 运行时）产出到
`android\app\build\selftest\vectors-android.json`。我做的正式比对：

```powershell
node tests\unlock\ref-vectors.mjs --compare TC-tools\android\app\build\selftest\vectors-android.json
```

```
android gradle compare exit=0 (0=MATCH)
  K_session MATCH / PROOF MATCH / SEAL frame MATCH(45B) / SEAL tag MATCH / SEAL ciphertext MATCH / base64url MATCH
```

即：**该端在自己的正式构建链（gradle + AGP）下产出的自测结果，与我的独立权威值逐字节一致**。
加上我另用独立 kotlinc 编译同一份源码复跑（结论相同），两条互不相同的编译/执行路径指向同一结果。

本轮 `SealedFrameAssembler.kt` 有修改（哈希 `68866789…` → `46CDA88F…`），我已**在新版本上重跑**
独立 kotlinc 自测与双向互通，均 PASS（输出见 §3.2 / §3.4 / `evidence/`）。

### 3.3 Windows 宿主自带自测（真实 `tctool-unlock` 程序）

```powershell
toolchain\dotnet8\dotnet.exe tcyunlock\bin\Debug\net8.0-windows10.0.19041.0\tctool-unlock.dll selftest
```

```
24/24 checks passed
=== selftest exit=0 ===
[PASS] §8 inputs / K_session / PROOF / base64url / SEAL 45-byte frame / 长度恒等式 / IV layout
[PASS] OPEN round trip returns the original plaintext with counter 1
[PASS] counter replay rejected: counter must be strictly greater than the last accepted one
[PASS] counter 2 accepted after counter 1
[PASS] tampered GCM tag rejected with decrypt-failed
[PASS] tampered ciphertext rejected with decrypt-failed
[PASS] non-zero IV padding rejected with bad-frame
[PASS] 28-byte frame rejected with bad-frame
[PASS] wrong key rejected with decrypt-failed
[PASS] multi-block plaintext (200 bytes, counter 7) round trips
[PASS] PROOF with a different PEER_ID does not verify
[PASS] FixedTimeEquals accepts identical buffers and rejects different ones
[PASS] §3.1 UUIDs match the frozen protocol constants
[PASS] frame constants: MinSealedLength=29, MaxNotifyPayload=180, PROOF=32
[PASS] host 'ready' notification fits the §3.2 180-byte notify cap   (61 bytes)
[PASS] §6 error codes and §3.3.2 reasons are exactly the documented set
[PASS] pairing payload encodes to QR and renders to PNG
```

### 3.4 双向互通（跨端互认）—— 两端作者都未做的测试

```powershell
powershell -ExecutionPolicy Bypass -File TC-tools\tests\unlock\run-interop.ps1
```

两端各自从 §8 的 PSK/NONCE **独立派生** K_session，不交换密钥；两个宿主刻意忽略输入 JSON 里的任何密钥字段。

```
[ok] Windows 端成功解开 Android 端的 counter=2 帧
     {"opened":true,"counter":2,"expected_counter":2,"counter_ok":true,
      "plaintext":"{\u0022type\u0022:\u0022ping\u0022}","k_session_self_derived":"21f2a1a3…af47"}
[ok] Android 端成功解开 Windows 端的 counter=3 帧
     {"opened":true,"counter":3,"expected_counter":3,"counter_ok":true,
      "plaintext":"{\"type\":\"bye\"}"}
[ok] 负例被正确拒绝（counter 不匹配不会误判为成功）
=== 双向互通测试：全部通过 ===
```

---

## 4. 协议逐条符合性

分类：**实测** = 我运行代码得到输出；**静态** = 逐行审查源码（附 `文件:行号`）；**未验证** = 本机无法执行（见 §6）。

| # | 要求（协议出处） | 结论 | 证据 |
|---|---|---|---|
| R1 | `K_session = HMAC-SHA256(PSK, NONCE \|\| "TCUNLOCK-SESSION-V1")`（§2） | ✅ 实测 | §2.1 五路一致；§3.1 `win-proto-raw.json`；§3.2 Android PASS。`tcyunlock/src/Protocol.cs:38-48`、`TcProtocol.kt:125-132` |
| R2 | `PROOF = HMAC-SHA256(PSK, NONCE \|\| "TCUNLOCK-PROOF-V1" \|\| HOST_ID \|\| PEER_ID)`，拼接顺序固定、ASCII 无 `\0` | ✅ 实测 | 同上。`Protocol.cs:55-74`（`Encoding.ASCII.GetBytes`，顺序 nonce→label→host→peer）、`TcProtocol.kt:137-150` |
| R3 | `base64url(PSK)` 无填充 | ✅ 实测 | `AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8`（43 字符）两端一致。`Protocol.cs:171-188`、`TcProtocol.kt:103-110` |
| R4 | SEAL 线格式 `IV(12) \|\| ciphertext \|\| tag(16)`（§2.1） | ✅ 实测 | 45B 整帧两端与权威值逐字节一致；`Protocol.cs:95-112`、`TcProtocol.kt:179-188` |
| R5 | `IV = counter(8B big-endian) \|\| 4B 0x00` | ✅ 实测 | `000000000000000100000000`；`Protocol.cs:100-103`、`TcProtocol.kt:155-164` |
| R6 | `OPEN` 失败即丢弃（AEAD 完整性） | ✅ 实测 | 篡改 tag/密文 → `decrypt-failed`；错误 key → `decrypt-failed`；两端 selftest 均 PASS |
| R7 | 两端可互通（同一帧可跨端解密） | ✅ 实测 | §3.4 双向互通，含负例 |
| R8 | §3.1 三个 UUID 固定 | ✅ 实测 | 三 UUID 与协议字面一致；`Protocol.cs:266-271`、`TcProtocol.kt:33-36` |
| R9 | Char A = Read + Notify | ✅ 静态 | `tcyunlock/src/Ble/GattUnlockServer.cs:90`：`Read \| Notify` |
| R10 | Char B = Write | ✅ 静态 | `GattUnlockServer.cs:113`：仅 `Write`（含长写/偏移支持）；Android 侧用 `WRITE_TYPE_DEFAULT`（带响应），`BleCentral.kt:222,228` |
| R11 | PROOF 帧 = 裸 32 字节（**§3.3.2**；原 §3.3.1 因 IDENT 插入而顺延） | ✅ 实测 | Android `[PASS] PROOF 长度 = 32 字节`；`Protocol.cs:21`；`TcProtocol.kt:246-249` |
| R12 | SEALED 帧 ≥ 29 字节（**§3.3.3**；原 §3.3.2 顺延） | ✅ 实测 | `MinSealedLength = 12+1+16 = 29`；28 字节帧 → `bad-frame`（两端 selftest） |
| R13 | Notify 载荷 ≤180 字节，超长须按 2 字节大端长度前缀分片（§3.2） | ✅ **已修复并实测** | 原为 warn-only（见 §5 F1），现已改为**硬约束**：新增 `NotifyBudget`（`GattUnlockServer.cs:754+`，`MaxPlaintext = 180-12-16 = 152` 循环夹取），`SendSealedAsync` 出口断言 `frame.Length <= 180`，超出即**拒发**并记 `[error] refusing to send…`（`:639-643`）。宿主自测新增断言并 PASS：`§3.2: an over-long error message is clamped so the sealed notify frame stays <= 180 bytes (179 bytes (plaintext 151 <= 152))` → **25/25 checks passed**。Android 侧两种线格式仍实测可解 |
| R14 | Android：`requestMtu(517)` + CCCD 订阅 + 累积重组 | ✅ 静态 | `BleCentral.kt:343`（requestMtu 517）、`:399-406`（setCharacteristicNotification + CCCD 写）、`SealedFrameAssembler.kt` 累积（上限 4096B 防御） |
| R15 | counter 严格递增，未递增即丢弃并视为攻击（§2.1） | ✅ 实测（密码学层）+ 静态（会话层） | 宿主自测 `counter replay rejected` / `counter 2 accepted`；Android selftest `重放…被拒`；互通负例 exit=1。会话层：`GattUnlockServer.cs:338-345`（`ShouldAcceptCounter` → `ErrorCodes.Replay`）、`SealedFrameAssembler.kt:98-100` |
| R16 | 连续 5 次 PROOF 失败 → 失效 PSK 并断开（**§3.3.2**） | ✅ **实测（判定函数）+ 静态（接线）** | **rc2 新增可执行断言**：`UnlockPolicy.RegisterProofFailure`（纯函数，宿主与 selftest **共用同一份代码**），宿主 selftest `[PASS] §3.3.1: only the 5th consecutive PROOF failure invalidates the PSK`（29/29）。接线处 `GattUnlockServer.cs` 的 `FailCount++/PskInvalidated=true/session.Dead=true` 仍为静态审查。**未在真实 GATT 会话上驱动过** |
| R17 | 未认证不得执行命令（**§3.3.3** / §6 `not-authenticated`） | ✅ 静态 | `GattUnlockServer.cs:330-336`：认证前的 SEALED 帧直接丢弃（无密钥可回），不足 29B 且非 32B 的帧亦丢弃 |
| R18 | 认证成功后立即刷新 challenge，防旧 proof 重放（§3.2） | ✅ 静态 | `GattUnlockServer.cs:486`：`RandomNumberGenerator.Fill(_readValue)`；且 `:480-482` 重置 `OutgoingCounter/LastIncomingCounter/LastUnlockTicks` |
| R19 | 同会话两次 `unlock` 间隔 <1500ms → `reason="throttled"`（§5.5） | ✅ **实测（判定函数）+ 静态（接线）** | **rc2 新增**：`UnlockPolicy.ShouldThrottle`（`< 1500` 才节流，**恰好 1500ms 放行**），selftest `[PASS] §5.5 throttle: sliding window of 1500 ms, boundary at exactly 1500 ms is allowed`。滑动窗口语义已由 lead 裁定可接受并在 `README §6.1` 声明 |
| R20 | 未锁屏不注入，返回 `ok=false, reason="not-locked"`；`injectWhenUnlocked` 默认 `false`（§5.4） | ✅ **实测（判定函数）+ 静态（接线）** | **rc2 新增**：`UnlockPolicy.RejectionReason(hasPassword, locked, injectWhenUnlocked)` 纯函数化并被 selftest 覆盖；默认值 `HostStore.cs:22`（`bool` 默认 `false`）；锁屏探测 `SessionDesktop.Query()` 仍为静态 |
| R21 | 不支持字符 → 整体中止、不得部分输入（§5.3） | ✅ **实测（判定函数）+ 静态（接线）** | **rc2 新增断言**：`[PASS] §5.3: an untypeable character aborts the entire plan (0 keystrokes); ASCII plans len+1 keys   (character #1 (U+00E4) has no key on the current keyboard layout)` —— 同时验证"0 次按键"与"ASCII 计划 = 长度+1 键（含回车）"。原 `InputInjector.Plan` 全量预校验逻辑不变 |
| R22 | 指纹通过前不得生成 PROOF（§1 安全要点） | ✅ 静态（单调用点） | 全仓 `computeProof` **仅一处调用**：`UnlockViewModel.kt:226`，位于 `onBiometricSuccess()`（`:216`）内；该方法仅由 `MainActivity.kt:133-168` 的 `BiometricPrompt.onAuthenticationSucceeded`（`:137`）触发。未认证分支不触碰 PROOF |
| R23 | PROOF 常量时间比较（**§3.3.2**） | ✅ 实测 + 静态 | 宿主自测 `FixedTimeEquals accepts identical buffers and rejects different ones`；`Protocol.cs:165-166`（`CryptographicOperations.FixedTimeEquals`）、`TcProtocol.kt:113-114`（`MessageDigest.isEqual`） |
| R24 | §6 错误码 / §3.3.3 reason 集合完全一致 | ✅ 实测 | 宿主自测 `§6 error codes and §3.3.2 reasons are exactly the documented set`；`Protocol.cs:243-263`（该自测文案里的 `§3.3.2` 是顺延前的编号，内容不变） |
| R25 | §4.1 配对载荷（`v/p/id/name/psk`，base64url 无填充） | ✅ 实测 | Android selftest 往返一致 + 两类非法输入被拒；`Provisioning.kt:53-89` |
| R26 | Windows 宿主可编译（§7 `net8.0-windows10.0.19041.0`）**且交付物自包含** | ✅ 实测（**rc2**） | ① `dotnet build tcyunlock` → **0 警告 0 错误**；② **rc2** `dist\tctool-unlock.exe`（self-contained，41,483,515 B，SHA256 `2BC9BB1C…`）在 **`DOTNET_ROOT` 清空、PATH 无 dotnet** 下直接运行 → `selftest` **29/29 PASS, exit 0**、`version --json` exit 0（`appVersion:"0.2.0-rc2"`, `protocol:1`）。运行前后哈希一致。**目标机无需预装 .NET 8 运行时** |
| R27 | Android 协议核心可编译运行 | ✅ 实测（双路径） | ① 独立 kotlinc 编译 6 个产品源文件 + 运行 `SelfTestMainKt` → exit 0，比对 MATCH；② mobile 的 `gradle :app:selfTest` 产物比对 6/6 MATCH（§3.2.1） |
| R28 | Android **整个 app 模块**能编译（BLE/ViewModel/Compose UI） | ✅ 实测（class 产物为证） | `android\app\build\tmp\kotlin-classes\debug\com\tctools\unlock\` 下存在 **110 个 .class**，包分布 `unlock 27 / ble 20 / data 7 / protocol 16 / ui 40` —— 含 BLE 层与 Compose UI，说明 `compileDebugKotlin`（含 Compose 编译器插件）已整体通过。且三份关键 class 的时间戳**均晚于**对应源码（`SealedFrameAssembler` src 14:39:37→class 14:40:12；`BleCentral`/`UnlockViewModel` src 14:36→class 14:38:41），产物与当前源码同步。**"Android UI/BLE 未编译"一项就此消除** |
| R29 | **§3.3.1（本轮新冻结）IDENT 帧**：36 字节 ASCII 小写 UUID，**必须在 PROOF 之前发送**；Host 必须校验其合法性（恰 36B 且形如小写 UUID），并用**本次连接收到的 IDENT** 计算期望 PROOF，未收到时仅容错回退 `""`；Android 应等 `onCharacteristicWrite` 后再写 PROOF | ✅ 实测 + 静态（两端均已实现，偏差已修） | Android：`BleCentral.kt:423-441` 先写 36B IDENT（`WRITE_TYPE_DEFAULT`）才读 challenge；`:372-373` 的 `onCharacteristicWrite` 才会 complete 被 `runOp`(`:484-488`) await 的 op，满足 §3.3.1 的实践提示；`:425-429` 校验长度 36。Windows：`GattUnlockServer.cs:33`（`IdentLength=36`）、`:317-325` 校验并登记、`:395-410` `LooksLikeUuid` 仅接受小写 hex + 固定位置连字符。候选档位曾多出一档，**已按规范收敛为两档并复测**（见 §5 F7） |
| R30 | Android **APK 打包**可构建（**rc2 终局版**） | ✅ **实测（我独立复核产品本体）** | `dist\android\TC-Tools-Unlock-0.2.0-rc2.apk`（mtime 2026-10-04 14:57:23）：size **11,475,766 B**、SHA256 `12F01A782BB424875F683EF50C48D370D14EC4DA373253B356956E6626B98359` —— **与 lead 声明逐字符一致**。zip 容器合法，**482** 条目，含 `AndroidManifest.xml`、`resources.arsc`、**9 个 classes*.dex**，native libs 覆盖 `arm64-v8a / armeabi-v7a / x86 / x86_64`（**x86_64 存在 → 模拟器可装**）。`dist\android\vectors-android.json`（1,057 B / `D3F34DA3…`）与权威向量比对 **6/6 MATCH**。旧 0.1.0 APK 已从 `dist\android\` 删除（目录现仅 rc2 APK + vectors）。<br>**版本沿革**：0.1.0 →（重建）`11,556,096 / 71C4DF5E…` →（rc2）`11,475,766 / 12F01A78…`；前两者均已作废。<br>**仍未独立复核**：aapt2 元数据（versionCode/versionName/label/sdk 与 `UnlockTileService` 清单条目）—— `aapt2` 至终局时仍未安装（SDK 只有 cmdline-tools），该部分**引用 lead 的 aapt2 输出**，我未独立验证 |

---

## 5. 发现的缺陷与观察（按严重度）

> 我只报告，不代改。F0/F4/F5 已由对应负责人在验证过程中修复并被我复测确认。

**F0（已修复并复测）— Android `open()` 对 ct‖tag 主体误用整帧最小长度。**
`TcProtocol.kt:196` 原以 `MIN_SEALED_LEN(29)` 校验不含 IV 的 `ct||tag` 主体（应为 `1+16=17`），会误拒明文 ≤12 字节的合法消息。
修复后 Android selftest 新增并 PASS：`ct||tag 主体长度 = 1+16 = 17`、`短明文(1 字节)的 ct||tag 主体可解`。当前无实际影响（协议最短消息 `{"type":"bye"}` 为 14B），但属真实潜在缺陷。

**F4（已修复并复测）— Android 未校验 IV 的 4 字节填充必须为 0x00。**
`counterOfSealedFrame` 原仅读前 8 字节；Windows 端 `Protocol.cs:125` 会校验并在非零时返回 `bad-frame`，两端行为不对称。
修复后 selftest PASS：`IV 填充非 0x00 被判非法`、`IV 填充非 0 的帧被组装器拒绝（bad-frame）`。

**F5（已修复并复测）— spike 中 Char B 多暴露了 `WriteWithoutResponse`。**
`tcyunlock/spike/Program.cs` 原为 `Write | WriteWithoutResponse`，与 §3.3 只要求 `Write` 不符，且无响应写无法完成 >MTU 的长写。
正式实现 `GattUnlockServer.cs:113` 现仅暴露 `Write` ✓。

**F1（✅ 已修复并复测）— 宿主 Notify 超过 180 字节时只告警，不强制上限也不分片。**
原状：`GattUnlockServer.cs:634-638` 在 `frame.Length > Proto.MaxNotifyPayload` 时仅 `_log("[warn] …")`，随后照常 `NotifyAsync(frame)`。
§3.2 的实现约定是"限制在 ≤180 字节；如需更长，改为分片：前 2 字节为大端总长度前缀"。**lead 裁定：不接受静默越界** → winhost 改为硬约束：
- 新增 `NotifyBudget`（`GattUnlockServer.cs:754+`）：`MaxPlaintext = 180 - 12 - 16 = 152`，对明文做**循环收敛夹取**（不靠算术猜，避免多字节 UTF-8 被切坏）；
- `SendSealedAsync` 改用 `NotifyBudget.Serialize`（`:630`），出口保留硬断言：超长则记 `[error] refusing to send a N-byte notification` 且**不发送**（`:639-643`）。

**我的复测证据**（`dotnet build tcyunlock` → 0 警告 0 错误，随后真实宿主 `selftest`）：
```
[PASS] §3.2: an over-long error message is clamped so the sealed notify frame stays <= 180 bytes   (179 bytes (plaintext 151 <= 152))
25/25 checks passed
selftest exit=0
```
即断言口径"任意 message 经 NotifyBudget 后 `12+len(json)+16 ≤ 180`，否则不发送"成立。**该项关闭。**（R13 已同步更新为 ✅）

**F2（✅ 已声明并复核）— 限流命中时会顺延计时基准。**
`GattUnlockServer.cs:553` 在被判 `throttled` 时仍执行 `session.LastUnlockTicks = now`，形成滑动窗口。
协议 §5.5 只要求"两次 unlock 间隔 <1500ms 时返回 throttled"；lead 判定：滑动窗口更保守，**不构成功能缺陷**，但必须显式声明。
**已落地并复核**：`tcyunlock/README.md` 新增 **§6.1「限流是滑动窗口（重要语义声明）」**：
```
line 230: ### 6.1 限流是**滑动窗口**（重要语义声明）
line 232: 限流命中时会**顺延计时基准**（即把"上次尝试时刻"更新为当前时刻），因此
```
**该项关闭。**（R19 已同步更新）

**F3（✅ 已实际执行并通过 —— 由"winhost 自述"升级为"verify 实测"）— 构建产物 exe 需要已注册的 .NET 运行时。**
原状：直接运行 `tcyunlock\bin\Debug\…\tctool-unlock.exe` 报
`You must install .NET to run this application. Failed to resolve hostfxr.dll [not found]. Error code: 0x80008083`。
**winhost 交付 self-contained 单文件** `tcyunlock\dist\tctool-unlock.exe`。我的复核分两步：

**(a) 只读复核**
```
size   : 41472515 bytes   (winhost 声明值 → MATCH)
sha256 : 816C91AA768A7276FBFD9E32CB057A99E83AA0F2271E199DFB04AD3E015A07F1   ← 我独立计算
PE 签名: MZ   (合法 Windows 可执行)
dist 目录: 仅 tctool-unlock.exe + selftest-vectors.json（publish 残留 .pdb 已清理）
```
并独立比对 `dist\selftest-vectors.json`：**6/6 MATCH，exit=0**（`evidence/winhost-dist-compare.json`）。

**(b) lead 放行后的实际执行**（命令与结果原文，`evidence/f3-dist-exe-selftest.txt`）：
```powershell
cd TC-tools
$env:DOTNET_ROOT = ''          # 清空
$env:DOTNET_ROOT_X86 = ''
$env:PATH = (($env:PATH -split ';') | Where-Object { $_ -notmatch 'dotnet' }) -join ';'
.\tcyunlock\dist\tctool-unlock.exe selftest
```
```
DOTNET_ROOT      = ''
DOTNET_ROOT_X86  = ''
PATH 上的 dotnet  = <无>
toolchain dotnet8 是否在 PATH = False
=== tctool-unlock selftest (protocol 1, no Bluetooth required) ===
  [PASS] K_session matches the frozen §8 vector
  [PASS] SEAL(K_session, 1, "{\"type\":\"unlock\"}") matches the frozen 45-byte frame
  [PASS] tampered GCM tag / ciphertext rejected with decrypt-failed
  [PASS] frame constants: MinSealedLength=29, MaxNotifyPayload=180, PROOF=32
  [PASS] host 'ready' notification fits the §3.2 180-byte notify cap   (61 bytes)
  [PASS] §3.2: an over-long error message is clamped so the sealed notify frame stays <= 180 bytes   (179 bytes (plaintext 151 <= 152))
25/25 checks passed
selftest exit=0
tctool-unlock 1.0.0 (unlock protocol 1)     version exit=0
```
**运行前后 exe SHA256 一致**（`816C91AA…A07F1`），确认跑的就是上表那份产物。
→ **结论：目标机无需安装 .NET 8 运行时即可运行该交付物；F3 关闭。**（R26 已同步更新）

**(c) rc2 复跑（v0.2.0-rc2，任务 task-6 重新发布后）** —— 同一条件、同一命令，仅产物更新：
```
exe    : dist\tctool-unlock.exe   41,483,515 B   SHA256 2BC9BB1C78398BD2F09F551E11FC15C9D0712373A6F2A1AF9C9D652DB64FF392
         （与 winhost 声明值逐字符 MATCH；运行前后哈希一致）
DOTNET_ROOT='' / DOTNET_ROOT_X86='' / PATH 上的 dotnet = <无>
version --json : {"tool":"tctool-unlock","appVersion":"0.2.0-rc2","protocol":1,"targetFramework":"net8.0-windows10.0.19041.0","minWindows":"10.0.16299.0","runtime":".NET 8.0.31"}   exit=0
selftest       : 29/29 checks passed   exit=0
  [PASS] §5.5 throttle: sliding window of 1500 ms, boundary at exactly 1500 ms is allowed
  [PASS] §3.3.1: only the 5th consecutive PROOF failure invalidates the PSK
  [PASS] §5.3: an untypeable character aborts the entire plan (0 keystrokes); ASCII plans len+1 keys   (character #1 (U+00E4) has no key on the current keyboard layout)
```
→ **rc2 在同等无运行时条件下依旧自包含可运行；且 25 → 29 条断言中新增的 3 条，正好把我此前"只能静态审查"的 R16/R19/R21 变成了可回归项。**
证据：`evidence/f3-rc2-selftest.txt`。

**(d) 我的建议被落地：`src/UnlockPolicy.cs`（可测试 seam）** —— 我在 §10.2-4 曾建议"把状态机与 BLE 传输层解耦出可注入 seam"，winhost 在 rc2 中实现：
`UnlockPolicy.cs` 只有 3 个纯函数 + 2 个常量，且**宿主与 selftest 共用同一份代码**（非平行实现）：
```csharp
public const int UnlockThrottleMs = 1500;          // §5.5
public const int MaxProofFailures = 5;             // §3.3.1
public static bool ShouldThrottle(long now, long last, int windowMs = UnlockThrottleMs)
    => last != 0 && now - last < windowMs;         // 恰好 1500ms 放行
public static ProofFailureOutcome RegisterProofFailure(int current, int max = MaxProofFailures)
    => current + 1 >= max ? new(0, true) : new(current + 1, false);   // 第 5 次才失效并清零
public static string? RejectionReason(bool hasPassword, bool locked, bool injectWhenUnlocked)
    => !hasPassword ? Reasons.NoPassword : (!locked && !injectWhenUnlocked ? Reasons.NotLocked : null);
```
语义与协议一致（含"恰好 1500ms 放行"这一边界）。**注意仍存在的缺口**：判定函数已被实测，但"GATT 会话是否真的调用了它们"仍是静态审查——见 R16/R19/R20/R21 的双重标注。

**F6（已解决）— 原"协议未定义的 IDENT 帧"已由 lead 登记为冻结规范 §3.3.1。**
`docs/UNLOCK-PROTOCOL.md` 新增 **§3.3.1 IDENT 帧（明文，36 字节，必须在 PROOF 之前发送）**，原 §3.3.1/§3.3.2 顺延为 **§3.3.2/§3.3.3**；协议**自此刻再次冻结，不得再扩展格式**。
我已按新规范复核两端实现（结论见 §4 **R29**）：Android 先写 IDENT、等 `onCharacteristicWrite` 再读 challenge，Windows 校验 36B 小写 UUID 并优先使用本次连接的 IDENT —— **均符合**。

**F7（✅ 已修复并复测）— 宿主 PROOF 校验多了一个冻结规范未许可的中间候选。**
原状：候选列表为**三档** `[本次连接 IDENT, 配对时存储的 PeerId, ""]`，而冻结后的 §3.3.1 只允许两档（用本次 IDENT；**未收到**时才回退 `""`）。
后果：手机的 IDENT 与期望不符时，宿主可能用**旧存储值**放行 —— "规范要求判失败的情形被接受"，会掩盖真实的 PEER_ID 不一致。安全性无损失（任何候选仍需持有 PSK），故低危。
**修复已复核**（`GattUnlockServer.cs:430-437` 源码原文）：
```csharp
// Frozen protocol §3.3.1 allows exactly two candidates: the PEER_ID from
var candidates = new List<string>(2);
if (!string.IsNullOrEmpty(session.IdentPeerId)) candidates.Add(session.IdentPeerId);
else candidates.Add(string.Empty);
```
**恰为两档，存储值已删除**；存储的 `peerId` 仅用于 §4.2 显示用途，**不参与** PROOF 校验。宿主 selftest 的 `PROOF with a different PEER_ID does not verify` 仍覆盖该路径。**该项关闭。**

**F8（✅ 由 mobile 自查修复，我复测确认）— Android 分片重组器把"长度前缀"误当 IV 的真实缺陷。**
在 F4（新增 IV 填充校验）落地后暴露的**同源边界问题**：`SealedFrameAssembler.tryDecode()` 在"长度前缀已声明 total、但数据尚未收全"的分支里，曾把**整段缓冲当裸帧**去解；一旦加上 IV 填充校验，长度前缀字节就会被当成 IV 填充 → 误判 `bad-frame` → **丢弃缓冲** → 合法分片解码失败。
- 发现方式：mobile 自己的单测 `分片重组 - 20 字节 MTU 切片` 当场抓住（**"修复引入回归、被测试网兜住"的正面范例**）。
- 修复：该情形一律 `return null` 等待后续分片；至此"前缀 vs 裸帧"的歧义**只由 AEAD tag 裁决**，语义确定。
- **我的复测**（新版本 `SealedFrameAssembler.kt` 哈希 `68866789…` → `46CDA88F…`）：
```
[PASS] 分片重组：45B 帧切 3 片后完整解出（counter=1）
[PASS] 长度前缀分片格式可解出
[PASS] IV 填充非 0 的帧被组装器拒绝（bad-frame）
SELFTEST_RESULT=PASS     独立 kotlinc 自测比对 exit=0 (0=MATCH)
```
**该项关闭**，并作为"多字节边界 + 分片歧义"类回归风险记入 §10。


---

## 6. 未验证项清单（及为何本机无法验证）

**A. 真机蓝牙链路（未验证 —— 且已有一次"预期失败"的实证）**
- 未验证内容：真实 GATT 广播/扫描/连接/配对、`requestMtu(517)` 实际协商结果、Notify 分片在真实 MTU 下的到达与重组、长写（>MTU）在本机适配器上的行为、断连重连、多客户端。
- 本机限制：**无 Android 设备接入**（无 USB 设备、无 adb）；两端从未在真实无线链路上通信过。
- **硬件备注（已更新）**：本机蓝牙适配器 `英特尔(R) 无线 Bluetooth(R)`（`USB\VID_8087&PID_0026`）**确实存在**。winhost 已用它实测到：服务侧 `GattServiceProvider.CreateAsync = Success`、`AdvertisementStatus = Started`，即**宿主侧服务/特征/广播能力成立**；
  但**同机自连测试失败**：`FromBluetoothAddressAsync(自身地址)` 返回 `null` —— 符合预期（单机不能既当 peripheral 又当 central 连自己）。
  **结论：宿主侧可发布，但对端缺失使链路无法闭环。真实手机↔电脑的读写/Notify 仍属未验证。**
- 另一条现场事实（影响复现）：起初适配器报 `Peripheral=True` 但**电台处于关闭状态**，winhost 执行 `SetStateAsync(On)` 后才跑通广播。
  **只看适配器属性会误判"蓝牙可用"**；复现本报告任何 BLE 相关步骤前须确认电台已开。（当前为开）

**B. 真实锁屏 / 安全桌面注入（完全未验证）**
- 未验证内容：`OpenInputDesktop` 对安全桌面的判定是否准确、`SendInput` 在 Winlogon 安全桌面是否被 UIPI/会话隔离拦截、Unicode 扫描码 + `Shift` 注入对真实密码框是否逐字符正确、`VK_RETURN` 是否真正提交。
- 本机限制：验证需要**真的把机器锁屏**并在安全桌面上注入按键。这样做会真实改动交互式桌面状态（可能把本会话踢到锁屏、影响正在运行的其他成员与用户操作），且失败时可能造成会话不可用。非破坏性、自动化地验证安全桌面注入在本机不成立。
- 附带限制：注入用的密码由用户配置；我没有、也不应接触真实密码。

**C. Windows 10 1709 运行时（未验证）**
- 本机是 Windows 11 10.0.26200，无法代表 1709（10.0.16299）。
- 已做：仅确认 `TargetFramework=net8.0-windows10.0.19041.0` + `TargetPlatformMinVersion=10.0.16299.0`（`tctool-unlock.csproj:9-10`）且编译期未使用更高版本 API——**编译期兼容不等于运行期兼容**。

**D. 真机指纹与生物识别流程（未验证）**
- `BiometricPrompt` 的可用性分支、错误码文案、取消/锁定行为需要真机。静态审查确认了"PROOF 仅在 `onAuthenticationSucceeded` 之后计算"这一结构性质（R22），但未在真机上跑通"指纹 → PROOF → 解锁"的完整链路。
- lead 计划用 `adb emu finger touch` 在模拟器上触发指纹：这能覆盖**回调时序与 PROOF 签发时机**，但**模拟注入不是真实生物识别硬件路径**，故本项在模拟器验证后仍应保留"真机未验证"标注。

**E. 状态机运行时行为（未验证）**
- R16（5 次失败失效 PSK）、R17、R19（1500ms 限流）、R20（未锁屏不注入）、R21（不支持字符整体中止）均**只有静态审查**。
- 原因：这些逻辑位于 `GattUnlockServer` 内，其入口 `HandleProofAsync`/`HandleUnlockAsync` 为 `private`，且依赖 `GattLocalCharacteristic` 与真实连接的 `Session`；在无手机的情况下无法构造真实会话。**要让它们可被测，需要把状态机与 BLE 传输层解耦出一个可注入的 seam**（架构建议，非本次缺陷）。
- 补充：R16/R17/R19/R20/R21 所依赖的**协议层字节行为**（重放拒绝、5 次计数阈值、错误码/原因码集合）已由两端 selftest 在本机实测覆盖；缺的只是"经由真实 GATT 会话驱动"的那一层。

**F. Android APK 与应用层（✅ 该项已消除）**
- 原结论"APK 未产出、BLE/UI/Compose 未经编译验证"**已不成立**，见 §4 R28 / R30：
  - app 模块整体编译通过（110 个 .class，含 `ble` 20 / `ui` 40，且 class 时间戳晚于源码）；
  - APK 已构建并被**独立复核**（size/SHA256/zip/dex/ABI 四项均实测）；
  - `dist\android\vectors-android.json` 与我权威值 **6/6 MATCH**；
  - mobile 另报告 `:app:testDebugUnitTest` **8 tests 0 failed**。
- **仍未验证**（转由 lead 的模拟器验证覆盖，不属我的独立实测）：APK 在设备/模拟器上的**安装与启动**、`aapt2` 元数据（package/label/minSdk/targetSdk）、运行时 UI 行为。
  > 我**未运行 adb**（lead 独占模拟器/adb 期间我遵守该约束），故 mobile 给出的 `adb install` / logcat 自测路径我**没有执行**，不能背书。

---

## 7. 复现命令（全部）

```powershell
# 前置：node v24 / toolchain\dotnet8 / toolchain\jdk17 就绪
cd TC-tools

# 1) 权威向量自检 + 生成 + 确定性校验
node tests\unlock\ref-vectors.mjs --selftest
node tests\unlock\ref-vectors.mjs --json --out tests\unlock\vectors.json

# 2) 第 3、4、5 方交叉验证
python tests\unlock\crosscheck.py
toolchain\dotnet8\dotnet.exe run --project tests\unlock\dotnet-crosscheck
& "C:\Program Files\Git\usr\bin\openssl.exe" mac -digest SHA256 -macopt hexkey:<psk.hex 内容> -in tests\unlock\evidence\ksession-input.bin HMAC

# 3) Windows 产品代码（直接编译产品源文件）
toolchain\dotnet8\dotnet.exe run --project tests\unlock\winproto-harness
node tests\unlock\ref-vectors.mjs --compare tests\unlock\evidence\win-proto-raw.json

# 4) Android 产品代码（独立 kotlinc，无需 Android SDK）
powershell -ExecutionPolicy Bypass -File tests\unlock\run-android-selftest.ps1
node tests\unlock\ref-vectors.mjs --compare tests\unlock\evidence\android-selftest-raw.json

# 5) 真实宿主自测 + 构建
toolchain\dotnet8\dotnet.exe build tcyunlock
toolchain\dotnet8\dotnet.exe tcyunlock\bin\Debug\net8.0-windows10.0.19041.0\tctool-unlock.dll selftest

# 6) 双向互通
powershell -ExecutionPolicy Bypass -File tests\unlock\run-interop.ps1
```

Android 端 kotlinc 所需的 8 个 jar（`%TEMP%\dsh-verify-tools\`，来自 `repo.maven.apache.org`）：
`kotlin-compiler-embeddable`、`kotlin-stdlib`、`kotlin-reflect`、`kotlin-script-runtime`、
`kotlin-daemon-embeddable`、`kotlinx-coroutines-core-jvm`、`annotations`、`trove4j`（版本 2.0.21 / 1.8.1 / 24.0.1 / 1.0.20200330）。

---

## 8. 证据文件索引

| 文件 | 内容 |
|---|---|
| `tests/unlock/vectors.json` | 冻结的权威向量（确定性输出） |
| `tests/unlock/ref-vectors.mjs` | 独立参考实现 + `--compare` 比对工具 |
| `tests/unlock/crosscheck.py` | CPython 独立交叉验证 |
| `tests/unlock/dotnet-crosscheck/` | .NET 8 独立交叉验证 |
| `tests/unlock/winproto-harness/` | 直接编译产品 `Protocol.cs` 的三模式宿主 |
| `tests/unlock/android-interop/InteropMain.kt` | Android 端互通宿主（验证工具，非产品代码） |
| `tests/unlock/run-android-selftest.ps1` | 独立 kotlinc 编译并运行 Android 产品代码 |
| `tests/unlock/run-interop.ps1` | 双向互通测试 |
| `tests/unlock/evidence/` | 全部原始输出（见下） |
| `tests/unlock/evidence/verified-revisions.txt` | 被验证文件的 SHA256 快照 |

原始输出：`win-proto-raw.json`、`win-proto-compare.json`、`winhost-selftest.txt`、
`android-selftest-raw.json`、`android-selftest-console.txt`、`android-compare.json`、
`interop-transcript.txt`、`interop/`（两端互发的帧 JSON）、`python-crosscheck.txt`、`openssl-*.txt`、`dotnet-crosscheck.txt`。

---

## 9. 被验证代码的 SHA256

产品代码在本报告生成期间持续被修改；以下为**结论所对应的确切版本**（完整列表见 `evidence/verified-revisions.txt`）：

```
6BB28B4899E00F7AB50786B3CCE41F7B597C63F983ECA4F1EFCAF7423620F1FE  tcyunlock/src/Protocol.cs
5950A56AEA1EB2ADDE626A9D912BE5A1920A935C698D6ABDCACA69D923D38092  tcyunlock/src/Win/InputInjector.cs
D13B1BEEC3F188E7…                                                tcyunlock/src/Ble/GattUnlockServer.cs   （F1/F7 修复后）
54168F1D6C1D171F…                                                tcyunlock/src/Program.cs                （F1 自测断言后）
02F8E1DF650FB6E2FCE0BAD04E222CC6844332BEE77FC7ED6CAA7F62D21C1504  android/.../protocol/TcProtocol.kt
46CDA88FCDC2FE4B…                                                android/.../protocol/SealedFrameAssembler.kt  （F8 修复后）
051CF6AB92D935FF…                                                android/.../ble/BleCentral.kt
067784F7269311C9…                                                android/.../UnlockViewModel.kt
```
（`…` 为省略的末 48 位；完整 64 位见 `evidence/verified-revisions.txt`。文件哈希可用 `Get-FileHash <path> -Algorithm SHA256` 复核。）

**版本漂移实录（本报告最需要注意的一点）**：本轮验证期间，产品代码与产物**至少变动了 4 次**：
`Protocol.cs` 稳定于 `6BB28B48`；`GattUnlockServer.cs` 由 `EA451B66 → DB5006FE → D13B1BEE`；`Program.cs` 由 `B5653065 → 54168F1D`；
Android 侧 `SealedFrameAssembler.kt` 由 `68866789 → 46CDA88F`，`BleCentral.kt`/`UnlockViewModel.kt` 亦变更；
**APK 更是在我核验后被重建**（`11,458,389 / C66734F8…` → `11,556,096 / 71C4DF5E…`）。
→ 因此：**§4 中标注"静态"的结论必须按上表哈希重新对齐；任何在上表哈希之后发生的改动都会使其失效。** 终局结论应以一次"冻结版本 + 同哈希全量复跑"为准（§10.2 待办 6）。

---

## 10. 本轮状态更新（lead 拍板后）与我的剩余待办

> 协议已因新增 §3.3.1 IDENT 而**再次冻结**；以下是相对 §4/§5 第一版的状态变更。

### 10.1 状态变更（截至本轮，全部复核完毕）

| 事项 | 第一版结论 | 现状态 |
|---|---|---|
| F6 IDENT 帧 | "协议未定义的扩展，需登记" | ✅ **已解决**：lead 写入冻结规范 §3.3.1（原 3.3.1/3.3.2 顺延为 3.3.2/3.3.3）；两端实现复核通过（**R29**） |
| F2 限流语义 | "待确认是否故意" | ✅ **已关闭**：lead 裁定"更严格、可接受、须声明"；`tcyunlock/README.md` **§6.1** 已写明滑动窗口语义（我已复核原文） |
| F1 Notify >180B | "建议修（低危）" | ✅ **已修复并复测**：`NotifyBudget` 夹取 + 出口硬断言；宿主 selftest **25/25 PASS**（新断言实测 179B ≤ 180）。**R13 已转 ✅** |
| F3 运行时依赖 | "建议 self-contained" | ✅ **已关闭（实测）**：`dist\tctool-unlock.exe`（`816C91AA…`）在 `DOTNET_ROOT` 清空、PATH 无 dotnet 下直接运行 `selftest` → **25/25 PASS, exit 0**（§5 F3(b)） |
| **F7 候选档位** | 本轮新发现（三档超规范） | ✅ **已修复并复测**：候选收敛为**恰两档** `[IDENT]` / `[""]`，存储值退出 PROOF 校验（源码原文已附 §5 F7） |
| **F8 分片歧义** | — | ✅ mobile 自查发现并修复（长度前缀曾被误当 IV → 丢弃缓冲）；我在新哈希上复跑自测与互通均 PASS（§5 F8） |
| R28 / R30 | "Android APK/UI 未编译" | ✅ **已消除**：app 模块 110 个 class 编译通过（含 ble/ui）+ APK 独立复核（`11,556,096 B` / `71C4DF5E…` / 8 dex / 4 ABI） |
| R11/R12/R16/R17/R23/R24 的 §编号 | 引用旧 §3.3.x | ✅ 已按顺延更新（§3.3.2 / §3.3.3） |
| 权威向量一致性 | — | ✅ 本轮又新增两条独立证据：mobile 的 `gradle :app:selfTest` 产物 **6/6 MATCH**、winhost 的 `dist\selftest-vectors.json` **6/6 MATCH**；连同此前 5 路独立实现，结论未变 |

### 10.2 我的剩余待办

1. ~~**F3 的"实际执行"**~~ → ✅ **已完成**（§5 F3(b)：25/25 PASS, exit 0）。
2. **终局复跑（唯一剩余动作）**：**等 lead 发出"冻结"信号**。注意 —— 当前 `dist\android\…-0.1.0.apk` **不是最终交付**：用户新增需求，Android 端正在出 **0.2.0-rc2**（控制中心磁贴 + realme 适配），APK 会再变。
   冻结后需在**同一版本**上复跑 §7 全套命令，把 §4 的"静态"结论升级为"实测"，并**列出所有参与哈希**（§11.3）。
3. **APK 安装/启动验证**：lead 已在模拟器上 `adb install` 成功（`versionName=0.1.0`、`versionCode=1`）。⚠️ 该结论绑定 0.1.0，rc2 出来后需重做；我本人未运行 adb，不背书 mobile 给出的 logcat 路径。
4. **可选加固建议**（非缺陷）：把 `GattUnlockServer` 的状态机与 BLE 传输层解耦出可注入 seam，使 R16/R19/R20/R21 这类规则能在无手机时用单元测试覆盖——这正是 F8 那类"修复引入回归"最容易漏掉的地方。

### 10.3 关于 emulator 端到端链路的说明（供 lead 参考）

lead 用模拟器（Android 14 / google_apis / x86_64，WHPX）验证：App 启动不崩、自测在真实 Android 运行时跑通、指纹提示可拉起。这能覆盖：App 安装/启动、指纹回调触发、PROOF 签发时序、UI 状态机。
但需注意三点边界（前两点请写入最终交付的口径）：
- **模拟器的蓝牙不是真实空口**（emulator 无真实蓝牙射频），因此**不能替代**真机蓝牙链路验证；"真实 GATT 广播/连接/MTU 协商/长写"仍属未验证项（§6 A）。winhost 的同机自连测试已实证本机**无法**闭环该链路（`FromBluetoothAddressAsync(自身) = null`）。
- `adb emu finger touch` 模拟的是**指纹事件**，不是真实生物识别硬件路径；真机指纹流程仍建议单独确认（§6 D）。
- 模拟器验证能覆盖 **R22（指纹前置 PROOF）** 的时序、以及 UI 对 `throttled`/`auth-failed`/`not-locked` 的展示；但 **R16/R19/R20/R21 的判定发生在 Windows 宿主侧**，模拟器不会让它们"被驱动"——除非宿主真的收到 `unlock` 帧。若要点亮这些分支，需要模拟器 App 真的连上宿主 GATT（即真实或虚拟空口），这与上一条的限制相同。
把这几条在最终验收口径里区分开，可避免把"模拟器跑通"误读为"端到端已验收"。

---

## 11. 终局报告口径（lead 指定，预留待冻结后落地）

> lead 要求在最终版体现以下三条口径。**本节即为最终版的骨架**；待 v0.2.0-rc2 冻结、§7 全量复跑后补入哈希与最终数字。

### 11.1 已验证（有可复现命令与原始输出）

| 类别 | 结论 | 证据锚点 |
|---|---|---|
| 协议字节级 | ✅ | §2（**5 路独立实现**：node:crypto、手写 HMAC+CTR+BigInt GHASH、CPython、OpenSSL CLI、.NET `AesGcm` 全部一致）+ §3.2/§3.2.1/§3.3（两端 selftest 与权威值逐字节一致） |
| 两端互通 | ✅ | §3.4 双向互通：Windows 解 Android 的 counter=2 帧、Android 解 Windows 的 counter=3 帧、反例被拒 |
| 两端可编译可运行 | ✅ | 宿主 `dotnet build` 0 警告 0 错误；app 模块 110 个 class（含 ble/ui）；Android 协议核心独立 kotlinc 编译运行 |
| 宿主交付物自包含 | ✅ | §5 F3(b)+(c)：`DOTNET_ROOT` 清空 + PATH 无 dotnet 下直接运行 —— 1.0.0 版 **25/25 PASS**；**rc2 版 29/29 PASS**（均 exit 0） |
| 决策规则可回归（R16/R19/R20/R21） | ✅ 判定函数实测 | rc2 新增 `src/UnlockPolicy.cs`（宿主与 selftest **共用**的纯函数）；selftest 断言覆盖 1500ms 边界、第 5 次失败才失效、不可键入字符 0 次按键。**接入实时 GATT 会话仍为静态审查** |
| 二维码图像编解码 | ✅（**verify 独立执行，两次**） | 我亲自运行 `python tcyunlock\tools\verify-qr.py --masks`：**Part A 5/5** OpenCV 5.0.0 解码逐字节还原（含中文名/v8/v9/填充边界）；**Part B 40/40** 与 segno 1.6.6 消息区码字流一致（`exact-fill` 场景 44 码字**完全相同**）。`RESULT: ALL VERIFIED`，exit=0。差异仅存在于 ISO 忽略的填充码字。**注意：校验脚本由 winhost 提供、第三方解码器 OpenCV 与参考实现 segno**；我独立执行并复核了输出（`evidence/qr-independent-verify.txt`）。<br>**第二次（脚本修复后复跑）**：winhost 把脚本默认 exe 从框架依赖的 `bin\Release\…` 改为自包含的 `dist\tctool-unlock.exe`（我报的 nit）。我在 **`DOTNET_ROOT` 清空、PATH 无 dotnet** 下**未截断整段**复跑 → 解析到 `dist\` 产物、5/5 + 40/40、`RESULT: ALL VERIFIED`、**真实退出码 exit=0**（`evidence/qr-independent-verify-v2.txt`）。同时复核其产物未变（exe 仍 `2BC9BB1C…` / 41,483,515 B；vectors 仍 4258 B），README §9.2/§9.3/§10 口径已与我的区分一致（§9.2 明写"请勿升级为运行时已验证"）。 |
| 自启动默认关闭（task-6） | ✅ 只读实测 | 我运行 `tctool-unlock autostart status --json`（**未执行 enable**）：`{"enabled":false,"scope":"user","method":"none","taskTargetsUs":false,"runKeyTargetsUs":false,"startupShortcutTargetsUs":false,"appVersion":"0.2.0-rc2","protocol":1}` —— **默认确实关闭，且三处落点均未指向本程序** |
| APK 可构建 | ✅ | §4 R30：size/SHA256/zip/8 dex/4 ABI 独立复核（**绑定 0.1.0，rc2 待重验**） |
| APK 可安装可启动 | ✅（**lead 验证**，绑定 0.1.0） | lead 在模拟器 `adb install` 成功，`versionName=0.1.0`/`versionCode=1`；rc2 需重做。**verify 未运行 adb** |
| 模拟器上的自测与 UI 流程 | 🔶（**lead 进行中**） | 结果由 lead 提供；verify 未参与、不背书 |

### 11.2 未验证（环境固有限制，非缺陷）

1. **真机 BLE 空口链路** —— 无 Android 设备接入；本机适配器存在且宿主可发布（`CreateAsync=Success`/`Advertising=Started`），但同机自连返回 `null`，链路无法闭环。**所有 GATT 空口行为均未在真实射频上验证。**
2. **真实生物识别硬件** —— `adb emu finger touch` 只模拟指纹事件；`BiometricPrompt` 的真机分支（录入/锁定/取消/错误码）未验证。
3. **真实锁屏安全桌面注入** —— 需真的锁屏并在 Winlogon 安全桌面注入；会破坏交互式会话且涉及真实密码，无法非破坏性自动化验证。
4. **Windows 10 1709 运行时** —— 本机为 Win11 10.0.26200；仅确认 TFM/`TargetPlatformMinVersion` 与编译期兼容，**编译期兼容 ≠ 运行期兼容**。
5. **决策规则到实时 GATT 会话的接线**（R16/R19/R20/R21）—— 判定函数已实测（见 §11.1），但"服务器在真实会话中确实调用它们"仍是静态审查；入口依赖真实 `Session`，无手机无法驱动。
6. **自启动的真实生效**（task-6）—— "默认关闭"已由我只读实测；但**"重启/登录后是否按时拉起"未验证**。winhost 已在 `README §13.6/§10` 如实记录本机限制：`schtasks /create` 以标准用户被系统拒绝（`Access is denied.`）、`HKCU\…\Run` 写入被间歇性拦截（安全策略/安全软件），因此 auto 模式的实测落点是**启动文件夹快捷方式**；计划任务与 Run 键两条路径**在本机无法稳定复现成功**。这两条路径的真实重启行为属未验证。
7. **aapt2 元数据**（package/label/minSdk/targetSdk）—— `aapt2` 未安装，引用 mobile 报告，未独立验证。

### 11.3 版本漂移风险（终局结论必须绑定冻结哈希）

- 本轮验证期间**至少发生 6 次代码/产物变更**（§9 实录）：`GattUnlockServer.cs` 4 版、`Program.cs` 3 版、APK 被重建、宿主 exe 由 1.0.0（`816C91AA…`）换为 **rc2（`2BC9BB1C…`）**；`Protocol.cs` 与 §8 权威向量始终未变。
- 因此：**任何"通过"结论都必须绑定到具体哈希**；哈希一变，标注"静态"与"实测"的结论都需重跑。
- **终局交付必须列出全部参与哈希**，至少包含：`Protocol.cs`、`GattUnlockServer.cs`、`Program.cs`、`UnlockPolicy.cs`、`InputInjector.cs`、`TcProtocol.kt`、`SealedFrameAssembler.kt`、`BleCentral.kt`、`UnlockViewModel.kt`、`dist\tctool-unlock.exe`、`dist\android\*.apk`、`dist\android\vectors-android.json`、`tcyunlock\dist\selftest-vectors.json`、`tests\unlock\vectors.json`。
- **rc2 当前哈希（我方复核时的版本，供终局比对）**：
```
6BB28B4899E00F7A…  tcyunlock/src/Protocol.cs          （自始至终未变）
BDB44D94B98F28F2…  tcyunlock/src/Ble/GattUnlockServer.cs
5C9A53F4149B11C1…  tcyunlock/src/UnlockPolicy.cs       （rc2 新增）
5950A56AEA1EB2AD…  tcyunlock/src/Win/InputInjector.cs
41483515 B / 2BC9BB1C78398BD2F09F551E11FC15C9D0712373A6F2A1AF9C9D652DB64FF392  tcyunlock/dist/tctool-unlock.exe  ← **冻结交付物（绑定本报告全部 Windows 实测结论）**
11556096 B / 71C4DF5E44699186356488179F789422EB024999B9D1B4498F1B00D4ABA71769  dist/android/TC-Tools-Unlock-0.1.0.apk
```
- ⚠️ **源码 / 冻结产物「故意不同步」（lead 裁定后由 winhost 主动通报，我已独立核实）**：
  - **事实**：我方复核后，`src/Program.cs` 由 `B26E8D7E63AB97C7…` 变为 **`2C49CFC7921CA443…`**（我方独立重算确认），其余 4 个源文件**均未变**。
  - **冻结产物未受影响（我已独立验证）**：`dist\tctool-unlock.exe` 仍为 **41,483,515 B / `2BC9BB1C…`**、LastWriteTime 仍为 **2026-10-04 14:50:55**；`dist\selftest-vectors.json` 仍 4258 B。winhost 只跑了 `dotnet build`（写 `bin/obj`），未跑 `publish`，因此 `dist\` 未被触碰。**本报告全部 Windows 实测结论继续有效，无需重跑。**
  - **变更内容**：`CmdForget` 增加"删除过期导出物 `payload.txt`/`payload.png`"（标注 `since v0.2.0-rc3 / deferred from rc2`），即 §11.5 那条 UX 项的源码落地；lead 裁定**不重建 rc2 二进制**。
  - **路径安全复核（我另行审查，因为这是删除文件的代码路径）**：`Program.cs:456-462` 的删除目标来自 `HostPaths.PayloadTxt` / `HostPaths.PayloadPng` —— **编译期固定的两个路径**（`Path.Combine(Dir, "payload.txt"|"payload.png")`），**无通配符、无递归、不接受用户输入**，不存在路径穿越或误删风险。✓
  - **给后续复核者的重要提醒（可复现性缺口）**：**用当前 `src/` 重新构建，不会得到 `2BC9BB1C…`**（源码已领先一个 rc3 改动）。因此终局结论**以冻结产物哈希为准**；若需从源码复现，必须先检出与 `2BC9BB1C…` 对应的源码版本。
  - 建议措辞（已采用）：**"冻结产物 = rc2 二进制（哈希见上）；`src/` 含一项 rc3 变更，随下一次构建生效，不影响 rc2 结论。"**
- **`dist\android\…-0.1.0.apk` 不是最终交付**（rc2 的 APK 尚未产出，我复核时该目录仍是 0.1.0）；R30 的 APK 数字对应 0.1.0，**rc2 APK 出来后须整条重验**。
- **rc2 的协议回归我已跑过并全部通过**：§8 向量 MATCH、`dist\selftest-vectors.json` MATCH、双向互通全过。协议层未受 rc2 影响。

### 11.4 证据溯源说明（本轮唯一的归因争议，已了结）

**事项**：我第一次运行 `verify-qr.py --masks` 时退出码为 1，被我如实记录。winhost 随后在给我的消息中**猜测**这是"PowerShell 用 `| Select-Object -First N` 截断输出造成的、脚本自身退出码是 0"。

**核实结论（以证据为准）**：该猜测**不成立**。那次我**没有截断输出**，脚本自身抛出了真实异常：
```
RuntimeError: qrdump failed (2147516547): You must install .NET to run this application.
Failed to resolve hostfxr.dll [not found]. Error code: 0x80008083
```
**根因**：脚本当时的 `DEFAULT_EXE` 指向框架依赖的 `bin\Release\…\tctool-unlock.exe`，而该次运行未设置 `DOTNET_ROOT` —— 这正是我提交的那条 nit。修复后我在**无 dotnet 环境**下复跑，得到 `exit=0` 与 `RESULT: ALL VERIFIED`（§11.1）。

**已由我独立核验的两点**（未采信自述）：
1. **错误归因未进入任何交付物**。我在 `tcyunlock/**` 全量检索 `截断|测量误差|Select-Object|exit code: 1`，仅 3 处命中，**全部为无关的正当用法**：`build.ps1:117` 的 `Select-Object -Last 3`（构建冒烟的正常输出裁剪）、`README.md:175` 的 `error.msg` 超长截断（即 F1 的夹取）、`README.md:205` 的 host.json"临时文件+替换"防崩溃截断。**没有一处是本条归因。**
2. **该失败模式已被固化为可读错误**：`tools/verify-qr.py:105-107` 现对 `hostfxr` / `You must install .NET` 输出明确提示（"that build is framework-dependent. Run build.ps1 … or set DOTNET_ROOT"），后续复现者会直接看到原因。

**为什么值得记进报告**：把"验证者的真实失败"重新解释为"测量误差"，会削弱**后续所有退出码证据**的可信度。记录保留这一节的目的是：**本项目对"通过/失败"的判定以可复现命令与原始输出为准，不以任何一方的解释为准** —— 包括验证者自己的解释。

**winhost 的处置（如实记录）**：他主动撤回了该猜测，确认其未污染持久记录，并请我以本报告的表述为准。这是一次健康的纠错。

### 11.5 冻结状态与残余风险

- **Windows 侧 rc2 已冻结**（winhost 声明，我已独立复核数值一致）：`dist\tctool-unlock.exe` 41,483,515 B / `2BC9BB1C…`；`dist\selftest-vectors.json` 4258 B。
- **残余事项（1 条，需 lead 决策）—— 已核定为「UX/语义项」，安全影响为零**（不是安全问题）：
  winhost 声明存在一个已文档化但**未落地**的改动：`forget` 后不清理过期导出的 `payload.txt`/`payload.png`。
  **我没有采信其"安全影响为零"的自述，而是独立核实了三条腿**：
  1. **代码路径**：`Program.cs:442-447` `CmdForget` → `HostStore.ClearPairing()`；`HostStore.cs:215-218` 明确 `State.PskProtected = null` 后 `Save()`。即 `forget` **确实清除 PSK 密文**。
  2. **磁盘现状（我以只读方式实查）**：`%LOCALAPPDATA%\TC-tools\unlock\` 下**只有 `host.json`（231 B），无 `payload.*` 残留**；`host.json` 的顶层键为 `v / hostId / hostName / pskInvalidated / failCount / injectWhenUnlocked / keyDelayMs / publishLocalName` —— **不含 `pskProtected`、不含 `passwordProtected`**（唯一含 "psk" 的是布尔标志 `pskInvalidated`，非密钥材料）。检查时我只输出键名、未打印任何值。
  3. **逻辑闭环**：即便用户扫到旧二维码，宿主在无 PSK 时 `HandleProofAsync` 直接提前返回（`GattUnlockServer.cs:417-422`：`PROOF received but this host is not paired (no PSK)`），**PROOF 不可能通过** → 不会产生未授权解锁。
  → **结论：最坏后果仅为"用户扫旧码后认证失败、困惑一次"；不构成安全缺陷，无可用密钥材料残留。** 归档为 UX 项。
  **winhost 的建议**（如实记录）：保持 rc2 冻结、该项**不在本轮修**，理由是为一次低频路径的体验问题重建安装包不划算（会触发 exe 哈希变化 → 我重跑 F3/回归 + lead 重打安装包 + C++ 端重新对照哈希）；转为"已文档化、延后到下一次构建"。
  **lead 决策（已落地）**：**不重建 rc2 二进制**，该项转为"源码已落地、随下次构建生效"。故 **`2BC9BB1C…` 即 Windows 侧终局哈希**。我已独立核实：`Program.cs` 变化（`B26E8D7E…` → `2C49CFC7…`）但 `dist\tctool-unlock.exe` 与 `selftest-vectors.json` **均未变**（详见 §11.3 的可复现性缺口）。
- **Android 侧 rc2 APK 已就位** → 见 §12 终局复核。

---

## 12. 终局复核结论（v0.2.0-rc2 冻结版）

> lead 于 2026-10-04 发出冻结信号后执行。**本节为最终结论**，此前各节的个别中间状态以本节为准。
> 复跑命令与原始输出见 §7、`evidence/`。

### 12.1 终局复跑结果：**全绿**

| # | 检查项 | 命令 | 结果 |
|---|---|---|---|
| 1 | 权威向量自检 + 确定性 | `node tests\unlock\ref-vectors.mjs --selftest` | `SELFTEST OK`；`vectors.json` 重生成**字节一致** |
| 2 | 5 路独立实现交叉 | `crosscheck.py` / `dotnet-crosscheck` / `openssl mac` | Python `ALL MATCH`、.NET `ALL MATCH`、OpenSSL `21F2A1A3…AF47` |
| 3 | Windows 产品代码 §8 向量 | `dotnet run --project tests\unlock\winproto-harness` + `--compare` | **exit=0（MATCH）** |
| 4 | Android 产品代码（独立 kotlinc） | `run-android-selftest.ps1` + `--compare` | `SELFTEST_RESULT=PASS`、**exit=0（MATCH）** |
| 5 | 双向互通 | `run-interop.ps1` | Windows↔Android 互解 counter=2/3 + 反例拒绝 → **全部通过** |
| 6 | 冻结产物自包含（F3） | 清空 `DOTNET_ROOT`、PATH 无 dotnet 直跑 `dist\tctool-unlock.exe selftest` | **29/29 PASS, exit=0** |
| 7 | rc2 APK 本体 | size/SHA256/zip/dex/ABI | **与 lead 声明逐字符一致**（§4 R30） |
| 8 | rc2 自测向量 | `--compare dist\android\vectors-android.json` | **6/6 MATCH, exit=0** |

### 12.2 构建清单已**被验证**（不只是被引用）

`docs/BUILD-MANIFEST-v0.2.0-rc2.md` 声明 `sourceCommit = f7d953b9299b902c220a6fad66492cb0cd8d61f2` 并列出 6 个产物哈希。
**我独立复算了全部 6 个，逐字符一致**：

| 产物 | 大小 | SHA-256（我复算 = 清单值） |
|---|---|---|
| `dist\TCtools-installer-0.2.0-rc2.exe` | 35,183,812 | `70689E3ED284E7E6268D6DD3A33E0ADC0DDDAD7767BCDBF0F4923B570D30FA98` ✅ |
| `dist\tctool.exe` | 1,161,771 | `EAD0861AE1D9896BA04EE94934D895E7E81E7CC68668A82E508036A04957BC28` ✅ |
| `dist\android\TC-Tools-Unlock-0.2.0-rc2.apk` | 11,475,766 | `12F01A782BB424875F683EF50C48D370D14EC4DA373253B356956E6626B98359` ✅ |
| `tcyunlock\dist\tctool-unlock.exe` | 41,483,515 | `2BC9BB1C78398BD2F09F551E11FC15C9D0712373A6F2A1AF9C9D652DB64FF392` ✅ |
| `tcyunlock\dist\selftest-vectors.json` | 4,258 | `649DF454603A743A13F67F9A9F9CA88AE6C18D0063BEDDC0E5ACF07D1935AFEF` ✅ |
| `dist\android\vectors-android.json` | 1,057 | `D3F34DA39172FBBF63189FAA10C039DD461E5DD3D6B00FBC200E3808A9A41458` ✅ |

**我提的"源码版本标识"建议已被 lead 采纳并落地为本清单**，§11.3 的"可复现性缺口"现指向该文件。
**但仍有一条残余缺口（清单自己也写明了，`BUILD-MANIFEST` 第 38-39 行）**：`f7d953b` **仍不能复现** `2BC9BB1C…` 这个解锁组件（它对应更早的源码状态，只能靠 `tcyunlock/README.md §1.4` 对账）。
→ **建议下一次冻结时，为解锁组件单独给出对应 commit**，彻底闭合该缺口。

### 12.3 最终验收口径（三段式）

**✅ 已验证（可复现、有原始输出）**
1. **协议字节级**：§8 权威向量由 **5 条独立实现**算出并一致；两端在**各自正式构建链**下产出的自测结果与权威值 **6/6 MATCH**。
2. **两端互通**：Windows 解 Android 的帧、Android 解 Windows 的帧，含 counter=2/3 与反例拒绝。
3. **可编译可运行**：宿主 `dotnet build` 0 错误 0 警告；app 模块 110 class（含 ble/ui）；Android 协议核心独立 kotlinc 编译运行。
4. **交付物自包含**：清空 `DOTNET_ROOT`、PATH 无 dotnet 下 `selftest` **29/29 PASS, exit 0**。
5. **APK 可构建**：size/SHA256/zip/9 dex/4 ABI 独立复核；`vectors-android.json` 6/6 MATCH。
6. **APK 可安装可启动 + 磁贴拉起 App**：由 lead 在 Android 14 模拟器实测（`adb install` Success、无 FATAL、磁贴 `click-tile` 拉起 MainActivity）。**verify 未运行 adb，此项为 lead 验证。**
7. **决策规则可回归**：`UnlockPolicy.cs` 纯函数被 selftest 覆盖（1500ms 边界、第 5 次失效、零按键中止）。
8. **二维码图像编解码**：verify 独立执行 `verify-qr.py --masks` → **5/5 + 40/40, `RESULT: ALL VERIFIED`, exit 0**。
9. **自启动默认关闭**：只读实测 `enabled:false, method:"none"`，三处落点均未指向本程序。
10. **产物 ↔ 源码绑定**：`BUILD-MANIFEST-v0.2.0-rc2.md` 的 6 个哈希**经我全部独立复算一致**。

**❌ 未验证（环境固有限制，非缺陷）**
1. **真机 BLE 空口链路** —— 无 Android 设备接入；本机适配器存在且宿主可发布，但同机自连返回 `null`；模拟器无真实蓝牙射频。**所有 GATT 空口行为未经真实射频验证。**
2. **真实生物识别硬件** —— `adb emu finger touch` 只模拟指纹事件。
3. **真实锁屏安全桌面注入** —— 需真的锁屏并在 Winlogon 安全桌面注入（会破坏交互式会话、涉及真实密码）。
4. **Windows 10 1709 运行时** —— 本机 Win11 26200；仅确认 TFM/`TargetPlatformMinVersion` 与编译期兼容。
5. **决策规则到实时 GATT 会话的接线**（R16/R19/R20/R21 后半句）—— 判定函数已实测，接线仍为静态审查。
6. **自启动的真实生效** —— "默认关闭"已验证；**"重启/登录后是否按时拉起"未验证**（需重启用户机）。
7. **aapt2 元数据** —— `aapt2` 未安装，引用 lead 输出，未独立验证。
8. **解锁组件的源码级复现** —— `f7d953b` 不能复现 `2BC9BB1C…`（见 §12.2）。

**⚠️ 版本漂移风险**
- 本次复核期内，产品代码/产物**至少变动 8 次**：`GattUnlockServer.cs` 4 版、`Program.cs` 3 版（含一项 rc3）、APK 3 版（`C66734F8…` → `71C4DF5E…` → `12F01A78…`）、宿主 exe 2 版（1.0.0 `816C91AA…` → rc2 `2BC9BB1C…`）。`Protocol.cs` 与 §8 权威向量**自始至终未变**。
- **终局结论绑定下列哈希**（上表 §12.2）。**任何哈希变动都会使对应结论失效**，须重跑 §7。
- **最有效的防伪机制**：结论跟着哈希走；先查 `BUILD-MANIFEST`，再决定是否需要重跑。
