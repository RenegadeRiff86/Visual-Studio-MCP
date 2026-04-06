param(
    [string]$OutputDirectory,

    [string]$WindowTitleContains = "",

    [int]$ProcessId,

    [int]$PollIntervalMs = 1000,

    [int]$ConsecutiveUnresponsiveSamples = 3,

    [int]$CooldownSeconds = 30,

    [int]$MaxCaptures = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-OutputDirectory {
    if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
        return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($OutputDirectory))
    }

    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    return Join-Path $repoRoot "artifacts\freeze-captures"
}

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

function Select-VisualStudioProcess {
    $candidates = @(Get-VisualStudioCandidateProcesses)
    if ($candidates.Count -eq 0) {
        throw "No Visual Studio window matched the requested filters."
    }

    return $candidates | Sort-Object StartTime -Descending | Select-Object -First 1
}

function New-FreezeSnapshot {
    param(
        [System.Diagnostics.Process]$TargetProcess,
        [string]$CaptureDirectory,
        [System.Collections.Generic.List[object]]$RecentSamples
    )

    $timestamp = Get-Date
    $stamp = $timestamp.ToString("yyyyMMdd-HHmmss")
    $screenshotPath = Join-Path $CaptureDirectory ("vs-freeze-{0}.png" -f $stamp)
    $jsonPath = Join-Path $CaptureDirectory ("vs-freeze-{0}.json" -f $stamp)
    $captureScript = Join-Path $PSScriptRoot "capture-vs-window.ps1"

    & powershell -ExecutionPolicy Bypass -File $captureScript -OutputPath $screenshotPath -ProcessId $TargetProcess.Id | Out-Null

    $payload = [ordered]@{
        capturedAt = $timestamp.ToString("O")
        processId = $TargetProcess.Id
        processName = $TargetProcess.ProcessName
        mainWindowTitle = $TargetProcess.MainWindowTitle
        responding = $TargetProcess.Responding
        startTime = $TargetProcess.StartTime.ToString("O")
        totalProcessorTime = $TargetProcess.TotalProcessorTime.ToString()
        workingSetBytes = $TargetProcess.WorkingSet64
        privateMemoryBytes = $TargetProcess.PrivateMemorySize64
        handleCount = $TargetProcess.HandleCount
        threadCount = $TargetProcess.Threads.Count
        samples = @($RecentSamples)
        screenshotPath = $screenshotPath
    }

    $payload | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding UTF8

    Write-Output ("Captured freeze snapshot for PID {0} at {1}" -f $TargetProcess.Id, $timestamp.ToString("T"))
    Write-Output ("Screenshot: {0}" -f $screenshotPath)
    Write-Output ("Snapshot: {0}" -f $jsonPath)
}

$resolvedOutputDirectory = Resolve-OutputDirectory
[System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null

$recentSamples = New-Object 'System.Collections.Generic.List[object]'
$consecutiveUnresponsiveCount = 0
$capturesTaken = 0
$lastCaptureAt = [DateTimeOffset]::MinValue

Write-Output ("Watching Visual Studio for responsiveness stalls. Output: {0}" -f $resolvedOutputDirectory)

while ($capturesTaken -lt $MaxCaptures) {
    try {
        $targetProcess = Select-VisualStudioProcess
    }
    catch {
        Write-Warning $_
        Start-Sleep -Milliseconds $PollIntervalMs
        continue
    }

    $sample = [ordered]@{
        timestamp = (Get-Date).ToString("O")
        processId = $targetProcess.Id
        responding = $targetProcess.Responding
        mainWindowTitle = $targetProcess.MainWindowTitle
        totalProcessorTime = $targetProcess.TotalProcessorTime.ToString()
    }

    $recentSamples.Add($sample)
    while ($recentSamples.Count -gt 20) {
        $recentSamples.RemoveAt(0)
    }

    if ($targetProcess.Responding) {
        $consecutiveUnresponsiveCount = 0
    }
    else {
        $consecutiveUnresponsiveCount++
    }

    $secondsSinceLastCapture = ([DateTimeOffset]::Now - $lastCaptureAt).TotalSeconds
    $canCapture = $secondsSinceLastCapture -ge $CooldownSeconds

    if ($consecutiveUnresponsiveCount -ge $ConsecutiveUnresponsiveSamples -and $canCapture) {
        New-FreezeSnapshot -TargetProcess $targetProcess -CaptureDirectory $resolvedOutputDirectory -RecentSamples $recentSamples
        $capturesTaken++
        $lastCaptureAt = [DateTimeOffset]::Now
    }

    Start-Sleep -Milliseconds $PollIntervalMs
}

Write-Output ("Reached MaxCaptures={0}. Stopping watcher." -f $MaxCaptures)
