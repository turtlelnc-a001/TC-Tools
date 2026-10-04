<#
.SYNOPSIS
  双向互通测试：Windows 产品代码 ↔ Android 产品代码（verify/task-4）。

.DESCRIPTION
  用**真实产品源码**做跨端互认，两端各自独立派生 K_session，不交换密钥：
    1) Android 端 TcProtocol.seal 生成 counter=2 的 {"type":"ping"} 帧
    2) Windows 端 Proto.TryOpen 解开它（校验 counter==2、明文一致）
    3) Windows 端 Proto.Seal 生成 counter=3 的 {"type":"bye"} 帧
    4) Android 端 TcProtocol.openWithEmbeddedIv 解开它（校验 counter==3、明文一致）

  任何一步失败都会以非 0 退出码结束。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File TC-tools\tests\unlock\run-interop.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$UnlockDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $UnlockDir)
$SrcDir    = Join-Path $RepoRoot 'android\app\src\main\java\com\tctools\unlock\protocol'
$ToolDir   = Join-Path $env:TEMP 'dsh-verify-tools'
$ClassDir  = Join-Path $ToolDir 'android-interop-classes'
$EvDir     = Join-Path $UnlockDir 'evidence'
$WorkDir   = Join-Path $EvDir 'interop'
New-Item -ItemType Directory -Force -Path $EvDir, $WorkDir | Out-Null

function Fail($msg) { Write-Host "`n[FAIL] $msg" -ForegroundColor Red; exit 1 }

# ---- 定位 JDK / Kotlin 编译器 ----------------------------------------------
$JavaExe = Get-ChildItem (Join-Path $RepoRoot '..\toolchain\jdk17') -Directory -ErrorAction SilentlyContinue |
           ForEach-Object { Join-Path $_.FullName 'bin\java.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $JavaExe) {
    if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) { $JavaExe = Join-Path $env:JAVA_HOME 'bin\java.exe' }
    else { Fail "找不到 JDK 17（toolchain\jdk17 或 JAVA_HOME）" }
}
$KotlinCp = @('kotlin-compiler-embeddable.jar','kotlin-stdlib.jar','kotlin-reflect.jar',
              'kotlin-script-runtime.jar','kotlin-daemon-embeddable.jar',
              'kotlinx-coroutines-core-jvm.jar','annotations.jar','trove4j.jar') |
    ForEach-Object { Join-Path $ToolDir $_ }
foreach ($j in $KotlinCp) { if (-not (Test-Path $j)) { Fail "缺少 $j" } }
$StdlibJar = Join-Path $ToolDir 'kotlin-stdlib.jar'

$DotnetExe = Join-Path $RepoRoot '..\toolchain\dotnet8\dotnet.exe'
if (-not (Test-Path $DotnetExe)) { Fail "找不到 $DotnetExe" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$transcript = New-Object System.Collections.Generic.List[string]
function Emit($line) { $line | ForEach-Object { $transcript.Add($_) }; Write-Host $line }

Emit "=== 双向互通测试（真实产品代码）==="
Emit "java   : $JavaExe"
Emit "dotnet : $DotnetExe"

# ---- 1. 编译 Android 端（产品源码 + 互通宿主）-------------------------------
Emit "`n-- [1/4] 编译 Android 端产品源码 + InteropMain.kt --"
if (Test-Path $ClassDir) { Remove-Item $ClassDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ClassDir | Out-Null
$Sources = @(Get-ChildItem $SrcDir -Filter *.kt | Sort-Object Name | Select-Object -ExpandProperty FullName)
$Sources += (Join-Path $UnlockDir 'android-interop\InteropMain.kt')
$compileArgs = @('-cp', ($KotlinCp -join ';'), 'org.jetbrains.kotlin.cli.jvm.K2JVMCompiler',
                 '-no-stdlib', '-nowarn', '-classpath', $StdlibJar, '-d', $ClassDir) + $Sources
$cOut = & $JavaExe @compileArgs 2>&1
if ($LASTEXITCODE -ne 0) { $cOut | ForEach-Object { Emit $_ }; Fail "Android 端编译失败" }
Emit "编译成功（$($Sources.Count) 个源文件，仅引用产品源码，未修改）"

function Run-Android([string[]]$a) {
    $r = & $JavaExe -cp "$ClassDir;$StdlibJar" com.tctools.unlock.protocol.InteropMainKt @a 2>&1
    $script:andExit = $LASTEXITCODE
    return $r
}

# ---- 2. Android 生成 counter=2 帧 → Windows 解 ---------------------------------
Emit "`n-- [2/4] Android 端 seal(counter=2, kind=ping) --"
$andF2 = Join-Path $WorkDir 'android-frame-c2.json'
$r = Run-Android @('--emit','2','ping',$andF2)
if ($script:andExit -ne 0) { $r | ForEach-Object { Emit $_ }; Fail "Android emit 失败" }
$r | ForEach-Object { Emit $_ }

Emit "`n-- Windows 端 TryOpen(Android 的帧, 期望 counter=2) --"
$winOpen = & $DotnetExe run --project (Join-Path $UnlockDir 'winproto-harness') -v quiet -- --open $andF2 2 2>&1
$winOpenExit = $LASTEXITCODE
$winOpen | ForEach-Object { Emit $_ }
[System.IO.File]::WriteAllText((Join-Path $EvDir 'interop-win-opens-android.json'), ($winOpen -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
if ($winOpenExit -ne 0) { Fail "Windows 端无法解开 Android 端的帧（exit=$winOpenExit）" }
Emit "[ok] Windows 端成功解开 Android 端的 counter=2 帧"
$script:t1 = $true

# ---- 3. Windows 生成 counter=3 帧 → Android 解 ---------------------------------
Emit "`n-- [3/4] Windows 端 Seal(counter=3, kind=bye) --"
$winF3 = Join-Path $WorkDir 'windows-frame-c3.json'
$emitOut = & $DotnetExe run --project (Join-Path $UnlockDir 'winproto-harness') -v quiet -- --emit 3 bye $winF3 2>&1
if ($LASTEXITCODE -ne 0) { $emitOut | ForEach-Object { Emit $_ }; Fail "Windows emit 失败" }
$emitOut | ForEach-Object { Emit $_ }

Emit "`n-- Android 端 openWithEmbeddedIv(Windows 的帧, 期望 counter=3) --"
$andOpen = Run-Android @('--open',$winF3,'3')
$andOpenExit = $script:andExit
$andOpen | ForEach-Object { Emit $_ }
[System.IO.File]::WriteAllText((Join-Path $EvDir 'interop-android-opens-windows.json'), ($andOpen -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
if ($andOpenExit -ne 0) { Fail "Android 端无法解开 Windows 端的帧（exit=$andOpenExit）" }
Emit "[ok] Android 端成功解开 Windows 端的 counter=3 帧"

# ---- 4. 负例：counter 期望值故意写错，必须被判定失败 ---------------------------
Emit "`n-- [4/4] 负例：把期望 counter 写成 9，两端都必须拒绝 --"
$negWin = & $DotnetExe run --project (Join-Path $UnlockDir 'winproto-harness') -v quiet -- --open $andF2 9 2>&1
$negWinExit = $LASTEXITCODE
$negAnd = Run-Android @('--open',$andF2,'9')
$negAndExit = $script:andExit
Emit "Windows 期望 counter=9 → exit=$negWinExit (应为 1)"
Emit "Android 期望 counter=9 → exit=$negAndExit (应为 1)"
if ($negWinExit -eq 0 -or $negAndExit -eq 0) { Fail "负例未被拒绝：counter 校验可能失效" }
Emit "[ok] 负例被正确拒绝（counter 不匹配不会误判为成功）"

Emit "`n=== 双向互通测试：全部通过 ==="
[System.IO.File]::WriteAllText((Join-Path $EvDir 'interop-transcript.txt'), ($transcript -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "证据: $(Join-Path $EvDir 'interop-transcript.txt')" -ForegroundColor Cyan
exit 0
