# v0.2.0-rc2 构建清单（产物 ↔ 源码对应关系）

> **用途**：把一个交付产物的哈希**绑定到唯一的源码版本**，让后续复核者能安全复现。
> 若你手上有一个产物哈希，请在下表找到它，并检出对应的 `sourceCommit` 再构建。

- **sourceCommit**：`f7d953b9299b902c220a6fad66492cb0cd8d61f2`
  （`v0.2.0-rc2: Android quick-settings unlock tile, user-controlled autostart, release notes`）
- 远程：`git@github.com:turtlelnc/TC-Tools.git`（分支 `main`）
- 复现方式：`git checkout f7d953b` 后按 `build-all.ps1`（Windows 端）与 `android/build-apk.ps1`（手机端）构建

---

## 交付产物哈希

| 产物 | 大小（字节） | SHA-256 |
|---|---|---|
| `dist\TCtools-installer-0.2.0-rc2.exe` | 35,183,812 | `70689E3ED284E7E6268D6DD3A33E0ADC0DDDAD7767BCDBF0F4923B570D30FA98` |
| `dist\tctool.exe`（C++ 主程序） | 1,161,771 | `EAD0861AE1D9896BA04EE94934D895E7E81E7CC68668A82E508036A04957BC28` |
| `dist\android\TC-Tools-Unlock-0.2.0-rc2.apk` | 11,475,766 | `12F01A782BB424875F683EF50C48D370D14EC4DA373253B356956E6626B98359` |
| `tcyunlock\dist\tctool-unlock.exe`（电脑端解锁组件） | 41,483,515 | `2BC9BB1C78398BD2F09F551E11FC15C9D0712373A6F2A1AF9C9D652DB64FF392` |
| `tcyunlock\dist\selftest-vectors.json` | 4,258 | `649DF454603A743A13F67F9A9F9CA88AE6C18D0063BEDDC0E5ACF07D1935AFEF` |
| `dist\android\vectors-android.json` | 1,057 | `D3F34DA39172FBBF63189FAA10C039DD461E5DD3D6B00FBC200E3808A9A41458` |

> `2BC9BB1C…`（电脑端解锁组件）是 **verify 独立复跑的冻结哈希**：
> 清空 `DOTNET_ROOT`、PATH 无 dotnet 时直接运行该 exe，`selftest` → **29/29 通过，exit 0**。

## ⚠️ 一个必须知道的偏差：源码比二进制新（**有意为之**）

`tcyunlock\src\Program.cs` 含一项 **rc3 改动**（`forget` 会顺带删除过期的 `payload.txt` / `payload.png`），
但**没有重建 `dist\tctool-unlock.exe`** —— 因为重建会改变已被 verify 复核并冻结的哈希，
而该问题经核实**安全影响为零**（残留的只是已作废的 PSK；无 PSK 时认证必然失败）。

**后果（请务必知情）**：用当前 `src/` 重新构建，**不会再得到** `2BC9BB1C…`。

因此：

- **验证产物** → 用上表哈希（冻结产物为准）；
- **从源码复现** → 必须检出与产物对应的 commit（`f7d953b` 对 rc2 二进制仍不匹配该 exe，
  该 exe 对应的是**更早的源码状态**；如需逐一复现，请以 `tcyunlock/README.md` §1.4 的记录为准）。

`tcyunlock/README.md` **§1.4「当前版本状态」** 里也单独写明了这一点，供复核者对账。

## 生成命令（可复现）

```powershell
# Windows 端（C++ 主程序 + NSIS 安装程序；若 tcyunlock\dist\tctool-unlock.exe 存在会自动打包进 unlock\）
powershell -ExecutionPolicy Bypass -File .\build-all.ps1

# 仅重建解锁组件（会改变其哈希，需重新通知验证方）
powershell -ExecutionPolicy Bypass -File .\tcyunlock\build.ps1

# 手机端 APK
$env:JAVA_HOME='C:\tctoolchain\jdk17'; $env:ANDROID_HOME='C:\tctoolchain\android-sdk'
$env:GRADLE_USER_HOME='C:\tctoolchain\gradle-home'
& 'C:\tctoolchain\gradle-8.14\bin\gradle.bat' --no-daemon -p .\android :app:assembleDebug

# 校验哈希
Get-FileHash .\dist\TCtools-installer-0.2.0-rc2.exe -Algorithm SHA256
Get-FileHash .\tcyunlock\dist\tctool-unlock.exe -Algorithm SHA256
Get-FileHash .\dist\android\TC-Tools-Unlock-0.2.0-rc2.apk -Algorithm SHA256
```

## 未纳入 Git 的产物

`.gitignore` 排除了 `dist/`、`*.exe`、`*.apk`。安装程序、APK 与解锁组件通过
**GitHub Release** 分发（见 `README.md` 的下载表与 `scripts/create-release.ps1`）。
