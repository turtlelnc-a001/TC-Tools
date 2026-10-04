# TC-tools Unlock — 蓝牙解锁协议规范 v1.0

> 本文件是 **Windows 端** 与 **Android 端** 的唯一权威契约。
> 任何一端偏离本规范都会导致无法互通。实现前请完整阅读。

- 项目：TC-tools / 手机端应用名 **“TC-Tools 解锁电脑”**
- 协议版本：`1`
- 状态：**冻结（Frozen）** — 修改需同时通知两端负责人

---

## 1. 角色与总体流程

| 角色 | 平台 | 蓝牙角色 |
|---|---|---|
| **Host（电脑端）** | Windows 10 1709+ | **GATT Server / Peripheral**（发布广播） |
| **Peer（手机端）** | Android 8.0+ | **GATT Central / Client**（扫描并连接） |

```
[一次性配对] Host 生成 32 字节 PSK → 显示二维码 → 手机扫码/粘贴 → 双方保存 PSK
[日常解锁]   手机(App) 连接 Host 的 GATT 服务
             → 读取 32 字节随机 challenge
             → 用户指纹验证通过(仅在指纹通过后才计算)
             → 手机用 PSK 计算 HMAC proof 写入 Host
             → Host 校验通过 → 建立临时会话密钥
             → 手机发送 AES-256-GCM 封装(unlock) → Host 注入键盘输入解锁
             → Host 回传加密状态 → 手机显示结果
```

**安全要点**：PSK 永不通过蓝牙传输，只经二维码/二维码文本传递；每次连接的 challenge 与
会话密钥都是**一次性**的；所有 unlock 指令都必须经过指纹验证后才产生。

---

## 2. 加密原语（两端必须完全一致）

| 名称 | 定义 |
|---|---|
| `PSK` | 32 字节预共享密钥（一次性配对产生，random） |
| `NONCE` | 32 字节随机数，**每次 GATT 连接重新生成**（Host 生成） |
| `HKDF` | 直接用 HMAC-SHA256 代替（本项目不使用完整 HKDF） |
| `K_session` | `HMAC-SHA256(PSK, NONCE \|\| "TCUNLOCK-SESSION-V1")` → 32 字节 |
| `PROOF` | `HMAC-SHA256(PSK, NONCE \|\| "TCUNLOCK-PROOF-V1" \|\| HOST_ID \|\| PEER_ID)` → 32 字节 |
| `SEAL(key, counter, plaintext)` | AES-256-GCM，见 2.1 |
| `OPEN(key, counter, ciphertext)` | AES-256-GCM 解密，失败即丢弃 |

字符串一律取 **ASCII 原始字节**（不含结尾 `\0`）。`HOST_ID` / `PEER_ID` 为 UUID 字符串
的 ASCII 形式（含连字符，小写，36 字节）。

### 2.1 SEAL 输出线格式

```
offset  size  field
0       12    IV    = counter(8 字节, big-endian) || 4 字节 0x00
12      n     AES-256-GCM ciphertext
12+n    16    GCM tag
```

- `counter`：**每次连接内从 1 开始单调递增**，用于 IV 唯一性与重放防护；两端都必须校验
  收到的 counter 严格大于上次成功的 counter，否则**丢弃并视为攻击**。
- `plaintext` 为 UTF-8 JSON（见第 5 节）。

### 2.2 会话编号

`SESSION_ID` = `OPEN(K_session, 1, host_ready.ciphertext)` 中 `sid` 字段的 4 字节十六进制小写。
两端各自独立计算，用于日志与 UI 显示，不参与安全判定。

---

## 3. BLE GATT 定义

### 3.1 UUID（固定，不可更改）

| 用途 | UUID |
|---|---|
| Service | `7a1c9e40-2f3d-4b6c-9a11-6c5d3e8f2b01` |
| Characteristic A（challenge / notify） | `7a1c9e41-2f3d-4b6c-9a11-6c5d3e8f2b01` |
| Characteristic B（command / write） | `7a1c9e42-2f3d-4b6c-9a11-6c5d3e8f2b01` |
| Scan Response / 广播内 Service UUID | 同 Service UUID |

### 3.2 Characteristic A — Challenge（属性：Read + Notify）

- **Read 返回值**：当前会话的 32 字节 `NONCE`（未认证时为随机值；认证成功后 Host 会
  **立即刷新**为新的随机值，防止旧 proof 重放）。
- **Notify**：Host 借此下发加密消息（即 `SEAL` 字节流，长度 29 起）。
  - Notify 载荷 **不带任何头部**，就是纯 `SEAL(K_session, counter, json)` 字节。
  - 长度可能超过默认 20 字节 MTU；两端都必须按 MTU 分片/重组：
    - Android 端：`requestMtu(517)`，用 `onCharacteristicChanged` 累积直到长度匹配。
    - Windows 端：`GattLocalCharacteristic` 使用 **长写/长读**（写请求可带偏移）。
  - **实现约定（务实）**：Notify 载荷默认限制在 **≤ 180 字节**（状态消息足够小）。若确需更长，
    必须改为**分片 + 前 2 字节大端总长度前缀**（单帧 ≤180 字节）。两端按此实现。

### 3.3 Characteristic B — Command（属性：Write）

手机写入该特征即发送命令。所有写入都**允许未加密链路**，安全性由第 2 节的 AEAD 保证。

#### 3.3.1 IDENT 帧（明文，36 字节，**必须在 PROOF 之前发送**）

```
PUT IDENT:  36 字节 = PEER_ID 的 ASCII 小写 UUID 字符串
                     例如 "8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d"
```

- **为什么需要它**：`PROOF` 的计算包含 `PEER_ID`（见第 2 节），而认证前 Host 无法从其他途径得知手机的
  `PEER_ID`（二维码载荷里也没有该字段）。因此手机必须在发送 PROOF **之前**先把自己的 `PEER_ID` 明文告诉 Host。
- 该帧不含密钥、可被伪造，但它只决定"Host 用哪个 PEER_ID 去算期望 PROOF"；
  伪造者仍必须持有 `PSK` 才能通过校验，**不产生安全损失**。
- Host 校验其合法性（必须恰为 36 字节、形如小写 UUID），不合法则丢弃。
- Host 计算期望 PROOF 时，**必须**使用本次连接收到的 IDENT；未收到 IDENT 时可回退尝试空串 `""`
  （仅容错；新实现必须发送 IDENT）。
- 实践提示：Android 端在写完 IDENT 后应等待栈确认写入完成（onCharacteristicWrite），再写 PROOF。

#### 3.3.2 PROOF 帧（明文，32 字节）

```
PUT PROOF:  32 字节 PROOF（见第 2 节）
```

- Host 收到后：比较 `PROOF` 与自己计算的期望值（常量时间比较）。
- 成功：进入已认证状态。
- 失败：累计失败次数 `failCount`；连续失败 **≥ 5 次** 时 Host 判定暴力破解，**立即失效当前
  PSK**（写入 `pskInvalidated=true` 状态，要求用户重新配对），并断开连接。

#### 3.3.3 SEALED 帧（加密命令，≥ 29 字节）

认证成功后，手机写入 `SEAL(K_session, counter, json_utf8)` 得到的字节流（无头部）。

Host 解密后按 `type` 分派：

| type | 方向 | 字段 | 说明 |
|---|---|---|---|
| `unlock` | 手机 → 电脑 | `type`, `counter`(可选), `sid`(可选) | 执行解锁 |
| `ping` | 手机 → 电脑 | `type` | 保活/测延迟 |
| `bye` | 手机 → 电脑 | `type` | 主动结束会话 |

Host 回传（通过 Characteristic A 的 Notify，同样是 `SEAL`）：

| type | 字段 | 说明 |
|---|---|---|
| `ready` | `type`, `sid` | 认证成功，会话建立（counter = 1） |
| `unlock_result` | `type`, `ok`(bool), `reason`(string), `locked`(bool), `sent`(bool) | 解锁结果 |
| `pong` | `type` | ping 回应 |
| `error` | `type`, `code`(string), `msg`(string) | 协议错误 |

`reason` 取值：`"ok"`、`"no-password"`、`"not-locked"`、`"inject-failed"`、`"unsupported"`、
`"throttled"`。

---

## 4. 一次性配对（Provisioning）

### 4.1 二维码载荷（UTF-8 JSON，单行，无换行）

```json
{"v":1,"p":"tcunlock","id":"<HOST_ID>","name":"<电脑名>","psk":"<base64url(PSK),无填充>"}
```

- `id`：Host 生成的 UUID v4 字符串（小写）。
- `name`：电脑名（UTF-8，可含中文）。
- `psk`：`base64url` 编码（`-` `_`，**去掉 `=` 填充**）。
- 示例：
  `{"v":1,"p":"tcunlock","id":"3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01","name":"DESKTOP-ABC","psk":"sT1k...Q"}`

> 两端都必须同时支持"**显示/复制这段文本**"的通道：手机端在无法扫码时允许**手动粘贴**，
> 便于自动化测试与无障碍使用。

### 4.2 配对状态

- Host 保存：`hostId`, `hostName`, `psk`, `peerId`, `peerName`, `pairedAt`。
- 手机保存：`hostId`, `hostName`, `psk`, `pairedAt`（使用 EncryptedSharedPreferences）。
- **重新配对**会生成新 PSK 并覆盖旧值；旧手机立即失效。

---

## 5. 解锁执行（Windows 端语义）

1. 校验 `unlock` 消息的 AEAD 与 counter。
2. 读取本机是否处于锁定状态（尽力而为）：
   - `OpenInputDesktop` 失败或桌面名 != `Default` ⇒ 判定为锁定/安全桌面。
3. 通过 `SendInput` 以 **Unicode 扫描码** 注入密码字符，然后注入 `VK_RETURN`。
   - 字符 → 扫描码使用 `VkKeyScanW`；需要 `Shift` 时正确置位。
   - 不支持/无法映射的字符：整个注入中止，返回 `reason="unsupported"`（**不得部分输入**）。
4. 返回 `unlock_result`：`ok=true, sent=true, locked=<检测值>`。
   - 电脑**未锁屏**时：不注入（避免把密码打进当前窗口），返回 `ok=false, reason="not-locked"`。
     —— 该行为可由配置 `injectWhenUnlocked`（默认 `false`）放开。
5. 限流：同一会话内两次 `unlock` 间隔 < 1500 ms 时返回 `reason="throttled"`。

---

## 6. 错误码（`error.code`）

| code | 含义 |
|---|---|
| `bad-frame` | 长度/结构非法 |
| `not-authenticated` | 未通过 PROOF 就发命令 |
| `auth-failed` | PROOF 校验失败 |
| `replay` | counter 未递增 |
| `decrypt-failed` | GCM 校验失败 |
| `nopsz` | Host 未配置密码 |
| `busy` | 正在处理上一条 |

---

## 7. 兼容性与实现约束

- **Windows**：`net8.0-windows10.0.19041.0`，C#，编译后可在 Windows 10 1709 运行
  （不使用高于 10.0.19041 的 API；`.NET 8` 支持 Win10 1607+）。
- **Android**：`minSdk 26`、`targetSdk 34`、Kotlin + Jetpack Compose（Apple 风格 UI）。
- 两端都必须在**无真机**时可用 **回环自测模式**（loopback self-test）验证第 2 节的
  密码学与第 3 节的消息结构，不得依赖真实蓝牙才能跑测试。

---

## 8. 回环自测向量（两端必须能跑通并给出一致结论）

测试用例（不涉及真实 BLE，直接在进程内构造字节）：

1. `PSK = 0x00..0x1f`（0 到 31 共 32 字节）
2. `NONCE = 0x20..0x3f`（32 到 63 共 32 字节）
3. `HOST_ID = "3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01"`，`PEER_ID = "8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d"`
4. 期望 `K_session`（十六进制）与 `PROOF`（十六进制）：**待填充**
   `K_session = 21f2a1a3890968e1da28553de67b49cab6ab2ffb7f3ecbaec72d284852b9af47`
   `PROOF     = 353d6fdd6e73a63620389b806711e6c2c726407045d4c7ddae29264a9e8bab10`

> ⚠️ 这两个期望值最初为 `PENDING-VERIFIER`。**验证负责人**用第三种独立实现（Node.js，
> 见 `tests/unlock/`）算出权威值后填入本文件，两端再据此对齐。
> 在填充之前，两端**不得**各自"按自己的输出改期望值"——必须以交叉核对结果为准。
> 该用例同时用于验证：两端对 `HMAC-SHA256 / AES-256-GCM / base64url / 计数器 IV` 的理解一致。

明文测试载荷（用于 SEAL 测试）：

```json
{"type":"unlock"}
```

`SEAL(K_session, 1, ...)` 的期望密文长度 = 12 + len(plaintext) + 16。
