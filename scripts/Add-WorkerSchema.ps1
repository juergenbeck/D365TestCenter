#requires -Version 7.0
<#
.SYNOPSIS
    ADR-0009 Phase 0: legt das Schema fuer die Worker-Ausfuehrung (Koordinator + Worker) an.

.DESCRIPTION
    Idempotent (Existenz-Check pro Komponente), Web-API EntityDefinitions/Attributes/Keys + Actions.
    Alle Komponenten gehen in die Solution D365TestCenter (MSCRM.SolutionUniqueName).
    Reihenfolge (Schema-Spezifikation, Abschnitt 5):
      1. Globales OptionSet jbe_chunkstatus (Neu/Laeuft/Fortsetzen/Verarbeitet/Fehler, 105710000-004)
      2. jbe_teststatus + Wert 105710004 "Aufteilung laeuft" (InsertOptionValue)
      3. jbe_testchunk-Entity (AutoNumber-Primary) + Attribute + N:1 jbe_testrun -> jbe_testchunk
      4. Neue jbe_testrun-Felder (Chunk-Zaehler, Cursor, Lauf-Statistik)
      5. Alternate Key jbe_testrunresult_run_test_key auf (jbe_testrunid, jbe_testid)
      6. PublishXml fuer jbe_testrun, jbe_testchunk, jbe_testrunresult

    Konstanten exakt aus WorkerSchema.cs / schema-adr0009-phase0.md (Konstanten-Vertrag fuer
    RunCoordinator/RunChunkWorker). Prefix durchgaengig 105710xxx.

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
    pwsh ./Add-WorkerSchema.ps1 -OrgUrl https://contoso-dev.crm4.dynamics.com `
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

# ── DEV-only Hard-Guard ──────────────────────────────────────────────
if ($OrgUrl -notmatch "-dev\." -and -not $AllowNonDev) {
    throw "DEV-only: -OrgUrl '$OrgUrl' enthaelt nicht '-dev.'. Schema-Aenderung verweigert " +
          "(nutze -AllowNonDev nur mit ausdruecklicher Freigabe)."
}

# ── Auth (client_credentials) ────────────────────────────────────────
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
Write-Host "==> ADR-0009 Phase 0: Worker-Schema" -ForegroundColor Cyan
Write-Host "    Org: $OrgUrl  Solution: $SolutionName" -ForegroundColor Gray
if ($WhatIf) { Write-Host "    [WhatIf: keine Aenderungen]" -ForegroundColor Yellow }
Write-Host ""

# ── Helfer ───────────────────────────────────────────────────────────
function Invoke-Read([string]$url) {
    return Invoke-RestMethod -Uri $url -Headers $readH -Method Get -ErrorAction Stop
}
function Invoke-Write([string]$url, $bodyObj) {
    $json = $bodyObj | ConvertTo-Json -Depth 20 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    # Retry: direkt nach einem Entity-Create wirft das Attribut-Anlegen gern den transienten
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
function Test-EntityExists([string]$logical) {
    try { Invoke-Read "$base/EntityDefinitions(LogicalName='$logical')?`$select=LogicalName" | Out-Null; return $true }
    catch { return $false }
}
function Test-AttributeExists([string]$entity, [string]$attr) {
    try { Invoke-Read "$base/EntityDefinitions(LogicalName='$entity')/Attributes(LogicalName='$attr')?`$select=LogicalName" | Out-Null; return $true }
    catch { return $false }
}
function Get-GlobalOptionSet([string]$name) {
    try { return Invoke-Read "$base/GlobalOptionSetDefinitions(Name='$name')" } catch { return $null }
}
function Test-RelationshipExists([string]$schema) {
    try { Invoke-Read "$base/RelationshipDefinitions(SchemaName='$schema')?`$select=SchemaName" | Out-Null; return $true }
    catch { return $false }
}
function Test-KeyExists([string]$entity, [string]$keySchema) {
    try {
        $r = Invoke-Read "$base/EntityDefinitions(LogicalName='$entity')/Keys?`$select=SchemaName&`$filter=SchemaName eq '$keySchema'"
        return ($r.value.Count -gt 0)
    } catch { return $false }
}

# Attribut-Bauer (geben das Body-Hashtable zurueck)
function New-IntegerAttr([string]$schema, [string]$display, [int]$min, [int]$max) {
    return @{
        "@odata.type"="Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
        AttributeType="Integer"; AttributeTypeName=@{ Value="IntegerType" }
        SchemaName=$schema; MinValue=$min; MaxValue=$max
        RequiredLevel=(New-Required); DisplayName=(New-Label $display)
    }
}
function New-MemoAttr([string]$schema, [string]$display, [int]$maxLen) {
    return @{
        "@odata.type"="Microsoft.Dynamics.CRM.MemoAttributeMetadata"
        AttributeType="Memo"; AttributeTypeName=@{ Value="MemoType" }
        SchemaName=$schema; MaxLength=$maxLen; Format="TextArea"
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
function New-DateTimeAttr([string]$schema, [string]$display) {
    return @{
        "@odata.type"="Microsoft.Dynamics.CRM.DateTimeAttributeMetadata"
        AttributeType="DateTime"; AttributeTypeName=@{ Value="DateTimeType" }
        SchemaName=$schema; Format="DateAndTime"
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

# ═══════════════════════════════════════════════════════════════════
# 1. Globales OptionSet jbe_chunkstatus
# ═══════════════════════════════════════════════════════════════════
Write-Host "1) OptionSet jbe_chunkstatus" -ForegroundColor Cyan
$chunkStatusOptions = @(
    @{ Value=105710000; Label="Neu" }
    @{ Value=105710001; Label="Laeuft" }
    @{ Value=105710002; Label="Fortsetzen" }
    @{ Value=105710003; Label="Verarbeitet" }
    @{ Value=105710004; Label="Fehler" }
)
if (Get-GlobalOptionSet "jbe_chunkstatus") {
    Write-Host "  [SKIP] jbe_chunkstatus existiert" -ForegroundColor Yellow
} elseif ($WhatIf) {
    Write-Host "  [WhatIf] jbe_chunkstatus (5 Werte)" -ForegroundColor Yellow
} else {
    $body = @{
        "@odata.type"="Microsoft.Dynamics.CRM.OptionSetMetadata"
        Name="jbe_chunkstatus"; OptionSetType="Picklist"; IsGlobal=$true
        DisplayName=(New-Label "Chunk Status")
        Options=@($chunkStatusOptions | ForEach-Object {
            @{ Value=$_.Value; Label=(New-Label $_.Label) }
        })
    }
    Invoke-Write "$base/GlobalOptionSetDefinitions" $body | Out-Null
    Write-Host "  [CREATE] jbe_chunkstatus (5 Werte)" -ForegroundColor Green
}

# ═══════════════════════════════════════════════════════════════════
# 2. jbe_teststatus + 105710004 "Aufteilung laeuft"
# ═══════════════════════════════════════════════════════════════════
Write-Host "2) jbe_teststatus += 105710004 (Aufteilung laeuft)" -ForegroundColor Cyan
$ts = Get-GlobalOptionSet "jbe_teststatus"
if (-not $ts) {
    Write-Host "  [WARN] jbe_teststatus nicht gefunden -- Basis-Schema fehlt?" -ForegroundColor Red
} elseif ($ts.Options.Value -contains 105710004) {
    Write-Host "  [SKIP] Wert 105710004 existiert" -ForegroundColor Yellow
} elseif ($WhatIf) {
    Write-Host "  [WhatIf] InsertOptionValue 105710004" -ForegroundColor Yellow
} else {
    $body = @{ OptionSetName="jbe_teststatus"; Value=105710004; Label=(New-Label "Aufteilung laeuft") }
    Invoke-Write "$base/InsertOptionValue" $body | Out-Null
    Write-Host "  [CREATE] jbe_teststatus 105710004" -ForegroundColor Green
}

# ═══════════════════════════════════════════════════════════════════
# 3. jbe_testchunk-Entity
# ═══════════════════════════════════════════════════════════════════
Write-Host "3) Entity jbe_testchunk" -ForegroundColor Cyan
if (Test-EntityExists "jbe_testchunk") {
    Write-Host "  [SKIP] jbe_testchunk existiert" -ForegroundColor Yellow
} elseif ($WhatIf) {
    Write-Host "  [WhatIf] jbe_testchunk (Primary jbe_name, AutoNumber CH-{SEQNUM:6})" -ForegroundColor Yellow
} else {
    $primary = @{
        "@odata.type"="Microsoft.Dynamics.CRM.StringAttributeMetadata"
        AttributeType="String"; AttributeTypeName=@{ Value="StringType" }
        SchemaName="jbe_name"; IsPrimaryName=$true; MaxLength=100
        AutoNumberFormat="CH-{SEQNUM:6}"; FormatName=@{ Value="Text" }
        RequiredLevel=(New-Required); DisplayName=(New-Label "Name")
    }
    $entity = @{
        "@odata.type"="Microsoft.Dynamics.CRM.EntityMetadata"
        SchemaName="jbe_testchunk"; OwnershipType="UserOwned"
        IsActivity=$false; HasActivities=$false; HasNotes=$false
        DisplayName=(New-Label "Test Chunk"); DisplayCollectionName=(New-Label "Test Chunks")
        Description=(New-Label "Fan-Out-Chunk eines Testlaufs (ADR-0009 Worker-Ausfuehrung)")
        Attributes=@($primary)
    }
    Invoke-Write "$base/EntityDefinitions" $entity | Out-Null
    Write-Host "  [CREATE] jbe_testchunk" -ForegroundColor Green
}

# 3b. jbe_testchunk-Attribute (ohne jbe_testrunid -- das macht die Relationship)
Write-Host "3b) jbe_testchunk-Attribute" -ForegroundColor Cyan
Add-Attribute "jbe_testchunk" (New-IntegerAttr  "jbe_chunkindex"     "Chunk Index"      0 100000)
Add-Attribute "jbe_testchunk" (New-MemoAttr     "jbe_testids"        "Test IDs (JSON)"  1000000)
Add-Attribute "jbe_testchunk" (New-IntegerAttr  "jbe_group_cursor"   "Group Cursor"     0 100000)
Add-Attribute "jbe_testchunk" (New-IntegerAttr  "jbe_processedcount" "Processed Count"  0 100000)
Add-Attribute "jbe_testchunk" (New-IntegerAttr  "jbe_failedcount"    "Failed Count"     0 100000)
Add-Attribute "jbe_testchunk" (New-DateTimeAttr "jbe_startedon"      "Started On")
Add-Attribute "jbe_testchunk" (New-DateTimeAttr "jbe_completedon"    "Completed On")
Add-Attribute "jbe_testchunk" (New-IntegerAttr  "jbe_durationms"     "Duration (ms)"    0 2147483647)
Add-Attribute "jbe_testchunk" (New-IntegerAttr  "jbe_continuations"  "Continuations"    0 100000)
Add-Attribute "jbe_testchunk" (New-MemoAttr     "jbe_errordetails"   "Error Details"    1000000)

# 3c. Picklist jbe_chunkstatus (an globales OptionSet gebunden)
if (Test-AttributeExists "jbe_testchunk" "jbe_chunkstatus") {
    Write-Host "  [SKIP] jbe_testchunk.jbe_chunkstatus" -ForegroundColor Yellow
} elseif ($WhatIf) {
    Write-Host "  [WhatIf] jbe_testchunk.jbe_chunkstatus (-> global jbe_chunkstatus)" -ForegroundColor Yellow
} else {
    $gos = Get-GlobalOptionSet "jbe_chunkstatus"
    $pick = @{
        "@odata.type"="Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
        AttributeType="Picklist"; AttributeTypeName=@{ Value="PicklistType" }
        SchemaName="jbe_chunkstatus"; RequiredLevel=(New-Required)
        DisplayName=(New-Label "Chunk Status")
        "GlobalOptionSet@odata.bind"="/GlobalOptionSetDefinitions($($gos.MetadataId))"
    }
    Invoke-Write "$base/EntityDefinitions(LogicalName='jbe_testchunk')/Attributes" $pick | Out-Null
    Write-Host "  [CREATE] jbe_testchunk.jbe_chunkstatus" -ForegroundColor Green
}

# 3d. N:1 jbe_testrun (1) -> jbe_testchunk (N) -- erzeugt das Lookup jbe_testrunid
Write-Host "3d) Relationship jbe_testrun_testchunk (Lookup jbe_testrunid, Cascade Delete)" -ForegroundColor Cyan
if (Test-RelationshipExists "jbe_testrun_testchunk") {
    Write-Host "  [SKIP] jbe_testrun_testchunk" -ForegroundColor Yellow
} elseif ($WhatIf) {
    Write-Host "  [WhatIf] jbe_testrun_testchunk" -ForegroundColor Yellow
} else {
    $rel = @{
        "@odata.type"="Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata"
        SchemaName="jbe_testrun_testchunk"
        ReferencedEntity="jbe_testrun"; ReferencingEntity="jbe_testchunk"
        CascadeConfiguration=@{
            Assign="NoCascade"; Share="NoCascade"; Unshare="NoCascade"; Reparent="NoCascade"
            Delete="Cascade"; Merge="NoCascade"
        }
        Lookup=@{
            "@odata.type"="Microsoft.Dynamics.CRM.LookupAttributeMetadata"
            SchemaName="jbe_TestRunId"; RequiredLevel=(New-Required)
            DisplayName=(New-Label "Test Run")
        }
        AssociatedMenuConfiguration=@{
            Behavior="UseCollectionName"; Group="Details"; Order=10000
            Label=(New-Label "Test Chunks")
        }
    }
    Invoke-Write "$base/RelationshipDefinitions" $rel | Out-Null
    Write-Host "  [CREATE] jbe_testrun_testchunk" -ForegroundColor Green
}

# ═══════════════════════════════════════════════════════════════════
# 4. Neue jbe_testrun-Felder
# ═══════════════════════════════════════════════════════════════════
Write-Host "4) Neue jbe_testrun-Felder" -ForegroundColor Cyan
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_chunks_total"       "Chunks Total"        0 100000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_chunks_done"        "Chunks Done"         0 100000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_chunks_failed"      "Chunks Failed"       0 100000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_coordinator_cursor" "Coordinator Cursor"  0 100000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_chunksize"          "Chunk Size"          1 1000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_durationms"         "Duration (ms)"       0 2147483647)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_totaltestms"        "Total Test (ms)"     0 2147483647)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_avgtestms"          "Avg Test (ms)"       0 600000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_mediantestms"       "Median Test (ms)"    0 600000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_mintestms"          "Min Test (ms)"       0 600000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_maxtestms"          "Max Test (ms)"       0 600000)
Add-Attribute "jbe_testrun" (New-StringAttr  "jbe_slowesttestid"      "Slowest Test ID"     100)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_errored"            "Errored"             0 100000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_skipped"            "Skipped"             0 100000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_recordscreated"     "Records Created"     0 1000000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_continuations"      "Continuations"       0 100000)
Add-Attribute "jbe_testrun" (New-IntegerAttr "jbe_maxconcurrent"      "Max Concurrent"      0 1000)

# ═══════════════════════════════════════════════════════════════════
# 5. Alternate Key jbe_testrunresult_run_test_key
# ═══════════════════════════════════════════════════════════════════
Write-Host "5) Alternate Key jbe_testrunresult_run_test_key (jbe_testrunid, jbe_testid)" -ForegroundColor Cyan
if (Test-KeyExists "jbe_testrunresult" "jbe_testrunresult_run_test_key") {
    Write-Host "  [SKIP] Alternate Key existiert" -ForegroundColor Yellow
} elseif ($WhatIf) {
    Write-Host "  [WhatIf] Alternate Key" -ForegroundColor Yellow
} else {
    $key = @{
        "@odata.type"="Microsoft.Dynamics.CRM.EntityKeyMetadata"
        SchemaName="jbe_testrunresult_run_test_key"
        DisplayName=(New-Label "Run + Test Key")
        KeyAttributes=@("jbe_testrunid","jbe_testid")
    }
    Invoke-Write "$base/EntityDefinitions(LogicalName='jbe_testrunresult')/Keys" $key | Out-Null
    Write-Host "  [CREATE] Alternate Key (Aktivierung asynchron)" -ForegroundColor Green
}

# ═══════════════════════════════════════════════════════════════════
# 6. PublishXml
# ═══════════════════════════════════════════════════════════════════
if (-not $WhatIf) {
    Write-Host "6) PublishXml (jbe_testrun, jbe_testchunk, jbe_testrunresult)" -ForegroundColor Cyan
    $parameterXml = "<importexportxml><entities>" +
        "<entity>jbe_testrun</entity><entity>jbe_testchunk</entity><entity>jbe_testrunresult</entity>" +
        "</entities></importexportxml>"
    try {
        Invoke-Write "$base/PublishXml" @{ ParameterXml=$parameterXml } | Out-Null
        Write-Host "  PublishXml OK" -ForegroundColor Green
    } catch {
        Write-Host "  PublishXml WARN: $($_.ErrorDetails.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Worker-Schema abgeschlossen." -ForegroundColor Green
Write-Host "Hinweis: Alternate-Key-Aktivierung laeuft asynchron (System Job). Vor dem Plugin-Deploy" -ForegroundColor Gray
Write-Host "         pruefen, dass der Key 'Active' ist." -ForegroundColor Gray
