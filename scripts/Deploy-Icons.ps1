<#
.SYNOPSIS
    Spielt die App- und Tabellen-Icons (SVG) nach Dataverse DEV ein - ohne den
    vollen Solution-Deploy.

.DESCRIPTION
    Fokussiert und idempotent. Fasst ausschließlich die Icons an:
    1. Laedt die sechs SVG-Web-Resources aus ../webresource/icons/ hoch
       (webresourcetype 11 = SVG), Create oder Update per Name.
    2. Setzt IconVectorName der fuenf Tabellen auf die jeweilige SVG-Web-Resource.
    3. Setzt das App-Icon (appmodule.webresourceid) der App jbe_D365TestCenter.
    4. PublishAllXml.

    Auth per Client-Credentials, identisch zu Register-ReportingApis.ps1.
    DEV-Guard: bricht ab, wenn -OrgUrl nicht '-dev.' enthält (Override nur mit
    -AllowNonDev und Freigabe).

.EXAMPLE
    # Mit Client-Credentials (Service Principal)
    ./Deploy-Icons.ps1 -OrgUrl "https://markant-dev.crm4.dynamics.com" `
        -ClientId $cid -ClientSecret $secret -TenantId $tid

.EXAMPLE
    # Mit fertigem Bearer-Token (z.B. aus einem Token-Vault)
    ./Deploy-Icons.ps1 -OrgUrl "https://markant-dev.crm4.dynamics.com" -BearerToken $tok
#>

param(
    [Parameter(Mandatory)] [string]$OrgUrl,
    # Auth-Variante A: fertiger Bearer-Token (z.B. aus einem Token-Vault)
    [string]$BearerToken,
    # Auth-Variante B: Client-Credentials (Service Principal)
    [string]$ClientId,
    [string]$ClientSecret,
    [string]$TenantId,
    [switch]$AllowNonDev,
    [string]$SolutionName = "D365TestCenter"
)

$ErrorActionPreference = "Stop"

if ($OrgUrl -notmatch "-dev\." -and -not $AllowNonDev) {
    throw "DEV-only: -OrgUrl '$OrgUrl' enthält nicht '-dev.' (nutze -AllowNonDev nur mit Freigabe)."
}
$OrgUrl = $OrgUrl.TrimEnd("/")

# -- Auth: Bearer-Token direkt ODER Client-Credentials ----------------------
if ($BearerToken) {
    $tok = $BearerToken
} elseif ($ClientId -and $ClientSecret -and $TenantId) {
    $tok = (Invoke-RestMethod -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
        -Method Post -Body @{ grant_type="client_credentials"; client_id=$ClientId; client_secret=$ClientSecret; scope="$OrgUrl/.default" } `
        -ContentType "application/x-www-form-urlencoded").access_token
} else {
    throw "Auth fehlt: entweder -BearerToken ODER -ClientId/-ClientSecret/-TenantId angeben."
}

$readH  = @{ Authorization="Bearer $tok"; Accept="application/json" }
$writeH = @{ Authorization="Bearer $tok"; "Content-Type"="application/json; charset=utf-8"; "OData-MaxVersion"="4.0"; "OData-Version"="4.0" }
$base   = "$OrgUrl/api/data/v9.2"

$scriptDir      = $PSScriptRoot
$webresourceDir = Join-Path (Split-Path $scriptDir -Parent) "webresource"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  D365 Test Center - Icons einspielen" -ForegroundColor Cyan
Write-Host "  Ziel: $OrgUrl" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# ==========================================================================
#  1. WEB RESOURCES HOCHLADEN
# ==========================================================================
Write-Host ""
Write-Host "-- 1/4: Web Resources hochladen -----------------------------------" -ForegroundColor Cyan

$icons = @(
    @{ Name = "jbe_/icons/app.svg";           File = "icons/app.svg"           }
    @{ Name = "jbe_/icons/testcase.svg";      File = "icons/testcase.svg"      }
    @{ Name = "jbe_/icons/teststep.svg";      File = "icons/teststep.svg"      }
    @{ Name = "jbe_/icons/testchunk.svg";     File = "icons/testchunk.svg"     }
    @{ Name = "jbe_/icons/testrun.svg";       File = "icons/testrun.svg"       }
    @{ Name = "jbe_/icons/testrunresult.svg"; File = "icons/testrunresult.svg" }
)

foreach ($ic in $icons) {
    $filePath = Join-Path $webresourceDir $ic.File
    if (-not (Test-Path $filePath)) { throw "Datei nicht gefunden: $filePath" }

    $base64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($filePath))
    $existing = Invoke-RestMethod -Method Get `
        -Uri "$base/webresourceset?`$filter=name eq '$($ic.Name)'&`$select=webresourceid" -Headers $readH

    if ($existing.value.Count -gt 0) {
        $wrId = $existing.value[0].webresourceid
        Write-Host "  [UPDATE] $($ic.Name)" -ForegroundColor Yellow
        $body = @{ content = $base64 } | ConvertTo-Json
        Invoke-RestMethod -Method Patch -Uri "$base/webresourceset($wrId)" `
            -Headers ($writeH + @{ "If-Match" = "*" }) -Body $body | Out-Null
    } else {
        Write-Host "  [CREATE] $($ic.Name)" -ForegroundColor Green
        $body = @{ name = $ic.Name; displayname = $ic.Name; webresourcetype = 11; content = $base64 } | ConvertTo-Json
        Invoke-RestMethod -Method Post -Uri "$base/webresourceset" `
            -Headers ($writeH + @{ "MSCRM.SolutionUniqueName" = $SolutionName }) -Body $body | Out-Null
    }
}

# ==========================================================================
#  2. TABELLEN-ICONS VERDRAHTEN (IconVectorName)
# ==========================================================================
Write-Host ""
Write-Host "-- 2/4: Tabellen-Icons verdrahten ---------------------------------" -ForegroundColor Cyan

$entityIcons = @(
    @{ Entity = "jbe_testcase";      Icon = "jbe_/icons/testcase.svg"      }
    @{ Entity = "jbe_teststep";      Icon = "jbe_/icons/teststep.svg"      }
    @{ Entity = "jbe_testchunk";     Icon = "jbe_/icons/testchunk.svg"     }
    @{ Entity = "jbe_testrun";       Icon = "jbe_/icons/testrun.svg"       }
    @{ Entity = "jbe_testrunresult"; Icon = "jbe_/icons/testrunresult.svg" }
)

foreach ($ei in $entityIcons) {
    Write-Host "  [ICON] $($ei.Entity) -> $($ei.Icon)" -ForegroundColor Green
    # EntityMetadata unterstützt kein PATCH (Fehler 0x80060888). Korrekt: volle
    # Definition holen, IconVectorName setzen, per PUT zurück - MSCRM.MergeLabels
    # erhält dabei die lokalisierten Labels.
    $def = Invoke-RestMethod -Method Get -Uri "$base/EntityDefinitions(LogicalName='$($ei.Entity)')" -Headers $readH
    $def.IconVectorName = $ei.Icon
    $defJson = $def | ConvertTo-Json -Depth 20
    Invoke-RestMethod -Method Put -Uri "$base/EntityDefinitions(LogicalName='$($ei.Entity)')" `
        -Headers ($writeH + @{ "MSCRM.MergeLabels" = "true" }) -Body ([System.Text.Encoding]::UTF8.GetBytes($defJson)) | Out-Null
}

# ==========================================================================
#  3. APP-ICON VERDRAHTEN (appmodule.webresourceid)
# ==========================================================================
Write-Host ""
Write-Host "-- 3/4: App-Icon verdrahten ---------------------------------------" -ForegroundColor Cyan

$appIconWr = Invoke-RestMethod -Method Get `
    -Uri "$base/webresourceset?`$filter=name eq 'jbe_/icons/app.svg'&`$select=webresourceid" -Headers $readH
$appMod = Invoke-RestMethod -Method Get `
    -Uri "$base/appmodules?`$filter=uniquename eq 'jbe_D365TestCenter'&`$select=appmoduleid" -Headers $readH

if ($appIconWr.value.Count -gt 0 -and $appMod.value.Count -gt 0) {
    Write-Host "  [ICON] App jbe_D365TestCenter -> jbe_/icons/app.svg" -ForegroundColor Green
    $body = @{ webresourceid = $appIconWr.value[0].webresourceid } | ConvertTo-Json
    Invoke-RestMethod -Method Patch -Uri "$base/appmodules($($appMod.value[0].appmoduleid))" `
        -Headers ($writeH + @{ "If-Match" = "*" }) -Body $body | Out-Null
} else {
    Write-Host "  [WARN] App oder App-Icon-Web-Resource nicht gefunden - App-Icon übersprungen" -ForegroundColor Yellow
}

# ==========================================================================
#  4. PUBLISH
# ==========================================================================
Write-Host ""
Write-Host "-- 4/4: PublishAllXml ---------------------------------------------" -ForegroundColor Cyan
Invoke-RestMethod -Method Post -Uri "$base/PublishAllXml" -Headers $writeH | Out-Null

Write-Host ""
Write-Host "Fertig. Icons hochgeladen, verdrahtet und publiziert." -ForegroundColor Green
Write-Host "Prüfen: App-Kachel im App-Launcher und die fünf Tabellen in der Sitemap." -ForegroundColor Green
