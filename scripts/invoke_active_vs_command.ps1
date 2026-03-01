param(
    [string]$SolutionContains = "",

    [Parameter(Mandatory = $true)]
    [string]$CommandName,

    [string]$CommandArgs = "",

    [string]$OutputPath = "",

    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputPath) -and $CommandName -match "^Tools(\.Tools)?\.Ide") {
    $safeName = ($CommandName -replace "[^A-Za-z0-9._-]", "_")
    $OutputPath = Join-Path $env:TEMP "vs-ide-bridge\$safeName.active.json"
}

function Acquire-BridgeMutex {
    param(
        [int]$TimeoutSeconds = 120
    )

    $mutex = New-Object System.Threading.Mutex($false, "Global\VsIdeBridge.VisualStudio18.Automation")
    $acquired = $false

    try {
        $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))
    }
    catch [System.Threading.AbandonedMutexException] {
        $acquired = $true
    }

    if (-not $acquired) {
        $mutex.Dispose()
        throw "Timed out waiting for the VS IDE Bridge automation lock."
    }

    return $mutex
}

function Release-BridgeMutex {
    param(
        $Mutex
    )

    if ($null -eq $Mutex) {
        return
    }

    try {
        $Mutex.ReleaseMutex()
    }
    catch {
    }
    finally {
        $Mutex.Dispose()
    }
}

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class RunningObjectTableHelper
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    public static string[] GetDisplayNames(string prefix)
    {
        var names = new List<string>();
        IRunningObjectTable rot;
        IEnumMoniker enumMoniker;

        GetRunningObjectTable(0, out rot);
        rot.EnumRunning(out enumMoniker);
        enumMoniker.Reset();

        IMoniker[] monikers = new IMoniker[1];
        IntPtr fetched = IntPtr.Zero;

        while (enumMoniker.Next(1, monikers, fetched) == 0)
        {
            IBindCtx bindContext;
            string displayName;
            CreateBindCtx(0, out bindContext);
            monikers[0].GetDisplayName(bindContext, null, out displayName);
            if (prefix == null || displayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(displayName);
            }
        }

        return names.ToArray();
    }

    public static object GetByDisplayName(string displayName)
    {
        IRunningObjectTable rot;
        IEnumMoniker enumMoniker;

        GetRunningObjectTable(0, out rot);
        rot.EnumRunning(out enumMoniker);
        enumMoniker.Reset();

        IMoniker[] monikers = new IMoniker[1];
        IntPtr fetched = IntPtr.Zero;

        while (enumMoniker.Next(1, monikers, fetched) == 0)
        {
            IBindCtx bindContext;
            string currentDisplayName;
            object runningObject;

            CreateBindCtx(0, out bindContext);
            monikers[0].GetDisplayName(bindContext, null, out currentDisplayName);
            if (string.Equals(currentDisplayName, displayName, StringComparison.OrdinalIgnoreCase))
            {
                rot.GetObject(monikers[0], out runningObject);
                return runningObject;
            }
        }

        return null;
    }
}
"@

function Get-TargetDte {
    $instances = @()
    foreach ($displayName in [RunningObjectTableHelper]::GetDisplayNames("!VisualStudio.DTE.18.0")) {
        try {
            $dte = [RunningObjectTableHelper]::GetByDisplayName($displayName)
            if ($null -eq $dte) {
                continue
            }

            $solutionPath = ""
            try {
                $solutionPath = $dte.Solution.FullName
            }
            catch {
            }

            $instances += [PSCustomObject]@{
                DisplayName = $displayName
                Dte = $dte
                SolutionPath = $solutionPath
            }
        }
        catch {
        }
    }

    if ($instances.Count -eq 0) {
        throw "No Visual Studio 18 DTE instances found."
    }

    if ([string]::IsNullOrWhiteSpace($SolutionContains)) {
        return $instances[0]
    }

    $match = $instances |
        Where-Object { $_.SolutionPath -and $_.SolutionPath.IndexOf($SolutionContains, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        Select-Object -First 1
    if ($null -eq $match) {
        throw "No Visual Studio 18 DTE instance matched '$SolutionContains'."
    }

    return $match
}

function Resolve-CommandName {
    param(
        [Parameter(Mandatory = $true)]
        $Dte,

        [Parameter(Mandatory = $true)]
        [string]$RequestedName
    )

    $candidates = @($RequestedName)
    if ($RequestedName -like "Tools.*" -and $RequestedName -notlike "Tools.Tools.*") {
        $candidates += ($RequestedName -replace "^Tools\.", "Tools.Tools.")
    }

    foreach ($candidate in $candidates) {
        $command = $Dte.Commands | Where-Object { $_.Name -eq $candidate } | Select-Object -First 1
        if ($null -ne $command) {
            return $command.Name
        }
    }

    throw "Command not found: $RequestedName"
}

function Wait-ForOutputFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path -LiteralPath $Path) -and (Get-Item -LiteralPath $Path).Length -gt 0) {
            return
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out waiting for output: $Path"
}

$bridgeMutex = $null

try {
    $bridgeMutex = Acquire-BridgeMutex -TimeoutSeconds $TimeoutSeconds

    $target = Get-TargetDte
    $resolvedCommand = Resolve-CommandName -Dte $target.Dte -RequestedName $CommandName

    $fullCommandArgs = if ([string]::IsNullOrWhiteSpace($CommandArgs)) { "" } else { $CommandArgs.Trim() }
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $resolvedOutputPath = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($OutputPath))
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedOutputPath)) | Out-Null
        if (Test-Path -LiteralPath $resolvedOutputPath) {
            Remove-Item -LiteralPath $resolvedOutputPath -Force
        }

        if ($fullCommandArgs.Length -gt 0) {
            $fullCommandArgs += " "
        }

        $fullCommandArgs += "--out ""$resolvedOutputPath"""
    }

    $target.Dte.ExecuteCommand($resolvedCommand, $fullCommandArgs)

    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        Wait-ForOutputFile -Path $resolvedOutputPath -TimeoutSeconds $TimeoutSeconds
        $json = Get-Content -LiteralPath $resolvedOutputPath -Raw | ConvertFrom-Json
        Write-Host ("command={0}" -f $json.Command)
        Write-Host ("success={0}" -f $json.Success.ToString().ToLowerInvariant())
        Write-Host ("summary={0}" -f $json.Summary)
        if ($null -ne $json.Error) {
            Write-Host ("error_code={0}" -f $json.Error.code)
        }
    }
    else {
        Write-Host ("command={0}" -f $resolvedCommand)
        Write-Host "success=true"
        Write-Host "summary=Visual Studio command executed."
    }
}
finally {
    Release-BridgeMutex -Mutex $bridgeMutex
}
