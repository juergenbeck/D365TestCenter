#requires -Version 7.0
<#
.SYNOPSIS
    FB-46 / OE-12: legt die Custom API jbe_RecoverStaleChunks an und registriert den Async-Step
    RecoverStaleChunks (Stale-"Laeuft"-Chunk-Recovery).

.DESCRIPTION
    Erganzt Register-WorkerSteps.ps1 um den Recovery-Trigger (ADR-0009 Takt-Option b, fuer Recovery).

    WARUM Custom API + Async-Step (nicht sync): Der StaleChunkRecoveryService und
    RunCompletionService nutzen try/catch um OC-Updates (IfRowVersionMatches). In einem SYNC-Plugin /
    einer sync-Custom-API triggert das den Sandbox-Waechter (0x80040265, ADR-0005 / FB-31);
    Async-Plugins sind davon nicht betroffen. Darum: Custom API mit
    AllowedCustomProcessingStepType=AsyncOnly und KEINEM Main-Plugin, plus ein Async-Step, der die
    Logik ausfuehrt.

    Idempotent: existiert die Custom API / der Step, wird nur re-gepointet bzw. uebersprungen.
    Der Step ist async (mode=1), PostOperation (stage=40), Auto-Delete bei Erfolg.

    Getaktet wird die Custom API von einem Power-Automate-Recurrence-Flow (z.B. alle 5 min), der die
    Unbound Action jbe_RecoverStaleChunks aufruft. Setup-Anleitung:
    docs/recovery-recurrence-flow.md.

    Voraussetzung: Plugin-Package mit dem PluginType D365TestCenter.CrmPlugin.RecoverStaleChunks ist
    deployt (Deploy-PluginPackage.ps1) und das Schema (jbe_lastclaimedon/jbe_recoverycount) existiert
    (Add-WorkerSchema.ps1).

    DEV-only-Hard-Guard (-AllowNonDev fuer freigegebene Nicht-DEV-Envs).

.EXAMPLE
    pwsh ./Register-RecoveryApi.ps1 -OrgUrl https://contoso-dev.crm4.dynamics.com `
        -ClientId <id> -ClientSecret <secret> -TenantId <tenant>
#>

param(
    [Parameter(Mandatory)] [string]$OrgUrl,
    [Parameter(Mandatory)] [string]$ClientId,
    [Parameter(Mandatory)] [string]$ClientSecret,
    [Parameter(Mandatory)] [string]$TenantId,
    [switch]$AllowNonDev,
    [string]$SolutionName = "D365TestCenter"
)

$ErrorActionPreference = "Stop"

if ($OrgUrl -notmatch "-dev\." -and -not $AllowNonDev) {
    throw "DEV-only: -OrgUrl '$OrgUrl' enthaelt nicht '-dev.' (nutze -AllowNonDev nur mit Freigabe)."
}

$ApiName = "jbe_RecoverStaleChunks"
$PluginTypeName = "D365TestCenter.CrmPlugin.RecoverStaleChunks"

$tok = (Invoke-RestMethod -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
    -Method Post -Body @{ grant_type="client_credentials"; client_id=$ClientId; client_secret=$ClientSecret; scope="$OrgUrl/.default" } `
    -ContentType "application/x-www-form-urlencoded").access_token

$readH  = @{ Authorization="Bearer $tok"; Accept="application/json" }
$writeH = @{ Authorization="Bearer $tok"; "Content-Type"="application/json; charset=utf-8"; "OData-MaxVersion"="4.0"; "OData-Version"="4.0" }
$solH   = $writeH + @{ "MSCRM.SolutionName"=$SolutionName }
$base = "$OrgUrl/api/data/v9.2"

function Post-Json([string]$url, [hashtable]$headers, $body) {
    $json = $body | ConvertTo-Json -Compress -Depth 6
    return Invoke-RestMethod -Uri $url -Headers $headers -Method Post `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($json))
}

Write-Host ""
Write-Host "==> Recovery-API-Registrierung ($OrgUrl)" -ForegroundColor Cyan

# ── 1. Custom API jbe_RecoverStaleChunks ────────────────────────────
$existing = (Invoke-RestMethod -Uri ("$base/customapis?`$filter=uniquename eq '$ApiName'&`$select=customapiid") -Headers $readH).value
if ($existing.Count -gt 0) {
    Write-Host "  [SKIP] Custom API $ApiName existiert" -ForegroundColor Yellow
} else {
    $api = @{
        uniquename                      = $ApiName
        name                            = "RecoverStaleChunks"
        displayname                     = "Recover Stale Chunks"
        description                     = "FB-46/OE-12: Setzt zu lange in Laeuft steckende jbe_testchunk auf Fortsetzen zurueck (Async-Step)."
        bindingtype                     = 0    # Global / Unbound
        isfunction                      = $false
        isprivate                       = $false
        allowedcustomprocessingsteptype = 1    # AsyncOnly -> Async-Step erlaubt, kein Main-Plugin noetig
    }
    Post-Json "$base/customapis" $solH $api | Out-Null
    Write-Host "  [CREATE] Custom API $ApiName (Global, AsyncOnly, kein Main-Plugin)" -ForegroundColor Green
}

# ── 2. Async-Step RecoverStaleChunks auf der Custom-API-Message ──────
$ptId = (Invoke-RestMethod -Uri ("$base/plugintypes?`$filter=typename eq '$PluginTypeName'&`$select=plugintypeid") -Headers $readH).value
if ($ptId.Count -eq 0) { throw "PluginType '$PluginTypeName' nicht gefunden -- Package deployt?" }
$pluginTypeId = $ptId[0].plugintypeid

# Die Custom-API-Message heisst wie die uniquename.
$msg = (Invoke-RestMethod -Uri ("$base/sdkmessages?`$filter=name eq '$ApiName'&`$select=sdkmessageid") -Headers $readH).value
if ($msg.Count -eq 0) { throw "sdkmessage '$ApiName' nicht gefunden -- Custom API angelegt?" }
$messageId = $msg[0].sdkmessageid

$stepName = "D365TestCenter.RecoverStaleChunks: $ApiName"
$stepExisting = (Invoke-RestMethod -Uri ("$base/sdkmessageprocessingsteps?`$filter=name eq '$stepName'&`$select=sdkmessageprocessingstepid,_plugintypeid_value") -Headers $readH).value
if ($stepExisting.Count -gt 0) {
    $id = $stepExisting[0].sdkmessageprocessingstepid
    if ($stepExisting[0]._plugintypeid_value -ne $pluginTypeId) {
        $patch = @{ "plugintypeid@odata.bind"="/plugintypes($pluginTypeId)" } | ConvertTo-Json -Compress
        Invoke-RestMethod -Uri "$base/sdkmessageprocessingsteps($id)" -Headers ($writeH + @{ "If-Match"="*" }) -Method Patch -Body ([System.Text.Encoding]::UTF8.GetBytes($patch)) | Out-Null
        Write-Host "  [REPOINT] $stepName" -ForegroundColor Green
    } else {
        Write-Host "  [SKIP] $stepName" -ForegroundColor Yellow
    }
} else {
    # Global-Message-Step: kein sdkmessagefilter (Unbound).
    $step = @{
        name                      = $stepName
        mode                      = 1    # Async
        stage                     = 40   # PostOperation
        rank                      = 1
        asyncautodelete           = $true
        "sdkmessageid@odata.bind" = "/sdkmessages($messageId)"
        "plugintypeid@odata.bind" = "/plugintypes($pluginTypeId)"
    }
    Post-Json "$base/sdkmessageprocessingsteps" $solH $step | Out-Null
    Write-Host "  [CREATE] $stepName (async, PostOp, Auto-Delete)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Recovery-API-Registrierung abgeschlossen." -ForegroundColor Green
Write-Host "Naechster Schritt: Recurrence-Flow anlegen (docs/recovery-recurrence-flow.md)." -ForegroundColor Gray
