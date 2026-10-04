#Requires -Version 5.1
<#
.SYNOPSIS
    Builds tctool-unlock and produces the self-contained deliverable used by the
    C++ front-end and the installer.

.DESCRIPTION
    Two stages:
      1. `dotnet build -c Release`            -> bin\Release\...\tctool-unlock.exe
         (framework-dependent: needs a .NET 8 runtime, or DOTNET_ROOT pointing at
          a portable SDK such as toolchain\dotnet8)
      2. `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`
         -> dist\tctool-unlock.exe  (NO .NET runtime required on the target PC)

    The C++ side looks for the component at
      <exe dir>\unlock\tctool-unlock.exe, <exe dir>\tctool-unlock.exe,
      <exe dir>\..\tcyunlock\dist\tctool-unlock.exe, <exe dir>\tcyunlock\dist\tctool-unlock.exe, PATH
    so dist\ is the shipping location.

.PARAMETER DotnetRoot
    Directory holding dotnet.exe. Defaults to the portable SDK installed for
    this project; falls back to whatever `dotnet` is on PATH.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER SkipPublish
    Only run the (fast) framework-dependent build.

.PARAMETER NoSingleFile
    Publish self-contained but as a folder (dist\ gets the exe plus its DLLs).
    Use this if single-file publishing ever misbehaves.

.EXAMPLE
    pwsh -File build.ps1
#>
param(
    [string]$DotnetRoot = 'C:\Users\吴桥生\Videos\DSHworkarea\toolchain\dotnet8',
    [string]$Configuration = 'Release',
    [switch]$SkipPublish,
    [switch]$NoSingleFile
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here 'tctool-unlock.csproj'
$dist = Join-Path $here 'dist'

$dotnet = Join-Path $DotnetRoot 'dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) {
        $dotnet = $onPath.Source
        Write-Host "portable SDK not found at $DotnetRoot; using $dotnet"
    } else {
        throw "dotnet.exe not found. Install a .NET 8 SDK or pass -DotnetRoot <dir>."
    }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host "=== [1/2] dotnet build -c $Configuration ===" -ForegroundColor Cyan
& $dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$built = Join-Path $here "bin\$Configuration\net8.0-windows10.0.19041.0\tctool-unlock.exe"
Write-Host "built: $built"

if ($SkipPublish) {
    Write-Host "publish skipped (-SkipPublish)" -ForegroundColor Yellow
    return
}

Write-Host "=== [2/2] self-contained publish ($Configuration, win-x64) ===" -ForegroundColor Cyan
Write-Host "note: the first run downloads Microsoft.NETCore.App.Runtime.win-x64 from NuGet."
$publishDir = Join-Path $here 'publish'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $publishDir
)
if (-not $NoSingleFile) {
    $publishArgs += '-p:PublishSingleFile=true'
    $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
}

& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

# Replace dist\ with exactly what we just published (keep selftest-vectors.json
# and other evidence files that live there).
Get-ChildItem -Path $dist -Include 'tctool-unlock*', '*.dll', '*.json' -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'selftest-vectors.json' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Copy-Item -Path (Join-Path $publishDir '*') -Destination $dist -Recurse -Force

$exe = Join-Path $dist 'tctool-unlock.exe'
if (-not (Test-Path $exe)) { throw "expected $exe to exist after publish" }

$size = (Get-Item $exe).Length
Write-Host ""
Write-Host "deliverable: $exe  ($([math]::Round($size / 1MB, 1)) MB, self-contained)" -ForegroundColor Green
Write-Host ""
Write-Host "smoke test (no DOTNET_ROOT, no dotnet on PATH):"
$savedRoot = $env:DOTNET_ROOT
$env:DOTNET_ROOT = $null
try {
    & $exe selftest | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "self-contained selftest failed with exit code $LASTEXITCODE" }
} finally {
    $env:DOTNET_ROOT = $savedRoot
}
Write-Host "self-contained build OK" -ForegroundColor Green
