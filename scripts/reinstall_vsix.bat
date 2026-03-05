@echo off
setlocal

set "VSIXINSTALLER=C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe"
set "EXTENSION_ID=StanElston.VsIdeBridge"
set "VSIX_PATH=C:\Users\elsto\source\repos\vs-ide-bridge\src\VsIdeBridge\bin\Debug\net472\VsIdeBridge.vsix"

if not exist "%VSIXINSTALLER%" (
  echo ERROR: VSIXInstaller not found: %VSIXINSTALLER%
  exit /b 1
)

if not exist "%VSIX_PATH%" (
  echo ERROR: VSIX not found: %VSIX_PATH%
  exit /b 1
)

taskkill /F /IM devenv.exe >nul 2>nul
taskkill /F /IM VSIXInstaller.exe >nul 2>nul
taskkill /F /IM MSBuild.exe >nul 2>nul

echo Uninstalling %EXTENSION_ID%...
"%VSIXINSTALLER%" /q /shutdownprocesses /u:%EXTENSION_ID%
set "UNINSTALL_EXIT=%ERRORLEVEL%"
echo Uninstall exit code: %UNINSTALL_EXIT%

echo Installing %VSIX_PATH%...
"%VSIXINSTALLER%" /q /shutdownprocesses "%VSIX_PATH%"
set "INSTALL_EXIT=%ERRORLEVEL%"
echo Install exit code: %INSTALL_EXIT%

if not "%INSTALL_EXIT%"=="0" (
  exit /b %INSTALL_EXIT%
)

exit /b %UNINSTALL_EXIT%
