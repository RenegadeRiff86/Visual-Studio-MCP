@echo off
setlocal

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

set "EXTENSION_ID=RenegadeRiff86.VsIdeBridge"
set "LEGACY_ID=StanElston.VsIdeBridge"
set "VSIX_PATH="

:: Locate VSIXInstaller via vswhere
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -prerelease -property installationPath 2^>nul`) do (
  set "VS_INSTALL=%%I"
)

if not defined VS_INSTALL (
  echo ERROR: Visual Studio installation not found via vswhere.
  exit /b 1
)

set "VSIXINSTALLER=%VS_INSTALL%\Common7\IDE\VSIXInstaller.exe"

if not exist "%VSIXINSTALLER%" (
  echo ERROR: VSIXInstaller not found: %VSIXINSTALLER%
  exit /b 1
)

:: Find VSIX: prefer Release, fall back to Debug
set "ROOT=%~dp0.."
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command ^
  "$candidates=@('%ROOT%\src\VsIdeBridge\bin\Release\net472\VsIdeBridge.vsix','^
  '%ROOT%\src\VsIdeBridge\bin\Debug\net472\VsIdeBridge.vsix') | Where-Object { Test-Path $_ }; ^
  if ($candidates.Count -gt 0) { $candidates | Sort-Object { (Get-Item $_).LastWriteTimeUtc } -Descending | Select-Object -First 1 | Write-Output }"`) do (
  set "VSIX_PATH=%%~I"
)

if not defined VSIX_PATH (
  echo ERROR: VSIX not found. Build the project first.
  exit /b 1
)

echo Config  : %CONFIG%
echo VSIX    : %VSIX_PATH%
echo Installer: %VSIXINSTALLER%
echo.

taskkill /F /IM devenv.exe >nul 2>nul
taskkill /F /IM VSIXInstaller.exe >nul 2>nul

echo Uninstalling legacy id %LEGACY_ID%...
"%VSIXINSTALLER%" /quiet /shutdownprocesses /uninstall:%LEGACY_ID%

echo Uninstalling %EXTENSION_ID%...
"%VSIXINSTALLER%" /quiet /shutdownprocesses /uninstall:%EXTENSION_ID%
set "UNINSTALL_EXIT=%ERRORLEVEL%"
echo Uninstall exit code: %UNINSTALL_EXIT%

echo Installing %VSIX_PATH%...
"%VSIXINSTALLER%" /quiet /shutdownprocesses "%VSIX_PATH%"
set "INSTALL_EXIT=%ERRORLEVEL%"
echo Install exit code: %INSTALL_EXIT%

exit /b %INSTALL_EXIT%
