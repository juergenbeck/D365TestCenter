#requires -Version 7.0
<#
.SYNOPSIS
    ADR-0009 Phase 0 (MVP-2): legt die jbe_testcase-Metadatenfelder fuer die Reporting-Paritaet an.

.DESCRIPTION
    Idempotent (Existenz-Check pro Komponente), Web-API EntityDefinitions/Attributes + Actions.
    Alle Komponenten gehen in die Solution D365TestCenter (MSCRM.SolutionUniqueName).
    Reihenfolge (Schema-Spezifikation):
      1. Globales OptionSet jbe_lifecyclestatus (Entwurf/Aktiv/Instabil/Historisch/Archiviert, 105710000-004)
      2. jbe_testcase-Felder:
         jbe_lifecyclestatus (Picklist -> global), jbe_domain (String, Freitext),
         jbe_testlevel (Int), jbe_owner (String), jbe_tickets (String/CSV),
         jbe_envscope (String/CSV), jbe_estimatedminutes (Int), jbe_zephyrkey (String)
      3. PublishXml fuer jbe_testcase

    Konstanten exakt aus WorkerSchema.cs (Tc*-Konstanten + Lifecycle*-OptionSet-Werte). Prefix 105710xxx.

    DEV-only-Hard-Guard: -OrgUrl muss '-dev.' enthalten. Andere Envs nur mit -AllowNonDev
    (explizite Freigabe), wegen der Promotion-Falle (Schema vor Plugin auf ALLEN Ziel-Envs).

.PARAMETER OrgUrl
    Org-URL, z.B. https://contoso-dev.crm4.dynamics.com (muss '-dev.' enthalten, sonst -AllowNonDev).

.PARAMETER ClientId / ClientSecret / TenantId
    App-Registration (client_credentials). Generisch -- keine projektspezifischen Defaults.

.PARAMETER AllowNonDev
    Hebt den DEV-Guard auf (nur mit ausdruecklicher Freigabe fuer TEST/CDHTEST/DATATEST verwenden).

.PARAMETER WhatIf
    Zeigt nur, was angelegt wuerde.

.EXAMPLE
    pwsh ./Add-TestCaseSchema.ps1 -OrgUrl https://contoso-dev.crm4.dynamics.com `
        -ClientId <id> -ClientSecret <secret> -TenantId <tenant>
#>

param(
    [Parameter(Mandatory)] [string]$OrgUrl,
    [Parameter(Mandatory)] [string]$ClientId,
    [Parameter(Mandatory)] [string]$ClientSecret,
    [Parameter(Mandatory)] [string]$TenantId,
    [switch]$AllowNonDev,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$SolutionName = "D365TestCenter"

# -- DEV-only Hard-Guard --------------------------------------------------
if ($OrgUrl -notmatch "-dev\." -and -not $AllowNonDev) {
    throw "DEV-only: -OrgUrl '$OrgUrl' enthaelt nicht '-dev.'. Schema-Aenderung verweigert " +
          "(nutze -AllowNonDev nur mit ausdruecklicher Freigabe)."
}

# -- Auth (client_credentials) --------------------------------------------
$tok = (Invoke-RestMethod -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
    -Method Post -Body @{ grant_type="client_credentials"; client_id=$ClientId; client_secret=$ClientSecret; scope="$OrgUrl/.default" } `
    -ContentType "application/x-www-form-urlencoded").access_token

$readH  = @{ Authorization="Bearer $tok"; Accept="application/json" }
$writeH = @{
    Authorization="Bearer $tok"
    "Content-Type"="application/json; charset=utf-8"
    "OData-MaxVersion"="4.0"
    "OData-Version"="4.0"
    "MSCRM.SolutionUniqueName"=$SolutionName
}
$base = "$OrgUrl/api/data/v9.2"

Write-Host ""
Write-Host "==> ADR-0009 Phase 0 (MVP-2): jbe_testcase-Metadatenfelder" -ForegroundColor Cyan
Write-Host "    Org: $OrgUrl  Solution: $SolutionName" -ForegroundColor Gray
if ($WhatIf) { Write-Host "    [WhatIf: keine Aenderungen]" -ForegroundColor Yellow }
Write-Host ""

# -- Helfer ---------------------------------------------------------------
function Invoke-Read([string]$url) {
    return Invoke-RestMethod -Uri $url -Headers $readH -Method Get -ErrorAction Stop
}
function Invoke-Write([string]$url, $bodyObj) {
    $json = $bodyObj | ConvertTo-Json -Depth 20 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    # Retry: direkt nach einem Schema-Create wirft das Folge-Anlegen gern den transienten
    # 0x80040216 ("unexpected error", Metadaten-Propagation). Bis zu 4 Versuche mit Backoff.
    for ($attempt = 1; ; $attempt++) {
        try {
            return Invoke-RestMethod -Uri $url -Headers $writeH -Method Post -Body $bytes -ErrorAction Stop
        } catch {
            $msg = "$($_.ErrorDetails.Message)$($_.Exception.Message)"
            $transient = $msg -match "0x80040216" -or $msg -match "unexpected error" -or $msg -match "0x80044150"
            if ($attempt -ge 4 -or -not $transient) { throw }
            Write-Host "    (transient, Versuch $attempt -> Retry in 5s)" -ForegroundColor DarkYellow
            Start-Sleep -Seconds 5
        }
    }
}
function New-Label([string]$text) {
    return @{
        "@odata.type" = "Microsoft.Dynamics.CRM.Label"
        LocalizedLabels = @(@{
            "@odata.type" = "Microsoft.Dynamics.CRM.LocalizedLabel"
            Label = $text; LanguageCode = 1033
        })
    }
}
function New-Required() {
    return @{ Value="None"; CanBeChanged=$true; ManagedPropertyLogicalName="canmodifyrequirementlevelsettings" }
}
function Test-AttributeExists([string]$entity, [string]$attr) {
    try { Invoke-Read "$base/EntityDefinitions(LogicalName='$entity')/Attributes(LogicalName='$attr')?`$select=LogicalName" | Out-Null; return $true }
    catch { return $false }
}
function Get-GlobalOptionSet([string]$name) {
    try { return Invoke-Read "$base/GlobalOptionSetDefinitions(Name='$name')" } catch { return $null }
}

function New-IntegerAttr([string]$schema, [string]$display, [int]$min, [int]$max) {
    return @{
        "@odata.type"="Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
        AttributeType="Integer"; AttributeTypeName=@{ Value="IntegerType" }
        SchemaName=$schema; MinValue=$min; MaxValue=$max
        RequiredLevel=(New-Required); DisplayName=(New-Label $display)
    }
}
function New-StringAttr([string]$schema, [string]$display, [int]$maxLen) {
    return @{
        "@odata.type"="Microsoft.Dynamics.CRM.StringAttributeMetadata"
        AttributeType="String"; AttributeTypeName=@{ Value="StringType" }
        SchemaName=$schema; MaxLength=$maxLen; FormatName=@{ Value="Text" }
        RequiredLevel=(New-Required); DisplayName=(New-Label $display)
    }
}

function Add-Attribute([string]$entity, $attrBody) {
    $logical = $attrBody.SchemaName.ToLowerInvariant()
    if (Test-AttributeExists $entity $logical) { Write-Host "  [SKIP] $entity.$logical" -ForegroundColor Yellow; return }
    if ($WhatIf) { Write-Host "  [WhatIf] $entity.$logical" -ForegroundColor Yellow; return }
    Invoke-Write "$base/EntityDefinitions(LogicalName='$entity')/Attributes" $attrBody | Out-Null
    Write-Host "  [CREATE] $entity.$logical" -ForegroundColor Green
}

# =====================================================================
# 1. Globales OptionSet jbe_lifecyclestatus
# =====================================================================
Write-Host "1) OptionSet jbe_lifecyclestatus" -ForegroundColor Cyan
$lifecycleOptions = @(
    @{ Value=105710000; Label="Entwurf" }
    @{ Value=105710001; Label="Aktiv" }
    @{ Value=105710002; Label="Instabil" }
    @{ Value=105710003; Label="Historisch" }
    @{ Value=105710004; Label="Archiviert" }
)
if (Get-GlobalOptionSet "jbe_lifecyclestatus") {
    Write-Host "  [SKIP] jbe_lifecyclestatus existiert" -ForegroundColor Yellow
} elseif ($WhatIf) {
    Write-Host "  [WhatIf] jbe_lifecyclestatus (5 Werte)" -ForegroundColor Yellow
} else {
    $body = @{
        "@odata.type"="Microsoft.Dynamics.CRM.OptionSetMetadata"
        Name="jbe_lifecyclestatus"; OptionSetType="Picklist"; IsGlobal=$true
        DisplayName=(New-Label "Lifecycle Status")
        Options=@($lifecycleOptions | ForEach-Object {
            @{ Value=$_.Value; Label=(New-Label $_.Label) }
        })
    }
    Invoke-Write "$base/GlobalOptionSetDefinitions" $body | Out-Null
    Write-Host "  [CREATE] jbe_lifecyclestatus (5 Werte)" -ForegroundColor Green
}

# =====================================================================
# 2. jbe_testcase-Felder
# =====================================================================
Write-Host "2) jbe_testcase-Felder" -ForegroundColor Cyan

# 2a. Picklist jbe_lifecyclestatus (an globales OptionSet gebunden)
if (Test-AttributeExists "jbe_testcase" "jbe_lifecyclestatus") {
    Write-Host "  [SKIP] jbe_testcase.jbe_lifecyclestatus" -ForegroundColor Yellow
} elseif ($WhatIf) {
    Write-Host "  [WhatIf] jbe_testcase.jbe_lifecyclestatus (-> global jbe_lifecyclestatus)" -ForegroundColor Yellow
} else {
    # Die MetadataId eines frisch angelegten globalen OptionSets ist nicht sofort propagiert;
    # der Bind /GlobalOptionSetDefinitions(<leer>) wirft sonst 0x80060888. Retry bis sie da ist.
    $gos = $null
    for ($i = 1; $i -le 6; $i++) {
        $gos = Get-GlobalOptionSet "jbe_lifecyclestatus"
        if ($gos -and $gos.MetadataId) { break }
        Write-Host "    (OptionSet-MetadataId noch nicht propagiert, Versuch $i -> Retry in 5s)" -ForegroundColor DarkYellow
        Start-Sleep -Seconds 5
    }
    if (-not ($gos -and $gos.MetadataId)) { throw "jbe_lifecyclestatus MetadataId nicht abrufbar (nach Retries)." }
    $pick = @{
        "@odata.type"="Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
        AttributeType="Picklist"; AttributeTypeName=@{ Value="PicklistType" }
        SchemaName="jbe_lifecyclestatus"; RequiredLevel=(New-Required)
        DisplayName=(New-Label "Lifecycle Status")
        "GlobalOptionSet@odata.bind"="/GlobalOptionSetDefinitions($($gos.MetadataId))"
    }
    Invoke-Write "$base/EntityDefinitions(LogicalName='jbe_testcase')/Attributes" $pick | Out-Null
    Write-Host "  [CREATE] jbe_testcase.jbe_lifecyclestatus" -ForegroundColor Green
}

# 2b. Skalare Felder (jbe_domain ist Freitext -- generisch ueber Projekte, Entscheidung Juergen S42)
Add-Attribute "jbe_testcase" (New-StringAttr  "jbe_domain"            "Domain"             100)
Add-Attribute "jbe_testcase" (New-IntegerAttr "jbe_testlevel"         "Test Level"         0 1000)
Add-Attribute "jbe_testcase" (New-StringAttr  "jbe_owner"             "Owner"              100)
Add-Attribute "jbe_testcase" (New-StringAttr  "jbe_tickets"           "Tickets"            500)
Add-Attribute "jbe_testcase" (New-StringAttr  "jbe_envscope"          "Env Scope"          200)
Add-Attribute "jbe_testcase" (New-IntegerAttr "jbe_estimatedminutes"  "Estimated Minutes"  0 100000)
Add-Attribute "jbe_testcase" (New-StringAttr  "jbe_zephyrkey"         "Zephyr Key"         100)

# =====================================================================
# 3. PublishXml
# =====================================================================
if (-not $WhatIf) {
    Write-Host "3) PublishXml (jbe_testcase)" -ForegroundColor Cyan
    $parameterXml = "<importexportxml><entities><entity>jbe_testcase</entity></entities></importexportxml>"
    try {
        Invoke-Write "$base/PublishXml" @{ ParameterXml=$parameterXml } | Out-Null
        Write-Host "  PublishXml OK" -ForegroundColor Green
    } catch {
        Write-Host "  PublishXml WARN: $($_.ErrorDetails.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "jbe_testcase-Schema abgeschlossen." -ForegroundColor Green
