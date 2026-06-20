#requires -Version 7.0
<#
.SYNOPSIS
    ADR-0009 Phase 4 (MVP-2): legt die drei sync Custom APIs fuer die Reporting-Paritaet an
    (jbe_ValidatePack, jbe_GenerateReport, jbe_BuildInventory) inkl. Request-/Response-Parameter.

.DESCRIPTION
    Pattern 1 (plugintypeid direkt am customapi, Stage 30 MainOperation, SYNC): die UI ruft per
    executeAction und bekommt den Ergebnis-String sofort zurueck (kein Async-Step, kein Flow).
    bindingtype=0 (Global/Unbound), isfunction=false (Action/POST).

    Idempotent: existiert die Custom API, wird nur der PluginType re-gepointet; existiert ein
    Parameter (per uniquename), wird er uebersprungen. Alle Komponenten gehen in die Solution.

    Voraussetzung: Plugin-Package mit den drei PluginTypes ist deployt (Deploy-PluginPackage.ps1
    -Mode Update) und das jbe_testcase-Schema existiert (Add-TestCaseSchema.ps1).

    DEV-only-Hard-Guard (-AllowNonDev fuer freigegebene Nicht-DEV-Envs).

.EXAMPLE
    pwsh ./Register-ReportingApis.ps1 -OrgUrl https://contoso-dev.crm4.dynamics.com `
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

# customapifieldtype: 0=Boolean 5=EntityReference 7=Integer 10=String 12=Guid
$T_STRING = 10
$T_INT    = 7

$apis = @(
    @{
        UniqueName  = "jbe_ValidatePack"; Name = "ValidatePack"; Display = "Validate Pack"
        PluginType  = "D365TestCenter.CrmPlugin.ValidatePackApi"
        Description = "ADR-0009 Phase 4: validiert die jbe_testcase-Definitionen (Symbol-Table) und liefert Findings als JSON."
        Request     = @( @{ Name="TestFilter"; Type=$T_STRING; Optional=$true } )
        Response    = @( @{ Name="Findings"; Type=$T_STRING }, @{ Name="ErrorCount"; Type=$T_INT } )
    },
    @{
        UniqueName  = "jbe_GenerateReport"; Name = "GenerateReport"; Display = "Generate Report"
        PluginType  = "D365TestCenter.CrmPlugin.GenerateReportApi"
        Description = "ADR-0009 Phase 4: erzeugt den Durchfuehrungsbericht eines Laufs (Markdown/HTML) aus Dataverse."
        Request     = @(
            @{ Name="RunId";  Type=$T_STRING; Optional=$false },
            @{ Name="Detail"; Type=$T_STRING; Optional=$true },
            @{ Name="Format"; Type=$T_STRING; Optional=$true }
        )
        Response    = @( @{ Name="Report"; Type=$T_STRING }, @{ Name="Format"; Type=$T_STRING } )
    },
    @{
        UniqueName  = "jbe_BuildInventory"; Name = "BuildInventory"; Display = "Build Inventory"
        PluginType  = "D365TestCenter.CrmPlugin.BuildInventoryApi"
        Description = "ADR-0009 Phase 4: erzeugt das Management-Inventar (Markdown) ueber die jbe_testcase-Landschaft."
        Request     = @( @{ Name="Filter"; Type=$T_STRING; Optional=$true } )
        Response    = @( @{ Name="Inventory"; Type=$T_STRING }, @{ Name="Count"; Type=$T_INT } )
    }
)

$tok = (Invoke-RestMethod -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
    -Method Post -Body @{ grant_type="client_credentials"; client_id=$ClientId; client_secret=$ClientSecret; scope="$OrgUrl/.default" } `
    -ContentType "application/x-www-form-urlencoded").access_token

$readH = @{ Authorization="Bearer $tok"; Accept="application/json" }
$writeH = @{ Authorization="Bearer $tok"; "Content-Type"="application/json; charset=utf-8"; "OData-MaxVersion"="4.0"; "OData-Version"="4.0" }
$solH  = $writeH + @{ "MSCRM.SolutionName"=$SolutionName }
$base = "$OrgUrl/api/data/v9.2"

function Post-Json([string]$url, [hashtable]$headers, $body) {
    $json = $body | ConvertTo-Json -Compress -Depth 6
    return Invoke-RestMethod -Uri $url -Headers $headers -Method Post -Body ([System.Text.Encoding]::UTF8.GetBytes($json))
}
function Get-PluginTypeId([string]$typename) {
    $r = (Invoke-RestMethod -Uri ("$base/plugintypes?`$filter=typename eq '$typename'&`$select=plugintypeid") -Headers $readH).value
    if ($r.Count -eq 0) { throw "PluginType '$typename' nicht gefunden -- Package deployt (Deploy-PluginPackage -Mode Update)?" }
    return $r[0].plugintypeid
}

Write-Host ""
Write-Host "==> Reporting-Custom-APIs ($OrgUrl)" -ForegroundColor Cyan

foreach ($api in $apis) {
    Write-Host "$($api.UniqueName) ($($api.PluginType))" -ForegroundColor Cyan
    $ptId = Get-PluginTypeId $api.PluginType

    # 1. Custom API (idempotent: re-point plugintypeid)
    $existing = (Invoke-RestMethod -Uri ("$base/customapis?`$filter=uniquename eq '$($api.UniqueName)'&`$select=customapiid,_plugintypeid_value") -Headers $readH).value
    if ($existing.Count -gt 0) {
        $apiId = $existing[0].customapiid
        if ($existing[0]._plugintypeid_value -ne $ptId) {
            $patch = @{ "PluginTypeId@odata.bind"="/plugintypes($ptId)" } | ConvertTo-Json -Compress
            Invoke-RestMethod -Uri "$base/customapis($apiId)" -Headers ($writeH + @{ "If-Match"="*" }) -Method Patch -Body ([System.Text.Encoding]::UTF8.GetBytes($patch)) | Out-Null
            Write-Host "  [REPOINT] Custom API" -ForegroundColor Green
        } else {
            Write-Host "  [SKIP] Custom API existiert" -ForegroundColor Yellow
        }
    } else {
        $body = @{
            uniquename                      = $api.UniqueName
            name                            = $api.Name
            displayname                     = $api.Display
            description                     = $api.Description
            bindingtype                     = 0      # Global / Unbound
            isfunction                      = $false # Action (POST)
            isprivate                       = $false
            allowedcustomprocessingsteptype = 0      # None: Pattern-1-Main-Plugin, keine zusaetzlichen Steps
            "PluginTypeId@odata.bind"       = "/plugintypes($ptId)"
        }
        $apiId = (Post-Json "$base/customapis" ($solH + @{ "Prefer"="return=representation" }) $body).customapiid
        Write-Host "  [CREATE] Custom API (Pattern 1, sync, Global)" -ForegroundColor Green
    }

    # 2. Request-Parameter (idempotent pro API; uniquename = Plugin-Input-Key, OHNE Punkt --
    #    Dataverse nutzt den uniquename als Property-Name, der darf kein ':' '.' '@' enthalten)
    foreach ($p in $api.Request) {
        $un = $p.Name
        $ex = (Invoke-RestMethod -Uri ("$base/customapirequestparameters?`$filter=uniquename eq '$un' and _customapiid_value eq $apiId&`$select=customapirequestparameterid") -Headers $readH).value
        if ($ex.Count -gt 0) { Write-Host "    [SKIP] req $($p.Name)" -ForegroundColor Yellow; continue }
        $body = @{
            uniquename                = $un
            name                      = $p.Name
            displayname               = $p.Name
            type                      = $p.Type
            isoptional                = [bool]$p.Optional
            "CustomAPIId@odata.bind"  = "/customapis($apiId)"
        }
        Post-Json "$base/customapirequestparameters" $solH $body | Out-Null
        Write-Host "    [CREATE] req $($p.Name) (type=$($p.Type), optional=$([bool]$p.Optional))" -ForegroundColor Green
    }

    # 3. Response-Properties (idempotent pro API; uniquename = Plugin-Output-Key, OHNE Punkt)
    foreach ($p in $api.Response) {
        $un = $p.Name
        $ex = (Invoke-RestMethod -Uri ("$base/customapiresponseproperties?`$filter=uniquename eq '$un' and _customapiid_value eq $apiId&`$select=customapiresponsepropertyid") -Headers $readH).value
        if ($ex.Count -gt 0) { Write-Host "    [SKIP] resp $($p.Name)" -ForegroundColor Yellow; continue }
        $body = @{
            uniquename                = $un
            name                      = $p.Name
            displayname               = $p.Name
            type                      = $p.Type
            "CustomAPIId@odata.bind"  = "/customapis($apiId)"
        }
        Post-Json "$base/customapiresponseproperties" $solH $body | Out-Null
        Write-Host "    [CREATE] resp $($p.Name) (type=$($p.Type))" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Reporting-Custom-APIs abgeschlossen." -ForegroundColor Green
