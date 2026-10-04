<#
.SYNOPSIS
  独立运行 Android 端**产品源码**（纯 JVM 部分）的协议回环自测 —— verify/task-4。

.DESCRIPTION
  不复制、不改写任何产品代码：直接用 Kotlin 编译器编译 android/app/src/main/java/
  com/tctools/unlock/protocol/ 下的 .kt 源文件，然后运行 SelfTestMain。

  为什么不用 gradlew：Android 单元测试需要 AGP + Android SDK；而协议核心是纯 JVM
  （不 import android.*），用独立 kotlinc 就能编译运行，验证成本更低、且与 mobile
  自己的构建相互独立（我不占用他们的构建目录，产物落在 $env:TEMP）。

  编译产物写到 $env:TEMP\dsh-verify-tools\android-classes，不污染仓库。

.PARAMETER OutJson
  自测 JSON 输出路径，默认 tests\unlock\evidence\android-selftest-raw.json

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File TC-tools\tests\unlock\run-android-selftest.ps1
#>
[CmdletBinding()]
param(
    [string]$OutJson
)

$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$UnlockDir  = Join-Path $RepoRoot 'tests\unlock'
$SrcDir     = Join-Path $RepoRoot 'android\app\src\main\java\com\tctools\unlock\protocol'
$ToolDir    = Join-Path $env:TEMP 'dsh-verify-tools'
$ClassDir   = Join-Path $ToolDir 'android-classes'
if (-not $OutJson) { $OutJson = Join-Path $UnlockDir 'evidence\android-selftest-raw.json' }

# ---- 定位 JDK --------------------------------------------------------------
$JavaExe = Join-Path $RepoRoot '..\toolchain\jdk17\jdk-17.0.13+11\bin\java.exe'
if (-not (Test-Path $JavaExe)) {
    $cand = Get-ChildItem (Join-Path $RepoRoot '..\toolchain\jdk17') -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'bin\java.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($cand) { $JavaExe = $cand }
    elseif ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) { $JavaExe = Join-Path $env:JAVA_HOME 'bin\java.exe' }
    else { throw "找不到 JDK 17。请设置 JAVA_HOME 或确认 toolchain\jdk17\ 已解压。" }
}

# ---- 定位 Kotlin 编译器 ----------------------------------------------------
$KotlinCp = @('kotlin-compiler-embeddable.jar','kotlin-stdlib.jar','kotlin-reflect.jar',
              'kotlin-script-runtime.jar','kotlin-daemon-embeddable.jar',
              'kotlinx-coroutines-core-jvm.jar','annotations.jar','trove4j.jar') |
    ForEach-Object { Join-Path $ToolDir $_ }
foreach ($j in $KotlinCp) {
    if (-not (Test-Path $j)) {
        throw "缺少 $j。下载命令见 tests\unlock\verify-report.md（repo.maven.apache.org 上 kotlin-*-2.0.21.jar）。"
    }
}
$StdlibJar = Join-Path $ToolDir 'kotlin-stdlib.jar'

# ---- 产品源文件（只读引用）-------------------------------------------------
$Sources = Get-ChildItem $SrcDir -Filter *.kt | Sort-Object Name | Select-Object -ExpandProperty FullName
if (-not $Sources) { throw "找不到产品源码：$SrcDir" }

Write-Host "== 独立运行 Android 端产品源码回环自测 ==" -ForegroundColor Cyan
Write-Host "java    : $JavaExe"
Write-Host "sources : $($Sources.Count) 个 .kt（只读）"
$Sources | ForEach-Object { Write-Host "          $([System.IO.Path]::GetFileName($_))" }
Write-Host "classes : $ClassDir"

if (Test-Path $ClassDir) { Remove-Item $ClassDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ClassDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $OutJson -Parent) | Out-Null

# ---- 编译 ------------------------------------------------------------------
Write-Host "`n-- kotlinc 编译 --" -ForegroundColor Cyan
$compileArgs = @(
    '-cp', ($KotlinCp -join ';'),
    'org.jetbrains.kotlin.cli.jvm.K2JVMCompiler',
    '-no-stdlib', '-nowarn',
    '-classpath', $StdlibJar,
    '-d', $ClassDir
) + $Sources
& $JavaExe @compileArgs
if ($LASTEXITCODE -ne 0) { throw "kotlinc 编译失败（exit=$LASTEXITCODE）" }

# ---- 运行 ------------------------------------------------------------------
Write-Host "`n-- 运行 SelfTestMainKt --" -ForegroundColor Cyan
& $JavaExe -cp "$ClassDir;$StdlibJar" com.tctools.unlock.protocol.SelfTestMainKt $OutJson
$rc = $LASTEXITCODE
Write-Host "`nSELFTEST exit code = $rc (0=全部通过)"
if (Test-Path $OutJson) { Write-Host "JSON -> $OutJson ($((Get-Item $OutJson).Length) bytes)" }
exit $rc
