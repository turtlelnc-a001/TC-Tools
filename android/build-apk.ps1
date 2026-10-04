# TC-tools Unlock — Android 一键构建脚本（Windows PowerShell）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File TC-tools\android\build-apk.ps1            # 构建 debug APK
#   powershell -ExecutionPolicy Bypass -File TC-tools\android\build-apk.ps1 -SelfTest  # 只跑协议回环自测
#   powershell -ExecutionPolicy Bypass -File TC-tools\android\build-apk.ps1 -UnitTest  # 只跑单元测试
#
# 说明：本机没有 JDK/Android SDK，工具链安装在 ASCII 路径下（中文路径对 aapt2 不友好）：
#   JAVA_HOME        = C:\tctoolchain\jdk17
#   ANDROID_HOME     = C:\tctoolchain\android-sdk
#   GRADLE_USER_HOME = C:\tctoolchain\gradle-home
#   Gradle           = C:\tctoolchain\gradle-8.14\bin\gradle.bat   （本机无法访问 services.gradle.org，故不用 wrapper）
param(
    [switch]$SelfTest,
    [switch]$UnitTest,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$root        = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir  = Join-Path $root 'app'
$distDir     = Join-Path (Split-Path -Parent $root) 'dist\android'
$jdkHome     = 'C:\tctoolchain\jdk17'
$sdkRoot     = 'C:\tctoolchain\android-sdk'
$gradleHome  = 'C:\tctoolchain\gradle-home'
$gradleBat   = 'C:\tctoolchain\gradle-8.14\bin\gradle.bat'

foreach ($p in @($jdkHome, $sdkRoot, $gradleBat)) {
    if (-not (Test-Path $p)) { throw "缺少工具链组件：$p（请先运行 toolchain\fetch-toolchain.ps1）" }
}

$env:JAVA_HOME        = $jdkHome
$env:ANDROID_HOME     = $sdkRoot
$env:ANDROID_SDK_ROOT = $sdkRoot
$env:GRADLE_USER_HOME = $gradleHome
$env:Path             = "$jdkHome\bin;$sdkRoot\platform-tools;$env:Path"

# local.properties（保证 sdk.dir 指向实际 SDK）
[System.IO.File]::WriteAllText(
    (Join-Path $root 'local.properties'),
    "sdk.dir=" + ($sdkRoot -replace '\\', '/') + "`n"
)

Push-Location $root
try {
    if ($Clean) {
        & $gradleBat --no-daemon clean
    }
    if ($SelfTest) {
        & $gradleBat --no-daemon :app:selfTest
        Write-Host "自测 JSON: $(Join-Path $projectDir 'build\selftest\vectors-android.json')"
        exit $LASTEXITCODE
    }
    if ($UnitTest) {
        & $gradleBat --no-daemon :app:testDebugUnitTest
        exit $LASTEXITCODE
    }

    & $gradleBat --no-daemon :app:assembleDebug
    if ($LASTEXITCODE -ne 0) { throw "gradle assembleDebug 失败（exit $LASTEXITCODE）" }

    $apk = Join-Path $projectDir 'build\outputs\apk\debug\app-debug.apk'
    if (-not (Test-Path $apk)) { throw "未找到 APK：$apk" }

    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    $target = Join-Path $distDir 'TC-Tools-Unlock-0.1.0.apk'
    Copy-Item $apk $target -Force

    $size = (Get-Item $target).Length
    Write-Host ""
    Write-Host "=== 构建成功 ==="
    Write-Host ("APK: {0}" -f $target)
    Write-Host ("大小: {0:N0} 字节 ({1:N2} MB)" -f $size, ($size / 1MB))
    Write-Host "安装: adb install -r `"$target`""
} finally {
    Pop-Location
}
