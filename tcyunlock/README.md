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

### 1.4 当前版本状态、源码身份与可复现性（**先读这一节再复核哈希**）

> **冻结产物（唯一交付二进制）**
> - `dist\tctool-unlock.exe` = **v0.2.0-rc2**：41,483,515 字节，
>   SHA256 `2BC9BB1C78398BD2F09F551E11FC15C9D0712373A6F2A1AF9C9D652DB64FF392`，
>   构建时间 2026-10-04 14:50:55。
> - 它是验收与安装包使用的**唯一**二进制；**禁止**在未通知验证方的情况下替换。
> - **安装包必须校验"被复制的那个文件的哈希"，不能用"从源码重新构建"来代替**（原因见下）。

> **源码身份（可追溯，但不等于可复现）**
> - 产生该产物的源码状态 = **git 提交 `17047b4b77f157509af1efb8ba90f51993bbfd8a`**（TC-tools 仓库）。
>   除 `src/Program.cs` 外，交付中用到的源文件在该提交与当前工作树**完全一致**；
>   `tcyunlock/src/Program.cs` 在 rc2 状态的 blob SHA256 =
>   `B26E8D7E63AB97C788F9EABADDB4A3A803A76CA6DABFDC1E4C8BC51E0D3BFCEC`（67,274 字节，验证方独立测得过同值）。
> - **两个提交之间的差距（务必按两个口径分别读，不要只看单个文件）**：
>   - 全仓 `17047b4 → fcca14d` = **26 files changed, 1458 insertions(+), 206 deletions(-)**
>     （含 Android 磁贴/Manifest、构建脚本、文档等——rc2→rc3 的**版本跨度不止一个补丁**）；
>   - 其中与本次 Windows 端改动相关的 `tcyunlock/src` 部分 = **1 file changed / +50 / −4**，
>     即 `src/Program.cs` 中 `forget` 顺带删除过期导出的 `payload.txt`/`payload.png`
>     （`--json` 增加 `exportsRemoved`），代码注释标注 `since v0.2.0-rc3 / deferred from rc2`。
>   - 该 hunk 由 **`fcca14d`** 引入（其后提交如 `b61df1c` 及当前 HEAD 均包含它），
>     但**不在**上面那份 rc2 二进制里，随**下一次构建（rc3）**生效——
>     "源码比 dist 新"是**有意为之**，不是漏构建。
> - 两份 `src/Program.cs` 的**原始字节**身份（`git show <commit>:<path>` 取原始比特后算 SHA-256，
>   不受 `core.autocrlf` 影响）：rc2 `17047b4` = 67,274 B /
>   `B26E8D7E63AB97C788F9EABADDB4A3A803A76CA6DABFDC1E4C8BC51E0D3BFCEC`；
>   引入 rc3 改动的 `fcca14d` = 69,277 B /
>   `2C49CFC7921CA4430CBCEB444FF9A0BE9AE053D90523939FE73CA7DC7F3B6638`。
>   对应的 git blob（SHA-1）分别是 `ca56295dd3a62c03008200da5cbe14f52824184d` 与
>   `37b66366713fb6d99a80fac9afd7e74f83924001`（后者即 `git rev-parse fcca14d:tcyunlock/src/Program.cs`）。

> **可复现性（实测结论，重要）**
> - **该单文件产物不是"从提交即可位级复现"的**：单文件发布会把**构建路径**相关的数据打进 exe，
>   实测同样源码在不同目录发布会得到不同字节：
>   - 同源码 + 同长度输出目录（`…\Temp\detA` / `…\Temp\detB`）→ **三次构建逐字节相同**（构建本身是确定性的）；
>   - 同源码 + 输出目录名 33 字符 vs 72 字符 → `41,484,005` vs `41,484,008` 字节（不同）；
>   - rc2 源码 + 工作树内的输出目录 → `41,483,513` 字节 / `CFE9E3CF…`；
>     rc2 源码 + 规范输出目录 → `41,483,508` 字节 / `D3595394…`；冻结产物 → `41,483,515` 字节 / `2BC9BB1C…`。
> - **结论**：源码身份用于**审计**（能查到是哪份源码），而**哈希只对"那一个文件"负责**。
>   任何"重新构建得到同样二进制"的假设都不成立；换机器/换目录重建必须视为**新产物**，
>   重新记录哈希并重新验证。
> - 需要完全相同的字节时，唯一可靠做法是**复制那个已验证的文件并校验哈希**（例如
>   安装程序打包前后各算一次 SHA256）。
> - **残留未知项（如实记录，不要用猜测填补）**：最忠实的一次重建（rc2 源码按原始字节、规范
>   项目/输出路径、清空 `obj`/`bin`、完整 `build`→`publish` 序列）得到 `41,483,513` 字节 /
>   `3B05C22E…`，与冻结产物 `41,483,515` 字节 / `2BC9BB1C…` **仍差 2 字节，原因未确定**。
>   已**排除**的原因：源码差异（按原始字节比对一致）、项目/输出路径差异（同为规范路径）、
>   `obj`/`bin` 残留（已清空）、构建序列差异（与 `build.ps1` 相同）、以及
>   **NuGet 运行时包变化**（缓存中只有 `8.0.31`，安装于 14:39，早于 14:50:55 的冻结构建，
>   且此后无任何更新）。剩余差异的可能来源尚未找到。
> - 上述所有实验都在临时目录/worktree 中进行，**没有改动 `dist\`**；实验后已清理临时工作树并恢复 `publish\`。

---

## 2. CLI 命令参考

```
tctool-unlock status [--json] [--no-probe] [--enable-radio]
tctool-unlock pair [--json] [--qr=ascii|unicode|none] [--no-png]
tctool-unlock pair --show [--json]
tctool-unlock pair --payload <json> [--json]
tctool-unlock forget [--json]
tctool-unlock set-password [--clear] [--json]
tctool-unlock run [--duration N] [--json] [--enable-radio] [--quiet]
tctool-unlock autostart status|enable|disable [--json] [--method auto|task|startup|runkey] [--exe <path>]
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
 "hostJson":"C:\\Users\\…\\host.json","advertisingDetail":"advertising (Started)",
 "version":"0.2.0-rc2","appVersion":"0.2.0-rc2","protocol":1}
```

`peerName` 缺失时输出空串（不是 `null`），便于 C++ 侧按字符串解析。
`appVersion`（应用版本，如 `0.2.0-rc2`）与 `protocol`（线协议版本，恒为 `1`）是**两个独立字段**，
版本号升级不会改变协议号；旧字段 `version`/`protocolVersion` 为兼容保留。

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

### 2.4 `forget`

清除配对（删掉 `host.json` 里的 PSK 密文与 peer 信息），保留 `hostId`。
幂等：本来就没配对时也返回成功。

**rc2 已知问题（UX，无安全影响）**：rc2 **不**删除先前导出的 `payload.txt`/`payload.png`，
而其中的 PSK 已作废，所以扫旧码会认证失败。**rc3 起 `forget` 会自动删除这两个导出文件**
（`--json` 增加 `exportsRemoved` / `exportsRemovedCount` 字段），并在输出里说明。
详见 §1.4 的版本状态说明与 §12 故障排查。

### 2.5 `set-password`

交互式录入，**不回显**（`Console.ReadKey(intercept: true)`），要求二次确认。
若 stdin 被重定向则退化为按行读取并**明确警告无法隐藏**。

保存时会用 `VkKeyScanW` 预检每个字符能否在当前键盘布局上键入；不能键入的字符
（中文、emoji、需要 AltGr 的符号等）会**明确警告**，因为解锁时只能整体中止并返回
`unsupported`（见 §6）。`--clear` 清除已保存密码。

### 2.6 `run` / `run --quiet`

前台常驻启动 GATT 服务，直到 Ctrl+C 或 `--duration N` 秒后退出。
启动成功后写运行时心跳 `runtime.json`（`pid`/`advertising`/`peerConnected`/`connections`/`unlocks`），
每 500 ms 刷新；退出时删除。检测到已有存活实例时拒绝启动（退出码 3）。

**`--quiet` 是给自启动用的模式**（详见 §13）：完全没有控制台输出（并主动隐藏自己的控制台窗口），
关键日志写 `%LOCALAPPDATA%\TC-tools\unlock\service.log`，超过 1 MB 时滚动为 `service.log.1`。
启动失败也会写日志（`[fatal]`）并非 0 退出，便于排查"开机没起来"。

### 2.7 `autostart`

自启动开关，**默认关闭**，**用户级**（不需要管理员），`enable`/`disable` 都幂等，
`status` 真实探测系统状态。用法/选型理由/限制见 §13。

### 2.8 `selftest`

**不需要蓝牙**，进程内跑协议 §8 权威向量与安全断言（当前 29 条），失败返回 4。
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
 "advertisingDetail":"advertising (Started)","version":"0.2.0-rc2","appVersion":"0.2.0-rc2","protocol":1}
```

### 9.2 `selftest`（29/29，含 §8 权威向量）

```
K_session      = 21f2a1a3890968e1da28553de67b49cab6ab2ffb7f3ecbaec72d284852b9af47
PROOF          = 353d6fdd6e73a63620389b806711e6c2c726407045d4c7ddae29264a9e8bab10
psk_base64url  = AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8
SEAL plaintext = {"type":"unlock"} (counter=1)
  IV (12B)     = 000000000000000100000000
  ciphertext   = 678def935e80e0eeb8ca5a9ee9bbef97f6
  tag (16B)    = a2c4302b392deeb76b81f496665496c6
  FRAME(45B)   = 000000000000000100000000678def935e80e0eeb8ca5a9ee9bbef97f6a2c4302b392deeb76b81f496665496c6
29/29 checks passed
```

这些期望值是**硬编码在 selftest 里的断言**（来自第三方独立实现
[`tests/unlock/ref-vectors.mjs`](../tests/unlock/ref-vectors.mjs)），不是"按自己的输出反推"。

覆盖：§8 向量逐字节比对、base64url、SEAL/OPEN 往返、多块明文、counter 重放拒绝、
篡改 tag/密文拒绝、非零 IV 填充拒绝、短帧拒绝、错误密钥拒绝、常量时间比较、
UUID 常量、§6/§3.3.2 错误码与 reason 全集、ready 与超长 error 的 180 字节 Notify 预算、
二维码编码与 PNG 生成，以及**从 BLE 传输层解耦出来的四条策略断言**（这样它们不需要手机就能回归）：
`§5.5` 滑动窗口限流边界、`§3.3.1` 只有第 5 次连续 PROOF 失败才失效 PSK、
`§5.4` 注入前判定矩阵（no-password / not-locked / injectWhenUnlocked）、
`§5.3` 不可键入字符必须整体中止（0 次按键）且 ASCII 密码按键数 = 长度+1。
后四条与宿主**共用同一份 `UnlockPolicy` / `InputInjector` 代码**，不是平行实现。

> **口径说明（与 verify 报告一致，请勿升级为"运行时已验证"）**：这四条断言证明的是
> **判定函数本身**正确（§5.5 窗口边界、§3.3.1 第 5 次才失效、§5.4 判定矩阵、§5.3 零按键中止）。
> "真实 GATT 会话中服务器确实调用了这些函数、并在收到手机帧时按此分支"仍属
> **静态审查 + 待真机**——需要一台装了安卓端 App 的手机才能闭环（见 §9.4、§10）。

### 9.3 独立交叉核对

- `node tests/unlock/ref-vectors.mjs --compare dist/selftest-vectors.json` → **6/6 MATCH, allMatch=true, exit 0**
- verify 用另一个宿主直接编译 `src/Protocol.cs` 跑 §8 → **6/6 逐字节 MATCH**
- verify 复核 Host/BLE 部分 → selftest 全过（verify 复核时为 25 条；F1 修复后新增策略断言，当前 29 条），
  静态审查逐条通过
  （见 [`tests/unlock/verify-report.md`](../tests/unlock/verify-report.md)）
- 二维码：`tools/verify-qr.py` —— **由 verify 独立执行并给出 `RESULT: ALL VERIFIED, exit 0`**
  （命令与参考实现由本目录提供，解码器为第三方 OpenCV；本次修复后默认使用自包含
  `dist\tctool-unlock.exe`，**无需 DOTNET_ROOT 即可复现**）：
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
2. 与真实安卓手机/App 的端到端互通（见 §9.4）。此项**同时**覆盖：§3.3.1/§5.3/§5.4/§5.5 的
   **实时接线**——判定函数已被 selftest 覆盖（见 §9.2 口径说明），但"服务器在真实会话里
   按这些分支处理手机帧"只能在有手机时确认。
3. 安全桌面（锁屏）注入与服务化（见 §7）。
4. 长写分片重组：逻辑已实现（按 `Offset` 累积 + 120 ms 收尾判定），但**没有真实手机**
   制造 >MTU 的 Prepare/Execute 长写来实测。
5. `pair --payload` 反向导入（协议标注为可选实现）。
6. **自启动的"计划任务"与"HKCU Run"两条路径未能在本机稳定复现成功**（环境限制，非代码缺陷）：
   - `schtasks /create` 以标准用户身份被系统拒绝（`ERROR: Access is denied.`），
     用 `schtasks.exe` 直接手敲同样报错，与我们的代码无关；
   - `HKCU\…\Run` 的写入在本会话中**间歇性**被拒（`UnauthorizedAccessException`），
     同一数值有时成功有时失败，而 `reg.exe` 手写与 PowerShell 直写同样受影响，
     说明是本机安全策略/安全软件的拦截，而不是 .NET API 用法问题。
   - 因此本机实测走的是**启动文件夹快捷方式**回退路径（见 §13.6），该路径完整验证通过；
     另两条路径的代码已实现，并对失败做了探测与明确报错。
   - **未验证**：真实重启/登录后自启动是否按时拉起（本机不便重启），以及计划任务在
     具备权限的系统上是否成功。

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
│  ├─ UnlockPolicy.cs       限流/失败计数/注入前判定（纯函数，selftest 直接复用）
│  ├─ Autostart.cs          用户级自启动（计划任务/启动文件夹/HKCU Run）+ service.log 滚动
│  ├─ HostStore.cs          host.json / DPAPI / runtime.json 心跳
│  ├─ Ble/GattUnlockServer.cs  GATT Server、会话状态机、帧重组、解锁分派
│  ├─ Win/Native.cs         P/Invoke（SendInput / OpenInputDesktop / VkKeyScanW / 隐藏控制台）
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
| `autostart enable` 报 `schtasks /create failed: Access is denied` | 标准用户在该系统上无权创建计划任务（很常见）。auto 模式会自动退回启动文件夹快捷方式；见 §13。 |
| 开机后没有自动启动 | `tctool-unlock autostart status --json` 看 `enabled`/`method`；再查 `service.log`（启动失败会写 `[fatal]`）。 |
| 扫了 `payload.png` 却认证失败 | **rc2 已知问题（UX，无安全影响）**：`forget` 只清除 PSK，**不会**删除先前导出的 `payload.txt`/`payload.png`，而那个 PSK 已作废，所以扫旧码必然认证失败。重新 `pair` 会覆盖这两个文件，再扫新的即可；在 rc2 上也可以手动删除这两个文件。**rc3 起 `forget` 会自动清理这两个导出文件。** |

---

## 13. 自启动（用户级，默认关闭）

**需求**：电脑端是否自启动由**用户自己设置**，默认关闭，安装/首次运行**绝不**偷偷启用。

### 13.1 用法

```powershell
tctool-unlock autostart status            # 人类可读
tctool-unlock autostart status --json     # 单行 ASCII JSON（供 C++ 前端解析）
tctool-unlock autostart enable            # 启用（幂等）
tctool-unlock autostart disable           # 关闭（未启用时也返回成功）
```

`status --json` 至少包含：

```json
{"enabled":false,"scope":"user","method":"none","taskName":"TC-tools Unlock Service",
 "command":"\"…\\tctool-unlock.exe\" run --quiet",
 "taskExists":false,"taskTargetsUs":false,
 "startupShortcutExists":false,"startupShortcutTargetsUs":false,
 "startupShortcutPath":"C:\\Users\\…\\Startup\\TC-tools Unlock Service.lnk",
 "runKeyExists":false,"runKeyTargetsUs":false,
 "exePath":"…","exeExists":true,"detail":"…","serviceLog":"…",
 "appVersion":"0.2.0-rc2","protocol":1}
```

- `enabled`：**真实探测**的结果（三种机制的任一被确认指向本 exe），不是读配置文件。
- `method`：实际生效的机制 —— `task` / `startup` / `runkey` / `none`。
- `taskTargetsUs` / `startupShortcutTargetsUs` / `runKeyTargetsUs`：是否**指向本可执行文件**
  （.lnk 会被真正打开读回 TargetPath/Arguments；计划任务会读它的 XML 动作）。
  手工改过、或指向别处的条目会被如实标为 `false`。

### 13.2 选型：三种机制 + 自动回退

`enable` 默认 `--method auto`，按顺序尝试，第一个成功即停：

| 顺序 | 机制 | 说明 | 需要管理员？ |
|---|---|---|---|
| 1 | 计划任务（`schtasks /create /sc onlogon /f`） | Task Scheduler 启动进程**不会分配控制台窗口**，且用户可在任务计划程序里看到/管理 | **可能需要**：很多系统上标准用户创建任务会被拒（本机实测 `ERROR: Access is denied`） |
| 2 | **启动文件夹快捷方式**（默认落点） | `%APPDATA%\…\Start Menu\Programs\Startup\TC-tools Unlock Service.lnk`，纯用户目录文件操作；用户在 `shell:startup` 里**看得见**，删掉即关闭 | 否 |
| 3 | `HKCU\…\CurrentVersion\Run` | 经典用户级自启动；但部分安全软件/策略会拦截该键写入（本机实测**间歇性** `UnauthorizedAccessException`） | 否 |

`--method task|startup|runkey` 可强制指定单一机制（不做回退）。`disable` 会**同时**清理三种机制，
因此无论当初用哪种方式启用、或用户手工加过别的条目，都能一次关干净。

> 为什么不是只做计划任务：任务是"最干净"的方案（无窗口），但标准用户常常创建不了；
> 用户要求"自启动由用户设置、不需要管理员"，所以必须有一个总能成功的用户级落点。
> 启动文件夹快捷方式是三者中**最可靠且对用户可见**的，因此作为默认落点。

### 13.3 用户级限制（重要）

- **登录后**才启动（`onlogon` / 启动文件夹都是登录时机），因此**只能注入到用户会话**——
  这正好符合 SendInput 的需求；但它**不能**在无人登录时解锁锁屏（那需要 SYSTEM 服务，
  见 §7）。
- 不请求提权、不写 HKLM、不创建服务，**不需要管理员**；卸载/关闭只需 `autostart disable`。
- 快捷方式设了"最小化"窗口样式，并且 `run --quiet` 会立即隐藏自己的控制台窗口，
  所以正常看不到窗口（计划任务方式则完全没有窗口）。

### 13.4 如何确认已启用

1. `tctool-unlock autostart status` → `enabled : True`，`mechanism` 为实际机制；
2. **启动文件夹方式（本机的实际生效路径）**：`Win+R` 输入 `shell:startup`，应看到
   `TC-tools Unlock Service.lnk`；右键"属性"能看到目标与参数。
   注意：**任务管理器 →「启动应用」标签页里也会列出它**（因为启动文件夹与 HKCU Run
   都属于"启动应用"，而计划任务不会显示在这里）；
3. 计划任务方式：任务计划程序里应有名为 `TC-tools Unlock Service` 的任务；
4. 实际效果：注销/重启后用 `tctool-unlock status --json` 看 `running` 与 `advertising`
   是否为 `true`（自启动实例会写 `runtime.json` 心跳），或查看
   `%LOCALAPPDATA%\TC-tools\unlock\service.log` 的 `[start]` 行。

### 13.5 如何关闭

```powershell
tctool-unlock autostart disable
```

幂等：没有启用时也返回成功（退出码 0）。它会删除启动文件夹快捷方式、删除计划任务、
删除 HKCU Run 值，并再次探测确认 `enabled=false`。

**不用命令行也能关**（给最终用户看的，C++ 端菜单也指向本节）：

- **推荐**：右键任务栏 → **任务管理器**（或 `Ctrl+Shift+Esc`）→ **「启动应用」**标签页 →
  找到 `TC-tools Unlock Service`（或 `TC-tools Unlock`）→ 右键 **禁用**。
  这里同时覆盖"启动文件夹快捷方式"与"HKCU Run 值"两种机制；
- 或者：`Win+R` → `shell:startup` → 删除 `TC-tools Unlock Service.lnk`；
- 计划任务方式：任务计划程序 → 任务计划程序库 → 删除 `TC-tools Unlock Service`。

无论用哪种方式关闭，`tctool-unlock autostart status` 都会**如实反映**（它是探测系统状态，
不读自己的配置）。

### 13.6 本机实测（Windows 11 26200，标准用户，非管理员）

```
[1] status（启用前）      -> enabled:false, method:"none"
[2] enable（auto）        -> enabled:true,  method:"startup"
    detail: "task: schtasks /create failed (exit 1): ERROR: Access is denied.;
             fell back to the Startup folder: startup shortcut '…\Startup\TC-tools Unlock Service.lnk'
             created (user scope, no elevation): \"…\tctool-unlock.exe\" run --quiet"
[3] enable 再来一次        -> exit 0，Startup 里仍只有 1 个快捷方式（幂等）
[4] 独立读回 .lnk（WScript.Shell，非本程序代码）
    TargetPath : C:\…\tctool-unlock.exe
    Arguments  : run --quiet
    WindowStyle: 7
[5] 手工执行该命令行（run --quiet --duration 6）
    -> 控制台输出 0 字节；service.log 记录 advertising=True 与三条 [start] 行、[stop] 行
[6] 滚动测试：预填 1,100,030 字节 -> 运行后 service.log=848B，service.log.1=1,100,030B
[7] disable               -> enabled:false, "startup shortcut deleted"，exit 0
[9] disable 再来一次       -> exit 0（幂等）
```

> 说明：本机（含本会话的沙箱）**拒绝非管理员创建计划任务**，且对 `HKCU Run` 的写入
> **间歇性拒绝**（同样的值有时成功有时 `UnauthorizedAccessException`），因此上表走的是
> 启动文件夹回退路径——这恰好验证了自动回退的价值。计划任务与 Run 键两条路径的代码
> 均已实现并做了探测/错误处理，但在本机**无法稳定复现成功**，属于环境限制（详见 §10）。
