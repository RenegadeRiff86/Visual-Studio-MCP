param(
    [string]$OutputPath,

    [string]$WindowTitleContains = "",

    [int]$ProcessId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class NativeWindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
"@

function Get-VisualStudioCandidateProcesses {
    $processes = Get-Process -Name "devenv" -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 }

    if ($ProcessId -gt 0) {
        $processes = $processes | Where-Object { $_.Id -eq $ProcessId }
    }

    if (-not [string]::IsNullOrWhiteSpace($WindowTitleContains)) {
        $processes = $processes | Where-Object { $_.MainWindowTitle -like "*$WindowTitleContains*" }
    }

    return @($processes)
}

function Select-VisualStudioWindow {
    $candidates = @(Get-VisualStudioCandidateProcesses)
    if ($candidates.Count -eq 0) {
        throw "No Visual Studio window matched the requested filters."
    }

    $foregroundHandle = [NativeWindowCapture]::GetForegroundWindow()
    if ($foregroundHandle -ne [IntPtr]::Zero) {
        [uint32]$foregroundProcessId = 0
        [void][NativeWindowCapture]::GetWindowThreadProcessId($foregroundHandle, [ref]$foregroundProcessId)
        $foregroundMatch = $candidates | Where-Object { $_.Id -eq [int]$foregroundProcessId } | Select-Object -First 1
        if ($null -ne $foregroundMatch) {
            return $foregroundMatch
        }
    }

    return $candidates | Sort-Object StartTime -Descending | Select-Object -First 1
}

function Resolve-OutputPath {
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($OutputPath))
    }

    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $outputDirectory = Join-Path $repoRoot "artifacts\screenshots"
    $fileName = "vs-window-{0}.png" -f (Get-Date -Format "yyyyMMdd-HHmmss")
    return Join-Path $outputDirectory $fileName
}

$targetProcess = Select-VisualStudioWindow
$handle = [IntPtr]$targetProcess.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) {
    throw "The selected Visual Studio process does not have a main window handle."
}

$rect = New-Object NativeWindowCapture+RECT
if (-not [NativeWindowCapture]::GetWindowRect($handle, [ref]$rect)) {
    throw "Failed to read the Visual Studio window bounds."
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "The Visual Studio window bounds are invalid: ${width}x${height}."
}

$resolvedOutputPath = Resolve-OutputPath
$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($resolvedOutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output ("Captured Visual Studio window: {0}" -f $targetProcess.MainWindowTitle)
Write-Output ("Saved screenshot to: {0}" -f $resolvedOutputPath)
