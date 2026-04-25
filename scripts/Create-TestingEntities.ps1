#requires -Version 7.0
<#
.SYNOPSIS
    Erstellt die Dataverse-Entities für das Integration Test Center.
    Solution: jbe_testing (eigene Solution, getrennt von Produktionscode)

.DESCRIPTION
    Entities:
    - jbe_testcase: Testfall-Speicherung (JSON-Definition, Metadaten)
    - jbe_testrun: Testlauf-Steuerung (Status, Filter, Ergebnis)
    - jbe_testrunresult: Einzelergebnis pro Testfall (N:1 auf testrun + testcase)

.PARAMETER Environment
    Zielumgebung: DEV, TEST (Standard: DEV)

.PARAMETER WhatIf
    Zeigt an, was erstellt würde, ohne tatsächlich Änderungen vorzunehmen.

.EXAMPLE
    pwsh .\Create-TestingEntities.ps1 -Environment DEV
    pwsh .\Create-TestingEntities.ps1 -Environment DEV -WhatIf
#>

param(
    [ValidateSet("DEV", "TEST")]
    [string]$Environment = "DEV",
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

# Auth: $headers and $baseUrl must be set before running this script
if (-not $headers) {
    Write-Error "No authentication headers provided. Set `$headers before running this script."
    return
}

$config = Get-Content (Join-Path $PSScriptRoot "deploy-config.json") -Raw | ConvertFrom-Json
$baseUrl = $config.resource.TrimEnd("/")
$apiUrl = "$baseUrl/api/data/v9.2"

Write-Host "=== Testing Entities erstellen ===" -ForegroundColor Cyan
Write-Host "Umgebung: $Environment ($baseUrl)"
if ($WhatIf) { Write-Host "[WhatIf-Modus: keine Änderungen]" -ForegroundColor Yellow }

# ═══════════════════════════════════════════════════════════════════
#  Globale OptionSets
# ═══════════════════════════════════════════════════════════════════

$optionSets = @(
    @{
        Name = "jbe_teststatus"
        DisplayName = "Test Status"
        Options = @(
            @{ Value = 595300000; Label = "Geplant" }
            @{ Value = 595300001; Label = "Läuft" }
            @{ Value = 595300002; Label = "Abgeschlossen" }
            @{ Value = 595300003; Label = "Fehler" }
        )
    },
    @{
        Name = "jbe_testoutcome"
        DisplayName = "Test Outcome"
        Options = @(
            @{ Value = 595300000; Label = "Passed" }
            @{ Value = 595300001; Label = "Failed" }
            @{ Value = 595300002; Label = "Error" }
            @{ Value = 595300003; Label = "Skipped" }
            @{ Value = 595300004; Label = "NotImplemented" }
        )
    },
    @{
        Name = "jbe_testcategory"
        DisplayName = "Test Category"
        Options = @(
            @{ Value = 595300000; Label = "Update Source Record" }
            @{ Value = 595300001; Label = "Create Source Record" }
            @{ Value = 595300002; Label = "PISA IF Changedate" }
            @{ Value = 595300003; Label = "Multi-Source Multi-Field" }
            @{ Value = 595300004; Label = "Additional Fields" }
            @{ Value = 595300005; Label = "Bridge E2E" }
            @{ Value = 595300006; Label = "Merge" }
            @{ Value = 595300007; Label = "Recompute" }
            @{ Value = 595300008; Label = "Error Injection" }
        )
    },
    @{
        Name = "jbe_testenvironment"
        DisplayName = "Test Environment"
        Options = @(
            @{ Value = 595300000; Label = "DEV" }
            @{ Value = 595300001; Label = "TEST" }
            @{ Value = 595300002; Label = "DATATEST" }
        )
    }
)

# ═══════════════════════════════════════════════════════════════════
#  Entity-Definitionen
# ═══════════════════════════════════════════════════════════════════

$entities = @(
    @{
        SchemaName = "jbe_testcase"
        DisplayName = "Test Case"
        DisplayCollectionName = "Test Cases"
        Description = "Testfall-Definition für das Integration Test Center"
        PrimaryField = @{ SchemaName = "jbe_name"; DisplayName = "Name"; AutoNumber = "TC-{SEQNUM:5}" }
        Attributes = @(
            @{ SchemaName = "jbe_testid"; Type = "String"; MaxLength = 20; DisplayName = "Test ID"; Description = "TC01, TCC05, BTC12" }
            @{ SchemaName = "jbe_title"; Type = "String"; MaxLength = 200; DisplayName = "Title"; Description = "Human-readable test title" }
            @{ SchemaName = "jbe_category"; Type = "Picklist"; OptionSet = "jbe_testcategory"; DisplayName = "Category" }
            @{ SchemaName = "jbe_tags"; Type = "String"; MaxLength = 500; DisplayName = "Tags"; Description = "Comma-separated tags" }
            @{ SchemaName = "jbe_enabled"; Type = "Boolean"; DisplayName = "Enabled"; DefaultValue = $true }
            @{ SchemaName = "jbe_definitionjson"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Definition (JSON)"; Description = "Full test case JSON (preconditions + steps + assertions)" }
        )
        AlternateKeys = @(
            @{ Name = "jbe_testid_key"; Attributes = @("jbe_testid") }
        )
    },
    @{
        SchemaName = "jbe_testrun"
        DisplayName = "Test Run"
        DisplayCollectionName = "Test Runs"
        Description = "Testlauf-Steuerung und Ergebnisse"
        PrimaryField = @{ SchemaName = "jbe_name"; DisplayName = "Name"; AutoNumber = "TR-{SEQNUM:5}" }
        Attributes = @(
            @{ SchemaName = "jbe_teststatus"; Type = "Picklist"; OptionSet = "jbe_teststatus"; DisplayName = "Status" }
            @{ SchemaName = "jbe_testcasefilter"; Type = "String"; MaxLength = 500; DisplayName = "Test Case Filter"; Description = "*, TC01,TC02, tag:LUW, category:Bridge" }
            @{ SchemaName = "jbe_environment"; Type = "Picklist"; OptionSet = "jbe_testenvironment"; DisplayName = "Environment" }
            @{ SchemaName = "jbe_startedon"; Type = "DateTime"; DisplayName = "Started On" }
            @{ SchemaName = "jbe_completedon"; Type = "DateTime"; DisplayName = "Completed On" }
            @{ SchemaName = "jbe_total"; Type = "Integer"; DisplayName = "Total"; Min = 0; Max = 10000 }
            @{ SchemaName = "jbe_passed"; Type = "Integer"; DisplayName = "Passed"; Min = 0; Max = 10000 }
            @{ SchemaName = "jbe_failed"; Type = "Integer"; DisplayName = "Failed"; Min = 0; Max = 10000 }
            @{ SchemaName = "jbe_testsummary"; Type = "Memo"; MaxLength = 100000; DisplayName = "Summary" }
            @{ SchemaName = "jbe_testresult_json"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Result (JSON)" }
            @{ SchemaName = "jbe_fulllog"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Full Log" }
            @{ SchemaName = "jbe_testconfig_json"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Test Config (JSON)"; Description = "Optional inline test JSON (backwards compatibility)" }
        )
    },
    @{
        SchemaName = "jbe_testrunresult"
        DisplayName = "Test Run Result"
        DisplayCollectionName = "Test Run Results"
        Description = "Einzelergebnis pro Testfall in einem Testlauf"
        PrimaryField = @{ SchemaName = "jbe_name"; DisplayName = "Name"; AutoNumber = "RE-{SEQNUM:6}" }
        Attributes = @(
            @{ SchemaName = "jbe_testrunid"; Type = "Lookup"; Target = "jbe_testrun"; DisplayName = "Test Run" }
            @{ SchemaName = "jbe_testcaseid"; Type = "Lookup"; Target = "jbe_testcase"; DisplayName = "Test Case" }
            @{ SchemaName = "jbe_testid"; Type = "String"; MaxLength = 20; DisplayName = "Test ID"; Description = "Denormalized for subgrid display" }
            @{ SchemaName = "jbe_outcome"; Type = "Picklist"; OptionSet = "jbe_testoutcome"; DisplayName = "Outcome" }
            @{ SchemaName = "jbe_durationms"; Type = "Integer"; DisplayName = "Duration (ms)"; Min = 0; Max = 600000 }
            @{ SchemaName = "jbe_errormessage"; Type = "Memo"; MaxLength = 100000; DisplayName = "Error Message" }
            @{ SchemaName = "jbe_assertionresults"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Assertion Results (JSON)" }
        )
    }
)

# ═══════════════════════════════════════════════════════════════════
#  Ausgabe
# ═══════════════════════════════════════════════════════════════════

Write-Host ""
Write-Host "Globale OptionSets:" -ForegroundColor Green
foreach ($os in $optionSets) {
    Write-Host "  $($os.Name) ($($os.DisplayName)): $($os.Options.Count) Werte"
    foreach ($opt in $os.Options) {
        Write-Host "    $($opt.Value) = $($opt.Label)"
    }
}

Write-Host ""
Write-Host "Entities:" -ForegroundColor Green
foreach ($entity in $entities) {
    Write-Host "  $($entity.SchemaName) ($($entity.DisplayName))"
    Write-Host "    Primary: $($entity.PrimaryField.SchemaName) (AutoNumber: $($entity.PrimaryField.AutoNumber))"
    Write-Host "    Attribute: $($entity.Attributes.Count)"
    foreach ($attr in $entity.Attributes) {
        $typeInfo = switch ($attr.Type) {
            "String"  { "String($($attr.MaxLength))" }
            "Memo"    { "Memo($($attr.MaxLength))" }
            "Integer" { "Integer($($attr.Min)..$($attr.Max))" }
            "Picklist" { "Picklist($($attr.OptionSet))" }
            "Boolean" { "Boolean(default=$($attr.DefaultValue))" }
            "DateTime" { "DateTime" }
            "Lookup" { "Lookup($($attr.Target))" }
            default { $attr.Type }
        }
        Write-Host "    - $($attr.SchemaName): $typeInfo"
    }
    if ($entity.AlternateKeys) {
        foreach ($ak in $entity.AlternateKeys) {
            Write-Host "    AlternateKey: $($ak.Name) [$($ak.Attributes -join ', ')]"
        }
    }
}

if ($WhatIf) {
    Write-Host ""
    Write-Host "[WhatIf] Keine Änderungen vorgenommen. Entferne -WhatIf um die Entities zu erstellen." -ForegroundColor Yellow
    return
}

Write-Host ""
Write-Host "HINWEIS: Die tatsächliche Erstellung der Entities erfolgt über das Maker Portal" -ForegroundColor Yellow
Write-Host "oder per pac CLI (pac solution import). Dieses Skript dokumentiert die Spezifikation." -ForegroundColor Yellow
Write-Host ""
Write-Host "Für die Erstellung per Web API:" -ForegroundColor Cyan
Write-Host "  1. Globale OptionSets anlegen (POST GlobalOptionSetDefinitions)"
Write-Host "  2. Entities anlegen (POST EntityDefinitions)"
Write-Host "  3. Attribute anlegen (POST EntityDefinitions(.../Attributes))"
Write-Host "  4. Alternate Keys anlegen (POST EntityDefinitions(.../Keys))"
Write-Host "  5. Solution jbe_testing erstellen und alle Komponenten hinzufügen"
