<#
.SYNOPSIS
    One-command release build for Tessel (DesktopAgent.WinUI).

.DESCRIPTION
    Clean, build, test, publish a self-contained win-x64 bundle and zip it.
    All output goes under artifacts\release. Nothing outside artifacts\release,
    bin\ and obj\ is deleted.

.PARAMETER Version
    Override the version (default: read from Directory.Build.props).

.PARAMETER SkipTests
    Skip the Release test run.

.PARAMETER Clean
    Delete every bin/, obj/ and previous artifacts\release content first.

.PARAMETER Launch
    Start the published executable after a successful publish.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Clean

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Version 1.0.1 -Launch
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$Clean,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$solution = Join-Path $repoRoot 'DesktopAgent.sln'
$appProject = Join-Path $repoRoot 'src\DesktopAgent.WinUI\DesktopAgent.WinUI.csproj'
$testProject = Join-Path $repoRoot 'tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj'
$releaseRoot = Join-Path $repoRoot 'artifacts\release'

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "== $message" -ForegroundColor Cyan
}

function Assert-LastExit([string]$what) {
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed with exit code $LASTEXITCODE"
    }
}

# ---------------------------------------------------------------- version
if (-not $Version) {
    $Version = (& dotnet msbuild $appProject -getProperty:Version -nologo 2>$null | Select-Object -Last 1)
    $Version = "$Version".Trim()
}
if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+') {
    throw "Could not resolve a valid version (got '$Version'). Pass -Version x.y.z."
}

$publishDir = Join-Path $releaseRoot "Tessel-$Version-win-x64"
$zipPath = "$publishDir.zip"

Write-Host "Tessel release build v$Version" -ForegroundColor Green
Write-Host "  repository : $repoRoot"
Write-Host "  output     : $publishDir"

# ---------------------------------------------------------------- clean
if ($Clean) {
    Write-Step 'Clean'
    Get-ChildItem -Path $repoRoot -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object {
            try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop }
            catch { Write-Host "  skipped (locked): $($_.FullName)" -ForegroundColor Yellow }
        }
    if (Test-Path $releaseRoot) {
        Get-ChildItem $releaseRoot -Force | ForEach-Object {
            try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop }
            catch { Write-Host "  skipped (locked): $($_.FullName)" -ForegroundColor Yellow }
        }
    }
    Write-Host '  cleaned bin/obj and artifacts\release'
}

# ---------------------------------------------------------------- restore + build
Write-Step 'Restore'
& dotnet restore $solution | Out-Null
Assert-LastExit 'restore'

Write-Step 'Build (Release, x64)'
& dotnet build $solution -c Release -p:Platform=x64 --no-restore --nologo
Assert-LastExit 'build'

# ---------------------------------------------------------------- test
if (-not $SkipTests) {
    Write-Step 'Test (Release, x64)'
    & dotnet test $testProject -c Release -p:Platform=x64 --no-build --nologo
    Assert-LastExit 'test'
}
else {
    Write-Host ''
    Write-Host '== Test skipped (-SkipTests)' -ForegroundColor DarkYellow
}

# ---------------------------------------------------------------- publish
Write-Step 'Publish (self-contained, win-x64)'
$running = Get-Process -Name 'DesktopAgent.WinUI' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "  note: $($running.Count) running instance(s) detected; they may lock files." -ForegroundColor Yellow
}
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& dotnet publish $appProject -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:PublishReadyToRun=false -p:PublishTrimmed=false -o $publishDir --nologo
Assert-LastExit 'publish'

# ---------------------------------------------------------------- verify + zip
Write-Step 'Verify'
$exe = Join-Path $publishDir 'DesktopAgent.WinUI.exe'
if (-not (Test-Path $exe)) { throw "Publish output is missing DesktopAgent.WinUI.exe" }
$bootstrap = Join-Path $publishDir 'Microsoft.WindowsAppRuntime.Bootstrap.dll'
$selfContainedRuntime = Get-ChildItem $publishDir -Filter 'Microsoft.WindowsAppRuntime*.dll' -ErrorAction SilentlyContinue
$files = Get-ChildItem $publishDir -File -Recurse
$sizeMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  exe        : $exe"
Write-Host "  files      : $($files.Count)"
Write-Host "  size       : $sizeMb MB"
Write-Host "  bootstrap  : $(if ($bootstrap) { 'present' } else { 'MISSING' })"
Write-Host "  winapprt   : $((Get-ChildItem $publishDir -Filter 'Microsoft.WindowsAppRuntime*.dll' -ErrorAction SilentlyContinue).Count) runtime assemblies"

Write-Step 'Zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "  $zipPath ($zipMb MB)"

Write-Host ''
Write-Host "Release v$Version ready." -ForegroundColor Green
Write-Host "  folder : $publishDir"
Write-Host "  zip    : $zipPath"
Write-Host ''
Write-Host 'Deploy on a target machine: unzip anywhere and run DesktopAgent.WinUI.exe.'
Write-Host 'First run requires the full desktop grid calibration for every display.'

if ($Launch) {
    Write-Step 'Launch'
    Start-Process -FilePath $exe -WorkingDirectory $publishDir
    Write-Host '  started.'
}
