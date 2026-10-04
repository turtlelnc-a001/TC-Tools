# tcyunlock — TC-tools 蓝牙解锁宿主（Windows 端）

Windows 10 1709+ 的 **BLE GATT Server**，与安卓端「TC-Tools 解锁电脑」App 配合，
用手机指纹验证解锁电脑。

字节级契约见 [`docs/UNLOCK-PROTOCOL.md`](../docs/UNLOCK-PROTOCOL.md)（v1.0，冻结）。
本目录是 Windows 端实现，角色是 **GATT Server / Peripheral**，手机是 **GATT Central**。

```
| 项目 | 值 |
|---|---|
| 可执行文件 | `dist\tctool-unlock.exe`（self-contained，**目标机无需安装 .NET**）|
| 目标框架 | `net8.0-windows10.0.19041.0`，`TargetPlatformMinVersion = 10.0.16299.0` |
| 协议版本 | 1 |
| Service UUID | `7a1c9e40-2f3d-4b6c-9a11-6c5d3e8f2b01` |
| Challenge 特征 | `7a1c9e41-2f3d-4b6c-9a11-6c5d3e8f2b01`（Read + Notify）|
| Command 特征 | `7a1c9e42-2f3d-4b6c-9a11-6c5d3e8f2b01`（Write）|
```

---

## 1. 构建与发布

### 1.1 一键构建（推荐）

```powershell
pwsh -File TC-tools\tcyunlock\build.ps1
```

`build.ps1` 做两件事：

1. `dotnet build -c Release` → `bin\Release\net8.0-windows10.0.19041.0\tctool-unlock.exe`
   （**框架依赖**版：需要目标机有 .NET 8 运行时，或设置 `DOTNET_ROOT` 指向便携 SDK）
2. `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
   → `dist\tctool-unlock.exe`（**自包含**版：约 70 MB，目标机无需任何 .NET）

常用参数：`-DotnetRoot <目录>`（默认 `C:\Users\吴桥生\Videos\DSHworkarea\toolchain\dotnet8`）、
`-SkipPublish`（只做快速构建）、`-NoSingleFile`（自包含但发布成目录而非单文件）。

### 1.2 手工命令

```powershell
$dotnet = 'C:\Users\吴桥生\Videos\DSHworkarea\toolchain\dotnet8\dotnet.exe'

# 开发构建（快；跑 exe 需要 DOTNET_ROOT 指向 SDK，或用 dotnet <dll> 启动）
& $dotnet build TC-tools\tcyunlock\tctool-unlock.csproj -c Release

# 交付构建：自包含单文件，不需要目标机装 .NET
& $dotnet publish TC-tools\tcyunlock\tctool-unlock.csproj -c Release `
    -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o TC-tools\tcyunlock\publish
Copy-Item TC-tools\tcyunlock\publish\tctool-unlock.exe TC-tools\tcyunlock\dist\ -Force
```

> **为什么必须用 self-contained 版**：本机（以及用户机）可能只有便携 SDK、没有注册系统级
> .NET 运行时。此时框架依赖版的 apphost 会直接报
> `You must install .NET to run this application. Failed to resolve hostfxr.dll [not found]`。
> `dist\tctool-unlock.exe` 自包含运行时，双击即可运行、无需 `DOTNET_ROOT`。

### 1.3 依赖

- `System.Security.Cryptography.ProtectedData` 8.0.0（DPAPI，.NET 8 共享框架里没有）
- WinRT 蓝牙投影来自 `Microsoft.Windows.SDK.NET`（由 TFM 自动引入，无需手工引用）

---

## 2. CLI 命令参考

```
tctool-unlock status [--json] [--no-probe] [--enable-radio]
tctool-unlock pair [--json] [--qr=ascii|unicode|none] [--no-png]
tctool-unlock pair --show [--json]
tctool-unlock pair --payload <json> [--json]
tctool-unlock forget [--json]
tctool-unlock set-password [--clear] [--json]
tctool-unlock run [--duration N] [--json] [--enable-radio]
tctool-unlock selftest [--json] [--out <path>]
tctool-unlock qrdump --text <payload> [--ecc L|M|Q|H] [--mask 0..7] [--png <path>] [--json]
tctool-unlock version [--json]
```

**退出码**：`0` 成功 · `1` 失败 · `2` 用法错误 · `3` 已有实例在运行 · `4` selftest 失败。

### 2.1 `--json` 约定（C++ 端按此解析）

- **单行** JSON，走 **stdout**；诊断信息一律走 stderr，不会污染 stdout。
- **纯 ASCII**：非 ASCII 字符（中文电脑名/手机名/路径）用 `\uXXXX` 转义。
  原因：调用方通过 `cmd /c` 管道读到的可能是 GBK 代码页字节，直接输出 UTF-8 中文会乱码。
- 字段名固定（`status`）：

```json
{"paired":true,"advertising":true,"passwordSet":true,"hostId":"…","hostName":"LAPTOP-9KC7VPLA",
 "peerName":"","pskValid":true,"running":false,"injectWhenUnlocked":false,
 "hostJson":"C:\\Users\\…\\host.json","advertisingDetail":"advertising (Started)","version":"1.0.0"}
```

`peerName` 缺失时输出空串（不是 `null`），便于 C++ 侧按字符串解析。

### 2.2 `status`

`advertising` 的取值来源，优先级从高到低：

1. `run` 实例存活（心跳文件 `runtime.json` 新鲜且 PID 存活）→ 用该实例的实际广播状态；
2. 否则**真机探测**：临时创建 GATT 服务并 `StartAdvertising`，确认拿到
   `GattServiceProviderAdvertisementStatus.Started` 后立即停止，`advertising=true`；
3. `--no-probe` → 不探测，`advertising=false`（`advertisingDetail="not probed"`）。

所以 `status --json` 里的 `advertising=true` 是**真机能力证据**（本机实测见 §9），不是硬编码。
`--enable-radio` 会先尝试打开蓝牙电台（默认**不**改用户系统设置）。

### 2.3 `pair`

生成 32 字节 PSK + HOST_ID（UUID v4，小写），打印二维码载荷 JSON 与 ASCII 二维码，
并写入 `payload.txt`（原始 UTF-8）与 `payload.png`（8 位灰度 PNG）。

统一配对模式：**AES-GCM 与 PSK 算法无关，密钥由用户物理接触（扫码/粘贴）传递**。
重新 `pair` 会生成新 PSK 并覆盖旧值，**旧手机立即失效**。

`--qr=ascii`（默认，纯 ASCII 双字符画，任何终端/代码页都能显示）、`--qr=unicode`（半块字符，更紧凑）、
`--qr=none`（只输出文本载荷）。文本载荷本身永远是有效输入，扫码不方便时可直接粘贴。

`pair --payload <json>`：反向导入手机端产生的载荷（`id`→`peerId`，`name`→`peerName`；
若带 `psk` 则同时采用该 PSK）。可选功能，用于"手机先配对"的流程。

### 2.4 `set-password`

交互式录入，**不回显**（`Console.ReadKey(intercept: true)`），要求二次确认。
若 stdin 被重定向则退化为按行读取并**明确警告无法隐藏**。

保存时会用 `VkKeyScanW` 预检每个字符能否在当前键盘布局上键入；不能键入的字符
（中文、emoji、需要 AltGr 的符号等）会**明确警告**，因为解锁时只能整体中止并返回
`unsupported`（见 §6）。`--clear` 清除已保存密码。

### 2.5 `run`

前台常驻启动 GATT 服务，直到 Ctrl+C 或 `--duration N` 秒后退出。
启动成功后写运行时心跳 `runtime.json`（`pid`/`advertising`/`peerConnected`/`connections`/`unlocks`），
每 500 ms 刷新；退出时删除。检测到已有存活实例时拒绝启动（退出码 3）。

### 2.6 `selftest`

**不需要蓝牙**，进程内跑协议 §8 权威向量与安全断言，失败返回 4。
`--json --out <path>` 输出单行 ASCII JSON 供交叉核对（本机产物示例见 §9.2）。

---

## 3. GATT 契约（与安卓端必须一致）

| 特征 | 属性 | 行为 |
|---|---|---|
| A `…9e41…` | `Read` + `Notify` | Read 返回当前 32 字节 NONCE（长读按 `Offset` 切片）；Notify 下发 `SEAL` 帧，**无头部、≤180 字节** |
| B `…9e42…` | **仅 `Write`** | 手机写入 IDENT / PROOF / SEALED 帧 |

- **为什么 Command 只声明 `Write`**：ATT Write Command（WriteWithoutResponse）无法承载长写、也没有确认；
  而默认 MTU 23 时 32 字节的 PROOF 与 >20 字节的 SEALED 帧都必须走
  ATT Prepare/Execute 长写。安卓端请用 `WRITE_TYPE_DEFAULT`（默认值），
  并等待 `onCharacteristicWrite` 回调（IDENT 之后尤其必须等）。
- **长写重组**：宿主按 `GattWriteRequest.Offset` 累加分片；`Offset==0` 开新帧，
  `Offset==当前长度` 追加，出现空洞则丢弃重来。若某一帧解密失败，会先等 120 ms
  看是否有后续分片（避免把"还没写完"误判成"帧损坏"），确认无后续才回 `decrypt-failed`。
- **Notify 预算**：`12 (IV) + len(plaintext) + 16 (tag) ≤ 180`，即明文 ≤152 字节。
  宿主对超长消息会把 `error.msg` 截断以适配预算；若仍超长则**拒发并记录错误**
  （不会静默发出超预算帧）。selftest 有对应断言。
- **IDENT 帧（协议 §3.3.1）**：认证前手机可先写 36 字节 ASCII 小写 UUID 作为 `PEER_ID`。
  宿主只在认证前、且恰好 36 字节、且严格形如小写 UUID 时才登记为 IDENT，其余落到别的分支。
  计算期望 PROOF 时**只允许两档**：本次连接收到的 IDENT；未收到时回退空串 `""`
  （**不会**使用配对时存储的旧 `peerId`，避免掩盖真实的 PEER_ID 不一致）。
  收到的合法 IDENT 会存进 `host.json` 的 `peerId` 仅供显示。

---

## 4. 存储与加密

配置目录：`%LOCALAPPDATA%\TC-tools\unlock\`

| 文件 | 内容 |
|---|---|
| `host.json` | `hostId`/`hostName`/`pskProtected`/`peerId`/`peerName`/`pairedAt`/`pskInvalidated`/`failCount`/`passwordProtected`/`injectWhenUnlocked`/`keyDelayMs` |
| `payload.txt` | 二维码载荷文本（原始 UTF-8，单行，无 BOM）|
| `payload.png` | 配对二维码（8 位灰度 PNG）|
| `runtime.json` | `run` 的心跳状态（进程退出即删除）|

加密方式：

- **PSK 与解锁密码**都用 **Windows DPAPI**（`ProtectedData.Protect`），固定的附加熵
  `"TC-tools/unlock/v1"`，默认作用域 **CurrentUser**，以 base64 存入 `host.json`。
  即密文只能被**同一个 Windows 用户**解出，拷贝 `host.json` 到别的机器/用户无效。
- `TCUNLOCK_DPAPI_SCOPE=localmachine` 可切换为机器作用域（见 §7 服务化）。
  解密时会自动尝试另一个作用域，方便在两种部署方式间迁移。
- 明文 PSK **永不打印**：`pair --show` 只显示掩码（前 4 字节 base64url + 长度）；
  `pair` 只在二维码载荷里包含它（这是配对的唯一目的）。
- `host.json` 写入用"临时文件 + 替换"，并在可能时置 Hidden 属性，避免崩溃截断导致丢密钥。

---

## 5. 协议与安全实现要点

- **K_session / PROOF**：`HMAC-SHA256(PSK, NONCE ‖ 标签 ‖ …)`，标签取 ASCII 原始字节、不带结尾 `\0`
  （`TCUNLOCK-SESSION-V1` / `TCUNLOCK-PROOF-V1`）。
- **常量时间比较**：`CryptographicOperations.FixedTimeEquals`。
- **AEAD**：`AesGcm(key, tagSizeInBytes: 16)`；线格式 `IV(12) ‖ ciphertext ‖ tag(16)`，
  `IV = counter(8 大端) ‖ 4×0x00`。宿主强制校验 IV 后 4 字节为 0，否则 `bad-frame`。
- **counter 严格递增**：counter 在 IV 里明文可见，宿主先做重放判定
  （`Proto.ShouldAcceptCounter`，与 selftest 共用同一判定函数），未递增直接回 `replay` 并丢弃；
  解密失败**不**推进 counter。
- **挑战刷新**：PROOF 通过后立即把 Read 值刷新为新的随机 32 字节，旧 proof 无法重放。
- **连续 5 次 PROOF 失败** → `pskInvalidated=true` + 会话作废 + 要求重新配对；成功即清零。
- **认证前不回任何加密消息**：此时还没有 `K_session`，若用 PSK 派生的密钥回一条已知明文错误
  就等于给攻击者一个**离线校验 PSK 猜测的预言机**。因此认证前的错误只记日志、不发送
  （比协议最低要求更严格）。
- 单连接管理：会话在首次读/写时惰性建立，空闲 5 分钟或收到 `bye` 后失效；重新连接可复用
  已认证会话（counter 仍严格递增，重放依然被拒）。

---

## 6. 解锁执行

1. **锁定检测**：`OpenInputDesktop` 失败，或桌面名 != `Default`（锁屏/安全桌面为 `Winlogon`）
   → 判定为已锁定。
2. **未锁屏默认不注入**（`injectWhenUnlocked=false`），返回 `ok=false, reason="not-locked"`，
   避免把密码打进当前活动窗口。
3. **不支持字符整体中止**：先对密码每个字符做 `VkKeyScanW` 映射预检，全部通过才发送；
   任一字符无法映射（或需要 Ctrl/Alt/AltGr）→ `reason="unsupported"`，**一个键都不发**
   （不会出现"输了一半"）。错误信息里只有字符下标，**绝不包含密码内容**。
4. **注入方式**：`SendInput` 发 `wVk + wScan`（`MapVirtualKeyW` 取扫描码），需要 Shift 时
   正确按下/抬起 Shift，最后注入 `VK_RETURN`。键间默认间隔 10 ms（`keyDelayMs`），
   避免某些登录界面丢键。
5. **限流**：同一会话内两次 `unlock` 之间的最小间隔为 1500 ms。

### 6.1 限流是**滑动窗口**（重要语义声明）

限流命中时会**顺延计时基准**（即把"上次尝试时刻"更新为当前时刻），因此
**连续高频请求会被持续延后**：只要请求间隔始终 <1500 ms，就会一直得到
`reason="throttled"`，而不会因为"距第一次尝试已超过 1500 ms"而被放行。
这比协议字面的固定间隔**更严格**，用于抵御重试风暴；该行为已由 Lead 裁定可接受并在此显式声明。

---

## 7. 服务化（SYSTEM）与安全桌面注入

**问题**：锁屏界面运行在 `Winlogon` 安全桌面上，完整性级别高于普通用户进程。
普通用户进程（即使管理员）通过 `SendInput` 向安全桌面注入通常会被 UIPI 拦截。
要在锁屏上真正解锁，注入进程需要以 **SYSTEM** 身份、运行在**当前活动控制台会话**里。

**本目录提供的只是脚本与说明，不含任何提权动作，也从未在本机执行过**（本机无管理员权限）。

可行路线（按推荐度）：

1. **计划任务 + 交互式用户会话**（最简单，无需 SYSTEM）：
   任务以"用户登录时"触发、勾选"使用最高权限运行"，运行 `tctool-unlock run`。
   适用于"用户已登录但会话被锁"的场景（Win+L）。**不保证**能注入 `Winlogon` 安全桌面。
2. **SYSTEM 服务 + `CreateProcessAsUser`**（真正解锁锁屏的标准做法）：
   服务以 SYSTEM 运行，用 `WTSGetActiveConsoleSessionId()` 取活动会话，
   `WTSQueryUserToken()` + `CreateProcessAsUser()` 把注入器投到用户会话中执行。
   需要自行封装 SCM 宿主（`StartServiceCtrlDispatcher`），本工具**没有**实现服务宿主，
   也不在无提权环境下测试。
3. **DPAPI 作用域的坑**：密码默认用 **CurrentUser** 加密，**SYSTEM 服务解不开**。
   若要服务化，必须用 `TCUNLOCK_DPAPI_SCOPE=localmachine` 重新 `set-password`
   （机器作用域意味着本机其他用户也能解密，安全性下降，请自行权衡）；
   或者把"读取密码 + 注入"这一步放到用户会话的进程里做。

**未验证声明**：安全桌面注入路径**未在本机验证**（无管理员权限、未做提权），
本机实测仅覆盖到"锁定检测"与"普通桌面的注入准备"逻辑（`selftest` 覆盖字符映射与中止语义）。

---

## 8. API 事实与陷阱（本机实测，写代码前先看）

以下结论来自 `spike\`（本目录下的 spike 工程）在本机的实测与 WinRT 投影元数据检查，
投影程序集 `Microsoft.Windows.SDK.NET 10.0.19041.38`。

1. **`GattServiceProviderResult` 没有 `.Status`**，`Error` 的类型是
   **`BluetoothError`**（不存在 `GattServiceProviderError` 这个类型）；
   `GattLocalCharacteristicResult.Error` 同样是 `BluetoothError`。
2. **`CryptographicBuffer` 在 `Windows.Security.Cryptography`**，不在 `Windows.Storage.Streams`。
3. **`GattWriteRequest` 有 `Offset`**（长写可能分片到达），`GattReadRequest` 也有
   `Offset`/`Length`（长读要按 offset 切片）。
4. **`GattLocalCharacteristic.NotifyValueAsync(IBuffer)` 没有单独的 minOS 标注**，
   类级 minOS = `Windows10.0.15063.0` → 属于 1709 可用 API（**不是** 19041+）；
   该投影里**没有**无参 `NotifyValueAsync()` 重载。
   本实现用到的所有类型的类级 minOS 均 ≤ 15063：

   | 类型/成员 | 类级 minOS |
   |---|---|
   | `BluetoothAdapter` | 10.0.15063 |
   | `GattServiceProvider`（`CreateAsync`/`StartAdvertising`/`AdvertisementStatus`） | 10.0.15063 |
   | `GattServiceProviderAdvertisingParameters`（`IsDiscoverable`/`IsConnectable`） | 10.0.15063 |
   | `GattLocalService.CreateCharacteristicAsync` | 10.0.15063 |
   | `GattLocalCharacteristic`（`ReadRequested`/`WriteRequested`/`SubscribedClientsChanged`/`NotifyValueAsync(IBuffer)`） | 10.0.15063 |
   | `GattReadRequest` / `GattWriteRequest` | 10.0.15063 |

   （`GattServiceProviderAdvertisingParameters.ServiceData` 是 18362，**本实现不使用**。）
5. **`BluetoothLEAdvertisementPublisher.Start()` 在非打包 Win32 进程里必然抛异常**：
   本机对**任何**载荷（含只有 `Flags` 的最小载荷）都抛
   `ArgumentException: Value does not fall within the expected range`（E_INVALIDARG）。
   因此"用一个独立广播发布本机名"这条路在本环境**走不通**，
   本实现只依赖 `GattServiceProvider.StartAdvertising(IsDiscoverable: true, IsConnectable: true)`。
6. **本机名（LocalName）不可靠**：Windows 在 `IsDiscoverable=true` 时自行决定是否把本机名放进
   scan response，`GattServiceProviderAdvertisingParameters` 没有设置它的入口。
   **安卓端必须按 Service UUID 过滤，不要依赖 LocalName**（协议 §3.1 也是这样定义的）。
7. **电台关闭时 `BluetoothAdapter` 依然报 `Peripheral=True`**：本机蓝牙原本是关闭的，
   此时 `BluetoothAdapter.GetDefaultAsync()` 正常返回适配器且 `IsPeripheralRoleSupported=True`，
   但 `GattServiceProvider.CreateAsync()` 返回 `Error=RadioNotAvailable(1)`、
   `ServiceProvider=null`，`BluetoothLEAdvertisementPublisher` 也失败。
   **只看适配器会误判**。正确做法是先看 `Radio` 状态
   （`Radio.GetRadiosAsync()` → `Kind=Bluetooth` 的 `State`），
   `Radio.SetStateAsync(RadioState.On)` 在本机返回 `Allowed` 且随后一切正常。
   本工具把 `RadioNotAvailable` 作为一等状态给出明确提示，`--enable-radio` 才会去开电台
   （默认不擅自修改用户的系统设置）。

---

## 9. 本机验证证据

环境：Acer AL14-71 笔记本，Windows 11 **10.0.26200**，Intel 蓝牙（`USB\VID_8087&PID_0026`，
适配器地址 `9009DF05F4AA`），.NET 8 SDK 8.0.425。

### 9.1 真实 GATT 广播（关键证据）

```
[ ok ] Adapter: 9009DF05F4AA LE=True Central=True Peripheral=True AdvOffload=True
[info] GattServiceProvider.CreateAsync -> Error=Success ServiceProvider=ok
[info] CreateCharacteristicAsync(A) -> Error=Success Char=ok
[info] CreateCharacteristicAsync(B) -> Error=Success Char=ok
[evt] AdvertisementStatusChanged -> Started (error=Success)
[ ok ] >>> ADVERTISING IS LIVE (status=Started) <<<
```

配合 `status --json` 的真机探测：

```json
{"paired":false,"advertising":true,"passwordSet":false,"hostId":"adefc231-6aad-48a0-bd28-fe91588dcd9a",
 "hostName":"LAPTOP-9KC7VPLA","peerName":"","pskValid":false,"running":false,"injectWhenUnlocked":false,
 "hostJson":"C:\\Users\\\u5434\u6865\u751F\\AppData\\Local\\TC-tools\\unlock\\host.json",
 "advertisingDetail":"advertising (Started)","version":"1.0.0"}
```

### 9.2 `selftest`（24/24，含 §8 权威向量）

```
K_session      = 21f2a1a3890968e1da28553de67b49cab6ab2ffb7f3ecbaec72d284852b9af47
PROOF          = 353d6fdd6e73a63620389b806711e6c2c726407045d4c7ddae29264a9e8bab10
psk_base64url  = AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8
SEAL plaintext = {"type":"unlock"} (counter=1)
  IV (12B)     = 000000000000000100000000
  ciphertext   = 678def935e80e0eeb8ca5a9ee9bbef97f6
  tag (16B)    = a2c4302b392deeb76b81f496665496c6
  FRAME(45B)   = 000000000000000100000000678def935e80e0eeb8ca5a9ee9bbef97f6a2c4302b392deeb76b81f496665496c6
24/24 checks passed
```

这些期望值是**硬编码在 selftest 里的断言**（来自第三方独立实现
[`tests/unlock/ref-vectors.mjs`](../tests/unlock/ref-vectors.mjs)），不是"按自己的输出反推"。

覆盖：§8 向量逐字节比对、base64url、SEAL/OPEN 往返、多块明文、counter 重放拒绝、
篡改 tag/密文拒绝、非零 IV 填充拒绝、短帧拒绝、错误密钥拒绝、常量时间比较、
UUID 常量、§6/§3.3.2 错误码与 reason 全集、ready 与超长 error 的 180 字节 Notify 预算、
二维码编码与 PNG 生成。

### 9.3 独立交叉核对

- `node tests/unlock/ref-vectors.mjs --compare dist/selftest-vectors.json` → **6/6 MATCH, allMatch=true, exit 0**
- verify 用另一个宿主直接编译 `src/Protocol.cs` 跑 §8 → **6/6 逐字节 MATCH**
- verify 复核 Host/BLE 部分 → `24/24 checks passed, exit 0`，静态审查逐条通过
  （见 [`tests/unlock/verify-report.md`](../tests/unlock/verify-report.md)）
- 二维码：`tools/verify-qr.py` —
  **Part A**：`qrdump --png` 产出的 PNG 用 OpenCV 独立解码，5/5 载荷**逐字节还原**
  （含中文名、版本 8/9、需要填充的场景）；
  **Part B**：与第三方编码器 `segno` 逐 mask 比对码字流，40/40 在**消息区完全一致**，
  差异仅存在于 ISO 忽略的填充码字（见下）。

> **关于 `segno` 的填充差异（不是本实现的缺陷）**：ISO/IEC 18004 §7.4.10 规定
> "仅当比特流没有落在码字边界时才补零"。`segno 1.6.6` 的
> `encoder.py:346` 写作 `buff.extend([0] * (8 - (length % 8)))`，在对齐时仍会补 **8 个零比特**，
> 于是它的填充码字前多一个 `0x00`。填充码字被解码器忽略，两种码流都能正常扫描；
> 本实现遵循标准写法（`0xEC`/`0x11` 交替），并且以**真实解码器**（OpenCV）验证可扫。

### 9.4 同机自连实验（端到端 GATT 链路的边界）

结论：**单适配器无法自连**，因此**完整的 GATT 端到端链路（手机↔电脑的真实读写/通知）未在本机验证**。
已验证的是：服务创建、两个特征创建、广播成功、事件式 API 绑定、协议层与字节级向量全部正确。
端到端需要一台真实手机（本机曾配对过 `Owen的真我V70s`，具备条件但需安装安卓端 App）。

---

## 10. 兼容性与未验证项

**1709 兼容红线（编码纪律，本机无法用 1709 验证）**：

- 只用**事件式** API：`ReadRequested` / `WriteRequested` / `SubscribedClientsChanged` /
  `NotifyValueAsync(IBuffer)` / `GattServiceProvider.StartAdvertising`。
- 不使用任何 19041+ 独有 API：不使用 `GattServiceProviderAdvertisingParameters.ServiceData`（18362）、
  `BluetoothAdapter.IsExtendedAdvertisingSupported`（19041）、
  `BluetoothLEAdvertisementPublisher` 的 `UseExtendedAdvertisement`/`IsAnonymous`（19041）等。
- 不使用高于 .NET 8 的框架特性。

**未在本机验证的项（如实列出）**：

1. Windows 10 1709（16299）真机运行 —— 本机是 Win11 26200；minOS 只证明 API 元数据允许，
   不代表 1709 上的一切行为（例如某些版本的 Windows 对非打包 Win32 进程使用 BLE 广播有额外限制）。
2. 与真实安卓手机/App 的端到端互通（见 §9.4）。
3. 安全桌面（锁屏）注入与服务化（见 §7）。
4. 长写分片重组：逻辑已实现（按 `Offset` 累积 + 120 ms 收尾判定），但**没有真实手机**
   制造 >MTU 的 Prepare/Execute 长写来实测。
5. `pair --payload` 反向导入（协议标注为可选实现）。

---

## 11. 目录结构

```
tcyunlock/
├─ tctool-unlock.csproj     主工程（net8.0-windows10.0.19041.0）
├─ build.ps1                构建 + 自包含发布（产物落到 dist\）
├─ README.md                本文件
├─ src/
│  ├─ Program.cs            CLI 命令与 --json 契约、selftest
│  ├─ Protocol.cs           §2/§2.1 字节级原语、错误码、reason 常量
│  ├─ HostStore.cs          host.json / DPAPI / runtime.json 心跳
│  ├─ Ble/GattUnlockServer.cs  GATT Server、会话状态机、帧重组、解锁分派
│  ├─ Win/Native.cs         P/Invoke（SendInput / OpenInputDesktop / VkKeyScanW）
│  ├─ Win/InputInjector.cs  字符预检与键盘注入
│  └─ Qr/QrCode.cs          无依赖二维码编码器（字节模式，版本 1–10）+ ASCII/PNG 渲染
├─ tools/
│  ├─ verify-qr.py          二维码独立验证（OpenCV 解码 + segno 码字比对）
│  ├─ qr-extract.py         从矩阵反向提取码字流（验证工具）
│  └─ debug-qr-diff.py      差异定位辅助脚本
├─ spike/                   技术可行性 spike（ApiDump/RadioProbe/LoopbackProbe），非交付物
└─ dist/                    交付产物：tctool-unlock.exe（自包含）+ selftest-vectors.json
```

---

## 12. 故障排查

| 现象 | 原因与处理 |
|---|---|
| `CreateAsync` 返回 `RadioNotAvailable` | 蓝牙电台关闭。打开 Windows 蓝牙，或 `run --enable-radio` / `status --enable-radio`。**注意此时适配器仍报 `Peripheral=True`**，不要只看适配器。 |
| `advertising=false`，`advertisingDetail` 有错误 | 看具体 Status/Error；电台关闭、服务被占用、策略限制都会导致。 |
| 手机搜不到设备 | 用 Service UUID 过滤（`7a1c9e40-…`），**不要**依赖设备名；确认 `status --json` 里 `advertising=true`。 |
| 手机连上但认证失败 | 未配对或 PSK 不匹配（重新 `pair` 并重扫码）；连续 5 次失败会失效 PSK（`pskValid=false`，需重新配对）。 |
| 解锁返回 `unsupported` | 密码含当前键盘布局无法键入的字符（中文/emoji/AltGr 符号）。改用 ASCII 密码。 |
| 解锁返回 `not-locked` | 电脑未锁屏且 `injectWhenUnlocked=false`（默认），这是**防止把密码打进当前窗口**的保护。 |
| 解锁返回 `no-password` | 还没 `set-password`。 |
| 锁屏下注入了但没解锁 | 见 §7：安全桌面注入需要 SYSTEM/用户会话内的进程，普通进程会被 UIPI 拦截。 |
| `You must install .NET … hostfxr.dll` | 你运行的是框架依赖版。用 `dist\tctool-unlock.exe`（自包含），或设置 `DOTNET_ROOT`。 |
