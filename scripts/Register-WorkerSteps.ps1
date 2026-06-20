#requires -Version 7.0
<#
.SYNOPSIS
    ADR-0009: registriert die SDK-Message-Processing-Steps fuer die Worker-Plugins
    RunCoordinator (auf jbe_testrun) und RunChunkWorker (auf jbe_testchunk).

.DESCRIPTION
    Erganzt Deploy-PluginPackage.ps1 (das nur die bestehenden RunTestsOnStatusChange-Steps kennt).
    Idempotent: existiert ein Step mit dem Namen, wird nur der PluginType neu gebunden (re-point);
    sonst wird er angelegt. Alle Steps async (mode=1), PostOperation (stage=40), Auto-Delete bei
    Erfolg (asyncautodelete=true, gegen AsyncOp-Flut, ADR B.2.1) und gehen in die Solution.

    Registrierte Steps:
      - RunCoordinator: Create of jbe_testrun
      - RunCoordinator: Update of jbe_testrun   (FilteringAttributes: jbe_teststatus)
      - RunChunkWorker: Create of jbe_testchunk
      - RunChunkWorker: Update of jbe_testchunk (FilteringAttributes: jbe_chunkstatus)

    Voraussetzung: Plugin-Package mit den beiden PluginTypes ist bereits deployt
    (Deploy-PluginPackage.ps1) und das Schema (jbe_testchunk) existiert (Add-WorkerSchema.ps1).

    DEV-only-Hard-Guard (-AllowNonDev fuer freigegebene Nicht-DEV-Envs).

.EXAMPLE
    pwsh ./Register-WorkerSteps.ps1 -OrgUrl https://contoso-dev.crm4.dynamics.com `
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

$tok = (Invoke-RestMethod -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
    -Method Post -Body @{ grant_type="client_credentials"; client_id=$ClientId; client_secret=$ClientSecret; scope="$OrgUrl/.default" } `
    -ContentType "application/x-www-form-urlencoded").access_token

$readH  = @{ Authorization="Bearer $tok"; Accept="application/json" }
$writeH = @{ Authorization="Bearer $tok"; "Content-Type"="application/json; charset=utf-8"; "OData-MaxVersion"="4.0"; "OData-Version"="4.0" }
$solH   = $writeH + @{ "MSCRM.SolutionName"=$SolutionName }
$base = "$OrgUrl/api/data/v9.2"

function Get-PluginTypeId([string]$typename) {
    $r = Invoke-RestMethod -Uri ("$base/plugintypes?`$filter=typename eq '$typename'&`$select=plugintypeid") -Headers $readH
    if ($r.value.Count -eq 0) { throw "PluginType '$typename' nicht gefunden -- Package deployt?" }
    return $r.value[0].plugintypeid
}
function Get-MessageId([string]$name) {
    return (Invoke-RestMethod -Uri ("$base/sdkmessages?`$filter=name eq '$name'&`$select=sdkmessageid") -Headers $readH).value[0].sdkmessageid
}
function Get-FilterId([string]$entity, [string]$messageId) {
    $r = Invoke-RestMethod -Uri ("$base/sdkmessagefilters?`$filter=primaryobjecttypecode eq '$entity' and _sdkmessageid_value eq $messageId&`$select=sdkmessagefilterid") -Headers $readH
    if ($r.value.Count -eq 0) { throw "sdkmessagefilter fuer ($entity,$messageId) nicht gefunden -- Entity vorhanden?" }
    return $r.value[0].sdkmessagefilterid
}

function Register-Step {
    param(
        [string]$Name, [string]$PluginTypeId, [string]$MessageId, [string]$FilterId,
        [string]$FilteringAttributes
    )
    $existing = (Invoke-RestMethod -Uri ("$base/sdkmessageprocessingsteps?`$filter=name eq '$Name'&`$select=sdkmessageprocessingstepid,_plugintypeid_value") -Headers $readH).value
    if ($existing.Count -gt 0) {
        $id = $existing[0].sdkmessageprocessingstepid
        if ($existing[0]._plugintypeid_value -ne $PluginTypeId) {
            $patch = @{ "plugintypeid@odata.bind"="/plugintypes($PluginTypeId)" } | ConvertTo-Json -Compress
            Invoke-RestMethod -Uri "$base/sdkmessageprocessingsteps($id)" -Headers ($writeH + @{ "If-Match"="*" }) -Method Patch -Body ([System.Text.Encoding]::UTF8.GetBytes($patch)) | Out-Null
            Write-Host "  [REPOINT] $Name" -ForegroundColor Green
        } else {
            Write-Host "  [SKIP] $Name" -ForegroundColor Yellow
        }
        return
    }
    $body = @{
        name                            = $Name
        mode                            = 1   # Async
        stage                           = 40  # PostOperation
        rank                            = 1
        asyncautodelete                 = $true
        "sdkmessageid@odata.bind"       = "/sdkmessages($MessageId)"
        "sdkmessagefilterid@odata.bind" = "/sdkmessagefilters($FilterId)"
        "plugintypeid@odata.bind"       = "/plugintypes($PluginTypeId)"
    }
    if ($FilteringAttributes) { $body.filteringattributes = $FilteringAttributes }
    $json = $body | ConvertTo-Json -Compress
    Invoke-RestMethod -Uri "$base/sdkmessageprocessingsteps" -Headers $solH -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) | Out-Null
    Write-Host "  [CREATE] $Name" -ForegroundColor Green
}

Write-Host ""
Write-Host "==> Worker-Step-Registrierung ($OrgUrl)" -ForegroundColor Cyan

$coordType  = Get-PluginTypeId "D365TestCenter.CrmPlugin.RunCoordinator"
$workerType = Get-PluginTypeId "D365TestCenter.CrmPlugin.RunChunkWorker"
$createMsg  = Get-MessageId "Create"
$updateMsg  = Get-MessageId "Update"
$runCreateF   = Get-FilterId "jbe_testrun"   $createMsg
$runUpdateF   = Get-FilterId "jbe_testrun"   $updateMsg
$chunkCreateF = Get-FilterId "jbe_testchunk" $createMsg
$chunkUpdateF = Get-FilterId "jbe_testchunk" $updateMsg
Write-Host "  PluginTypes: Coordinator=$coordType Worker=$workerType" -ForegroundColor Gray

Write-Host "RunCoordinator (jbe_testrun):" -ForegroundColor Cyan
Register-Step -Name "D365TestCenter.RunCoordinator: Create of jbe_testrun" -PluginTypeId $coordType -MessageId $createMsg -FilterId $runCreateF
Register-Step -Name "D365TestCenter.RunCoordinator: Update of jbe_testrun" -PluginTypeId $coordType -MessageId $updateMsg -FilterId $runUpdateF -FilteringAttributes "jbe_teststatus"

Write-Host "RunChunkWorker (jbe_testchunk):" -ForegroundColor Cyan
Register-Step -Name "D365TestCenter.RunChunkWorker: Create of jbe_testchunk" -PluginTypeId $workerType -MessageId $createMsg -FilterId $chunkCreateF
Register-Step -Name "D365TestCenter.RunChunkWorker: Update of jbe_testchunk" -PluginTypeId $workerType -MessageId $updateMsg -FilterId $chunkUpdateF -FilteringAttributes "jbe_chunkstatus"

Write-Host ""
Write-Host "Step-Registrierung abgeschlossen." -ForegroundColor Green
