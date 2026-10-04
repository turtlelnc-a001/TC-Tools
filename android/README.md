# TC-Tools 解锁电脑 · Android 端交付说明

> 实现依据：`TC-tools/docs/UNLOCK-PROTOCOL.md`（v1.0，冻结）。本端**不偏离字节级定义**。
> 技术栈：Kotlin 2.0.21 + Jetpack Compose（Material3，无 XML 布局）+ AGP 8.5.2 + Gradle 8.14 + JDK 17。
> minSdk 26 / targetSdk 34 / compileSdk 34。

---

## 1. 交付物

| 项目 | 值 |
|---|---|
| APK | `TC-tools\dist\android\TC-Tools-Unlock-0.1.0.apk` |
| 绝对路径 | `C:\Users\吴桥生\Videos\DSHworkarea\TC-tools\dist\android\TC-Tools-Unlock-0.1.0.apk` |
| 大小 | **11,556,096 字节（11.02 MB）** |
| SHA-256 | `71C4DF5E44699186356488179F789422EB024999B9D1B4498F1B00D4ABA71769` |
| 包名 / 版本 | `com.tctools.unlock` / versionCode 1 · versionName 0.1.0 |
| 应用名 | **TC-Tools 解锁电脑**（`aapt2 dump badging` → `application-label:'TC-Tools 解锁电脑'`） |
| 原始产物 | `TC-tools\android\app\build\outputs\apk\debug\app-debug.apk` |
| 回环自测 JSON | `TC-tools\dist\android\vectors-android.json`（同时留在 `TC-tools\android\app\build\selftest\vectors-android.json`） |

### 安装

```powershell
adb install -r "C:\Users\吴桥生\Videos\DSHworkarea\TC-tools\dist\android\TC-Tools-Unlock-0.1.0.apk"
```

支持的 ABI：`arm64-v8a` / `armeabi-v7a` / `x86` / `x86_64`（模拟器 x86_64 可直接安装）。

---

## 2. 构建与复现

本机原本没有 JDK / Android SDK / Gradle / Android Studio，且用户名与工作区路径含中文。
为规避 AGP/aapt2 对非 ASCII 路径的敏感性，**工具链全部安装在 ASCII 路径**：

| 组件 | 路径 |
|---|---|
| JDK 17 | `C:\tctoolchain\jdk17`（Microsoft OpenJDK 17.0.13+11） |
| Gradle 8.14 | `C:\tctoolchain\gradle-8.14\bin\gradle.bat` |
| Android SDK | `C:\tctoolchain\android-sdk`（platform-tools / platforms;android-34 / build-tools;34.0.0） |
| Gradle 缓存 | `C:\tctoolchain\gradle-home` |
| `local.properties` | `sdk.dir=C:/tctoolchain/android-sdk` |

> 不使用 Gradle Wrapper：本机 `services.gradle.org` 不可达；`dl.google.com` 与 `repo.maven.apache.org` 可达。

一键构建脚本（推荐）：

```powershell
powershell -ExecutionPolicy Bypass -File TC-tools\android\build-apk.ps1            # assembleDebug + 复制到 dist
powershell -ExecutionPolicy Bypass -File TC-tools\android\build-apk.ps1 -UnitTest  # :app:testDebugUnitTest
powershell -ExecutionPolicy Bypass -File TC-tools\android\build-apk.ps1 -SelfTest  # :app:selfTest（回环自测）
```

手工等价命令：

```powershell
$env:JAVA_HOME='C:\tctoolchain\jdk17'
$env:ANDROID_HOME='C:\tctoolchain\android-sdk'; $env:ANDROID_SDK_ROOT=$env:ANDROID_HOME
$env:GRADLE_USER_HOME='C:\tctoolchain\gradle-home'
& 'C:\tctoolchain\gradle-8.14\bin\gradle.bat' --no-daemon -p TC-tools\android :app:assembleDebug
```

### 非 ASCII 路径处理（重要）

AGP 在 Windows 上默认**硬性拒绝**含中文的项目路径：

```
Your project path contains non-ASCII characters. ... This warning can be disabled by adding
the line 'android.overridePathCheck=true' to gradle.properties
```

已在 `gradle.properties` 中加入 `android.overridePathCheck=true`。加放行后，
`mergeDebugResources` / `processDebugResources` / `processDebugMainManifest`（真正调用 aapt2 的 task）
**全部成功**，说明本机 aapt2 处理中文路径没有问题，因此无需镜像/junction，直接在项目原地构建。

### 实际构建日志（关键行）

```
> Task :app:compileDebugKotlin
> Task :app:testDebugUnitTest
> Task :app:selfTest
> Task :app:packageDebug
> Task :app:assembleDebug
BUILD SUCCESSFUL in 52s
45 actionable tasks: 9 executed, 36 up-to-date
```
测试统计（`app/build/test-results/testDebugUnitTest/*.xml`）：`tests=8 failures=0 errors=0 skipped=0`。

---

## 3. 代码结构

```
TC-tools\android\
├─ build-apk.ps1                 一键构建/测试/自测脚本
├─ settings.gradle.kts           仓库与插件解析（google + mavenCentral）
├─ build.gradle.kts              AGP 8.5.2 / Kotlin 2.0.21 / Compose 插件 2.0.21
├─ gradle.properties             含 android.overridePathCheck=true
├─ local.properties              sdk.dir=C:/tctoolchain/android-sdk
└─ app\
   ├─ build.gradle.kts           Compose BOM 2024.09.00、biometric 1.1.0、security-crypto 1.1.0-alpha06、zxing-android-embedded 4.3.0、selfTest 任务
   └─ src\
      ├─ main\java\com\tctools\unlock\
      │  ├─ protocol\TcProtocol.kt            ★ 协议核心（纯 JVM：HMAC / AES-256-GCM / base64url / 计数器 IV / SEAL·OPEN）
      │  ├─ protocol\SealedFrameAssembler.kt  ★ Notify 分片重组 + 重放防护（纯 JVM）
      │  ├─ protocol\Provisioning.kt          配对二维码载荷解析/序列化（纯 JVM）
      │  ├─ protocol\MiniJson.kt              极简 JSON（纯 JVM，避免 org.json 桩）
      │  ├─ protocol\LoopbackSelfTest.kt      §8 回环自测（纯 JVM）
      │  ├─ protocol\SelfTestMain.kt          JVM main 入口（:app:selfTest 直接跑）
      │  ├─ ble\BleCentral.kt                 GATT Central：扫描/连接/MTU/CCCD/IDENT/PROOF/SEALED/Notify
      │  ├─ ble\BlePermissions.kt             Android 12 前后 BLE 权限
      │  ├─ data\PairingStore.kt              PSK 存储（EncryptedSharedPreferences + Keystore 后备）
      │  ├─ data\AppPrefs.kt                  非机密界面偏好
      │  ├─ UnlockViewModel.kt                解锁状态机（指纹通过后才算 PROOF）
      │  ├─ MainActivity.kt                   Activity + 导航 + 权限 + BiometricPrompt + debug 自动化入口
      │  └─ ui\                               Theme / Components / HomeScreen / PairScreen / SettingsScreen(含 About)
      ├─ test\java\...\LoopbackSelfTestTest.kt  §8 向量 JUnit 测试（8 个用例）
      └─ main\res\                             iOS 风格色板（浅/深）、自适应图标、主题
```

---

## 4. 协议实现要点（逐条对照规范）

| 规范 | 实现 |
|---|---|
| §2 `K_session = HMAC-SHA256(PSK, NONCE \|\| "TCUNLOCK-SESSION-V1")` | `TcProtocol.deriveSessionKey()`，拼接顺序固定，ASCII 原始字节、无 `\0` |
| §2 `PROOF = HMAC-SHA256(PSK, NONCE \|\| "TCUNLOCK-PROOF-V1" \|\| HOST_ID \|\| PEER_ID)` | `TcProtocol.computeProof()`，**唯一调用点**是 `UnlockViewModel.onBiometricSuccess()` |
| §2.1 SEAL 线格式 `IV(12) ‖ ct ‖ tag(16)` | `TcProtocol.seal()`；`IV = counter(8B big-endian) ‖ 4B 0x00` |
| §2.1 counter 严格递增 | `SealedFrameAssembler` 拒绝 `counter <= lastCounter`（判为攻击/重放） |
| §2.2 SESSION_ID | `TcProtocol.sessionIdFromReady()`：`OPEN(K_session, 1, ready帧)` 取 `sid`，UI 显示 |
| §3.1 UUID | `SERVICE_UUID = 7a1c9e40-…`、Challenge `…41`、Command `…42`（常量固定） |
| §3.2 扫描按 Service UUID 过滤 | `ScanFilter.Builder().setServiceUuid(...)`；**不依赖广播名**（Windows 端不广播名字），显示名回退本地配对名 |
| §3.2 `requestMtu(517)` + Notify 重组（≤180B，可分片） | `requestMtu(517)`；`onMtuChanged` 未回调时 3 秒兜底继续；`SealedFrameAssembler` 同时支持**裸帧**与**2 字节大端长度前缀分片**，靠 AEAD tag 裁决，不猜长度 |
| §3.3.1 PROOF 帧 = 裸 32 字节 | `TcProtocol.proofFrame()`，写入 Characteristic B（WRITE_TYPE_DEFAULT） |
| §3.3.1 **IDENT 帧（新增）** | 发现服务 → 开 Notify → CCCD 写 → **写 36 字节 PEER_ID ASCII 小写 UUID** → 等 `onCharacteristicWrite` 回调确认 → 才 `readChallenge()` |
| §3.3.1 PEER_ID 终身不变 | `PairingStore.peerId()`：首次生成即持久化；参与 PROOF，换则无法解锁 |
| §3.3.2 SEALED 命令 | `unlock`（counter 从 1 开始）/`ping`/`bye` |
| §4.1 二维码载荷 | `Provisioning.parse()`：校验 `v=1`、`p=tcunlock`、`id` 为 UUID、`psk` base64url 解码后必须 32 字节；`name` 支持中文 |
| §4.2 PSK 存储 | `EncryptedSharedPreferences`（主）/ `Android Keystore AES-256-GCM + SharedPreferences`（后备，初始化失败自动降级并在「关于」页显示实际通道） |

### BLE 状态机（`BleCentral`）

```
startScan(按 Service UUID 过滤, 15s 超时)
  └─ 命中 → 1.2s 观察窗（挑最强 RSSI）→ connectGatt(TRANSPORT_LE)
       └─ onConnectionStateChange(CONNECTED) → discoverServices()
            └─ onServicesDiscovered → requestMtu(517)
                 └─ onMtuChanged → setCharacteristicNotification + CCCD 写
                      └─ 写 IDENT(36B) → onCharacteristicWrite
                           └─ readCharacteristic(challenge) → 32B NONCE → 可弹指纹
                                └─ [指纹通过] → 算 PROOF/K_session → 写 PROOF(32B)
                                     └─ Notify `ready`(counter=1) → 发 `unlock`(counter=1)
                                          └─ Notify `unlock_result`(counter=2) → 显示结果
```

所有 GATT 操作经**单条串行队列**下发（Android 同一时刻只允许一个未完成操作），每步 8s 超时、失败即上报中文原因。
GATT 133/断开等错误码带在状态文案里，便于真机排查。

### Apple 风格 UI（Compose 自绘）

- 大标题（Large Title 34sp）、iOS 分组式 inset grouped 卡片（14dp 圆角、行内缩进分割线）
- 强调色 `#0A84FF`；浅色背景 `#F2F2F7` / 卡片 `#FFFFFF`；深色背景 `#000000` / 卡片 `#1C1C1E`，**跟随系统**
- 底部 56dp 大圆角主按钮「指纹解锁」、iOS 风格开关（绿色轨道）、Canvas 手绘指纹图标（不引 material-icons-extended）
- 首页：指纹主视觉 + 连接状态卡片 + 目标电脑名 + 信号强度柱 + MTU + 会话编号 + 上次解锁时间 + 设置入口
- 全中文界面；配对页同时提供「扫码」与「手动粘贴」两条通道（后者是无障碍与自动化的必需路径，永远保留）

---

## 5. 回环自测（协议 §8）

运行方式（无需真机、无需 Android 设备）：

```powershell
# 方式一：JUnit（8 个用例，含全量断言清单）
powershell -File TC-tools\android\build-apk.ps1 -UnitTest
# 方式二：直接跑 JVM main，输出 JSON 供逐字节比对
powershell -File TC-tools\android\build-apk.ps1 -SelfTest
```

### 本端输出（与验证负责人 Node.js 权威实现逐字节一致）

```
PSK   = 000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f
NONCE = 202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f
HOST_ID = 3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01
PEER_ID = 8a7b6c5d-4e3f-4a2b-9c8d-7e6f5a4b3c2d

K_session = 21f2a1a3890968e1da28553de67b49cab6ab2ffb7f3ecbaec72d284852b9af47
PROOF     = 353d6fdd6e73a63620389b806711e6c2c726407045d4c7ddae29264a9e8bab10
base64url(PSK) = AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8   (43 字符，无 '=')
IV(counter=1)  = 000000000000000100000000
SEALED(K_session,1,"{"type":"unlock"}") = 45 字节
  000000000000000100000000678def935e80e0eeb8ca5a9ee9bbef97f6a2c4302b392deeb76b81f496665496c6
ciphertext = 678def935e80e0eeb8ca5a9ee9bbef97f6
tag        = a2c4302b392deeb76b81f496665496c6
SELFTEST_RESULT=PASS    （24/24 断言通过）
```

交叉核对命令（验证负责人提供）：

```powershell
node TC-tools\tests\unlock\ref-vectors.mjs --compare TC-tools\dist\android\vectors-android.json
```

自测覆盖：K_session/PROOF 权威值、base64url 无填充与往返、IV 布局与多 counter 取值、
SEALED 整帧 45 字节与 ciphertext/tag 分别比对、OPEN 往返、counter 不匹配与密文篡改必须失败、
**20 字节 MTU 切片重组**、**2 字节长度前缀分片**、**重放拒绝**、counter 递增接受、
IV 填充必须为 0x00、`ct||tag` 最小长度 17、配对载荷合法/非法（版本/产品标识/PSK 长度/UUID 格式）。

---

## 6. 只有真机才能验证的项（本机无 Android 设备/模拟器，**未验证**）

> 环境事实：本机没有 Android 设备，也没有蓝牙适配器；回环自测只覆盖**密码学与消息结构**（协议 §7 要求）。
> 以下全部需要真机或模拟器，**当前状态均为「未验证」**：

1. **BLE 真实链路**：扫描能否命中 Service UUID 广播、`connectGatt` 建连、MTU 协商（517 或 3 秒兜底）、
   CCCD 写入回调、`setCharacteristicNotification` 生效、IDENT 写入回调、长写（>MTU-3 的 45B SEALED 帧）行为、GATT 133 重试。
2. **与 Windows Host 的端到端互通**：`ready`(counter=1) → `unlock`(counter=1) → `unlock_result`(counter=2) 全流程、
   SESSION_ID 两端一致、PSK 校验失败累计 5 次后 Host 失效 PSK 的表现、1500ms 限流 `reason=throttled` 的实际触发与 UI 映射。
3. **指纹 / BiometricPrompt 真机行为**：指纹成功与失败、`ERROR_LOCKOUT`、`ERROR_LOCKOUT_PERMANENT`、
   取消返回码、无指纹硬件的提示、`DEVICE_CREDENTIAL`（图案/密码）回退在真机上的实际 UI 与回调码。
4. **PSK 存储链路的真实实现**：`EncryptedSharedPreferences` 在真实 Android Keystore 上的创建/读写，
   以及后备通道（Keystore AES-256-GCM）在 Keystore 被清除后的行为（JVM 上无法执行这些 Android API）。
5. **ZXing 扫码**：相机运行时权限弹窗、CaptureActivity 主题与方向、真实二维码识别成功率；
   相机不可用时「手动粘贴」通道的可用性。
6. **权限与系统版本差异**：Android 12+「附近的设备」授权/拒绝/永久拒绝后的文案与流程；
   Android 8/9/10 走 `ACCESS_FINE_LOCATION` 的路径；蓝牙关闭时的提示。
7. **UI 观感**：深浅色跟随系统、刘海/挖孔屏 insets、字体缩放、横竖屏（本机无法渲染 Compose）。

### 模拟器可验证（Lead 正在准备 android-34 x86_64 emulator）

- 安装/启动/应用名/图标、首页与设置页渲染、深浅色切换
- 手动粘贴配对通道全链路、`--es pairing_json` 注入、`--ez selftest true` 回环自测（结果打 `logcat -s TcSelfTest`）
- 指纹：`adb emu finger touch <id>`（需先在模拟器设置中录入指纹）
- **不能**在模拟器验证：真实 BLE（模拟器无蓝牙）、`EncryptedSharedPreferences` 的真机 Keystore 行为

---

## 7. debug 构建的自动化入口（供 adb 驱动）

```powershell
$APK="C:\Users\吴桥生\Videos\DSHworkarea\TC-tools\dist\android\TC-Tools-Unlock-0.1.0.apk"
adb install -r $APK

# 0) 预授权（否则扫描会静默失败）
adb shell pm grant com.tctools.unlock android.permission.BLUETOOTH_SCAN
adb shell pm grant com.tctools.unlock android.permission.BLUETOOTH_CONNECT

# 1) 注入配对信息（跳过扫码；JSON 就是 Windows 端二维码里的那段文本）
adb shell am start -n com.tctools.unlock/.MainActivity `
  --es pairing_json '{"v":1,"p":"tcunlock","id":"3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01","name":"DESKTOP-ABC","psk":"<base64url>"}'

# 2) 在手机内跑协议回环自测，逐行结果打到 logcat
adb shell am start -n com.tctools.unlock/.MainActivity --ez selftest true
adb logcat -s TcSelfTest

# 3) 直接发起一次解锁（仍会弹指纹，不会绕过）
adb shell am start -n com.tctools.unlock/.MainActivity --ez unlock true
```

这些入口只在 `BuildConfig.DEBUG` 下生效（release 构建不含）。

---

## 8. 已知限制

1. **未做真机蓝牙验证**（无设备）——协议字节层已由三方独立实现交叉核对，链路层仍需真机确认。
2. `peerId` 一旦生成即持久化，重新安装应用会生成新的 PEER_ID，**必须重新配对**（协议要求 PEER_ID 参与 PROOF）。
3. 重新配对（再次扫码）会覆盖本机 PSK；若 Host 侧已因 5 次失败失效 PSK，手机端也必须重新配对。
4. 同名多台电脑同时广播时无法区分（Windows 端不广播名字、广播内也不含 HOST_ID）；
   当前策略是 1.2 秒窗口内选信号最强者，`SESSION_ID` 可帮助确认连到了哪台。
5. 扫码通道依赖 `zxing-android-embedded` 的 `CaptureActivity`（已随包打入，主题已覆盖为深色无 ActionBar）。
6. `build-apk.ps1` 假定工具链位于 `C:\tctoolchain\*`（见第 2 节）；换机请修改脚本顶部变量。
