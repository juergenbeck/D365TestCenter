<#
.SYNOPSIS
    Imports SnapshotWriter + Recompute test cases into ITT on DEV.
.DESCRIPTION
    Creates 4 itt_testcase records from the snapshot-recompute.json pack.
    Idempotent: checks if testid already exists before creating.
    Sources: DYN-T805, DYN-T806, DYN-T807 (DYN-8243), DYN-T804 (DYN-8248)
#>

$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
. "$scriptDir\..\..\..\..\..\..\..\common\auth\TokenVault.ps1"
$headers = Get-VaultHeaders -System 'dataverse_dev'
$baseUrl = "https://markant-dev.crm4.dynamics.com/api/data/v9.2"

# Load pack JSON
$packFile = Join-Path (Split-Path $scriptDir -Parent) "webresource\packs\snapshot-recompute.json"
$pack = Get-Content $packFile -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host "Importiere $($pack.testCases.Count) Testfaelle aus Pack '$($pack.name)'..." -ForegroundColor Cyan

foreach ($tc in $pack.testCases) {
    $testId = $tc.itt_testid
    Write-Host "  $testId - $($tc.itt_title)" -ForegroundColor White -NoNewline

    # Check if already exists
    $existing = Invoke-RestMethod -Method Get `
        -Uri "$baseUrl/itt_testcases?`$filter=itt_testid eq '$testId'&`$select=itt_testcaseid&`$top=1" `
        -Headers $headers

    if ($existing.value.Count -gt 0) {
        Write-Host " [SKIP] existiert bereits" -ForegroundColor Yellow
        continue
    }

    # Convert definitionjson to string
    $defJson = $tc.itt_definitionjson | ConvertTo-Json -Depth 20 -Compress

    $body = @{
        itt_testid         = $tc.itt_testid
        itt_title          = $tc.itt_title
        itt_category       = $tc.itt_category
        itt_tags           = $tc.itt_tags
        itt_userstories    = $tc.itt_userstories
        itt_enabled        = $tc.itt_enabled
        itt_definitionjson = $defJson
    } | ConvertTo-Json -Depth 5

    $resp = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/itt_testcases" `
        -Headers $headers `
        -Body $body `
        -ContentType "application/json; charset=utf-8"

    Write-Host " [OK]" -ForegroundColor Green
}

Write-Host ""
Write-Host "Import abgeschlossen." -ForegroundColor Green
