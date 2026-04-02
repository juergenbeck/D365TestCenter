<#
.SYNOPSIS
    Imports test cases from pack JSON files into Dataverse itt_testcases.

.DESCRIPTION
    Reads test cases from a pack file and creates them as records
    in the itt_testcases entity. Idempotent: skips records where itt_testid
    already exists.

.NOTES
    Requires: $headers variable with Authorization header set before running.
    Requires: deploy-config.json with target environment URL.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

# --- Auth: $headers must be set before running this script ---
if (-not $headers) {
    Write-Error "No authentication headers provided. Set `$headers before running this script."
    exit 1
}

# Content-Type und OData-Header ergaenzen
$createHeaders = @{}
foreach ($key in $headers.Keys) {
    $createHeaders[$key] = $headers[$key]
}
$createHeaders['Content-Type']    = 'application/json; charset=utf-8'
$createHeaders['OData-MaxVersion'] = '4.0'
$createHeaders['OData-Version']   = '4.0'

$baseUrl = 'https://markant-dev.crm4.dynamics.com/api/data/v9.2'

# --- Pack-JSON lesen ---
$packPath = Join-Path $PSScriptRoot '..\webresource\packs\fg-testtool-legacy.json'
$packPath = [System.IO.Path]::GetFullPath($packPath)

if (-not (Test-Path $packPath)) {
    Write-Host "[FEHLER] Pack-Datei nicht gefunden: $packPath"
    exit 1
}

$packJson = Get-Content -Path $packPath -Raw -Encoding UTF8
$pack = $packJson | ConvertFrom-Json

$testCases = $pack.testCases
$totalCount = $testCases.Count
Write-Host "Pack geladen: $totalCount Testfaelle aus '$($pack.name)'"
Write-Host ""

# --- Zaehler ---
$created = 0
$skipped = 0
$errors  = 0

# --- Jeden Testfall verarbeiten ---
foreach ($tc in $testCases) {
    $testId = $tc.itt_testid
    $title  = $tc.itt_title

    try {
        # Idempotenz-Check: existiert der Record bereits?
        $filterUrl = "$baseUrl/itt_testcases?`$filter=itt_testid eq '$testId'&`$select=itt_testcaseid"
        $existCheck = Invoke-RestMethod -Uri $filterUrl -Headers $headers -Method Get

        if ($existCheck.value -and $existCheck.value.Count -gt 0) {
            Write-Host "[SKIP]   $testId : $title (existiert bereits)"
            $skipped++
            continue
        }

        # itt_definitionjson: Object -> JSON-String
        $defJsonString = $tc.itt_definitionjson | ConvertTo-Json -Depth 20 -Compress

        # Record-Body erstellen (nur die 7 Felder, kein PK, kein Name)
        $body = @{
            itt_testid         = $tc.itt_testid
            itt_title          = $tc.itt_title
            itt_category       = $tc.itt_category
            itt_tags           = $tc.itt_tags
            itt_userstories    = $tc.itt_userstories
            itt_enabled        = $tc.itt_enabled
            itt_definitionjson = $defJsonString
        } | ConvertTo-Json -Depth 5

        # POST: Record anlegen
        $createUrl = "$baseUrl/itt_testcases"
        Invoke-RestMethod -Uri $createUrl -Headers $createHeaders -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($body))

        Write-Host "[CREATE] $testId : $title"
        $created++
    }
    catch {
        Write-Host "[ERROR]  $testId : $title"
        Write-Host "         $($_.Exception.Message)"
        $errors++
    }
}

# --- Zusammenfassung ---
Write-Host ""
Write-Host "Fertig: $created erstellt, $skipped uebersprungen, $errors Fehler (von $totalCount gesamt)"
