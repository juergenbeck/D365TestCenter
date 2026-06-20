#requires -Version 7.0
<#
.SYNOPSIS
    FB-46 / OE-12: legt den Power-Automate-Recurrence-Flow an, der die Recovery-Custom-API
    jbe_RecoverStaleChunks zeitgesteuert aufruft (Stale-Chunk-Recovery-Takt, ADR-0009 Takt-Option b).

.DESCRIPTION
    Ein moderner Cloud Flow ist ein workflow-Record (category=5). Dieses Skript legt ihn vollstaendig
    per Web API an, packt ihn in die Solution (MSCRM.SolutionUniqueName) und aktiviert ihn -- kein
    Maker-Portal noetig. Generisch/Public-ready: Org/Action/Connection-Reference/Intervall sind
    Parameter (keine projektspezifischen Defaults).

    Mechanik + Stolperfallen verifiziert (Markant dataverse-web-api/metadata-und-actions.md):
      - clientdata: Recurrence-Trigger (concurrency.runs=1) + PerformUnboundAction auf -ActionName.
      - Connection Reference: MUSS dem aufrufenden Service-Principal gehoeren, sonst scheitert die
        Aktivierung mit 0x80060467 / ConnectionAuthorizationFailed. -ConnectionReferenceLogicalName
        ist daher Pflicht (die SP-eigene CR der Ziel-Umgebung) und wird vorab verifiziert.
      - Ruft der Flow eine FRISCH per Web API angelegte Custom API, scheitert die Aktivierung mit
        0x80060467 / InvalidOpenApiFlow (GetMetadataForUnboundAction NotFound), bis ein PublishAllXml
        gelaufen ist. Das Skript heilt das: bei InvalidOpenApiFlow PublishAllXml, dann Aktivierung erneut.

    Idempotent: existiert der Flow (category 5, gleicher Name), wird er nur gemeldet (kein Doppel-Anlegen).

    DEV-only-Hard-Guard (-AllowNonDev fuer freigegebene Nicht-DEV-Envs).

.EXAMPLE
    pwsh ./Create-RecurrenceFlow.ps1 -OrgUrl https://contoso-dev.crm4.dynamics.com `
        -ClientId <id> -ClientSecret <secret> -TenantId <tenant> `
        -ConnectionReferenceLogicalName contoso_sharedcommondataserviceforapps_abcde
#>

param(
    [Parameter(Mandatory)] [string]$OrgUrl,
    [Parameter(Mandatory)] [string]$ClientId,
    [Parameter(Mandatory)] [string]$ClientSecret,
    [Parameter(Mandatory)] [string]$TenantId,
    [Parameter(Mandatory)] [string]$ConnectionReferenceLogicalName,
    [string]$ActionName = "jbe_RecoverStaleChunks",
    [int]$IntervalMinutes = 5,
    [string]$FlowName = "D365TestCenter Stale-Chunk Recovery Sweep",
    [string]$SolutionName = "D365TestCenter",
    [switch]$AllowNonDev
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
$base = "$OrgUrl/api/data/v9.2"

function Patch-Json([string]$url, $body) {
    $json = $body | ConvertTo-Json -Compress -Depth 30
    Invoke-RestMethod -Uri $url -Headers ($writeH + @{ "If-Match"="*" }) -Method Patch `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) | Out-Null
}

Write-Host ""
Write-Host "==> Recovery-Recurrence-Flow ($OrgUrl)" -ForegroundColor Cyan

# ── Pre-Flight: SP-eigene Connection Reference vorhanden? ────────────
$cr = (Invoke-RestMethod -Uri ("$base/connectionreferences?`$filter=connectionreferencelogicalname eq '$ConnectionReferenceLogicalName'&`$select=connectionreferenceid,connectionreferencedisplayname") -Headers $readH).value
if ($cr.Count -eq 0) {
    throw "Connection Reference '$ConnectionReferenceLogicalName' nicht gefunden. Voraussetzung fehlt (muss dem aufrufenden SP gehoeren)."
}
Write-Host "  Connection Reference OK: $ConnectionReferenceLogicalName" -ForegroundColor Gray

# ── Idempotenz ──────────────────────────────────────────────────────
$existing = (Invoke-RestMethod -Uri ("$base/workflows?`$filter=category eq 5 and name eq '$FlowName'&`$select=workflowid,statecode,statuscode") -Headers $readH).value
if ($existing.Count -gt 0) {
    Write-Host "  [SKIP] Flow '$FlowName' existiert (workflowid=$($existing[0].workflowid), statecode=$($existing[0].statecode))." -ForegroundColor Yellow
    return
}

# ── clientdata bauen ────────────────────────────────────────────────
$connectionKey = "shared_commondataserviceforapps"
$clientdata = [ordered]@{
    properties = [ordered]@{
        connectionReferences = [ordered]@{
            $connectionKey = [ordered]@{
                runtimeSource = "embedded"
                connection    = [ordered]@{ connectionReferenceLogicalName = $ConnectionReferenceLogicalName }
                api           = [ordered]@{ name = $connectionKey }
            }
        }
        definition = [ordered]@{
            '$schema'      = "https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#"
            contentVersion = "1.0.0.0"
            parameters     = [ordered]@{
                '$connections'    = [ordered]@{ defaultValue = @{}; type = "Object" }
                '$authentication' = [ordered]@{ defaultValue = @{}; type = "SecureObject" }
            }
            triggers = [ordered]@{
                Recurrence = [ordered]@{
                    recurrence           = [ordered]@{ frequency = "Minute"; interval = $IntervalMinutes; timeZone = "UTC" }
                    type                 = "Recurrence"
                    runtimeConfiguration = [ordered]@{ concurrency = [ordered]@{ runs = 1 } }
                }
            }
            actions = [ordered]@{
                Perform_an_unbound_action = [ordered]@{
                    type     = "OpenApiConnection"
                    inputs   = [ordered]@{
                        host = [ordered]@{
                            connectionName = $connectionKey
                            apiId          = "/providers/Microsoft.PowerApps/apis/$connectionKey"
                            operationId    = "PerformUnboundAction"
                        }
                        parameters     = [ordered]@{ actionName = $ActionName }
                        authentication = "@parameters('`$authentication')"
                    }
                    runAfter = @{}
                }
            }
        }
    }
    schemaVersion = "1.0.0.0"
}
$clientdataJson = $clientdata | ConvertTo-Json -Depth 30 -Compress

# ── Anlegen (in die Solution) ───────────────────────────────────────
Write-Host "  Erstelle Flow '$FlowName' (Recurrence alle $IntervalMinutes min -> $ActionName)..."
$body = [ordered]@{
    category      = 5        # Modern Cloud Flow
    type          = 1        # Definition
    primaryentity = "none"
    scope         = 4        # Organization
    ondemand      = $false
    name          = $FlowName
    description   = "FB-46/OE-12: ruft $ActionName alle $IntervalMinutes min auf (Stale-Chunk-Recovery)."
    clientdata    = $clientdataJson
} | ConvertTo-Json -Depth 5
$solCreateH = $writeH + @{ "MSCRM.SolutionUniqueName"=$SolutionName; "Prefer"="return=representation" }
$created = Invoke-RestMethod -Uri "$base/workflows" -Headers $solCreateH -Method Post `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
$flowId = $created.workflowid
Write-Host "  [CREATE] workflowid=$flowId (statecode=$($created.statecode))" -ForegroundColor Green

# ── Aktivieren (mit PublishAllXml-Heilung bei InvalidOpenApiFlow) ────
function Activate-Flow {
    Patch-Json "$base/workflows($flowId)" ([ordered]@{ statecode = 1; statuscode = 2 })
}
try {
    Activate-Flow
    Write-Host "  [ACTIVATE] Flow aktiv (statecode=1)." -ForegroundColor Green
}
catch {
    $msg = "$($_.Exception.Message) $($_.ErrorDetails.Message)"
    if ($msg -match "InvalidOpenApiFlow" -or $msg -match "GetMetadataForUnboundAction") {
        Write-Host "  Aktivierung scheiterte (Custom-API-Metadata noch nicht aufloesbar) -> PublishAllXml..." -ForegroundColor Yellow
        Invoke-RestMethod -Uri "$base/PublishAllXml" -Headers $writeH -Method Post -Body "{}" | Out-Null
        Start-Sleep -Seconds 5
        Activate-Flow
        Write-Host "  [ACTIVATE] Flow aktiv nach PublishAllXml (statecode=1)." -ForegroundColor Green
    }
    elseif ($msg -match "ConnectionAuthorizationFailed") {
        throw "Aktivierung scheiterte: ConnectionAuthorizationFailed. Die Connection Reference '$ConnectionReferenceLogicalName' gehoert nicht dem aufrufenden Service-Principal. Korrekte SP-eigene CR waehlen."
    }
    else { throw }
}

Write-Host ""
Write-Host "Recovery-Recurrence-Flow angelegt + aktiviert." -ForegroundColor Green
