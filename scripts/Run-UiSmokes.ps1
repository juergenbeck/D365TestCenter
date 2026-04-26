#requires -Version 7.0
<#
.SYNOPSIS
    Runs the UI-smoke suite against a Dataverse environment via D365TestCenter.Cli (ADR-0006).

.DESCRIPTION
    Generic Cli wrapper for UI-smoke runs. Wraps `D365TestCenter.Cli run`
    with `--browser-state`, optional `--browser-trace`, and optional retry-on-flake
    logic for environments where async plugin cascades cause occasional first-run
    flakiness (Markant DEV with field-governance bridge cascades is a known case).

    Designed to be called from a project-specific deploy script as a pre-deploy
    gate (e.g. before importing a managed solution to TEST/DATATEST):

        $smokeOk = & "<repo>/scripts/Run-UiSmokes.ps1" -Org ... -StorageState ...
        if ($LASTEXITCODE -ne 0) { throw "UI-smoke gate failed" }

    Exit codes:
      0 = all smokes passed (deploy may proceed)
      1 = at least one smoke failed/errored after all attempts (deploy must abort)
      2 = fatal error (Cli not built, storage-state missing, ...)

    Retry behaviour:
      - On a non-zero Cli exit, the script retries up to MaxAttempts times.
      - Each retry uses a fresh jbe_testrun record (Cli is not idempotent on test
        runs by design — every invocation creates a new run).
      - Trace-zip path (if -Trace) is uniquely suffixed per attempt so the failing
        attempt's trace remains available for post-mortem.
      - Retry exists because Markant DEV's plugin-cascade latency can cause
        occasional first-run timeouts that are not actual UI bugs. Set
        MaxAttempts=1 to disable.

.PARAMETER Org
    Dataverse org URL (https://markant-dev.crm4.dynamics.com).

.PARAMETER ClientId
    Azure AD App Client ID for service-principal auth.

.PARAMETER ClientSecret
    Azure AD App Client Secret. Pass via parameter or set
    D365TC_UISMOKE_CLIENT_SECRET environment variable.

.PARAMETER TenantId
    Azure AD Tenant ID.

.PARAMETER StorageState
    Path to a Playwright storage-state JSON, created via Setup-PlaywrightStorageState.ps1.
    The file must exist and contain a non-expired login session (~24h SPA-flow lifetime).

.PARAMETER Filter
    Cli test-case filter (wildcard on TestId, tag:..., category:..., or comma-separated IDs).
    Default: "MARKANT-UI-*" (matches all Markant UI smokes by TestId-prefix).

.PARAMETER Config
    Cli config profile (standard, markant, lm). Default: markant.

.PARAMETER Trace
    Optional path for Playwright trace.zip. When set, the trace is captured per
    attempt with an attempt-suffix (e.g. trace.zip -> trace-attempt1.zip).

.PARAMETER MaxAttempts
    Maximum number of attempts (1 = no retry, 2 = one retry after failure).
    Default: 2.

.PARAMETER Headed
    Run browser headed instead of headless (local debug only).

.PARAMETER CliDll
    Path override for D365TestCenter.Cli.dll. Default: relative to this script's
    parent (../backend/D365TestCenter.Cli/bin/Debug/net8.0/D365TestCenter.Cli.dll).

.EXAMPLE
    # Pre-deploy gate before importing managed solution to Markant TEST
    .\Run-UiSmokes.ps1 `
        -Org "https://markant-dev.crm4.dynamics.com" `
        -ClientId "40a6fbac-..." `
        -ClientSecret $env:D365TC_MARKANT_DEV_SECRET `
        -TenantId "eba24dc8-..." `
        -StorageState "C:\...\markant-dev-juergen.json"

.EXAMPLE
    # Local debug run, no retry, headed browser, with trace capture
    .\Run-UiSmokes.ps1 -Org ... -ClientId ... -ClientSecret ... -TenantId ... `
        -StorageState ... -Headed -MaxAttempts 1 -Trace ".\trace.zip"
#>

param(
    [Parameter(Mandatory)] [string]$Org,
    [Parameter(Mandatory)] [string]$ClientId,
    [string]$ClientSecret = $env:D365TC_UISMOKE_CLIENT_SECRET,
    [Parameter(Mandatory)] [string]$TenantId,
    [Parameter(Mandatory)] [string]$StorageState,
    [string]$Filter = "MARKANT-UI-*",
    [string]$Config = "markant",
    [string]$Trace,
    [int]$MaxAttempts = 2,
    [switch]$Headed,
    [string]$CliDll
)

$ErrorActionPreference = "Stop"

# ── Validate inputs ────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($ClientSecret)) {
    Write-Host "ERROR: ClientSecret missing. Pass -ClientSecret or set `$env:D365TC_UISMOKE_CLIENT_SECRET." -ForegroundColor Red
    exit 2
}

if (-not (Test-Path $StorageState)) {
    Write-Host "ERROR: Storage-state file not found: $StorageState" -ForegroundColor Red
    Write-Host "       Run scripts/Setup-PlaywrightStorageState.ps1 first." -ForegroundColor Red
    exit 2
}

if (-not $CliDll) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $CliDll = Join-Path $repoRoot "backend\D365TestCenter.Cli\bin\Debug\net8.0\D365TestCenter.Cli.dll"
}
if (-not (Test-Path $CliDll)) {
    Write-Host "ERROR: D365TestCenter.Cli.dll not found at: $CliDll" -ForegroundColor Red
    Write-Host "       Build first: dotnet build backend\D365TestCenter.Cli" -ForegroundColor Red
    exit 2
}

if ($MaxAttempts -lt 1) { $MaxAttempts = 1 }

# ── Header ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  UI Smoke Gate" -ForegroundColor Cyan
Write-Host "  Org:         $Org" -ForegroundColor Cyan
Write-Host "  Filter:      $Filter" -ForegroundColor Cyan
Write-Host "  Config:      $Config" -ForegroundColor Cyan
Write-Host "  StorageState: $StorageState" -ForegroundColor Cyan
Write-Host "  MaxAttempts: $MaxAttempts" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# ── Run with retry ─────────────────────────────────────────────
$lastExit = 1
for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    Write-Host ""
    Write-Host "── Attempt $attempt/$MaxAttempts ──────────────────────────" -ForegroundColor Yellow

    # Per-attempt trace path so a failing attempt's trace survives a retry
    $tracePath = $null
    if ($Trace) {
        $traceDir = Split-Path $Trace -Parent
        $traceBase = [System.IO.Path]::GetFileNameWithoutExtension($Trace)
        $traceExt = [System.IO.Path]::GetExtension($Trace)
        if ([string]::IsNullOrEmpty($traceDir)) { $traceDir = "." }
        $tracePath = Join-Path $traceDir "$traceBase-attempt$attempt$traceExt"
    }

    $cliArgs = @(
        $CliDll, "run",
        "--org", $Org,
        "--client-id", $ClientId,
        "--client-secret", $ClientSecret,
        "--tenant-id", $TenantId,
        "--config", $Config,
        "--filter", $Filter,
        "--browser-state", $StorageState
    )
    if ($Headed) { $cliArgs += "--browser-headed" }
    if ($tracePath) { $cliArgs += @("--browser-trace", $tracePath) }

    & dotnet @cliArgs
    $lastExit = $LASTEXITCODE

    if ($lastExit -eq 0) {
        Write-Host ""
        Write-Host "============================================================" -ForegroundColor Green
        Write-Host "  UI Smoke Gate: PASSED (attempt $attempt/$MaxAttempts)" -ForegroundColor Green
        Write-Host "============================================================" -ForegroundColor Green
        exit 0
    }

    if ($attempt -lt $MaxAttempts) {
        Write-Host ""
        Write-Host "  Smoke failed (exit $lastExit) — retrying after 5s..." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
    }
}

# ── All attempts exhausted ─────────────────────────────────────
Write-Host ""
Write-Host "============================================================" -ForegroundColor Red
Write-Host "  UI Smoke Gate: FAILED after $MaxAttempts attempt(s)" -ForegroundColor Red
Write-Host "  Last Cli exit-code: $lastExit" -ForegroundColor Red
if ($Trace) {
    Write-Host "  Traces under: $((Split-Path $Trace -Parent))" -ForegroundColor Red
}
Write-Host "  Inspect jbe_testrunresult records on $Org for screenshots and step logs." -ForegroundColor Red
Write-Host "============================================================" -ForegroundColor Red
exit 1
