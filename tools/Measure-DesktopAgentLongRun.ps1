param(
    [ValidateRange(1, 1440)]
    [int]$DurationMinutes = 30,

    [ValidateRange(1, 300)]
    [int]$IntervalSeconds = 10,

    [string]$ProcessName = "DesktopAgent.WinUI",

    [string]$OutputPath = (Join-Path $PWD ("artifacts\m5-07\long-run-{0}.csv" -f (Get-Date -Format "yyyyMMdd-HHmmss")))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not ("DesktopAgentLongRun.NativeGuiResources" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace DesktopAgentLongRun
{
    public static class NativeGuiResources
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    }
}
"@
}

function Get-SingleProcess([string]$Name) {
    $matches = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    if ($matches.Count -eq 0) {
        throw "Process '$Name' was not found. Start DesktopAgent WinUI before sampling."
    }

    if ($matches.Count -gt 1) {
        $ids = ($matches | ForEach-Object Id) -join ", "
        throw "Multiple '$Name' processes were found: $ids. M5-07 sampling requires a single instance."
    }

    return $matches[0]
}

function Get-GuiResourceCount([System.Diagnostics.Process]$Process, [uint32]$Flag) {
    try {
        return [DesktopAgentLongRun.NativeGuiResources]::GetGuiResources($Process.Handle, $Flag)
    }
    catch {
        return $null
    }
}

function Get-Median([double[]]$Values) {
    if ($Values.Count -eq 0) {
        return $null
    }

    $sorted = @($Values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }

    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

$process = Get-SingleProcess $ProcessName
$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

if (Test-Path $OutputPath) {
    Remove-Item -Force $OutputPath
}

$logicalProcessorCount = [Environment]::ProcessorCount
$startedAt = [DateTimeOffset]::UtcNow
$deadline = $startedAt.AddMinutes($DurationMinutes)
$previousTimestamp = $startedAt
$previousCpuSeconds = $process.TotalProcessorTime.TotalSeconds
$sampleIndex = 0

Write-Host "M5-07 long-run sampling started"
Write-Host "Process : $ProcessName (PID $($process.Id))"
Write-Host "Duration: $DurationMinutes min"
Write-Host "Interval: $IntervalSeconds sec"
Write-Host "CSV     : $OutputPath"
Write-Host ""

while ([DateTimeOffset]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds $IntervalSeconds

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($process.Id)
        $process.Refresh()
    }
    catch {
        throw "Sampled process PID $($process.Id) exited. Existing samples remain at '$OutputPath'."
    }

    $now = [DateTimeOffset]::UtcNow
    $cpuSeconds = $process.TotalProcessorTime.TotalSeconds
    $sampleElapsedSeconds = [math]::Max(0.001, ($now - $previousTimestamp).TotalSeconds)
    $cpuDeltaSeconds = [math]::Max(0.0, $cpuSeconds - $previousCpuSeconds)
    $normalizedCpuPercent = ($cpuDeltaSeconds / $sampleElapsedSeconds / $logicalProcessorCount) * 100.0

    $sample = [pscustomobject]@{
        Sample              = $sampleIndex
        TimestampUtc        = $now.ToString("O")
        UptimeSeconds       = [math]::Round(($now - $startedAt).TotalSeconds, 1)
        CpuPercent          = [math]::Round($normalizedCpuPercent, 3)
        WorkingSetMiB       = [math]::Round($process.WorkingSet64 / 1MB, 2)
        PrivateMemoryMiB    = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
        HandleCount         = $process.HandleCount
        ThreadCount         = $process.Threads.Count
        GdiObjects          = Get-GuiResourceCount $process 0
        UserObjects         = Get-GuiResourceCount $process 1
    }

    $sample | Export-Csv -Path $OutputPath -NoTypeInformation -Encoding UTF8 -Append

    $line = "[{0:HH:mm:ss}] CPU {1,6:N2}% | WS {2,8:N2} MiB | Private {3,8:N2} MiB | Handles {4,5} | GDI {5,5} | USER {6,5}" -f (Get-Date), $sample.CpuPercent, $sample.WorkingSetMiB, $sample.PrivateMemoryMiB, $sample.HandleCount, $sample.GdiObjects, $sample.UserObjects
    Write-Host $line

    $sampleIndex++
    $previousTimestamp = $now
    $previousCpuSeconds = $cpuSeconds
}

$rows = @(Import-Csv -Path $OutputPath)
if ($rows.Count -eq 0) {
    throw "Sampling completed without producing any samples."
}

$first = $rows[0]
$last = $rows[-1]
$cpuValues = @($rows | ForEach-Object { [double]$_.CpuPercent })
$privateValues = @($rows | ForEach-Object { [double]$_.PrivateMemoryMiB })
$workingSetValues = @($rows | ForEach-Object { [double]$_.WorkingSetMiB })

$summary = [pscustomobject]@{
    Samples              = $rows.Count
    CpuMedianPercent     = [math]::Round((Get-Median $cpuValues), 3)
    CpuMaxPercent        = [math]::Round(($cpuValues | Measure-Object -Maximum).Maximum, 3)
    WorkingSetStartMiB   = [double]$first.WorkingSetMiB
    WorkingSetEndMiB     = [double]$last.WorkingSetMiB
    WorkingSetDriftMiB   = [math]::Round(([double]$last.WorkingSetMiB - [double]$first.WorkingSetMiB), 2)
    WorkingSetMaxMiB     = [math]::Round(($workingSetValues | Measure-Object -Maximum).Maximum, 2)
    PrivateStartMiB      = [double]$first.PrivateMemoryMiB
    PrivateEndMiB        = [double]$last.PrivateMemoryMiB
    PrivateDriftMiB      = [math]::Round(([double]$last.PrivateMemoryMiB - [double]$first.PrivateMemoryMiB), 2)
    PrivateMaxMiB        = [math]::Round(($privateValues | Measure-Object -Maximum).Maximum, 2)
    HandleDrift          = ([int]$last.HandleCount - [int]$first.HandleCount)
    ThreadDrift          = ([int]$last.ThreadCount - [int]$first.ThreadCount)
    GdiObjectDrift       = if ($first.GdiObjects -ne "" -and $last.GdiObjects -ne "") { [int]$last.GdiObjects - [int]$first.GdiObjects } else { $null }
    UserObjectDrift      = if ($first.UserObjects -ne "" -and $last.UserObjects -ne "") { [int]$last.UserObjects - [int]$first.UserObjects } else { $null }
}

Write-Host ""
Write-Host "M5-07 long-run summary"
$summary | Format-List | Out-Host
Write-Host "Raw samples: $OutputPath"
