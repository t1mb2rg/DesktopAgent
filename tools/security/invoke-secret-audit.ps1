<#
.SYNOPSIS
    Scan all locally reachable Git refs for potential credentials before publishing Tessel.

.DESCRIPTION
    Gitleaks must be installed separately (https://github.com/gitleaks/gitleaks).
    This script does not rewrite history or publish its scan reports.
    Redacted JSON reports stay under gitignored artifacts/open-source-audit.
    Git history means commits reachable through fetched branch/tag refs; unreachable
    objects, GitHub Actions logs, PR attachments, and binary screenshots need
    independent review.
#>
[CmdletBinding()]
param(
    [string]$GitleaksExecutable = 'gitleaks',
    [switch]$FetchAllRefs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (& git rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw 'Run this from a clone of the Tessel Git repository.'
}
Push-Location $repoRoot
try {
    if ($FetchAllRefs) {
        & git fetch --force --prune origin '+refs/heads/*:refs/remotes/origin/*' '+refs/tags/*:refs/tags/*'
        if ($LASTEXITCODE -ne 0) { throw 'Could not fetch every remote branch and tag.' }
    }

    & $GitleaksExecutable version
    if ($LASTEXITCODE -ne 0) { throw 'Gitleaks is not installed or cannot start.' }

    $outputDir = Join-Path $repoRoot 'artifacts/open-source-audit'
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

    $report = Join-Path $outputDir 'gitleaks-all-refs-redacted.json'
    Write-Host 'Scanning all reachable branch/tag histories. Raw secrets will not be printed.'
    # Do not print stdout or JSON in public CI logs; JSON is fully redacted.
    & $GitleaksExecutable git '--log-opts=--all' '--redact=100' '--report-format=json' "--report-path=$report" '.' *> (Join-Path $outputDir 'gitleaks-scan.log')
    $scanExit = $LASTEXITCODE
    if ($scanExit -eq 1) {
        Write-Warning "Potential credentials detected. Inspect the local redacted report: $report"
        Write-Warning 'Triage every finding; revoke/rotate real credentials BEFORE publishing.'
        exit 1
    }
    if ($scanExit -ne 0) {
        throw "Gitleaks failed to run (exit code $scanExit); no pass verdict."
    }
    Write-Host 'Git history secret scan: PASS (Gitleaks found no matches).'
    Write-Host 'This is NOT a complete privacy audit: review commit metadata, documents, screenshots, release assets and Actions logs.'
}
finally {
    Pop-Location
}
