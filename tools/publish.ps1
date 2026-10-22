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

.PARAMETER SkipTrim
    Keep the unused Windows App SDK machine-learning payload (onnxruntime,
    DirectML, Microsoft.Windows.AI.*). Dropping it saves roughly 40 MB.

.PARAMETER SkipInstaller
    Do not build the Inno Setup installer.

.PARAMETER IsccPath
    Path to ISCC.exe. Default: %LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe.

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
    [switch]$Launch,
    [switch]$SkipTrim,
    [switch]$SkipInstaller,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$solution = Join-Path $repoRoot 'DesktopAgent.sln'
$appProject = Join-Path $repoRoot 'src\DesktopAgent.WinUI\DesktopAgent.WinUI.csproj'
$testProject = Join-Path $repoRoot 'tests\DesktopAgent.Tests\DesktopAgent.Tests.csproj'
$releaseRoot = Join-Path $repoRoot 'artifacts\release'
$installerScript = Join-Path $repoRoot 'tools\installer\tessel.iss'

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
    -p:PublishReadyToRun=false -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false `
    -o $publishDir --nologo
Assert-LastExit 'publish'

# Debug symbols must never ship. .pdb files embed absolute build-machine source and
# NuGet cache paths (they contained "C:\Users\<name>\.nuget\packages\..." in 1.0.1),
# and end users do not need them. Removing them also saves ~1 MB.
Write-Step 'Drop debug symbols'
$pdbFiles = Get-ChildItem $publishDir -Filter *.pdb -File -Recurse -ErrorAction SilentlyContinue
$pdbBytes = 0
foreach ($pdb in $pdbFiles) { $pdbBytes += $pdb.Length; Remove-Item $pdb.FullName -Force }
Write-Host ("  removed {0} .pdb file(s), {1} MB" -f @($pdbFiles).Count, [math]::Round($pdbBytes / 1MB, 2))
if (Get-ChildItem $publishDir -Filter *.pdb -File -Recurse -ErrorAction SilentlyContinue) {
    throw "Debug symbols are still present in the publish output; refusing to package them."
}

# ---------------------------------------------------------------- trim
if (-not $SkipTrim) {
    Write-Step 'Trim unused Windows App SDK payload'
    # The Windows App SDK carries a full machine-learning stack (ONNX Runtime,
    # DirectML, the Microsoft.Windows.AI.* surface). Tessel only uses XAML,
    # so the payload is dead weight: ~40 MB on disk and ~15 MB in the zip.
    $trimPatterns = @(
        'onnxruntime.dll',
        'DirectML.dll',
        'Microsoft.ML.OnnxRuntime.dll',
        'Microsoft.Windows.AI.*',
        'Microsoft.Windows.Vision*',
        'Microsoft.Windows.Internal.Vision*'
    )
    $removedFiles = 0
    $removedBytes = 0
    foreach ($pattern in $trimPatterns) {
        Get-ChildItem $publishDir -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
            $removedBytes += $_.Length
            $removedFiles++
            Remove-Item $_.FullName -Force
        }
    }
    Write-Host ("  removed {0} file(s), {1} MB" -f $removedFiles, [math]::Round($removedBytes / 1MB, 1))
}
else {
    Write-Host ''
    Write-Host '== Trim skipped (-SkipTrim)' -ForegroundColor DarkYellow
}

# ---------------------------------------------------------------- legal files
# GPL-3.0 requires the license text to travel with the binaries, and the Windows
# App SDK requires its own terms to be redistributed with the runtime. Copy both
# into the published folder so the zip and the installer both carry them.
Write-Step 'License files'
$legalFiles = @(
    @{ Source = Join-Path $repoRoot 'LICENSE';                  Target = 'LICENSE.txt' },
    @{ Source = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md';   Target = 'THIRD-PARTY-NOTICES.md' },
    @{ Source = Join-Path $repoRoot 'src\DesktopAgent.WinUI\ThirdPartyNotices.txt'; Target = 'ThirdPartyNotices.txt' },
    @{ Source = Join-Path $repoRoot 'CHANGELOG.md';             Target = 'CHANGELOG.md' }
)
foreach ($item in $legalFiles) {
    if (Test-Path $item.Source) {
        Copy-Item -Path $item.Source -Destination (Join-Path $publishDir $item.Target) -Force
        Write-Host "  + $($item.Target)"
    }
    else {
        Write-Warning "  missing legal file: $($item.Source)"
    }
}

# ---------------------------------------------------------------- verify + zip
Write-Step 'Verify'
$exe = Join-Path $publishDir 'DesktopAgent.WinUI.exe'
if (-not (Test-Path $exe)) { throw "Publish output is missing DesktopAgent.WinUI.exe" }
foreach ($required in @('LICENSE.txt', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        throw "Published folder is missing $required - GPL and Windows App SDK terms must ship with the binaries."
    }
}
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

# ---------------------------------------------------------------- installer
$setupPath = $null
if (-not $SkipInstaller) {
    Write-Step 'Installer (Inno Setup)'
    if (-not $IsccPath) {
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe'
        )
        $IsccPath = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    }
    if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
        throw 'ISCC.exe not found. Install Inno Setup 6, pass -IsccPath, or use -SkipInstaller.'
    }
    if (-not (Test-Path $installerScript)) {
        throw "Installer script not found: $installerScript"
    }
    Write-Host "  compiler   : $IsccPath"
    & $IsccPath "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$releaseRoot" $installerScript |
        Select-String -Pattern 'Successful|Error|Warning|Output' | ForEach-Object { '  ' + $_.Line.Trim() }
    Assert-LastExit 'Inno Setup'
    $setupPath = Get-ChildItem $releaseRoot -Filter "Tessel-Setup-$Version*.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $setupPath) { throw "Installer was not produced in $releaseRoot" }
    $setupMb = [math]::Round($setupPath.Length / 1MB, 1)
    Write-Host "  $($setupPath.FullName) ($setupMb MB)"
}
else {
    Write-Host ''
    Write-Host '== Installer skipped (-SkipInstaller)' -ForegroundColor DarkYellow
}

Write-Host ''
Write-Host "Release v$Version ready." -ForegroundColor Green
Write-Host "  folder    : $publishDir"
Write-Host "  zip       : $zipPath"
if ($setupPath) { Write-Host "  installer : $($setupPath.FullName)" }
Write-Host ''
Write-Host 'Deploy on a target machine: unzip anywhere and run DesktopAgent.WinUI.exe.'
Write-Host 'First run requires the full desktop grid calibration for every display.'

if ($Launch) {
    Write-Step 'Launch'
    Start-Process -FilePath $exe -WorkingDirectory $publishDir
    Write-Host '  started.'
}
