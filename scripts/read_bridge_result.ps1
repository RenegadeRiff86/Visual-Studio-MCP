param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath
)

$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$probeCandidates = @(
    (Join-Path $root "src\IdeBridgeJsonProbe\bin\x64\Debug\IdeBridgeJsonProbe.exe"),
    (Join-Path $root "src\IdeBridgeJsonProbe\bin\x64\Release\IdeBridgeJsonProbe.exe")
)

$probePath = $probeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($probePath)) {
    throw "IdeBridgeJsonProbe.exe not found. Build the solution first with scripts\build_vsix.bat"
}

$resolvedInput = [System.IO.Path]::GetFullPath($InputPath)
if (-not (Test-Path -LiteralPath $resolvedInput)) {
    throw "Input file not found: $resolvedInput"
}

& $probePath --input $resolvedInput
exit $LASTEXITCODE
