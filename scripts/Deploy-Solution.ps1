<#
.SYNOPSIS
    Deploys the Integration Test Center (ITT) solution to Dataverse DEV.

.DESCRIPTION
    Creates:
    - Publisher "ITT" (prefix: itt, OptionValuePrefix: 100571)
    - Solution "IntegrationTestCenter"
    - 5 global OptionSets (itt_teststatus, itt_testoutcome, itt_testcategory, itt_stepphase, itt_stepstatus)
    - 4 custom tables (itt_testcase, itt_testrun, itt_testrunresult, itt_teststep)
    - All attributes on the 4 tables
    - N:1 relationships (testrunresult -> testrun, teststep -> testrunresult)
    - Web Resources (HTML + JSON packs)
    - Publishes all customizations

    Idempotent: checks existence before creating each component.

.NOTES
    Requires: PowerShell 5.1+ (Windows), TokenVault (common/auth)
    Target: DEV only
#>

$ErrorActionPreference = "Stop"

# -- Logging ----------------------------------------------------------------
$scriptDir = $PSScriptRoot
$logDir    = Join-Path $scriptDir "logs"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$logFile   = Join-Path $logDir "deploy-itt-solution_$timestamp.log"
Start-Transcript -Path $logFile

try {

# -- Auth -------------------------------------------------------------------
# TokenVault liegt unter projekte/common/auth/
$repoRoot = "C:\Users\Juerg\Source\repo\Markant"
. (Join-Path $repoRoot "projekte\common\auth\TokenVault.ps1")
$headers = Get-VaultHeaders -System 'dataverse_dev'

$config       = Get-Content (Join-Path $scriptDir "deploy-config.json") -Raw | ConvertFrom-Json
$baseUrl      = $config.resource.TrimEnd("/") + "/api/data/v9.2"
$solutionName = $config.solutionUniqueName
$pubPrefix    = $config.publisherPrefix
$pubUnique    = $config.publisherUniqueName
$optPrefix    = $config.publisherOptionValuePrefix

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Integration Test Center - Deployment" -ForegroundColor Cyan
Write-Host "  Target: $($config.resource)" -ForegroundColor Cyan
Write-Host "  Solution: $solutionName" -ForegroundColor Cyan
Write-Host "  Publisher: $pubUnique (prefix: $pubPrefix)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ==========================================================================
#  HELPER FUNCTIONS
# ==========================================================================

function Test-GlobalOptionSetExists([string]$Name) {
    try {
        $null = Invoke-RestMethod -Method Get `
            -Uri "$baseUrl/GlobalOptionSetDefinitions(Name='$Name')?`$select=Name" `
            -Headers $headers
        return $true
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        # 404 = HTTP Not Found, 400 = Dataverse-Fehler (0x80040217 "Could not find optionset")
        if ($code -eq 404 -or $code -eq 400) { return $false }
        throw
    }
}

function Test-EntityExists([string]$LogicalName) {
    try {
        $null = Invoke-RestMethod -Method Get `
            -Uri "$baseUrl/EntityDefinitions(LogicalName='$LogicalName')?`$select=LogicalName" `
            -Headers $headers
        return $true
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        if ($code -eq 404 -or $code -eq 400) { return $false }
        throw
    }
}

function Test-AttributeExists([string]$EntityLogicalName, [string]$AttributeLogicalName) {
    try {
        $null = Invoke-RestMethod -Method Get `
            -Uri "$baseUrl/EntityDefinitions(LogicalName='$EntityLogicalName')/Attributes(LogicalName='$AttributeLogicalName')?`$select=LogicalName" `
            -Headers $headers
        return $true
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        if ($code -eq 404) { return $false }
        # 400 mit "could not find" = nicht vorhanden
        if ($code -eq 400) {
            $errMsg = ""
            try {
                $sr = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
                $errMsg = $sr.ReadToEnd()
                $sr.Close()
            } catch {}
            if ($errMsg -match "could not find|not found|does not exist") { return $false }
        }
        throw
    }
}

function Test-RelationshipExists([string]$SchemaName) {
    try {
        $resp = Invoke-RestMethod -Method Get `
            -Uri "$baseUrl/RelationshipDefinitions?`$filter=SchemaName eq '$SchemaName'&`$select=SchemaName" `
            -Headers $headers
        return ($resp.value.Count -gt 0)
    } catch {
        return $false
    }
}

$globalOptionSetIds = @{}
function Get-GlobalOptionSetId([string]$Name) {
    if ($globalOptionSetIds.ContainsKey($Name)) { return $globalOptionSetIds[$Name] }
    $resp = Invoke-RestMethod -Method Get `
        -Uri "$baseUrl/GlobalOptionSetDefinitions(Name='$Name')?`$select=MetadataId" `
        -Headers $headers
    $id = $resp.MetadataId
    $globalOptionSetIds[$Name] = $id
    return $id
}

function New-Label([string]$DE, [string]$EN) {
    return @{
        LocalizedLabels = @(
            @{ "@odata.type" = "Microsoft.Dynamics.CRM.LocalizedLabel"; Label = $DE; LanguageCode = 1031 }
            @{ "@odata.type" = "Microsoft.Dynamics.CRM.LocalizedLabel"; Label = $EN; LanguageCode = 1033 }
        )
    }
}

function New-OptionItem([int]$Value, [string]$DE, [string]$EN) {
    return @{
        Value = $Value
        Label = New-Label $DE $EN
    }
}

function Add-ToSolution([string]$ComponentType, [string]$ComponentId) {
    $body = @{
        ComponentId          = $ComponentId
        ComponentType        = $ComponentType
        SolutionUniqueName   = $solutionName
        AddRequiredComponents = $false
    } | ConvertTo-Json -Depth 5

    try {
        Invoke-RestMethod -Method Post -Uri "$baseUrl/AddSolutionComponent" -Headers $headers -Body $body | Out-Null
    } catch {
        Write-Host "    Warnung: AddSolutionComponent fehlgeschlagen fuer $ComponentType/$ComponentId" -ForegroundColor Yellow
    }
}

function New-Attribute([string]$EntityLogicalName, [hashtable]$AttrDef) {
    $schemaLower = $AttrDef.SchemaName.ToLower()
    if (Test-AttributeExists $EntityLogicalName $schemaLower) {
        Write-Host "  [SKIP] $schemaLower" -ForegroundColor DarkGray
        return
    }

    Write-Host "  [CREATE] $schemaLower..." -ForegroundColor Green

    $body = $AttrDef | ConvertTo-Json -Depth 15
    try {
        Invoke-RestMethod -Method Post `
            -Uri "$baseUrl/EntityDefinitions(LogicalName='$EntityLogicalName')/Attributes" `
            -Headers ($headers + @{ "MSCRM.SolutionUniqueName" = $solutionName }) `
            -Body $body | Out-Null
    } catch {
        # Fehlertext: ErrorDetails.Message (PS 5.1) oder Exception.Message
        $errText = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        if ($errText -match "already exists") {
            Write-Host "  [SKIP] $schemaLower (bereits vorhanden)" -ForegroundColor DarkGray
        } else {
            Write-Host "  [FEHLER] $schemaLower : $errText" -ForegroundColor Red
            throw
        }
    }
}

# ==========================================================================
#  STEP 1: PUBLISHER
# ==========================================================================

Write-Host "-- Schritt 1/10: Publisher ----------------------------------------" -ForegroundColor Cyan

$existingPub = Invoke-RestMethod -Method Get `
    -Uri "$baseUrl/publishers?`$filter=uniquename eq '$pubUnique'&`$select=publisherid" `
    -Headers $headers

if ($existingPub.value.Count -gt 0) {
    $publisherId = $existingPub.value[0].publisherid
    Write-Host "  [SKIP] Publisher '$pubUnique' existiert bereits ($publisherId)" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] Publisher '$pubUnique'..." -ForegroundColor Green

    $pubBody = @{
        uniquename             = $pubUnique
        friendlyname           = "Integration Test Center"
        description            = "Publisher for the Integration Test Center product"
        customizationprefix    = $pubPrefix
        customizationoptionvalueprefix = $optPrefix
    } | ConvertTo-Json -Depth 5

    $resp = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/publishers" `
        -Headers ($headers + @{ "Prefer" = "return=representation" }) `
        -Body $pubBody

    $publisherId = $resp.publisherid
    Write-Host "    Erstellt: $pubUnique ($publisherId)" -ForegroundColor Green
}

# ==========================================================================
#  STEP 2: SOLUTION
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 2/10: Solution -----------------------------------------" -ForegroundColor Cyan

$existingSol = Invoke-RestMethod -Method Get `
    -Uri "$baseUrl/solutions?`$filter=uniquename eq '$solutionName'&`$select=solutionid" `
    -Headers $headers

if ($existingSol.value.Count -gt 0) {
    $solutionId = $existingSol.value[0].solutionid
    Write-Host "  [SKIP] Solution '$solutionName' existiert bereits ($solutionId)" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] Solution '$solutionName'..." -ForegroundColor Green

    $solBody = @{
        uniquename                = $solutionName
        friendlyname              = "Integration Test Center"
        description               = "Generische Integrations-Testplattform fuer Dynamics 365"
        version                   = "1.0.0.0"
        "publisherid@odata.bind"  = "/publishers($publisherId)"
    } | ConvertTo-Json -Depth 5

    $resp = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/solutions" `
        -Headers ($headers + @{ "Prefer" = "return=representation" }) `
        -Body $solBody

    $solutionId = $resp.solutionid
    Write-Host "    Erstellt: $solutionName ($solutionId)" -ForegroundColor Green
}

# ==========================================================================
#  STEP 3: GLOBAL OPTIONSETS
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 3/10: Globale OptionSets --------------------------------" -ForegroundColor Cyan

# OptionSet-Werte: Prefix * 10000 + Index  (10057 * 10000 = 100570000)
# Dataverse berechnet: customizationoptionvalueprefix * 10000 + laufende Nr.
$ovBase = $optPrefix * 10000

$optionSets = @(
    @{
        Name = "itt_teststatus"
        DE   = "Teststatus"
        EN   = "Test Status"
        Desc = "Status of a test run"
        Options = @(
            (New-OptionItem ($ovBase + 0) "Ausstehend" "Pending"),
            (New-OptionItem ($ovBase + 1) "Laeuft" "Running"),
            (New-OptionItem ($ovBase + 2) "Abgeschlossen" "Completed"),
            (New-OptionItem ($ovBase + 3) "Fehler" "Error")
        )
    },
    @{
        Name = "itt_testoutcome"
        DE   = "Testergebnis"
        EN   = "Test Outcome"
        Desc = "Outcome of a single test case execution"
        Options = @(
            (New-OptionItem ($ovBase + 0) "Bestanden" "Passed"),
            (New-OptionItem ($ovBase + 1) "Fehlgeschlagen" "Failed"),
            (New-OptionItem ($ovBase + 2) "Uebersprungen" "Skipped")
        )
    },
    @{
        Name = "itt_testcategory"
        DE   = "Testkategorie"
        EN   = "Test Category"
        Desc = "Category of a test case"
        Options = @(
            (New-OptionItem ($ovBase + 0) "Update Source" "Update Source"),
            (New-OptionItem ($ovBase + 1) "Create Source" "Create Source"),
            (New-OptionItem ($ovBase + 2) "Delete Source" "Delete Source"),
            (New-OptionItem ($ovBase + 3) "Multi-Source" "Multi-Source"),
            (New-OptionItem ($ovBase + 4) "Merge" "Merge"),
            (New-OptionItem ($ovBase + 5) "Custom API" "Custom API"),
            (New-OptionItem ($ovBase + 6) "Config" "Config"),
            (New-OptionItem ($ovBase + 7) "End-to-End" "End-to-End"),
            (New-OptionItem ($ovBase + 8) "Error Handling" "Error Handling")
        )
    },
    @{
        Name = "itt_stepphase"
        DE   = "Schrittphase"
        EN   = "Step Phase"
        Desc = "Phase of a test step"
        Options = @(
            (New-OptionItem ($ovBase + 0) "Precondition" "Precondition"),
            (New-OptionItem ($ovBase + 1) "Step" "Step"),
            (New-OptionItem ($ovBase + 2) "Assertion" "Assertion"),
            (New-OptionItem ($ovBase + 3) "Cleanup" "Cleanup")
        )
    },
    @{
        Name = "itt_stepstatus"
        DE   = "Schrittstatus"
        EN   = "Step Status"
        Desc = "Status of a test step"
        Options = @(
            (New-OptionItem ($ovBase + 0) "Erfolgreich" "Success"),
            (New-OptionItem ($ovBase + 1) "Fehlgeschlagen" "Failed"),
            (New-OptionItem ($ovBase + 2) "Uebersprungen" "Skipped")
        )
    }
)

foreach ($os in $optionSets) {
    if (Test-GlobalOptionSetExists $os.Name) {
        Write-Host "  [SKIP] $($os.Name) existiert bereits" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  [CREATE] $($os.Name)..." -ForegroundColor Green

    $body = @{
        "@odata.type" = "Microsoft.Dynamics.CRM.OptionSetMetadata"
        Name          = $os.Name
        DisplayName   = New-Label $os.DE $os.EN
        Description   = New-Label $os.Desc $os.Desc
        IsGlobal      = $true
        OptionSetType = "Picklist"
        Options       = $os.Options
    } | ConvertTo-Json -Depth 10

    $webResp = $null
    try {
        $resp = Invoke-WebRequest -Method Post `
            -Uri "$baseUrl/GlobalOptionSetDefinitions" `
            -Headers ($headers + @{ "MSCRM.SolutionUniqueName" = $solutionName }) `
            -Body $body `
            -UseBasicParsing
        $webResp = $resp
    } catch {
        throw "Fehler beim Erstellen von $($os.Name): $($_.Exception.Message)"
    }

    # MetadataId aus OData-EntityId Header extrahieren (Dataverse gibt sie dort zurück)
    $osId = $null
    $oDataEntityId = $webResp.Headers["OData-EntityId"]
    if ($oDataEntityId) {
        if ($oDataEntityId -match '\(([a-f0-9-]+)\)') { $osId = $Matches[1] }
    }
    # Fallback: aus Response-Body
    if (-not $osId) {
        $bodyObj = $webResp.Content | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($bodyObj -and $bodyObj.MetadataId) { $osId = $bodyObj.MetadataId }
    }

    Write-Host "    Erstellt: $($os.Name)" -ForegroundColor Green

    if ($osId) {
        Add-ToSolution "9" $osId
    }
}

# ==========================================================================
#  STEP 4: ENTITY itt_testcase
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 4/10: Tabelle itt_testcase ------------------------------" -ForegroundColor Cyan

if (Test-EntityExists "itt_testcase") {
    Write-Host "  [SKIP] itt_testcase existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] itt_testcase..." -ForegroundColor Green

    $tableBody = @{
        "@odata.type"          = "Microsoft.Dynamics.CRM.EntityMetadata"
        SchemaName             = "itt_testcase"
        DisplayName            = New-Label "Testfall" "Test Case"
        DisplayCollectionName  = New-Label "Testfälle" "Test Cases"
        Description            = New-Label "Definition eines Integrationstestfalls" "Definition of an integration test case"
        OwnershipType          = "UserOwned"
        IsActivity             = $false
        HasNotes               = $false
        HasActivities          = $false
        PrimaryNameAttribute   = "itt_name"
        Attributes             = @(
            @{
                "@odata.type"     = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
                SchemaName        = "itt_name"
                DisplayName       = New-Label "Name" "Name"
                Description       = New-Label "Automatisch generierte Testfall-Nr." "Auto-generated test case number"
                IsPrimaryName     = $true
                RequiredLevel     = @{ Value = "None" }
                MaxLength         = 100
                FormatName        = @{ Value = "Text" }
                AutoNumberFormat  = "TC-{SEQNUM:6}"
                AttributeType     = "String"
                AttributeTypeName = @{ Value = "StringType" }
            }
        )
    } | ConvertTo-Json -Depth 15

    Invoke-RestMethod -Method Post `
        -Uri ("$baseUrl/EntityDefinitions?`$select=MetadataId") `
        -Headers ($headers + @{ "MSCRM.SolutionUniqueName" = $solutionName }) `
        -Body $tableBody | Out-Null

    Write-Host "    Erstellt: itt_testcase" -ForegroundColor Green
}

# Attributes on itt_testcase
Write-Host "  Attribute auf itt_testcase:" -ForegroundColor Cyan

New-Attribute "itt_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_TestId"
    DisplayName   = New-Label "Test ID" "Test ID"
    Description   = New-Label "Eindeutige Test-ID (z.B. FG-LUW01)" "Unique test ID (e.g. FG-LUW01)"
    RequiredLevel = @{ Value = "ApplicationRequired" }
    MaxLength     = 50
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_Title"
    DisplayName   = New-Label "Titel" "Title"
    Description   = New-Label "Beschreibender Titel des Testfalls" "Descriptive title of the test case"
    RequiredLevel = @{ Value = "ApplicationRequired" }
    MaxLength     = 500
    FormatName    = @{ Value = "Text" }
}

$catOsId = Get-GlobalOptionSetId "itt_testcategory"
New-Attribute "itt_testcase" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "itt_Category"
    DisplayName                        = New-Label "Kategorie" "Category"
    RequiredLevel                      = @{ Value = "None" }
    "GlobalOptionSet@odata.bind"       = "/GlobalOptionSetDefinitions($catOsId)"
}

New-Attribute "itt_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_Tags"
    DisplayName   = New-Label "Tags" "Tags"
    Description   = New-Label "Kommagetrennte Tags" "Comma-separated tags"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 2000
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_UserStories"
    DisplayName   = New-Label "User Stories" "User Stories"
    Description   = New-Label "Kommagetrennte Story-Keys (z.B. DYN-8621,DYN-8768)" "Comma-separated story keys"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 2000
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.BooleanAttributeMetadata"
    SchemaName    = "itt_Enabled"
    DisplayName   = New-Label "Aktiv" "Enabled"
    Description   = New-Label "Ob der Testfall aktiv ist" "Whether the test case is active"
    RequiredLevel = @{ Value = "None" }
    DefaultValue  = $true
    OptionSet     = @{
        "@odata.type" = "Microsoft.Dynamics.CRM.BooleanOptionSetMetadata"
        TrueOption    = @{ Value = 1; Label = New-Label "Ja" "Yes" }
        FalseOption   = @{ Value = 0; Label = New-Label "Nein" "No" }
    }
}

New-Attribute "itt_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "itt_DefinitionJson"
    DisplayName   = New-Label "Definition (JSON)" "Definition (JSON)"
    Description   = New-Label "JSON-Definition mit Steps und Assertions" "JSON definition with steps and assertions"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

# ==========================================================================
#  STEP 5: ENTITY itt_testrun
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 5/10: Tabelle itt_testrun -------------------------------" -ForegroundColor Cyan

if (Test-EntityExists "itt_testrun") {
    Write-Host "  [SKIP] itt_testrun existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] itt_testrun..." -ForegroundColor Green

    $tableBody = @{
        "@odata.type"          = "Microsoft.Dynamics.CRM.EntityMetadata"
        SchemaName             = "itt_testrun"
        DisplayName            = New-Label "Testlauf" "Test Run"
        DisplayCollectionName  = New-Label "Testläufe" "Test Runs"
        Description            = New-Label "Ergebnis eines Testdurchlaufs" "Result of a test execution run"
        OwnershipType          = "UserOwned"
        IsActivity             = $false
        HasNotes               = $false
        HasActivities          = $false
        PrimaryNameAttribute   = "itt_name"
        Attributes             = @(
            @{
                "@odata.type"     = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
                SchemaName        = "itt_name"
                DisplayName       = New-Label "Name" "Name"
                Description       = New-Label "Automatisch generierte Testlauf-Nr." "Auto-generated test run number"
                IsPrimaryName     = $true
                RequiredLevel     = @{ Value = "None" }
                MaxLength         = 100
                FormatName        = @{ Value = "Text" }
                AutoNumberFormat  = "TR-{SEQNUM:6}"
                AttributeType     = "String"
                AttributeTypeName = @{ Value = "StringType" }
            }
        )
    } | ConvertTo-Json -Depth 15

    Invoke-RestMethod -Method Post `
        -Uri ("$baseUrl/EntityDefinitions?`$select=MetadataId") `
        -Headers ($headers + @{ "MSCRM.SolutionUniqueName" = $solutionName }) `
        -Body $tableBody | Out-Null

    Write-Host "    Erstellt: itt_testrun" -ForegroundColor Green
}

# Attributes on itt_testrun
Write-Host "  Attribute auf itt_testrun:" -ForegroundColor Cyan

$statusOsId = Get-GlobalOptionSetId "itt_teststatus"
New-Attribute "itt_testrun" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "itt_TestStatus"
    DisplayName                        = New-Label "Teststatus" "Test Status"
    RequiredLevel                      = @{ Value = "None" }
    "GlobalOptionSet@odata.bind"       = "/GlobalOptionSetDefinitions($statusOsId)"
}

New-Attribute "itt_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "itt_Passed"
    DisplayName   = New-Label "Bestanden" "Passed"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 100000
    Format        = "None"
}

New-Attribute "itt_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "itt_Failed"
    DisplayName   = New-Label "Fehlgeschlagen" "Failed"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 100000
    Format        = "None"
}

New-Attribute "itt_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "itt_Total"
    DisplayName   = New-Label "Gesamt" "Total"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 100000
    Format        = "None"
}

New-Attribute "itt_testrun" @{
    "@odata.type"    = "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata"
    SchemaName       = "itt_StartedOn"
    DisplayName      = New-Label "Gestartet am" "Started On"
    RequiredLevel    = @{ Value = "None" }
    Format           = "DateAndTime"
    DateTimeBehavior  = @{ Value = "TimeZoneIndependent" }
}

New-Attribute "itt_testrun" @{
    "@odata.type"    = "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata"
    SchemaName       = "itt_CompletedOn"
    DisplayName      = New-Label "Abgeschlossen am" "Completed On"
    RequiredLevel    = @{ Value = "None" }
    Format           = "DateAndTime"
    DateTimeBehavior  = @{ Value = "TimeZoneIndependent" }
}

New-Attribute "itt_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_TestCaseFilter"
    DisplayName   = New-Label "Filter" "Filter"
    Description   = New-Label "Testfall-Filter (z.B. *, tag:LUW, story:DYN-8621)" "Test case filter"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 500
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "itt_TestSummary"
    DisplayName   = New-Label "Zusammenfassung" "Summary"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100000
    Format        = "TextArea"
}

New-Attribute "itt_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "itt_FullLog"
    DisplayName   = New-Label "Vollstaendiges Log" "Full Log"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

New-Attribute "itt_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.BooleanAttributeMetadata"
    SchemaName    = "itt_KeepRecords"
    DisplayName   = New-Label "Testdaten beibehalten" "Keep Test Records"
    Description   = New-Label "Ob Testdaten nach dem Lauf beibehalten werden sollen" "Whether to keep test data after the run"
    RequiredLevel = @{ Value = "None" }
    DefaultValue  = $false
    OptionSet     = @{
        "@odata.type" = "Microsoft.Dynamics.CRM.BooleanOptionSetMetadata"
        TrueOption    = @{ Value = 1; Label = New-Label "Ja" "Yes" }
        FalseOption   = @{ Value = 0; Label = New-Label "Nein" "No" }
    }
}

# ==========================================================================
#  STEP 6: ENTITY itt_testrunresult
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 6/10: Tabelle itt_testrunresult -------------------------" -ForegroundColor Cyan

if (Test-EntityExists "itt_testrunresult") {
    Write-Host "  [SKIP] itt_testrunresult existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] itt_testrunresult..." -ForegroundColor Green

    $tableBody = @{
        "@odata.type"          = "Microsoft.Dynamics.CRM.EntityMetadata"
        SchemaName             = "itt_testrunresult"
        DisplayName            = New-Label "Testlauf-Ergebnis" "Test Run Result"
        DisplayCollectionName  = New-Label "Testlauf-Ergebnisse" "Test Run Results"
        Description            = New-Label "Einzelergebnis eines Testfalls in einem Testlauf" "Individual result of a test case in a test run"
        OwnershipType          = "UserOwned"
        IsActivity             = $false
        HasNotes               = $false
        HasActivities          = $false
        PrimaryNameAttribute   = "itt_name"
        Attributes             = @(
            @{
                "@odata.type"     = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
                SchemaName        = "itt_name"
                DisplayName       = New-Label "Name" "Name"
                Description       = New-Label "Automatisch generierte Ergebnis-Nr." "Auto-generated result number"
                IsPrimaryName     = $true
                RequiredLevel     = @{ Value = "None" }
                MaxLength         = 100
                FormatName        = @{ Value = "Text" }
                AutoNumberFormat  = "RR-{SEQNUM:6}"
                AttributeType     = "String"
                AttributeTypeName = @{ Value = "StringType" }
            }
        )
    } | ConvertTo-Json -Depth 15

    Invoke-RestMethod -Method Post `
        -Uri ("$baseUrl/EntityDefinitions?`$select=MetadataId") `
        -Headers ($headers + @{ "MSCRM.SolutionUniqueName" = $solutionName }) `
        -Body $tableBody | Out-Null

    Write-Host "    Erstellt: itt_testrunresult" -ForegroundColor Green
}

# Attributes on itt_testrunresult
Write-Host "  Attribute auf itt_testrunresult:" -ForegroundColor Cyan

New-Attribute "itt_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_TestId"
    DisplayName   = New-Label "Test ID" "Test ID"
    Description   = New-Label "Test-ID des ausgefuehrten Testfalls" "Test ID of the executed test case"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 50
    FormatName    = @{ Value = "Text" }
}

$outcomeOsId = Get-GlobalOptionSetId "itt_testoutcome"
New-Attribute "itt_testrunresult" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "itt_Outcome"
    DisplayName                        = New-Label "Ergebnis" "Outcome"
    RequiredLevel                      = @{ Value = "None" }
    "GlobalOptionSet@odata.bind"       = "/GlobalOptionSetDefinitions($outcomeOsId)"
}

New-Attribute "itt_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "itt_DurationMs"
    DisplayName   = New-Label "Dauer (ms)" "Duration (ms)"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 2147483647
    Format        = "None"
}

New-Attribute "itt_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "itt_ErrorMessage"
    DisplayName   = New-Label "Fehlermeldung" "Error Message"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100000
    Format        = "TextArea"
}

New-Attribute "itt_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "itt_AssertionResults"
    DisplayName   = New-Label "Assertion-Ergebnisse (JSON)" "Assertion Results (JSON)"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

New-Attribute "itt_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "itt_TrackedRecords"
    DisplayName   = New-Label "Erzeugte Records (JSON)" "Tracked Records (JSON)"
    Description   = New-Label "JSON-Array der von diesem Test erzeugten Dataverse-Records fuer spaeteres Cleanup" "JSON array of Dataverse records created by this test for later cleanup"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

# ==========================================================================
#  STEP 7: ENTITY itt_teststep
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 7/10: Tabelle itt_teststep -----------------------------" -ForegroundColor Cyan

if (Test-EntityExists "itt_teststep") {
    Write-Host "  [SKIP] itt_teststep existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] itt_teststep..." -ForegroundColor Green

    $tableBody = @{
        "@odata.type"          = "Microsoft.Dynamics.CRM.EntityMetadata"
        SchemaName             = "itt_teststep"
        DisplayName            = New-Label "Testschritt" "Test Step"
        DisplayCollectionName  = New-Label "Testschritte" "Test Steps"
        Description            = New-Label "Einzelner Schritt innerhalb eines Testergebnisses" "Individual step within a test run result"
        OwnershipType          = "UserOwned"
        IsActivity             = $false
        HasNotes               = $false
        HasActivities          = $false
        PrimaryNameAttribute   = "itt_name"
        Attributes             = @(
            @{
                "@odata.type"     = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
                SchemaName        = "itt_name"
                DisplayName       = New-Label "Name" "Name"
                Description       = New-Label "Automatisch generierte Testschritt-Nr." "Auto-generated test step number"
                IsPrimaryName     = $true
                RequiredLevel     = @{ Value = "None" }
                MaxLength         = 100
                FormatName        = @{ Value = "Text" }
                AutoNumberFormat  = "TS-{SEQNUM:8}"
                AttributeType     = "String"
                AttributeTypeName = @{ Value = "StringType" }
            }
        )
    } | ConvertTo-Json -Depth 15

    Invoke-RestMethod -Method Post `
        -Uri ("$baseUrl/EntityDefinitions?`$select=MetadataId") `
        -Headers ($headers + @{ "MSCRM.SolutionUniqueName" = $solutionName }) `
        -Body $tableBody | Out-Null

    Write-Host "    Erstellt: itt_teststep" -ForegroundColor Green
}

# Attributes on itt_teststep
Write-Host "  Attribute auf itt_teststep:" -ForegroundColor Cyan

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_Action"
    DisplayName   = New-Label "Aktion" "Action"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_Entity"
    DisplayName   = New-Label "Tabelle" "Entity"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 200
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_Alias"
    DisplayName   = New-Label "Alias" "Alias"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_RecordId"
    DisplayName   = New-Label "Record ID" "Record ID"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_RecordUrl"
    DisplayName   = New-Label "Record URL" "Record URL"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 200
    FormatName    = @{ Value = "Url" }
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_AssertionField"
    DisplayName   = New-Label "Assertionsfeld" "Assertion Field"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 200
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_AssertionOperator"
    DisplayName   = New-Label "Operator" "Operator"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 50
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_ExpectedValue"
    DisplayName   = New-Label "Erwarteter Wert" "Expected Value"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 2000
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "itt_ActualValue"
    DisplayName   = New-Label "Tatsaechlicher Wert" "Actual Value"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 2000
    FormatName    = @{ Value = "Text" }
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "itt_StepNumber"
    DisplayName   = New-Label "Schrittnummer" "Step Number"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 2147483647
    Format        = "None"
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "itt_DurationMs"
    DisplayName   = New-Label "Dauer (ms)" "Duration (ms)"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 2147483647
    Format        = "None"
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "itt_InputData"
    DisplayName   = New-Label "Eingabedaten" "Input Data"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "itt_OutputData"
    DisplayName   = New-Label "Ausgabedaten" "Output Data"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

New-Attribute "itt_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "itt_ErrorMessage"
    DisplayName   = New-Label "Fehlermeldung" "Error Message"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100000
    Format        = "TextArea"
}

$phaseOsId = Get-GlobalOptionSetId "itt_stepphase"
New-Attribute "itt_teststep" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "itt_Phase"
    DisplayName                        = New-Label "Phase" "Phase"
    RequiredLevel                      = @{ Value = "None" }
    "GlobalOptionSet@odata.bind"       = "/GlobalOptionSetDefinitions($phaseOsId)"
}

$stepStatusOsId = Get-GlobalOptionSetId "itt_stepstatus"
New-Attribute "itt_teststep" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "itt_StepStatus"
    DisplayName                        = New-Label "Status" "Status"
    RequiredLevel                      = @{ Value = "None" }
    "GlobalOptionSet@odata.bind"       = "/GlobalOptionSetDefinitions($stepStatusOsId)"
}

# ==========================================================================
#  STEP 8: RELATIONSHIPS
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 8/10: Relationships ------------------------------------" -ForegroundColor Cyan

# --- testrunresult -> testrun ---

$relSchemaName = "itt_testrunresult_testrun"

if (Test-RelationshipExists $relSchemaName) {
    Write-Host "  [SKIP] $relSchemaName existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] $relSchemaName..." -ForegroundColor Green

    $relBody = @{
        "@odata.type"      = "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata"
        SchemaName         = $relSchemaName
        ReferencedEntity   = "itt_testrun"
        ReferencingEntity  = "itt_testrunresult"
        Lookup             = @{
            "@odata.type" = "Microsoft.Dynamics.CRM.LookupAttributeMetadata"
            SchemaName    = "itt_TestRunId"
            DisplayName   = New-Label "Testlauf" "Test Run"
            RequiredLevel = @{ Value = "None" }
        }
        CascadeConfiguration = @{
            Assign   = "NoCascade"
            Delete   = "RemoveLink"
            Merge    = "NoCascade"
            Reparent = "NoCascade"
            Share    = "NoCascade"
            Unshare  = "NoCascade"
        }
    } | ConvertTo-Json -Depth 15

    Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/RelationshipDefinitions" `
        -Headers ($headers + @{ "MSCRM.SolutionUniqueName" = $solutionName }) `
        -Body $relBody | Out-Null

    Write-Host "    Erstellt: $relSchemaName" -ForegroundColor Green
}

# --- teststep -> testrunresult ---
$relSchemaName2 = "itt_teststep_testrunresult"

if (Test-RelationshipExists $relSchemaName2) {
    Write-Host "  [SKIP] $relSchemaName2 existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] $relSchemaName2..." -ForegroundColor Green

    $relBody2 = @{
        "@odata.type"      = "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata"
        SchemaName         = $relSchemaName2
        ReferencedEntity   = "itt_testrunresult"
        ReferencingEntity  = "itt_teststep"
        Lookup             = @{
            "@odata.type" = "Microsoft.Dynamics.CRM.LookupAttributeMetadata"
            SchemaName    = "itt_TestRunResultId"
            DisplayName   = New-Label "Testergebnis" "Test Run Result"
            RequiredLevel = @{ Value = "None" }
        }
        CascadeConfiguration = @{
            Assign   = "NoCascade"
            Delete   = "Cascade"
            Merge    = "NoCascade"
            Reparent = "NoCascade"
            Share    = "NoCascade"
            Unshare  = "NoCascade"
        }
    } | ConvertTo-Json -Depth 15

    Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/RelationshipDefinitions" `
        -Headers ($headers + @{ "MSCRM.SolutionUniqueName" = $solutionName }) `
        -Body $relBody2 | Out-Null

    Write-Host "    Erstellt: $relSchemaName2" -ForegroundColor Green
}

# ==========================================================================
#  STEP 9: WEB RESOURCES
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 9/10: Web Resources ------------------------------------" -ForegroundColor Cyan

$webresourceDir = Join-Path (Split-Path $scriptDir -Parent) "webresource"

$webResources = @(
    @{ Name = "itt_/testcenter.html";              File = "itt_testcenter.html";         Type = 1  } # 1 = HTML
    @{ Name = "itt_/packs/manifest.json";          File = "packs/manifest.json";         Type = 3  } # 3 = Script (JSON)
    @{ Name = "itt_/packs/standard.json";          File = "packs/standard.json";         Type = 3  }
    @{ Name = "itt_/packs/field-governance.json";   File = "packs/field-governance.json"; Type = 3  }
    @{ Name = "itt_/packs/membership.json";         File = "packs/membership.json";       Type = 3  }
    @{ Name = "itt_/packs/markant-base.json";       File = "packs/markant-base.json";     Type = 3  }
    @{ Name = "itt_/packs/fg-testtool.json";        File = "packs/fg-testtool.json";      Type = 3  }
    @{ Name = "itt_/packs/fg-testtool-v2.json";     File = "packs/fg-testtool-v2.json";   Type = 3  }
    @{ Name = "itt_/packs/fg-testtool-legacy.json"; File = "packs/fg-testtool-legacy.json"; Type = 3  }
    @{ Name = "itt_/packs/snapshot-recompute.json"; File = "packs/snapshot-recompute.json"; Type = 3  }
    @{ Name = "itt_/packs/empty.json";              File = "packs/empty.json";            Type = 3  }
)

foreach ($wr in $webResources) {
    $filePath = Join-Path $webresourceDir $wr.File

    if (-not (Test-Path $filePath)) {
        Write-Host "  [WARN] Datei nicht gefunden: $filePath" -ForegroundColor Yellow
        continue
    }

    # Check if web resource exists
    $existing = Invoke-RestMethod -Method Get `
        -Uri "$baseUrl/webresourceset?`$filter=name eq '$($wr.Name)'&`$select=webresourceid" `
        -Headers $headers

    $fileBytes   = [System.IO.File]::ReadAllBytes($filePath)
    $fileBase64  = [Convert]::ToBase64String($fileBytes)

    if ($existing.value.Count -gt 0) {
        $wrId = $existing.value[0].webresourceid
        Write-Host "  [UPDATE] $($wr.Name)..." -ForegroundColor Yellow

        $wrBody = @{
            content = $fileBase64
        } | ConvertTo-Json

        Invoke-RestMethod -Method Patch `
            -Uri "$baseUrl/webresourceset($wrId)" `
            -Headers ($headers + @{ "If-Match" = "*" }) `
            -Body $wrBody | Out-Null
    } else {
        Write-Host "  [CREATE] $($wr.Name)..." -ForegroundColor Green

        $wrBody = @{
            name                 = $wr.Name
            displayname          = $wr.Name
            webresourcetype      = $wr.Type
            content              = $fileBase64
        } | ConvertTo-Json

        $resp = Invoke-RestMethod -Method Post `
            -Uri "$baseUrl/webresourceset" `
            -Headers ($headers + @{ "MSCRM.SolutionUniqueName" = $solutionName; "Prefer" = "return=representation" }) `
            -Body $wrBody

        $wrId = $resp.webresourceid
        Write-Host "    Erstellt: $($wr.Name) ($wrId)" -ForegroundColor Green
    }
}

# ==========================================================================
#  STEP 10: PUBLISH
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 10/10: PublishAllXml -----------------------------------" -ForegroundColor Cyan

Invoke-RestMethod -Method Post `
    -Uri "$baseUrl/PublishAllXml" `
    -Headers $headers | Out-Null

Write-Host "  Alle Anpassungen publiziert" -ForegroundColor Green

# ==========================================================================
#  DONE
# ==========================================================================

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "  Deployment abgeschlossen!" -ForegroundColor Green
Write-Host "" -ForegroundColor Green
Write-Host "  Solution: $solutionName" -ForegroundColor Green
Write-Host "  Publisher: $pubUnique (prefix: $pubPrefix)" -ForegroundColor Green
Write-Host "  Entities: itt_testcase, itt_testrun, itt_testrunresult, itt_teststep" -ForegroundColor Green
Write-Host "  OptionSets: itt_teststatus, itt_testoutcome, itt_testcategory, itt_stepphase, itt_stepstatus" -ForegroundColor Green
Write-Host "  Web Resources: $($webResources.Count) Dateien" -ForegroundColor Green
Write-Host "" -ForegroundColor Green
Write-Host "  URL: $($config.resource)WebResources/itt_/testcenter.html" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Green

} finally {
    Stop-Transcript
}
