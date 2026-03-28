param(
    [switch]$Build,
    [string]$Configuration = "Release",
    [string]$IsccPath = "",
    [string]$ScriptPath = "installer\inno\vs-ide-bridge.iss"
)

Set-StrictMode -Version Latest

function Normalize-ExtendedPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }

    if ($Path.StartsWith('\\?\')) {
        return $Path.Substring(4)
    }

    return $Path
}

$ErrorActionPreference = "Stop"
$scriptFilePath = if (-not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
    $PSCommandPath
} elseif (-not [string]::IsNullOrWhiteSpace($MyInvocation.MyCommand.Path)) {
    $MyInvocation.MyCommand.Path
} else {
    throw "Unable to determine the path of build-setup.ps1."
}

$scriptFilePath = Normalize-ExtendedPath $scriptFilePath

$scriptRoot = [System.IO.Path]::GetDirectoryName($scriptFilePath)
if ([string]::IsNullOrWhiteSpace($scriptRoot)) {
    $currentDirectory = Normalize-ExtendedPath ((Get-Location).ProviderPath)
    if (Test-Path ([System.IO.Path]::Combine($currentDirectory, "installer", "inno", "vs-ide-bridge.iss"))) {
        $repoRoot = [System.IO.Path]::GetFullPath($currentDirectory)
        $scriptRoot = [System.IO.Path]::Combine($repoRoot, "scripts")
    } else {
        throw "Unable to determine the repository root for build-setup.ps1."
    }
} else {
    $repoRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($scriptRoot, ".."))
}

# ── Optional build step ────────────────────────────────────────────────────────
if ($Build) {
    $buildBat = [System.IO.Path]::Combine($scriptRoot, "build.bat")
    Write-Information "Building $Configuration..." -InformationAction Continue
    & cmd /c "`"$buildBat`" $Configuration"
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed (exit $LASTEXITCODE). Aborting installer package."
    }
    Write-Information "Build succeeded." -InformationAction Continue
}

# ── Locate ISCC.exe ───────────────────────────────────────────────────────────
$isccCandidates = @(
    $IsccPath,
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -ne "" }

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6: https://jrsoftware.org/isdl.php"
}

# ── Locate .iss script ────────────────────────────────────────────────────────
$script = if ([System.IO.Path]::IsPathRooted($ScriptPath)) {
    $ScriptPath
} else {
    [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($repoRoot, $ScriptPath))
}

if (-not (Test-Path $script)) {
    throw "Inno Setup script not found: $script"
}

Write-Information "ISCC    : $iscc" -InformationAction Continue
Write-Information "Script  : $script" -InformationAction Continue
Write-Information "" -InformationAction Continue

& $iscc $script
exit $LASTEXITCODE
