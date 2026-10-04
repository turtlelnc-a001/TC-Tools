# TC-tools 蓝牙解锁 — 验收总结（UNLOCK-VERIFY）

- 对应协议：`docs/UNLOCK-PROTOCOL.md` v1.0（冻结，含 §3.3.1 IDENT）
- 验证日期：2026-10-04 ｜ 验证人：`verify`（独立于两端作者）
- 详细技术报告（含每条要求的命令与原始输出）：`tests/unlock/verify-report.md`
- **状态：本轮全部复核已闭环；终局结论待 v0.2.0-rc2 冻结后在同一哈希上复跑（见文末）**

---

## 一句话结论

**密码学、帧格式、跨端互通这三件"最容易两端对不上"的事，已用 5 条独立实现证明是对的，
两端能互相解密对方的帧，两端的代码都能编译运行，Windows 交付物无需预装 .NET 即可运行，APK 也真的打包出来了；
但整个系统仍然没有在真机蓝牙空口上跑过一次，也从未在真实锁屏界面上注入过按键。**
因此当前状态是：**算法层与构建交付可放心，端到端链路待真机验收。**

> ⚠️ **交付物版本提醒**：本页 APK 数字对应 **0.1.0**，它**不是最终交付** ——
> 用户新增需求后 Android 端正在出 **0.2.0-rc2**（控制中心磁贴 + realme 适配），APK 会再变一次。
> **请勿以 0.1.0 作为终局验收依据**；冻结后需重做 APK 相关全部核验。

---

## ✅ 已经验证通过的（可复现，有原始输出）

| 项目 | 结果 | 怎么证的 |
|---|---|---|
| 协议 §8 权威向量（K_session / PROOF） | ✅ 逐字节一致 | 用 **5 条互不相同的实现**（Node、我手写的 HMAC+GCM、Python、OpenSSL 命令行、.NET）分别算，全部相同 |
| `base64url(PSK)` 无填充 | ✅ 43 字符，无 `=` | 两端产品代码实测一致 |
| SEAL 帧格式 `IV(12)‖密文‖tag(16)` | ✅ 45 字节整帧逐字节一致 | 两端产品代码实测一致 |
| `IV = counter(8B 大端)‖4B 0x00` | ✅ | 两端产品代码实测一致 |
| 加密完整性（改 1 bit 就拒绝） | ✅ 篡改密文/标签/换错密钥均返回 `decrypt-failed` | 两端自测实测 |
| **两端能互相解密对方生成的帧** | ✅ 双向通过（含 counter=2/3 与反例） | 我写了一个跨端互通测试，两端各自独立派生密钥，不交换密钥 |
| Windows 宿主能编译 | ✅ 0 错误 0 警告 | `dotnet build tcyunlock` |
| Windows 宿主自带自测 | ✅ 24/24 通过 | 运行真实 `tctool-unlock` 程序 |
| Android 协议核心能编译运行 | ✅ 26/26 断言通过 | 用独立 Kotlin 编译器直接编译产品源码并运行 |
| GATT 三个 UUID / 特征属性 | ✅ 与协议字面一致（Char A = Read+Notify，Char B = Write） | 源码逐行核对 |
| 指纹通过前不生成 PROOF | ✅ 代码结构保证：全项目只有一处调用，且位于指纹成功回调内 | 源码核对 |
| 重放与 counter 单调 | ✅ 密码学层与组装器层实测；会话层代码审查通过 | 两端自测 + 源码 |
| 限流 1500ms / 未锁屏不注入 / 不支持字符整体中止 / 连续 5 次失败失效 PSK | ⚠️ **代码审查通过，但没跑起来过**（见下） | 源码：`GattUnlockServer.cs:549-582`、`:455-472` |

验证过程中我发现的 3 个缺陷已由两端作者修复，并被我**复测确认**：Android 的 `ct‖tag` 最小长度误判、Android 未校验 IV 填充字节、BLE 特征多暴露了 `WriteWithoutResponse`。

---

## ❌ 尚未验证的（以及为什么）

### 1. 真机蓝牙链路 —— 完全没验证过
广播、扫描、连接、`requestMtu(517)` 的实际协商、超过 MTU 的长写与 Notify 分片在真实空口上的表现、断线重连——**全部只有源码审查，没有一次真实通信**。

> 原因：本机没有 Android 手机接入（未安装 `adb`，无 USB 设备）。
> 本机**确实有**可用的蓝牙适配器（Intel 无线 Bluetooth），所以电脑这边发布 GATT 服务是可测的；
> 但**没有对端手机，链路就无法闭环**。

### 2. 真实锁屏 / 安全桌面注入 —— 完全没验证过
`SendInput` 到底能不能在 Windows 的登录/锁屏安全桌面上输入、Unicode 扫描码+Shift 对真实密码框是否逐字符正确、回车是否真的提交——都没试过。

> 原因：要验证就必须**真的把电脑锁屏**并在安全桌面上注入按键。这会真实改变交互式桌面状态
> （可能把当前会话踢到锁屏，影响正在进行的其他工作），失败时还可能让会话不可用——无法用非破坏性的自动化方式验证。
> 另外，注入用的密码应由你配置，验证者不应接触真实密码。

### 3. Windows 10 1709 运行 —— 未验证
本机是 Windows 11（10.0.26200）。已确认项目声明支持 1709（`TargetPlatformMinVersion=10.0.16299.0`）且编译通过，
但**编译期兼容不等于运行期兼容**，低版本上的 WinRT/GATT API 行为未实测。

### 4. 真实指纹流程 —— 未验证
代码结构已确认"指纹成功后才计算 PROOF"，但"指纹 → PROOF → 解锁"的完整链路没有在真机上跑通过。

### 5. 状态机运行时行为 —— 只有代码审查
1500ms 限流、未锁屏不注入、不支持字符整体中止、**连续 5 次 PROOF 失败失效 PSK** 这四条，
我逐行确认了实现与协议一致，但**没有运行过**：这些逻辑在 `GattUnlockServer` 内部，
入口是 `private` 且依赖真实蓝牙连接对象，没有手机就构造不出真实会话。

> 架构建议（非缺陷）：把状态机与蓝牙传输层解耦出一个可注入的接口，这些规则就能在无手机时用单元测试覆盖。

### 6. Android APK 与应用界面 —— ✅ 已消除（编译 + 打包均已验证）
- **编译**：整个 app 模块编译通过（110 个 `.class`，含蓝牙层 20 个、Compose UI 40 个），且 class 时间戳晚于源码，产物与源码同步。
- **打包**：`dist\android\TC-Tools-Unlock-0.1.0.apk` 已构建，我独立复核了 size `11,556,096 B`、SHA256 `71C4DF5E…A71769`（与 mobile 声明逐字符一致）、480 个 zip 条目、8 个 `classes*.dex`、native libs 覆盖 `arm64-v8a/armeabi-v7a/x86/x86_64`（**x86_64 存在 → 模拟器可装**）。
- **自测**：`dist\android\vectors-android.json`（由真实 `gradle :app:selfTest` 生成）与我冻结的权威向量 **6/6 逐字节一致**。

> ⚠️ **版本漂移提醒**：APK 在本轮被重建过一次（旧 `11,458,389 B / C66734F8…` → 新 `11,556,096 B / 71C4DF5E…`）。
> 上面的数字对应**当前版本**；若你手上的 APK 哈希不同，说明它又被改过，本项结论需重跑。

仍未验证的（不属"编译"范畴）：APK 在设备/模拟器上的**安装与启动**、`aapt2` 元数据、运行时 UI 行为；以及
- **模拟器的蓝牙不是真实空口**，且 `adb emu finger touch` 不是真实生物识别硬件路径 → 模拟器跑通 ≠ 真机端到端验收通过。

---

## 需要你（项目负责人）决策/知晓的事项 —— 全部已闭环

协议已因新增 **§3.3.1 IDENT 帧**而**再次冻结**（原 3.3.1/3.3.2 顺延为 3.3.2/3.3.3）。逐条处置结果：

1. ~~**Notify 180 字节上限未强制**~~ → ✅ **已修复并复测**。宿主新增 `NotifyBudget`：把明文夹取到 `180-12-16=152` 字节，并在发送出口硬断言，超长直接**拒发**并记 error。
   我实测：宿主自测 **25/25 PASS**，含新断言 `§3.2: an over-long error message is clamped so the sealed notify frame stays <= 180 bytes (179 bytes)`。**静默越界已消除。**
2. ~~**安装包运行时依赖**~~ → ✅ **已修复并实测通过**。交付 self-contained 单文件 `tcyunlock\dist\tctool-unlock.exe`（41,472,515 B，SHA256 `816C91AA…`）。
   我在 **`DOTNET_ROOT` 清空、PATH 上无 dotnet** 的条件下**直接运行**它：`selftest` → **25/25 PASS, exit 0**，`version` → exit 0。
   **目标机无需预装 .NET 8 运行时**。该项关闭。
3. ~~**协议新增未登记的 IDENT 帧**~~ → ✅ **已解决**：登记为冻结规范 §3.3.1。我已复核两端实现均符合（Android 先写 IDENT 并等 `onCharacteristicWrite` 回调才读 challenge；Windows 校验 36B 小写 UUID 并优先使用本次连接的 IDENT）。
4. ~~**限流命中时会顺延计时**~~ → ✅ **已声明**：`tcyunlock/README.md` **§6.1「限流是滑动窗口（重要语义声明）」** 已写明，我已复核原文。
5. ~~**宿主 PROOF 候选多了一档（低危偏差）**~~ → ✅ **已修复并复测**：候选now 恰为规范允许的两档 `[本次连接 IDENT]` / `[""]`，存储的 `peerId` 已退出 PROOF 校验（仅用于显示），不会再出现"旧值放行、掩盖 PEER_ID 不一致"。
6. **mobile 自查发现并修复了一处真实缺陷（值得记录）**：新增 IV 填充校验后，分片重组器会把"长度前缀"误当 IV、误判 `bad-frame` 并丢弃缓冲（单测当场抓住）。已改为等待后续分片，歧义只由 GCM tag 裁决。我在新版本上复跑自测与双向互通均通过。

---

## 怎么自己复现（约 60 秒）

```powershell
cd TC-tools

# 权威向量自检
node tests\unlock\ref-vectors.mjs --selftest

# Windows 端产品代码实测
toolchain\dotnet8\dotnet.exe run --project tests\unlock\winproto-harness
node tests\unlock\ref-vectors.mjs --compare tests\unlock\evidence\win-proto-raw.json

# Android 端产品代码实测（不需要 Android SDK）
powershell -ExecutionPolicy Bypass -File tests\unlock\run-android-selftest.ps1

# 双向互通
powershell -ExecutionPolicy Bypass -File tests\unlock\run-interop.ps1

# 真实宿主自测
toolchain\dotnet8\dotnet.exe build tcyunlock
toolchain\dotnet8\dotnet.exe tcyunlock\bin\Debug\net8.0-windows10.0.19041.0\tctool-unlock.dll selftest

# self-contained 交付物：清空 DOTNET_ROOT 后**直接**运行（F3）
$env:DOTNET_ROOT=''
$env:PATH = (($env:PATH -split ';') | Where-Object { $_ -notmatch 'dotnet' }) -join ';'
.\tcyunlock\dist\tctool-unlock.exe selftest      # 期望 25/25 checks passed, exit 0
```

---

## 最终验收口径（三段式）

**✅ 已验证**（有可复现命令与原始输出）
- 协议字节级：K_session / PROOF / SEAL 帧由 **5 条独立实现**算出且逐字节一致；两端在各自正式构建链下产出的自测结果与权威值 **6/6 MATCH**。
- 两端互通：Windows 能解 Android 生成的帧、Android 能解 Windows 生成的帧（含 counter 与反例拒绝）。
- 两端可编译可运行：宿主 `dotnet build` 0 警告 0 错误；app 模块 110 个 class（含蓝牙层与 Compose UI）；Android 协议核心独立编译运行通过。
- 宿主交付物**自包含**：清空 `DOTNET_ROOT`、PATH 无 dotnet 时直接运行 `selftest` → **25/25 PASS, exit 0**。
- APK 可构建：size/SHA256/zip 结构/8 个 dex/4 种 ABI 均已独立复核；`adb install` 成功与启动情况由项目负责人用模拟器验证（**绑定 0.1.0**）。

**❌ 未验证**（环境固有限制，非缺陷）
1. **真机 BLE 空口链路** —— 无 Android 设备接入；本机适配器存在、宿主可发布广播，但同机自连返回 `null`，链路无法闭环。
2. **真实生物识别硬件** —— `adb emu finger touch` 只模拟指纹事件，不是真机指纹路径。
3. **真实锁屏安全桌面注入** —— 需真的锁屏并在 Winlogon 安全桌面注入（会破坏交互式会话、涉及真实密码）。
4. **Windows 10 1709 运行时** —— 本机为 Win11 26200；仅确认编译期兼容。
5. **宿主侧状态机运行时行为**（连续 5 次失败失效 PSK / 1500ms 限流 / 未锁屏不注入 / 不支持字符整体中止）—— 仅静态审查，入口 `private` 且依赖真实 GATT 会话，无手机无法驱动。
6. **二维码图像编解码** —— 由 winhost 自证（OpenCV 解码 5/5），**verify 未独立复核**。

**⚠️ 版本漂移风险**
- 本轮验证期间代码**至少变动 4 次**，APK 更是在我核验后被**重建过一次**（`11,458,389/C66734F8…` → `11,556,096/71C4DF5E…`）。
- 因此**每一条"通过"都必须绑定具体哈希**；哈希一变，结论即失效需重跑。
- **当前 APK（0.1.0）不是最终交付**：Android 端正在出 **0.2.0-rc2**（控制中心磁贴 + realme 适配）。
- **终局验收必须在 rc2 冻结后、在同一哈希上全量复跑 §7 命令**，并列出所有参与哈希（清单见 `tests/unlock/verify-report.md` §11.3）。

---

## 建议的真机验收清单（本机无法完成，需你配合）

1. 在 Android 手机上安装 APK（**最终 rc2 版**），与电脑配对（扫码或粘贴配对文本）。
2. 亮屏 → 打开 App → 指纹 → 确认电脑**真实解锁**。
3. 未锁屏时按解锁，确认返回 `not-locked` 且**没有**把密码打进当前窗口。
4. 故意输错配对密钥 5 次，确认 PSK 被作废、要求重新配对。
5. 1 秒内连按两次解锁，确认第二次返回 `throttled`。
6. 在 Windows 10 1709 机器上跑一次宿主，确认 GATT 服务能发布。

以上 6 条通过后，本功能才可判定为端到端验收通过。
