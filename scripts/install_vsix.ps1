param(
    [string]$Config = "Debug",
    [switch]$NoRelaunch,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$root         = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$vsix         = Join-Path $root "src\VsIdeBridge\bin\$Config\net472\VsIdeBridge.vsix"
$vsInstallDir = "${env:ProgramFiles}\Microsoft Visual Studio\18\Community"
$installer    = Join-Path $vsInstallDir "Common7\IDE\VSIXInstaller.exe"
$devenv       = Join-Path $vsInstallDir "Common7\IDE\devenv.exe"
$msbuild      = Join-Path $vsInstallDir "MSBuild\Current\Bin\MSBuild.exe"
$solution     = Join-Path $root "VsIdeBridge.sln"
$logFile      = Join-Path $env:TEMP "dd_VsIdeBridge_install.log"

# ── 1. Build ────────────────────────────────────────────────────────────────

if (-not $NoBuild) {
    if (-not (Test-Path $msbuild)) { throw "MSBuild not found: $msbuild" }
    Write-Host "Building $Config ..."
    & $msbuild $solution /restore /m /p:Configuration=$Config /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }
    Write-Host "Build OK."

    # Kill any lingering MSBuild worker nodes — VSIXInstaller refuses to run while they are alive
    Get-Process MSBuild -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

if (-not (Test-Path $vsix)) { throw "VSIX not found: $vsix" }
if (-not (Test-Path $installer)) { throw "VSIXInstaller not found: $installer" }

# ── 2. Capture running devenv solution paths ─────────────────────────────────

$solutionsOpen = @()
$devenvProcs   = @(Get-Process devenv -ErrorAction SilentlyContinue)

if ($devenvProcs.Count -gt 0) {
    foreach ($p in $devenvProcs) {
        try {
            $cmd = (Get-CimInstance -Query "SELECT CommandLine FROM Win32_Process WHERE ProcessId=$($p.Id)" -ErrorAction SilentlyContinue).CommandLine
            if ($cmd -match '"([^"]+\.sln)"') { $solutionsOpen += $matches[1] }
            elseif ($cmd -match '\b(\S+\.sln)\b') { $solutionsOpen += $matches[1] }
        } catch {}
    }

    Write-Host "Closing $($devenvProcs.Count) Visual Studio instance(s) ..."

    # Graceful close first (no dialog — processes are headless in this context)
    $devenvProcs | ForEach-Object {
        try { $_.CloseMainWindow() | Out-Null } catch {}
    }

    # Give it 4 s to close cleanly
    $deadline = (Get-Date).AddSeconds(4)
    while ((Get-Date) -lt $deadline -and (Get-Process devenv -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 500
    }

    # Force-kill anything still alive
    Get-Process devenv -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Write-Host "Visual Studio closed."
}

# ── 3. Install ───────────────────────────────────────────────────────────────

Write-Host "Installing: $vsix"
Write-Host "Log:        $logFile"

if (Test-Path $logFile) { Remove-Item $logFile -Force }

$proc = Start-Process -FilePath $installer `
    -ArgumentList "/quiet `"$vsix`" /logFile:`"$logFile`"" `
    -Wait -PassThru

if ($proc.ExitCode -ne 0) {
    Write-Host "Install failed (exit $($proc.ExitCode)). Last lines from log:"
    if (Test-Path $logFile) { Get-Content $logFile | Select-Object -Last 30 | Write-Host }
    exit $proc.ExitCode
}

Write-Host "Installed successfully."

# ── 4. Relaunch ──────────────────────────────────────────────────────────────

if (-not $NoRelaunch -and (Test-Path $devenv)) {
    if ($solutionsOpen.Count -gt 0) {
        foreach ($sln in $solutionsOpen) {
            Write-Host "Relaunching VS with: $sln"
            Start-Process -FilePath $devenv -ArgumentList "`"$sln`""
        }
    } else {
        Write-Host "Relaunching VS ..."
        Start-Process -FilePath $devenv
    }
}
