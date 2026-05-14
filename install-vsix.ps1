# VSIX path — relative to this script (repo root)
$vsix = Join-Path $PSScriptRoot "src\VsIdeBridge\bin\Release\net472\VsIdeBridge.vsix"
if (-not (Test-Path $vsix)) {
    Write-Error "VSIX not found at $vsix — build the Release configuration first."
    exit 1
}

# Locate VSIXInstaller.exe via vswhere (covers all editions and prerelease installs)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$installer = $null
if (Test-Path $vswhere) {
    $installPath = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VSSDK -property installationPath 2>$null
    if ($installPath) {
        $candidate = Join-Path $installPath "Common7\IDE\VSIXInstaller.exe"
        if (Test-Path $candidate) { $installer = $candidate }
    }
}

# Fallback: scan all VS editions under Program Files
if (-not $installer) {
    $years = @('2026','2022','2019')
    $editions = @('Enterprise','Professional','Community','BuildTools','preview')
    foreach ($year in $years) {
        foreach ($ed in $editions) {
            $candidate = "${env:ProgramFiles}\Microsoft Visual Studio\$year\$ed\Common7\IDE\VSIXInstaller.exe"
            if (Test-Path $candidate) { $installer = $candidate; break }
        }
        if ($installer) { break }
    }
}

if (-not $installer) {
    Write-Error "Could not locate VSIXInstaller.exe. Ensure Visual Studio is installed."
    exit 1
}

Write-Host "Installing: $vsix"
Write-Host "Using:      $installer"

$proc = Start-Process -FilePath $installer -ArgumentList "/quiet", $vsix -Wait -PassThru
Write-Host "Exit code: $($proc.ExitCode)"

# Verify — search the current user's VS extension directories
$vsRoot = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio"
$found = Get-ChildItem $vsRoot -Recurse -Filter "VsIdeBridge.dll" -Depth 6 -ErrorAction SilentlyContinue
if ($found) {
    foreach ($f in $found) { Write-Host "FOUND: $($f.FullName)" }
} else {
    Write-Host "NOT FOUND in $vsRoot — install may have failed or VS needs a restart."
}
