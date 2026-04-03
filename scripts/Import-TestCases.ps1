<#
.SYNOPSIS
    Imports test cases from pack JSON files into Dataverse jbe_testcases.

.DESCRIPTION
    Reads test cases from a pack file and creates them as records
    in the jbe_testcases entity. Idempotent: skips records where jbe_testid
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
    $testId = $tc.jbe_testid
    $title  = $tc.jbe_title

    try {
        # Idempotenz-Check: existiert der Record bereits?
        $filterUrl = "$baseUrl/jbe_testcases?`$filter=jbe_testid eq '$testId'&`$select=jbe_testcaseid"
        $existCheck = Invoke-RestMethod -Uri $filterUrl -Headers $headers -Method Get

        if ($existCheck.value -and $existCheck.value.Count -gt 0) {
            Write-Host "[SKIP]   $testId : $title (existiert bereits)"
            $skipped++
            continue
        }

        # jbe_definitionjson: Object -> JSON-String
        $defJsonString = $tc.jbe_definitionjson | ConvertTo-Json -Depth 20 -Compress

        # Record-Body erstellen (nur die 7 Felder, kein PK, kein Name)
        $body = @{
            jbe_testid         = $tc.jbe_testid
            jbe_title          = $tc.jbe_title
            jbe_category       = $tc.jbe_category
            jbe_tags           = $tc.jbe_tags
            jbe_userstories    = $tc.jbe_userstories
            jbe_enabled        = $tc.jbe_enabled
            jbe_definitionjson = $defJsonString
        } | ConvertTo-Json -Depth 5

        # POST: Record anlegen
        $createUrl = "$baseUrl/jbe_testcases"
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
