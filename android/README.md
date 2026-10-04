# TC-Tools 解锁电脑 · Android 端交付说明（v0.2.0-rc2）

> 实现依据：`TC-tools/docs/UNLOCK-PROTOCOL.md`（v1.0，冻结）。本端**不偏离字节级定义**。
> 技术栈：Kotlin 2.0.21 + Jetpack Compose（Material3，无 XML 布局）+ AGP 8.5.2 + Gradle 8.14 + JDK 17。
> minSdk 26（Android 8.0）/ targetSdk 34 / compileSdk 34。

**v0.2.0-rc2 新增**：① 控制中心**快捷设置磁贴**「解锁电脑」（含 realme/OPPO 等厂商的后台弹指纹降级路径）；
② 版本号 0.1.0 → 0.2.0-rc2（versionCode 1 → 2）；③ 本文档补齐磁贴用法与各厂商后台权限适配。

---

## 1. 交付物

| 项目 | 值 |
|---|---|
| APK | `TC-tools\dist\android\TC-Tools-Unlock-0.2.0-rc2.apk` |
| 绝对路径 | `C:\Users\吴桥生\Videos\DSHworkarea\TC-tools\dist\android\TC-Tools-Unlock-0.2.0-rc2.apk` |
| 大小 | **11,475,766 字节（10.94 MB）** |
| SHA-256 | `12F01A782BB424875F683EF50C48D370D14EC4DA373253B356956E6626B98359` |
| 包名 / 版本 | `com.tctools.unlock` / **versionCode 2 · versionName 0.2.0-rc2** |
| 应用名 | **TC-Tools 解锁电脑**（`aapt2 dump badging` → `application-label:'TC-Tools 解锁电脑'`） |
| 磁贴 label | **解锁电脑**（控制中心里显示，`@string/tile_label`） |
| 原始产物 | `TC-tools\android\app\build\outputs\apk\debug\app-debug.apk` |
| 回环自测 JSON | `TC-tools\dist\android\vectors-android.json` |
| 旧版本 | `TC-Tools-Unlock-0.1.0.apk` 已按要求删除 |

### 安装

```powershell
adb install -r "C:\Users\吴桥生\Videos\DSHworkarea\TC-tools\dist\android\TC-Tools-Unlock-0.2.0-rc2.apk"
```

支持的 ABI：`arm64-v8a` / `armeabi-v7a` / `x86` / `x86_64`（模拟器 x86_64 可直接安装）。

---

## 2. 控制中心快捷磁贴（v0.2.0-rc2 新增）

### 2.1 怎么用

1. 装好 App 并完成一次配对（扫码或粘贴配对 JSON）。
2. 下拉控制中心 → 点「编辑 / 铅笔」图标 → 把 **「解锁电脑」** 磁贴拖进面板
   （realme/OPPO 是「编辑开关」，小米是「编辑控制中心」，三星是「编辑按钮」）。
3. 之后每次解锁：**下拉控制中心 → 点「解锁电脑」磁贴** → App 自动拉起并发起解锁 → 按指纹 → 电脑解锁。

> Android 12+ 可以用命令直接把磁贴塞进面板并点击（供自动化）：
> ```powershell
> adb shell settings put secure sysui_qs_tiles "wifi,cell,battery,com.tctools.unlock/.tile.UnlockTileService"
> adb shell cmd statusbar click-tile com.tctools.unlock/.tile.UnlockTileService
> ```
> 部分 ROM 需要重启 SystemUI（`adb shell am crash com.android.systemui` 或重启设备）才会刷新磁贴列表。

### 2.2 磁贴状态文案（与真实状态联动）

| 磁贴文案 | 何时出现 |
|---|---|
| **未配对** · 点按去配对 | 本机还没有 PSK → 点磁贴只打开 App 的配对页 |
| **未连接** · 点按解锁 · `<电脑名>` | 已配对、当前没有 BLE 连接 |
| **连接中** · 正在连接 `<电脑名>` | 扫描 / 连接 / 发现服务中 |
| **已就绪** · 点按指纹解锁 | 已连上且已读到 32 字节 challenge |
| **解锁中** · 请验证指纹 | 指纹弹窗已弹出 / 正在发送 unlock |
| **已解锁** · 解锁成功 | 收到 `unlock_result(ok=true)`（约 6 秒后回落） |
| **解锁失败** · `<原因>` | 权限缺失、蓝牙关闭、PROOF 失败、`error.code` 等（约 6 秒后回落） |

磁贴 `label` 固定为「解锁电脑」，状态放在 `stateDescription` / `subtitle`（API 29+）/
`contentDescription`（无障碍朗读）里 —— 因此磁贴本身不会因为状态变化而改名，便于用户识别。

### 2.3 realme / OPPO 等厂商的「后台弹指纹」降级（关键设计）

国产 ROM（realme UI / ColorOS / MIUI(HyperOS) / EMUI / OriginOS / One UI）普遍限制
**后台启动 Activity** 与 **后台弹窗**。`TileService` 属于后台上下文，如果直接在磁贴里调用
`BiometricPrompt.authenticate()`，在这些机型上会**静默失败或根本不显示**。

本实现采用的路径（对所有厂商统一、无需分支判断）：

```
磁贴点击
  └─ startActivityAndCollapse(Intent/PendingIntent → MainActivity, EXTRA_UNLOCK)
       └─ MainActivity 进入前台（onCreate 或 onNewIntent）
            └─ ViewModel.requestUnlockSignal() → UI 侧检查 BLE 权限 + 蓝牙开关
                 └─ 扫描 → 连接 → 写 IDENT → 读 challenge
                      └─ Activity 已在前台 → BiometricPrompt 弹出（指纹/设备凭据）
                           └─ 通过后才计算 PROOF → 写 PROOF → 收 ready → 发 unlock → 显示结果
```

- **永远先拉起前台 Activity，再弹指纹** —— 这就是 realme/OPPO 机型上"最稳的做法"，也顺带绕开了
  「后台弹出界面」权限问题。
- Android 14（API 34）用 `startActivityAndCollapse(PendingIntent)`；旧版本用
  `startActivityAndCollapse(Intent)`（已按版本分支处理）。
- 磁贴与 App 同进程；进程被杀后点磁贴会用普通 SharedPreferences 里的**非机密**信息
  （是否已配对 / 电脑名）恢复磁贴文案，**PSK 永远只在 `PairingStore`（EncryptedSharedPreferences / Keystore）里**。
- 若系统在极端省电模式下仍拦截后台拉起 Activity，`startActivityAndCollapse` 会抛异常，
  已 try/catch 记日志（`adb logcat -s TcTile`），用户手动打开 App 即可正常使用。

### 2.4 各厂商后台权限适配（用户按需设置）

> 本应用的磁贴路径已尽量规避后台限制；下表用于**进一步保证**「屏幕锁定/后台时也能解锁」的体验。
> 设置项名称各 ROM 版本略有差异，按关键词找即可。

| 厂商 / ROM | 需要开启的设置 |
|---|---|
| **realme（realme UI）/ OPPO / 一加（ColorOS）** | ① 设置 → 应用管理 → TC-Tools 解锁电脑 → **允许自启动**；② **允许后台弹出界面 / 后台弹窗**（realme 在「权限管理 → 其他权限」）；③ 电池 → **允许后台运行 / 不优化**；④ 允许「锁屏显示」；⑤ 最近任务里给 App **加锁**（防一键清理）。磁贴：控制中心 → 编辑 → 添加「解锁电脑」 |
| **小米（MIUI / HyperOS）** | ① 设置 → 应用设置 → 应用管理 → 本应用 → **权限管理 → 后台弹出界面 = 允许**（关键）；② **自启动 = 允许**；③ 省电策略 = **无限制**；④ 锁屏显示 = 允许；⑤ 最近任务下拉给 App 加锁。磁贴：控制中心 → 编辑控制中心 → 添加 |
| **华为（EMUI / HarmonyOS）** | ① 设置 → 应用 → 应用启动管理 → 本应用 → **手动管理**（自启动 / 关联启动 / 后台活动 三项全开）；② 电池 → **忽略电池优化**；③ 通知与状态栏允许后台活动。磁贴：下拉 → 编辑 → 添加 |
| **vivo（OriginOS / Funtouch OS）** | ① 设置 → 电池 → **后台高耗电 = 允许**；② 设置 → 应用与权限 → 权限管理 → **自启动 / 后台弹出界面** 允许；③ i 管家 → 自启动管理加白名单。磁贴：控制中心 → 编辑 → 添加 |
| **三星（One UI）** | ① 设置 → 电池与设备维护 → 电池 → **后台使用限制 → 从不休眠的应用**加入本应用；② 关闭对该应用的「自适应电池」限制。磁贴：下拉 → 编辑按钮 → 添加 |
| **原生 Android / Pixel / 模拟器** | 无需额外设置（Android 13+ 首次点击磁贴仍需授予「附近的设备」权限） |

---

## 3. 构建与复现

本机原本没有 JDK / Android SDK / Gradle / Android Studio，且用户名与工作区路径含中文。
为规避 AGP/aapt2 对非 ASCII 路径的敏感性，**工具链全部安装在 ASCII 路径**：

| 组件 | 路径 |
|---|---|
| JDK 17 | `C:\tctoolchain\jdk17`（Microsoft OpenJDK 17.0.13+11） |
| Gradle 8.14 | `C:\tctoolchain\gradle-8.14\bin\gradle.bat` |
| Android SDK | `C:\tctoolchain\android-sdk`（platform-tools / platforms;android-34 / build-tools;34.0.0） |
| Gradle 缓存 | `C:\tctoolchain\gradle-home` |
| `local.properties` | `sdk.dir=C:/tctoolchain/android-sdk` |

> **不要用 Gradle Wrapper**：本机 `services.gradle.org` 不可达，用上面解压好的 Gradle 8.14。
> `dl.google.com` 与 `repo.maven.apache.org` 可达，`google()` + `mavenCentral()` 均可用。

一键脚本（会自动从 `app/build.gradle.kts` 读 versionName、并清理 dist 里的旧版本 APK）：

```powershell
powershell -ExecutionPolicy Bypass -File TC-tools\android\build-apk.ps1            # assembleDebug + 复制到 dist
powershell -ExecutionPolicy Bypass -File TC-tools\android\build-apk.ps1 -UnitTest  # :app:testDebugUnitTest
powershell -ExecutionPolicy Bypass -File TC-tools\android\build-apk.ps1 -SelfTest  # :app:selfTest（回环自测）
```

手工等价命令（本次 rc2 构建就用这条）：

```powershell
$env:JAVA_HOME='C:\tctoolchain\jdk17'
$env:ANDROID_HOME='C:\tctoolchain\android-sdk'; $env:ANDROID_SDK_ROOT=$env:ANDROID_HOME
$env:GRADLE_USER_HOME='C:\tctoolchain\gradle-home'
& 'C:\tctoolchain\gradle-8.14\bin\gradle.bat' --no-daemon -p TC-tools\android `
    :app:testDebugUnitTest :app:selfTest :app:assembleDebug
```

### 非 ASCII 路径处理

AGP 在 Windows 上默认**硬性拒绝**含中文的项目路径，已在 `gradle.properties` 写入
`android.overridePathCheck=true` 放行；放行后 `mergeDebugResources` / `processDebugResources` /
`processDebugMainManifest`（真正调用 aapt2 的 task）全部成功，因此无需镜像/junction，直接在原地构建。

### rc2 构建日志（关键行）

```
> Task :app:compileDebugKotlin
> Task :app:testDebugUnitTest      tests=8 failures=0 errors=0 skipped=0
> Task :app:selfTest               SELFTEST_RESULT=PASS（24/24 断言）
> Task :app:packageDebug
> Task :app:assembleDebug
BUILD SUCCESSFUL in 2m 26s
45 actionable tasks: 24 executed, 21 up-to-date
```

### aapt2 校验（v0.2.0-rc2）

```
package: name='com.tctools.unlock' versionCode='2' versionName='0.2.0-rc2' compileSdkVersion='34'
sdkVersion:'26'   targetSdkVersion:'34'
application-label:'TC-Tools 解锁电脑'
launchable-activity: name='com.tctools.unlock.MainActivity'
# 磁贴（AndroidManifest.xml 内）
service name="com.tctools.unlock.tile.UnlockTileService"
        permission="android.permission.BIND_QUICK_SETTINGS_TILE"
        intent-filter action="android.service.quicksettings.action.QS_TILE"
# 权限：BLUETOOTH_SCAN(neverForLocation) / BLUETOOTH_CONNECT / BLUETOOTH+ADMIN(maxSdk30)
#       / ACCESS_FINE_LOCATION(maxSdk30) / USE_BIOMETRIC / CAMERA
```

---

## 4. 代码结构

```
TC-tools\android\
├─ build-apk.ps1                 一键构建/测试/自测（versionName 自动读取 + 旧产物清理）
├─ settings.gradle.kts / build.gradle.kts / gradle.properties / local.properties
└─ app\
   ├─ build.gradle.kts           Compose BOM 2024.09.00、biometric 1.1.0、security-crypto 1.1.0-alpha06、
   │                             zxing-android-embedded 4.3.0、selfTest 任务；versionCode 2 / 0.2.0-rc2
   └─ src\
      ├─ main\java\com\tctools\unlock\
      │  ├─ protocol\TcProtocol.kt            ★ 协议核心（纯 JVM：HMAC / AES-256-GCM / base64url / 计数器 IV）
      │  ├─ protocol\SealedFrameAssembler.kt  ★ Notify 分片重组 + 重放防护（纯 JVM）
      │  ├─ protocol\Provisioning.kt          配对二维码载荷解析（纯 JVM）
      │  ├─ protocol\MiniJson.kt              极简 JSON（纯 JVM）
      │  ├─ protocol\LoopbackSelfTest.kt      §8 回环自测（纯 JVM）
      │  ├─ protocol\SelfTestMain.kt          JVM main 入口（:app:selfTest）
      │  ├─ ble\BleCentral.kt                 GATT Central：扫描/连接/MTU/CCCD/IDENT/PROOF/SEALED/Notify
      │  ├─ ble\BlePermissions.kt             Android 12 前后 BLE 权限
      │  ├─ tile\UnlockTileService.kt         ★ 快捷设置磁贴（v0.2.0-rc2）
      │  ├─ tile\UnlockTileState.kt           ★ 磁贴状态桥（进程内单例 + 非机密持久化）
      │  ├─ data\PairingStore.kt              PSK 存储（EncryptedSharedPreferences + Keystore 后备）
      │  ├─ data\AppPrefs.kt                  非机密偏好（含磁贴冷启动状态）
      │  ├─ UnlockViewModel.kt                解锁状态机（指纹通过后才算 PROOF）+ 磁贴状态同步
      │  ├─ MainActivity.kt                   Activity + 导航 + 权限 + BiometricPrompt + 磁贴/自动化入口
      │  └─ ui\                               Theme / Components / HomeScreen / PairScreen / SettingsScreen(含 About)
      ├─ test\java\...\LoopbackSelfTestTest.kt  §8 向量 JUnit 测试（8 用例）
      └─ main\res\                             iOS 色板（浅/深）、自适应图标、磁贴图标 ic_qs_unlock、字符串
```

---

## 5. 协议实现要点（逐条对照规范）

| 规范 | 实现 |
|---|---|
| §2 `K_session = HMAC-SHA256(PSK, NONCE \|\| "TCUNLOCK-SESSION-V1")` | `TcProtocol.deriveSessionKey()`；ASCII 原始字节、无 `\0` |
| §2 `PROOF = HMAC-SHA256(PSK, NONCE \|\| "TCUNLOCK-PROOF-V1" \|\| HOST_ID \|\| PEER_ID)` | `TcProtocol.computeProof()`，**唯一调用点**是 `UnlockViewModel.onBiometricSuccess()` |
| §2.1 SEAL 线格式 `IV(12) ‖ ct ‖ tag(16)`；IV = counter(8B BE) ‖ 4B 0x00 | `TcProtocol.seal()` / `ivFor()` / `hasValidIvPadding()` |
| §2.1 counter 严格递增、IV 填充必须为 0 | `SealedFrameAssembler` 拒绝 `counter <= lastCounter` 与非 0 填充（与 Windows `HasValidIvPadding` 对齐） |
| §2.2 SESSION_ID | `sessionIdFromReady()`：`OPEN(K_session,1,ready帧)` 取 `sid` |
| §3.1 UUID 固定 | Service `7a1c9e40-…`、Challenge `…41`、Command `…42` |
| §3.2 扫描按 Service UUID 过滤 | `ScanFilter.setServiceUuid(...)`；不依赖广播名（Windows 端不广播名字），显示名回退本地配对名 |
| §3.2 MTU 517 + Notify 重组（≤180B） | `requestMtu(517)` + 3 秒兜底；组装器支持裸帧与 2 字节长度前缀分片，靠 AEAD tag 裁决 |
| §3.3.1 PROOF 帧 = 裸 32 字节 | 写入 Characteristic B（WRITE_TYPE_DEFAULT） |
| §3.3.1 IDENT 帧 | 发现服务 → 开 Notify → CCCD 写 → **写 36 字节 PEER_ID ASCII** → 等写回调 → 读 challenge |
| §3.3.2 SEALED 命令 | `unlock`（counter 从 1）/ `ping` / `bye` |
| §4.1 二维码载荷 | `Provisioning.parse()`：`v=1`、`p=tcunlock`、UUID 校验、base64url 解码后必须 32 字节、`name` 支持中文 |
| §4.2 PSK 存储 | `EncryptedSharedPreferences`（主）/ Keystore AES-256-GCM + SharedPreferences（后备，自动降级并在「关于」页显示实际通道） |

---

## 6. 回环自测（协议 §8）

```powershell
powershell -File TC-tools\android\build-apk.ps1 -UnitTest   # 8 用例 JUnit
powershell -File TC-tools\android\build-apk.ps1 -SelfTest   # 直跑 JVM main，输出 JSON
node TC-tools\tests\unlock\ref-vectors.mjs --compare TC-tools\dist\android\vectors-android.json
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

覆盖：K_session/PROOF 权威值、base64url 无填充与往返、IV 布局与多 counter 取值、SEALED 整帧 45 字节、
ciphertext/tag 分别比对、OPEN 往返、counter 不匹配与密文篡改必须失败、**20 字节 MTU 切片重组**、
**2 字节长度前缀分片**、**重放拒绝**、counter 递增接受、**IV 填充必须为 0x00**、`ct||tag` 最小长度 17、
配对载荷合法/非法（版本 / 产品标识 / PSK 长度 / UUID 格式）。

> 已在 Android 14 模拟器实测：`adb install` 成功、App 启动正常、`--ez selftest true` 在**真实 Android 运行时
> 24/24 全过**（`adb logcat -s TcSelfTest`）。

---

## 7. 只有真机才能验证的项（本机无 Android 设备/蓝牙，**未验证**）

> 回环自测只覆盖**密码学与消息结构**（协议 §7 要求）；模拟器无真实蓝牙射频（rootcanal 是模拟的）。
> 以下状态均为「未验证」：

1. **BLE 真实链路**：扫描命中、`connectGatt` 建连、MTU 协商（517 或 3 秒兜底）、CCCD 写回调、
   `setCharacteristicNotification` 生效、IDENT 写回调、长写（45B SEALED 帧）、GATT 133 重试。
2. **与 Windows Host 的端到端互通**：`ready`(1) → `unlock`(1) → `unlock_result`(2)、SESSION_ID 两端一致、
   5 次失败失效 PSK、1500ms 限流 `reason=throttled` 的真实触发与 UI 映射。
3. **快捷磁贴真机行为**：磁贴能否添加到控制中心（各 ROM 的编辑入口不同）、点击后能否成功拉起 Activity、
   状态文案是否随解锁流程变化、磁贴图标在深浅色面板下的观感；
   **realme/OPPO 机型尤其需要实测**（本实现已采用"先拉起 Activity 再弹指纹"的降级路径，仍需真机确认）。
4. **指纹 / BiometricPrompt 真机行为**：指纹成功/失败、`ERROR_LOCKOUT`、取消返回码、无指纹硬件提示、
   `DEVICE_CREDENTIAL` 回退的实际 UI。
5. **PSK 存储真实实现**：`EncryptedSharedPreferences` 在真实 Keystore 上的创建/读写，以及后备通道行为。
6. **ZXing 扫码**：相机权限弹窗、CaptureActivity 主题、真实二维码识别；相机不可用时手动粘贴通道。
7. **权限与系统版本差异**：Android 12+「附近的设备」授权/拒绝；Android 8/9/10 的 `ACCESS_FINE_LOCATION` 路径；
   蓝牙关闭提示；**从磁贴触发时的权限申请流程**（磁贴拉起的 Activity 内弹权限对话框）。
8. **UI 观感**：深浅色跟随系统、刘海/挖孔 insets、字体缩放、横竖屏。

---

## 8. debug 构建的自动化入口（供 adb 驱动）

```powershell
$APK="C:\Users\吴桥生\Videos\DSHworkarea\TC-tools\dist\android\TC-Tools-Unlock-0.2.0-rc2.apk"
adb install -r $APK
adb shell pm grant com.tctools.unlock android.permission.BLUETOOTH_SCAN
adb shell pm grant com.tctools.unlock android.permission.BLUETOOTH_CONNECT

# 1) 注入配对信息（跳过扫码）
adb shell am start -n com.tctools.unlock/.MainActivity `
  --es pairing_json '{"v":1,"p":"tcunlock","id":"3f2a1c9e-4b6d-4f7a-9c11-6c5d3e8f2b01","name":"DESKTOP-ABC","psk":"<base64url>"}'

# 2) 协议回环自测（逐行打到 logcat）
adb shell am start -n com.tctools.unlock/.MainActivity --ez selftest true
adb logcat -s TcSelfTest

# 3) 直接发起解锁（等同磁贴点击，仍必须过指纹）
adb shell am start -n com.tctools.unlock/.MainActivity --ez unlock true

# 4) 磁贴（Android 12+）
adb shell settings put secure sysui_qs_tiles "wifi,cell,battery,com.tctools.unlock/.tile.UnlockTileService"
adb shell cmd statusbar click-tile com.tctools.unlock/.tile.UnlockTileService
adb logcat -s TcTile          # 磁贴点击与拉起日志
```

> `pairing_json` / `selftest` / `unlock` 三个 adb 入口只在 `BuildConfig.DEBUG` 下生效；
> 磁贴的 `EXTRA_UNLOCK` 通道是**正式功能**，release 构建同样有效。

---

## 9. 已知限制

1. **未做真机蓝牙验证**（本机无设备）：协议字节层已由第三方独立实现交叉核对，链路层仍需真机确认。
2. `peerId` 一旦生成即持久化；重新安装应用会生成新的 PEER_ID，**必须重新配对**。
3. 重新配对会覆盖本机 PSK；Host 因 5 次失败失效 PSK 时，手机端也必须重新配对。
4. 同名多台电脑同时广播时无法区分（Windows 端不广播名字、广播不含 HOST_ID）；
   策略是 1.2 秒窗口内选信号最强者，可用 `SESSION_ID` 辅助确认连到了哪台。
5. 磁贴在**极端省电模式**下若被系统拦截后台拉起 Activity，点击可能无反应（已 try/catch 记 `TcTile` 日志）；
   按第 2.4 节放开厂商后台限制即可。
6. 磁贴状态依赖 App 进程内的实时状态；进程被杀后只能显示「未配对 / 未连接 + 电脑名」这类非机密状态
   （这是有意的：磁贴进程里不接触 PSK）。
7. 扫码通道依赖 `zxing-android-embedded` 的 `CaptureActivity`（主题已覆盖为深色无 ActionBar）。
8. `build-apk.ps1` 假定工具链位于 `C:\tctoolchain\*`；换机请修改脚本顶部变量。
