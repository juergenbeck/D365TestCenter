<#
.SYNOPSIS
    Deploys the Integration Test Center (ITT) solution to Dataverse DEV.

.DESCRIPTION
    Creates:
    - Publisher "JBE" (prefix: itt, OptionValuePrefix: 100571)
    - Solution "D365TestCenter"
    - 5 global OptionSets (jbe_teststatus, jbe_testoutcome, jbe_testcategory, jbe_stepphase, jbe_stepstatus)
    - 4 custom tables (jbe_testcase, jbe_testrun, jbe_testrunresult, jbe_teststep)
    - All attributes on the 4 tables
    - N:1 relationships (testrunresult -> testrun, teststep -> testrunresult)
    - Web Resources (HTML + JSON packs)
    - Publishes all customizations

    Idempotent: checks existence before creating each component.

.PARAMETER Headers
    Hashtable with Authorization header. Example:
    @{ "Authorization" = "Bearer eyJ0..." }

.NOTES
    Requires: PowerShell 5.1+ (Windows)
    Pass authentication headers via -Headers parameter.
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
# Pass headers as parameter or set them before running this script.
# Example: $headers = @{ "Authorization" = "Bearer $token" }
if (-not $headers) {
    Write-Error "No authentication headers provided. Set `$headers before running this script."
    exit 1
}

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

# Detect available languages on the org
$script:orgLanguages = @(1031)  # Default: DE only
try {
    $orgInfo = Invoke-RestMethod -Uri "$baseUrl/organizations?`$select=languagecode" -Headers $headers
    $baseLang = $orgInfo.value[0].languagecode
    $script:orgLanguages = @($baseLang)
    # Try to add 1033 (EN) if base is not EN
    if ($baseLang -ne 1033) {
        try {
            # Test if EN is available by checking RetrieveAvailableLanguages
            $availLangs = Invoke-RestMethod -Uri "$baseUrl/RetrieveAvailableLanguages" -Headers $headers -Method Get
            if ($availLangs.LocaleIds -contains 1033) { $script:orgLanguages += 1033 }
        } catch {}
    }
    if ($baseLang -ne 1031 -and $script:orgLanguages -notcontains 1031) {
        try {
            $availLangs = Invoke-RestMethod -Uri "$baseUrl/RetrieveAvailableLanguages" -Headers $headers -Method Get
            if ($availLangs.LocaleIds -contains 1031) { $script:orgLanguages += 1031 }
        } catch {}
    }
} catch {}
Write-Host "  Languages: $($script:orgLanguages -join ', ')" -ForegroundColor Gray

function New-Label([string]$DE, [string]$EN) {
    $labels = @()
    if ($script:orgLanguages -contains 1031) {
        $labels += @{ "@odata.type" = "Microsoft.Dynamics.CRM.LocalizedLabel"; Label = $DE; LanguageCode = 1031 }
    }
    if ($script:orgLanguages -contains 1033) {
        $labels += @{ "@odata.type" = "Microsoft.Dynamics.CRM.LocalizedLabel"; Label = $EN; LanguageCode = 1033 }
    }
    # Fallback: at least one label with base language
    if ($labels.Count -eq 0) {
        $labels += @{ "@odata.type" = "Microsoft.Dynamics.CRM.LocalizedLabel"; Label = $DE; LanguageCode = $script:orgLanguages[0] }
    }
    return @{ LocalizedLabels = $labels }
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
        friendlyname           = "Juergen Beck"
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
        Name = "jbe_teststatus"
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
        Name = "jbe_testoutcome"
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
        Name = "jbe_testcategory"
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
        Name = "jbe_stepphase"
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
        Name = "jbe_stepstatus"
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
        $m = [regex]::Match("$oDataEntityId", '\(([a-f0-9-]+)\)')
        if ($m.Success) { $osId = $m.Groups[1].Value }
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
#  STEP 4: ENTITY jbe_testcase
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 4/10: Tabelle jbe_testcase ------------------------------" -ForegroundColor Cyan

if (Test-EntityExists "jbe_testcase") {
    Write-Host "  [SKIP] jbe_testcase existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] jbe_testcase..." -ForegroundColor Green

    $tableBody = @{
        "@odata.type"          = "Microsoft.Dynamics.CRM.EntityMetadata"
        SchemaName             = "jbe_testcase"
        DisplayName            = New-Label "Testfall" "Test Case"
        DisplayCollectionName  = New-Label "Testfälle" "Test Cases"
        Description            = New-Label "Definition eines Integrationstestfalls" "Definition of an integration test case"
        OwnershipType          = "UserOwned"
        IsActivity             = $false
        HasNotes               = $false
        HasActivities          = $false
        PrimaryNameAttribute   = "jbe_name"
        Attributes             = @(
            @{
                "@odata.type"     = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
                SchemaName        = "jbe_name"
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

    Write-Host "    Erstellt: jbe_testcase" -ForegroundColor Green
    Start-Sleep -Seconds 10  # Dataverse braucht Zeit nach Entity-Create
}

# Attributes on jbe_testcase
Write-Host "  Attribute auf jbe_testcase:" -ForegroundColor Cyan

New-Attribute "jbe_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_TestId"
    DisplayName   = New-Label "Test ID" "Test ID"
    Description   = New-Label "Eindeutige Test-ID (z.B. FG-LUW01)" "Unique test ID (e.g. FG-LUW01)"
    RequiredLevel = @{ Value = "ApplicationRequired" }
    MaxLength     = 50
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_Title"
    DisplayName   = New-Label "Titel" "Title"
    Description   = New-Label "Beschreibender Titel des Testfalls" "Descriptive title of the test case"
    RequiredLevel = @{ Value = "ApplicationRequired" }
    MaxLength     = 500
    FormatName    = @{ Value = "Text" }
}

$catOsId = Get-GlobalOptionSetId "jbe_testcategory"
New-Attribute "jbe_testcase" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "jbe_Category"
    DisplayName                        = New-Label "Kategorie" "Category"
    RequiredLevel                      = @{ Value = "None" }
    "GlobalOptionSet@odata.bind"       = "/GlobalOptionSetDefinitions($catOsId)"
}

New-Attribute "jbe_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_Tags"
    DisplayName   = New-Label "Tags" "Tags"
    Description   = New-Label "Kommagetrennte Tags" "Comma-separated tags"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 2000
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_UserStories"
    DisplayName   = New-Label "User Stories" "User Stories"
    Description   = New-Label "Kommagetrennte Story-Keys (z.B. DYN-8621,DYN-8768)" "Comma-separated story keys"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 2000
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.BooleanAttributeMetadata"
    SchemaName    = "jbe_Enabled"
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

New-Attribute "jbe_testcase" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "jbe_DefinitionJson"
    DisplayName   = New-Label "Definition (JSON)" "Definition (JSON)"
    Description   = New-Label "JSON-Definition mit Steps und Assertions" "JSON definition with steps and assertions"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

# ==========================================================================
#  STEP 5: ENTITY jbe_testrun
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 5/10: Tabelle jbe_testrun -------------------------------" -ForegroundColor Cyan

if (Test-EntityExists "jbe_testrun") {
    Write-Host "  [SKIP] jbe_testrun existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] jbe_testrun..." -ForegroundColor Green

    $tableBody = @{
        "@odata.type"          = "Microsoft.Dynamics.CRM.EntityMetadata"
        SchemaName             = "jbe_testrun"
        DisplayName            = New-Label "Testlauf" "Test Run"
        DisplayCollectionName  = New-Label "Testläufe" "Test Runs"
        Description            = New-Label "Ergebnis eines Testdurchlaufs" "Result of a test execution run"
        OwnershipType          = "UserOwned"
        IsActivity             = $false
        HasNotes               = $false
        HasActivities          = $false
        PrimaryNameAttribute   = "jbe_name"
        Attributes             = @(
            @{
                "@odata.type"     = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
                SchemaName        = "jbe_name"
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

    Write-Host "    Erstellt: jbe_testrun" -ForegroundColor Green
    Start-Sleep -Seconds 10
}

# Attributes on jbe_testrun
Write-Host "  Attribute auf jbe_testrun:" -ForegroundColor Cyan

$statusOsId = Get-GlobalOptionSetId "jbe_teststatus"
New-Attribute "jbe_testrun" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "jbe_TestStatus"
    DisplayName                        = New-Label "Teststatus" "Test Status"
    RequiredLevel                      = @{ Value = "None" }
    "GlobalOptionSet@odata.bind"       = "/GlobalOptionSetDefinitions($statusOsId)"
}

New-Attribute "jbe_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "jbe_Passed"
    DisplayName   = New-Label "Bestanden" "Passed"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 100000
    Format        = "None"
}

New-Attribute "jbe_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "jbe_Failed"
    DisplayName   = New-Label "Fehlgeschlagen" "Failed"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 100000
    Format        = "None"
}

New-Attribute "jbe_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "jbe_Total"
    DisplayName   = New-Label "Gesamt" "Total"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 100000
    Format        = "None"
}

New-Attribute "jbe_testrun" @{
    "@odata.type"    = "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata"
    SchemaName       = "jbe_StartedOn"
    DisplayName      = New-Label "Gestartet am" "Started On"
    RequiredLevel    = @{ Value = "None" }
    Format           = "DateAndTime"
    DateTimeBehavior  = @{ Value = "TimeZoneIndependent" }
}

New-Attribute "jbe_testrun" @{
    "@odata.type"    = "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata"
    SchemaName       = "jbe_CompletedOn"
    DisplayName      = New-Label "Abgeschlossen am" "Completed On"
    RequiredLevel    = @{ Value = "None" }
    Format           = "DateAndTime"
    DateTimeBehavior  = @{ Value = "TimeZoneIndependent" }
}

New-Attribute "jbe_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_TestCaseFilter"
    DisplayName   = New-Label "Filter" "Filter"
    Description   = New-Label "Testfall-Filter (z.B. *, tag:LUW, story:DYN-8621)" "Test case filter"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 500
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "jbe_TestSummary"
    DisplayName   = New-Label "Zusammenfassung" "Summary"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100000
    Format        = "TextArea"
}

New-Attribute "jbe_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "jbe_FullLog"
    DisplayName   = New-Label "Vollstaendiges Log" "Full Log"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

New-Attribute "jbe_testrun" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.BooleanAttributeMetadata"
    SchemaName    = "jbe_KeepRecords"
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
#  STEP 6: ENTITY jbe_testrunresult
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 6/10: Tabelle jbe_testrunresult -------------------------" -ForegroundColor Cyan

if (Test-EntityExists "jbe_testrunresult") {
    Write-Host "  [SKIP] jbe_testrunresult existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] jbe_testrunresult..." -ForegroundColor Green

    $tableBody = @{
        "@odata.type"          = "Microsoft.Dynamics.CRM.EntityMetadata"
        SchemaName             = "jbe_testrunresult"
        DisplayName            = New-Label "Testlauf-Ergebnis" "Test Run Result"
        DisplayCollectionName  = New-Label "Testlauf-Ergebnisse" "Test Run Results"
        Description            = New-Label "Einzelergebnis eines Testfalls in einem Testlauf" "Individual result of a test case in a test run"
        OwnershipType          = "UserOwned"
        IsActivity             = $false
        HasNotes               = $false
        HasActivities          = $false
        PrimaryNameAttribute   = "jbe_name"
        Attributes             = @(
            @{
                "@odata.type"     = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
                SchemaName        = "jbe_name"
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

    Write-Host "    Erstellt: jbe_testrunresult" -ForegroundColor Green
    Start-Sleep -Seconds 10
}

# Attributes on jbe_testrunresult
Write-Host "  Attribute auf jbe_testrunresult:" -ForegroundColor Cyan

New-Attribute "jbe_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_TestId"
    DisplayName   = New-Label "Test ID" "Test ID"
    Description   = New-Label "Test-ID des ausgefuehrten Testfalls" "Test ID of the executed test case"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 50
    FormatName    = @{ Value = "Text" }
}

$outcomeOsId = Get-GlobalOptionSetId "jbe_testoutcome"
New-Attribute "jbe_testrunresult" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "jbe_Outcome"
    DisplayName                        = New-Label "Ergebnis" "Outcome"
    RequiredLevel                      = @{ Value = "None" }
    "GlobalOptionSet@odata.bind"       = "/GlobalOptionSetDefinitions($outcomeOsId)"
}

New-Attribute "jbe_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "jbe_DurationMs"
    DisplayName   = New-Label "Dauer (ms)" "Duration (ms)"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 2147483647
    Format        = "None"
}

New-Attribute "jbe_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "jbe_ErrorMessage"
    DisplayName   = New-Label "Fehlermeldung" "Error Message"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100000
    Format        = "TextArea"
}

New-Attribute "jbe_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "jbe_AssertionResults"
    DisplayName   = New-Label "Assertion-Ergebnisse (JSON)" "Assertion Results (JSON)"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

New-Attribute "jbe_testrunresult" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "jbe_TrackedRecords"
    DisplayName   = New-Label "Erzeugte Records (JSON)" "Tracked Records (JSON)"
    Description   = New-Label "JSON-Array der von diesem Test erzeugten Dataverse-Records fuer spaeteres Cleanup" "JSON array of Dataverse records created by this test for later cleanup"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

# ==========================================================================
#  STEP 7: ENTITY jbe_teststep
# ==========================================================================

Write-Host ""
Write-Host "-- Schritt 7/10: Tabelle jbe_teststep -----------------------------" -ForegroundColor Cyan

if (Test-EntityExists "jbe_teststep") {
    Write-Host "  [SKIP] jbe_teststep existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] jbe_teststep..." -ForegroundColor Green

    $tableBody = @{
        "@odata.type"          = "Microsoft.Dynamics.CRM.EntityMetadata"
        SchemaName             = "jbe_teststep"
        DisplayName            = New-Label "Testschritt" "Test Step"
        DisplayCollectionName  = New-Label "Testschritte" "Test Steps"
        Description            = New-Label "Einzelner Schritt innerhalb eines Testergebnisses" "Individual step within a test run result"
        OwnershipType          = "UserOwned"
        IsActivity             = $false
        HasNotes               = $false
        HasActivities          = $false
        PrimaryNameAttribute   = "jbe_name"
        Attributes             = @(
            @{
                "@odata.type"     = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
                SchemaName        = "jbe_name"
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

    Write-Host "    Erstellt: jbe_teststep" -ForegroundColor Green
    Start-Sleep -Seconds 10
}

# Attributes on jbe_teststep
Write-Host "  Attribute auf jbe_teststep:" -ForegroundColor Cyan

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_Action"
    DisplayName   = New-Label "Aktion" "Action"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_Entity"
    DisplayName   = New-Label "Tabelle" "Entity"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 200
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_Alias"
    DisplayName   = New-Label "Alias" "Alias"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_RecordId"
    DisplayName   = New-Label "Record ID" "Record ID"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_RecordUrl"
    DisplayName   = New-Label "Record URL" "Record URL"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 200
    FormatName    = @{ Value = "Url" }
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_AssertionField"
    DisplayName   = New-Label "Assertionsfeld" "Assertion Field"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 200
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_AssertionOperator"
    DisplayName   = New-Label "Operator" "Operator"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 50
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_ExpectedValue"
    DisplayName   = New-Label "Erwarteter Wert" "Expected Value"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 2000
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
    SchemaName    = "jbe_ActualValue"
    DisplayName   = New-Label "Tatsaechlicher Wert" "Actual Value"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 2000
    FormatName    = @{ Value = "Text" }
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "jbe_StepNumber"
    DisplayName   = New-Label "Schrittnummer" "Step Number"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 2147483647
    Format        = "None"
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
    SchemaName    = "jbe_DurationMs"
    DisplayName   = New-Label "Dauer (ms)" "Duration (ms)"
    RequiredLevel = @{ Value = "None" }
    MinValue      = 0
    MaxValue      = 2147483647
    Format        = "None"
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "jbe_InputData"
    DisplayName   = New-Label "Eingabedaten" "Input Data"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "jbe_OutputData"
    DisplayName   = New-Label "Ausgabedaten" "Output Data"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 1048576
    Format        = "TextArea"
}

New-Attribute "jbe_teststep" @{
    "@odata.type" = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
    SchemaName    = "jbe_ErrorMessage"
    DisplayName   = New-Label "Fehlermeldung" "Error Message"
    RequiredLevel = @{ Value = "None" }
    MaxLength     = 100000
    Format        = "TextArea"
}

$phaseOsId = Get-GlobalOptionSetId "jbe_stepphase"
New-Attribute "jbe_teststep" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "jbe_Phase"
    DisplayName                        = New-Label "Phase" "Phase"
    RequiredLevel                      = @{ Value = "None" }
    "GlobalOptionSet@odata.bind"       = "/GlobalOptionSetDefinitions($phaseOsId)"
}

$stepStatusOsId = Get-GlobalOptionSetId "jbe_stepstatus"
New-Attribute "jbe_teststep" @{
    "@odata.type"                      = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
    SchemaName                         = "jbe_StepStatus"
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

$relSchemaName = "jbe_testrunresult_testrun"

if (Test-RelationshipExists $relSchemaName) {
    Write-Host "  [SKIP] $relSchemaName existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] $relSchemaName..." -ForegroundColor Green

    $relBody = @{
        "@odata.type"      = "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata"
        SchemaName         = $relSchemaName
        ReferencedEntity   = "jbe_testrun"
        ReferencingEntity  = "jbe_testrunresult"
        Lookup             = @{
            "@odata.type" = "Microsoft.Dynamics.CRM.LookupAttributeMetadata"
            SchemaName    = "jbe_TestRunId"
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
$relSchemaName2 = "jbe_teststep_testrunresult"

if (Test-RelationshipExists $relSchemaName2) {
    Write-Host "  [SKIP] $relSchemaName2 existiert bereits" -ForegroundColor DarkGray
} else {
    Write-Host "  [CREATE] $relSchemaName2..." -ForegroundColor Green

    $relBody2 = @{
        "@odata.type"      = "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata"
        SchemaName         = $relSchemaName2
        ReferencedEntity   = "jbe_testrunresult"
        ReferencingEntity  = "jbe_teststep"
        Lookup             = @{
            "@odata.type" = "Microsoft.Dynamics.CRM.LookupAttributeMetadata"
            SchemaName    = "jbe_TestRunResultId"
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
    @{ Name = "jbe_/testcenter.html";              File = "d365testcenter.html";         Type = 1  } # 1 = HTML
    @{ Name = "jbe_/packs/manifest.json";          File = "packs/manifest.json";         Type = 3  } # 3 = Script (JSON)
    @{ Name = "jbe_/packs/standard.json";          File = "packs/standard.json";         Type = 3  }
    @{ Name = "jbe_/packs/demo-standard.json";     File = "packs/demo-standard.json";    Type = 3  }
    @{ Name = "jbe_/packs/empty.json";              File = "packs/empty.json";            Type = 3  }
    # Add custom packs here:
    # @{ Name = "jbe_/packs/custom-pack.json";     File = "packs/custom-pack.json";    Type = 3  }
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
Write-Host "  Entities: jbe_testcase, jbe_testrun, jbe_testrunresult, jbe_teststep" -ForegroundColor Green
Write-Host "  OptionSets: jbe_teststatus, jbe_testoutcome, jbe_testcategory, jbe_stepphase, jbe_stepstatus" -ForegroundColor Green
Write-Host "  Web Resources: $($webResources.Count) Dateien" -ForegroundColor Green
Write-Host "" -ForegroundColor Green
Write-Host "  URL: $($config.resource)WebResources/jbe_/testcenter.html" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Green

} finally {
    Stop-Transcript
}
