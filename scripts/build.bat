@echo off
setlocal

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

set "ROOT=%~dp0.."
set "SOLUTION=%ROOT%\VsIdeBridge.sln"

:: Locate MSBuild via vswhere (works for Community, Professional, Enterprise, Preview)
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "%VSWHERE%" (
    echo ERROR: vswhere.exe not found. Install Visual Studio first.
    exit /b 1
)

for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -prerelease -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do (
    set "MSBUILD=%%I"
)

if not defined MSBUILD (
    echo ERROR: MSBuild not found via vswhere.
    exit /b 1
)

echo MSBuild : %MSBUILD%
echo Solution: %SOLUTION%
echo Config  : %CONFIG%
echo.

"%MSBUILD%" "%SOLUTION%" /restore /m /p:Configuration=%CONFIG%
exit /b %ERRORLEVEL%
