@echo off
setlocal

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install_vsix.ps1" -Config "%CONFIG%"
exit /b %ERRORLEVEL%
