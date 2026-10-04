# TC-tools v0.2.0-rc2 发布说明

> 本版本新增 **手机指纹蓝牙解锁电脑**：手机端 App「TC-Tools 解锁电脑」验证指纹后，通过蓝牙让电脑自动输入密码解锁。
> 同时提供 Windows 端用户可设置的自启动。

---

## 一、新增功能

### 1. 手机指纹蓝牙解锁（两端）

| 端 | 内容 |
|---|---|
| **手机端（Android 8.0+）** | 应用名「TC-Tools 解锁电脑」，Kotlin + Jetpack Compose，**Apple 风格 UI**；指纹验证（BiometricPrompt，支持设备凭据回退）；BLE Central；扫码/手动粘贴配对；**控制中心快捷磁贴**一键解锁（含 realme/OPPO/小米等厂商后台适配） |
| **电脑端（并入 TC-tools）** | 首页新增 **4. 解锁电脑（蓝牙）**；实际执行由 `unlock\tctool-unlock.exe`（.NET 8，自包含单文件）完成：BLE GATT Server、加密握手、DPAPI 密码保险箱、SendInput 注入、二维码配对、离线自测 |

**使用流程**

1. 电脑：`4 解锁电脑（蓝牙）` → `2 设置电脑解锁密码`（当前登录密码，DPAPI 加密保存）；
2. 电脑：`1 配对手机` → 生成二维码（含一次性密钥）；
3. 手机：扫码配对（或粘贴配对文本）；
4. 手机：点主按钮或控制中心磁贴 → **指纹验证通过** → 蓝牙发加密指令 → 电脑自动输密码解锁。

### 2. 电脑端自启动（用户可设置，默认关闭）

```bat
tctool-unlock autostart status     :: 查看（真实探测系统状态）
tctool-unlock autostart enable     :: 开启（用户级，无需管理员）
tctool-unlock autostart disable    :: 关闭（三种机制一次清理）
```

- 默认**关闭**，不会偷偷自启；用户可在 TC-tools 里开关；
- 用户级实现：优先计划任务 → 回退启动文件夹快捷方式 → HKCU Run（`--method` 可强制）；
- `run --quiet` 无控制台窗口，日志写 `%LOCALAPPDATA%\TC-tools\unlock\service.log`（1 MB 滚动）。

---

## 二、安全设计（要点）

- **密码不出电脑**：PC 密码用 Windows DPAPI 加密保存在本机，**任何情况下都不通过蓝牙或网络传输**；
- **配对密钥只走二维码**：PSK（32 字节随机）经二维码/配对文本传递，不经蓝牙明文传输；
- **一次性会话**：每次连接重新生成 32 字节随机挑战值；会话密钥 `HMAC-SHA256(PSK, NONCE‖"TCUNLOCK-SESSION-V1")`；
- **挑战-应答认证**：`PROOF = HMAC-SHA256(PSK, NONCE‖"TCUNLOCK-PROOF-V1"‖HOST_ID‖PEER_ID)`，常量时间比较；
- **语音指令全部加密**：AES-256-GCM（IV = 计数器大端 8 字节 + 4 字节 0x00），**单调计数器防重放**；
- **防暴力破解**：连续 5 次验证失败 → **立即作废配对密钥**，需重新配对；
- **指纹前置**：PROOF 只在指纹验证成功的回调里计算并发送（代码层已核对唯一调用点）。

> 局限（如实说明）：蓝牙采用**应用层加密**，未启用系统级 BLE 配对，因此不防"信号中继"式攻击；
> 请勿在人员复杂的公共场所长期开启服务。

**关于锁屏注入**：Windows **安全桌面**不接受普通用户进程的模拟输入。
若密码框出现在安全桌面，需要把 `tctool-unlock.exe` 以 **SYSTEM** 身份常驻（见 `tcyunlock/README.md`）；
普通"屏幕保护式锁定"可正常注入。

---

## 三、版本与兼容性

- 版本号：**v0.2.0-rc2**（C++ 端 / Node.js 版 / 手机端 / 电脑端解锁组件统一）
- 协议版本：`protocol = 1`（与 v0.1.0-rc* 的解锁协议一致，未破坏兼容）
- 系统要求：Windows 10 **1709（build 16299）** 及以上；手机端 Android 8.0+
- 电脑端解锁组件为 **self-contained**，目标机**无需预装 .NET 8 运行时**

---

## 四、产物清单

| 产物 | 说明 |
|---|---|
| `dist\TCtools-installer-0.2.0-rc2.exe` | Windows 安装程序（NSIS，中英双语；含解锁组件到 `unlock\` 子目录） |
| `dist\tctool.exe` | C++ 控制台主程序（静态单文件，可独立使用） |
| `dist\android\TC-Tools-Unlock-0.2.0-rc2.apk` | 手机端安装包 |
| `tcyunlock\dist\tctool-unlock.exe` | 电脑端蓝牙解锁组件（自包含） |
| npm `@turtlelnc/tc-tools@0.2.0-rc2` | Node.js 版（`npm install -g @turtlelnc/tc-tools`） |

> 大文件（安装程序/APK/exe）不进 Git，通过 **GitHub Release** 分发。

---

## 五、验证情况（诚实口径）

**已验证（有可复现证据）**

- 协议正确性：**5 条独立实现**（node:crypto、手工 HMAC+AES+GHASH、CPython、OpenSSL CLI、.NET CNG）算出的
  `K_session / PROOF / SEAL 帧` **逐字节一致**；两端**双向互通**（互相解密对方的帧）通过；
- 两端真实构建：C++ 用 MinGW g++ 编译通过并实跑；电脑端组件 `selftest` **29/29 通过**；Android
  `assembleDebug` 成功、单元测试 8/8、自测 24/24；
- Android APK 在 **真实 Android 14 运行时（模拟器）** 安装成功、App 正常启动、**协议自测 24/24 通过**；
- 电脑端 BLE **GATT 服务真实广播成功**（`AdvertisementStatusChanged -> Started`）；
- 自启动三次状态实测（默认关 → 开启 → 关闭，且用独立工具读回快捷方式验证）；
- 二维码经 **OpenCV 独立解码 5/5** 逐字节还原（含中文电脑名）。

**未验证（本机条件所限，如实列出）**

1. **真机蓝牙空口链路**：单适配器无法自连，模拟器蓝牙非真实射频 —— 需要一台真手机 + 一台电脑实测；
2. **真实生物识别硬件**：模拟器 `adb emu finger touch` 不等于真机指纹传感器路径；
3. **真实锁屏 / 安全桌面的注入**：需要真的锁屏并注入，会打断当前会话且涉及真实密码，未在本机执行；
4. **Windows 10 1709 运行时**：本机为 Windows 11 26200，仅确认 API 与目标框架兼容（未用 19041+ 独有 API）；
5. **真实重启后自启动是否按时拉起**；计划任务与 HKCU Run 两条路径在本机因权限/策略被拒，未能稳定复现。

**建议的真机验收清单**：见 `docs/UNLOCK-VERIFY.md`。

---

## 六、已知问题与说明

- 未做代码签名，Windows SmartScreen 可能提示"未知发布者"（计划申请开源代码签名证书）；
- 电脑端首次运行需保持蓝牙**已打开**（组件会检测电台状态并在关闭时给出明确提示）；
- 手机端在没有系统"后台弹出界面"权限的机型上，磁贴点击会先拉起 App 再弹指纹（realme/OPPO/小米等，见 `android/README.md`）；
- **`forget` 不会清理已导出的二维码文件**（rc2 行为）：`forget` 之后残留的 `payload.txt` / `payload.png` 里是**已作废的 PSK**，
  用户若再扫旧码只会认证失败一次（**无安全影响**：`host.json` 中已无可用密钥材料，无 PSK 时认证必然失败）。
  **rc3 起 `forget` 会自动删除这两个文件**（源码已就绪，随下次构建生效）。
- 自启动的三种机制中，**本机实际生效的是"启动文件夹快捷方式"**（计划任务被 `Access is denied.` 拒绝、
  `HKCU\...\Run` 在本机会话被安全策略间歇性拦截，二者均已实现但本机无法稳定复现成功）。
  关闭方式：任务栏右键 → 任务管理器 → **启动应用** → `TC-tools Unlock Service` → 禁用；
  或 `Win+R` → `shell:startup` → 删除 `TC-tools Unlock Service.lnk`。
  详见 `tcyunlock/README.md` §13。

## 七、产物 ↔ 源码对应关系

见 [`docs/BUILD-MANIFEST-v0.2.0-rc2.md`](BUILD-MANIFEST-v0.2.0-rc2.md)：

- **commit 标识"源码身份"（审计用）**，**SHA-256 标识"被交付的那个文件"**（验证以它为准）；
- **不要用"从同一提交重建"来证明与已验证产物相同** —— 实测发现本项目的自包含单文件产物**对构建环境敏感**：
  同一台机、同一 SDK、同源码，仅改变**输出目录名长度**就会让产物相差几个字节；
  且本仓库 `core.autocrlf=true` 会使 `git checkout` 落成 CRLF，而冻结产物由 **LF** 源码构建。
  **重新构建应视为新产物，须重新记录哈希并重新验证功能。**
- 另显式记录：`tcyunlock/src/Program.cs` 在 HEAD 上含一项 rc3 改动
  （`forget` 清理过期导出物），但**二进制故意未重建**（rc2 冻结哈希因此有效）。
