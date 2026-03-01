@echo off
setlocal

if "%~1"=="" (
    echo Usage: read_bridge_result.bat ^<path-to-command-envelope.json^>
    exit /b 2
)

set "ROOT=%~dp0.."
set "PROBE=%ROOT%\src\IdeBridgeJsonProbe\bin\x64\Debug\IdeBridgeJsonProbe.exe"
if not exist "%PROBE%" set "PROBE=%ROOT%\src\IdeBridgeJsonProbe\bin\x64\Release\IdeBridgeJsonProbe.exe"

if not exist "%PROBE%" (
    echo IdeBridgeJsonProbe.exe not found. Build the solution first with scripts\build_vsix.bat
    exit /b 2
)

"%PROBE%" --input "%~1"
exit /b %ERRORLEVEL%
