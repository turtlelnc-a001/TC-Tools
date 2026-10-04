# v0.2.0-rc2 构建清单（产物 ↔ 源码对应关系）

> **本清单的正确用法（重要）**
>
> - **commit 标识"源码身份"（审计用）**——它告诉你这次交付的源码是什么状态；
> - **SHA256 标识"被交付的那个文件"**——验证以它为准；
> - ⚠️ **不要用"从同一提交重建"来证明与已验证产物相同**：本项目的产物**对构建环境敏感**（见下节实测），
>   重新构建应视为**新产物**，必须**重新记录哈希并重新验证功能**。

- **源码身份（rc2 交付）**：`17047b4`（`tcyunlock/src/Program.cs` blob SHA256
  `B26E8D7E63AB97C788F9EABADDB4A3A803A76CA6DABFDC1E4C8BC51E0D3BFCEC`）
- **仓库当前状态（HEAD）**：`fcca14d` 之后见 `git log`；`tcyunlock/src/Program.cs` 在 HEAD 上多一个 **rc3** hunk（`forget` 清理导出物）
- 远程：`git@github.com:turtlelnc/TC-Tools.git`（分支 `main`）
- 构建入口：Windows 端 `build-all.ps1`；手机端 `android/build-apk.ps1`

---

## ⚠️ 实测：产物不是位级可复现的（请认真读）

同一台机器、同一 .NET SDK（8.0.425），用 rc2 源码做多种**忠实重建**，结果如下：

| 配置 | 大小（字节） | SHA-256 |
|---|---|---|
| **冻结交付产物** | **41,483,515** | **`2BC9BB1C78398BD2F09F551E11FC15C9D0712373A6F2A1AF9C9D652DB64FF392`** |
| rc2 完整源码(LF) + 临时 worktree 项目 + worktree 输出 | 41,483,513 | `CFE9E3CF…` |
| rc2 完整源码(LF) + 临时 worktree 项目 + 规范 `publish\` 输出 | 41,483,508 | `D3595394…` |
| rc2 源码(LF) + 规范项目目录 + 规范输出 | 41,483,513 | `3B05C22E…` |
| 同上 + 清空 `obj/bin` + 完整 `build.ps1` 序列 | 41,483,513 | `3B05C22E…`（与上一行相同） |

**结论**：

1. **构建是确定性的**——同源码、同路径连跑 3 次逐字节相同（上表后两行相同即证据）；
2. **但产物对构建环境敏感**——仅把输出目录名从 33 字符改为 72 字符，产物就从 41,484,005 变为 41,484,008 字节。
   这是 .NET 自包含单文件打包（bundle 内嵌路径）的固有特性，不是本项目代码问题；
3. **一个隐蔽的坑**：本仓库 `core.autocrlf=true`，`git checkout` 会把源码写成 **CRLF**，
   而冻结产物是用 **LF** 源码构建的 —— **源码行尾也会改变二进制字节**。复现前请先确认行尾。

因此：**"源码可复现"这个说法对二进制不成立**，本清单只声称"源码身份可追溯"。
如需位级复现，只能在**完全相同的机器、路径、行尾、SDK 与打包参数**下进行，且仍不保证跨环境一致。

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

`tcyunlock\src\Program.cs` 在 **HEAD** 上含一项 **rc3 改动**（`forget` 会顺带删除过期的 `payload.txt` / `payload.png`），
但**没有重建 `dist\tctool-unlock.exe`** —— 因为重建会改变已被 verify 复核并冻结的哈希，
而该问题经核实**安全影响为零**（残留的只是已作废的 PSK；无 PSK 时认证必然失败）。

**后果（请务必知情）**：用 HEAD 的 `src/` 重新构建，**不会再得到** `2BC9BB1C…`。

下表逐产物给出**源码身份**（审计用，**不是**位级复现的承诺 —— 见上文实测）：

| 产物 | 源码身份 commit | 说明 |
|---|---|---|
| `tcyunlock\dist\tctool-unlock.exe`（`2BC9BB1C…`） | **`17047b4`**（`Program.cs` blob `B26E8D7E…`） | rc2 交付源码 = 该组件最后一次真实构建时的状态 |
| `dist\tctool.exe`（`EAD0861A…`） | **`17047b4`** | 合并 origin/main 后的 C++ 源码（含并行下载 + 解锁菜单） |
| `dist\TCtools-installer-0.2.0-rc2.exe`（`70689E3E…`） | 由上表第 1、2 行的**这两个产物**打包而成 | 安装程序不含独立编译产物 |
| 手机端 APK（`12F01A78…`） | **`f7d953b`** | 含控制中心磁贴 + realme 适配 |
| 仓库整体（文档/说明） | **`fcca14d`**（= 本文档所在 commit） | 文档与报告的最新状态 |

> 换句话说：**`17047b4` 之后 `tcyunlock/src/Program.cs` 的改动属 rc3，不属于 rc2 二进制。**
> **验证产物**用上表哈希；**追溯源码**用上表 commit；**重新构建**按上文实测视为新产物，需重新记录与验证。
> `tcyunlock/README.md` **§1.4「当前版本状态」**里也有同样的声明，供复核者对账。

## 生成命令

```powershell
# Windows 端（C++ 主程序 + NSIS 安装程序；若 tcyunlock\dist\tctool-unlock.exe 存在会自动打包进 unlock\）
powershell -ExecutionPolicy Bypass -File .\build-all.ps1

# 仅重建解锁组件（产物会与冻结哈希不同，属新产物，需重新记录哈希并重新验证）
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

> 复现前请确认源码**行尾为 LF**（本仓库 `core.autocrlf=true`，`git checkout` 默认落成 CRLF，会改变二进制字节）。

## 未纳入 Git 的产物

`.gitignore` 排除了 `dist/`、`*.exe`、`*.apk`。安装程序、APK 与解锁组件通过
**GitHub Release** 分发（见 `README.md` 的下载表与 `scripts/create-release.ps1`）。
