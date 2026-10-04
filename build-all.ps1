# =============================================================
#  build-all.ps1 - TC-tools v0.2.0-rc2 一键构建（无需管理员）
#
#  用法（PowerShell）：
#     powershell -ExecutionPolicy Bypass -File .\build-all.ps1
#     powershell -ExecutionPolicy Bypass -File .\build-all.ps1 -SkipCpp
#
#  产物：
#     dist\tctool.exe                          C++ 控制台主程序（静态单文件）
#     dist\TCtools-installer-0.2.0-rc2.exe      NSIS 安装程序
#     若 tcyunlock\dist\tctool-unlock.exe 存在，则自动打包进安装程序
# =============================================================
param(
  [string]$Gpp    = 'C:\Mingw-w64\mingw64\bin\g++.exe',
  [string]$Makensis = 'C:\Users\吴桥生\Videos\DSHworkarea\tools\nsis-bundle\windows\makensis.exe',
  [switch]$SkipCpp,
  [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Step($msg) { Write-Host ""; Write-Host "=== $msg ===" -ForegroundColor Cyan }

# ---------------------------------------------------------------- C++ 构建 --
if (-not $SkipCpp) {
  Step "1/2 MinGW g++ 编译 C++ 主程序"
  if (-not (Test-Path $Gpp)) {
    $cand = @('C:\Mingw-w64\mingw64\bin\g++.exe', 'C:\Qt\Tools\mingw1310_64\bin\g++.exe') |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($cand) { $Gpp = $cand } else { throw "找不到 g++：请用 -Gpp <path\to\g++.exe> 指定" }
  }
  Write-Host "g++ = $Gpp"
  if (-not (Test-Path 'dist')) { New-Item -ItemType Directory -Path 'dist' | Out-Null }

  $sources = @(
    'src\main.cpp','src\pages.cpp','src\tools.cpp','src\cli.cpp','src\http.cpp',
    'src\json.cpp','src\util.cpp','src\app.cpp','src\lang.cpp','src\unlock.cpp'
  )
  $args = @(
    '-std=c++20','-O2','-Wall','-Wextra','-static','-static-libgcc','-static-libstdc++',
    '-finput-charset=UTF-8','-fexec-charset=UTF-8'
  ) + $sources + @(
    '-o','dist\tctool.exe','-lwinhttp','-lshell32','-luser32','-lversion','-ladvapi32'
  )
  & $Gpp @args
  if ($LASTEXITCODE -ne 0) { throw "C++ 编译失败（exit $LASTEXITCODE）" }
  $exe = Get-Item 'dist\tctool.exe'
  Write-Host ("OK  dist\tctool.exe  {0:N0} bytes" -f $exe.Length) -ForegroundColor Green
}

# ------------------------------------------------------------ NSIS 安装包 --
if (-not $SkipInstaller) {
  Step "2/2 NSIS 打包安装程序"
  if (-not (Test-Path $Makensis)) {
    $cand = @(
      'C:\Users\吴桥生\Videos\DSHworkarea\tools\nsis-bundle\windows\makensis.exe',
      'C:\Program Files (x86)\NSIS\makensis.exe',
      'C:\Program Files\NSIS\makensis.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($cand) { $Makensis = $cand } else { throw "找不到 makensis：请用 -Makensis <path> 指定" }
  }
  Write-Host "makensis = $Makensis"

  $nsi = Join-Path $root 'installer\TCtools-installer.nsi'
  # NSIS 需要 UTF-8 BOM，否则中文 LangString 直接编译失败
  $bytes = [System.IO.File]::ReadAllBytes($nsi)
  $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
  if (-not $hasBom) {
    Write-Host "提示：$nsi 缺少 UTF-8 BOM，已自动补回（NSIS 要求）" -ForegroundColor Yellow
    $text = [System.IO.File]::ReadAllText($nsi, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($nsi, $text, (New-Object System.Text.UTF8Encoding($true)))
  }

  $unlockDist = Join-Path $root 'tcyunlock\dist\tctool-unlock.exe'
  $nsisArgs = @()
  if (Test-Path $unlockDist) {
    $nsisArgs += '/DHAVE_UNLOCK=1'
    Write-Host "蓝牙解锁组件：已找到，将打包进 unlock\ 子目录" -ForegroundColor Green
  } else {
    Write-Host "蓝牙解锁组件：未找到（先构建 tcyunlock），本次不打包" -ForegroundColor Yellow
  }

  Push-Location (Join-Path $root 'installer')
  try { & $Makensis @nsisArgs 'TCtools-installer.nsi' } finally { Pop-Location }
  if ($LASTEXITCODE -ne 0) { throw "NSIS 打包失败（exit $LASTEXITCODE）" }

  $out = Get-Item 'dist\TCtools-installer-0.2.0-rc2.exe'
  Write-Host ("OK  dist\TCtools-installer-0.2.0-rc2.exe  {0:N0} bytes" -f $out.Length) -ForegroundColor Green
}

Write-Host ""
Write-Host "全部完成。" -ForegroundColor Cyan
