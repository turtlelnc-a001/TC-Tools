@echo off
rem ============================================================
rem  TCtools-installer build script
rem  Usage: build-installer.bat [path-to-makensis.exe]
rem
rem  If ..\tcyunlock\dist exists (the Bluetooth unlock component
rem  has been published there), it is bundled into <install>\unlock.
rem ============================================================
setlocal enabledelayedexpansion
set "MKNSIS=%~1"
if "%MKNSIS%"=="" set "MKNSIS=makensis"
pushd "%~dp0"
if not exist "..\dist" mkdir "..\dist"

set "UNLOCKFLAG="
set "UNLOCKINFO=not bundled (tcyunlock\dist not found)"
if exist "..\tcyunlock\dist\tctool-unlock.exe" (
  set "UNLOCKFLAG=/DHAVE_UNLOCK=1"
  set "UNLOCKINFO=bundled from tcyunlock\dist"
)

echo [Building ...] unlock component: !UNLOCKINFO!
"%MKNSIS%" !UNLOCKFLAG! TCtools-installer.nsi
if errorlevel 1 (
  echo Installer build FAILED. Build requires NSIS 3.x (Unicode).
  echo   https://nsis.sourceforge.io/
  exit /b 1
)
popd
echo [OK] ..\dist\TCtools-installer-0.2.0-rc2.exe
endlocal
