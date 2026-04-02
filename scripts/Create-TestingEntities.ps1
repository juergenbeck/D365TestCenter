#requires -Version 7.0
<#
.SYNOPSIS
    Erstellt die Dataverse-Entities für das Integration Test Center.
    Solution: itt_testing (eigene Solution, getrennt von Produktionscode)

.DESCRIPTION
    Entities:
    - itt_testcase: Testfall-Speicherung (JSON-Definition, Metadaten)
    - itt_testrun: Testlauf-Steuerung (Status, Filter, Ergebnis)
    - itt_testrunresult: Einzelergebnis pro Testfall (N:1 auf testrun + testcase)

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
        Name = "itt_teststatus"
        DisplayName = "Test Status"
        Options = @(
            @{ Value = 595300000; Label = "Geplant" }
            @{ Value = 595300001; Label = "Läuft" }
            @{ Value = 595300002; Label = "Abgeschlossen" }
            @{ Value = 595300003; Label = "Fehler" }
        )
    },
    @{
        Name = "itt_testoutcome"
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
        Name = "itt_testcategory"
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
        Name = "itt_testenvironment"
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
        SchemaName = "itt_testcase"
        DisplayName = "Test Case"
        DisplayCollectionName = "Test Cases"
        Description = "Testfall-Definition für das Integration Test Center"
        PrimaryField = @{ SchemaName = "itt_name"; DisplayName = "Name"; AutoNumber = "TC-{SEQNUM:5}" }
        Attributes = @(
            @{ SchemaName = "itt_testid"; Type = "String"; MaxLength = 20; DisplayName = "Test ID"; Description = "TC01, TCC05, BTC12" }
            @{ SchemaName = "itt_title"; Type = "String"; MaxLength = 200; DisplayName = "Title"; Description = "Human-readable test title" }
            @{ SchemaName = "itt_category"; Type = "Picklist"; OptionSet = "itt_testcategory"; DisplayName = "Category" }
            @{ SchemaName = "itt_tags"; Type = "String"; MaxLength = 500; DisplayName = "Tags"; Description = "Comma-separated tags" }
            @{ SchemaName = "itt_enabled"; Type = "Boolean"; DisplayName = "Enabled"; DefaultValue = $true }
            @{ SchemaName = "itt_definition_json"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Definition (JSON)"; Description = "Full test case JSON (preconditions + steps + assertions)" }
        )
        AlternateKeys = @(
            @{ Name = "itt_testid_key"; Attributes = @("itt_testid") }
        )
    },
    @{
        SchemaName = "itt_testrun"
        DisplayName = "Test Run"
        DisplayCollectionName = "Test Runs"
        Description = "Testlauf-Steuerung und Ergebnisse"
        PrimaryField = @{ SchemaName = "itt_name"; DisplayName = "Name"; AutoNumber = "TR-{SEQNUM:5}" }
        Attributes = @(
            @{ SchemaName = "itt_teststatus"; Type = "Picklist"; OptionSet = "itt_teststatus"; DisplayName = "Status" }
            @{ SchemaName = "itt_testcasefilter"; Type = "String"; MaxLength = 500; DisplayName = "Test Case Filter"; Description = "*, TC01,TC02, tag:LUW, category:Bridge" }
            @{ SchemaName = "itt_environment"; Type = "Picklist"; OptionSet = "itt_testenvironment"; DisplayName = "Environment" }
            @{ SchemaName = "itt_started_on"; Type = "DateTime"; DisplayName = "Started On" }
            @{ SchemaName = "itt_completed_on"; Type = "DateTime"; DisplayName = "Completed On" }
            @{ SchemaName = "itt_total"; Type = "Integer"; DisplayName = "Total"; Min = 0; Max = 10000 }
            @{ SchemaName = "itt_passed"; Type = "Integer"; DisplayName = "Passed"; Min = 0; Max = 10000 }
            @{ SchemaName = "itt_failed"; Type = "Integer"; DisplayName = "Failed"; Min = 0; Max = 10000 }
            @{ SchemaName = "itt_testsummary"; Type = "Memo"; MaxLength = 100000; DisplayName = "Summary" }
            @{ SchemaName = "itt_testresult_json"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Result (JSON)" }
            @{ SchemaName = "itt_fulllog"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Full Log" }
            @{ SchemaName = "itt_testconfig_json"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Test Config (JSON)"; Description = "Optional inline test JSON (backwards compatibility)" }
        )
    },
    @{
        SchemaName = "itt_testrunresult"
        DisplayName = "Test Run Result"
        DisplayCollectionName = "Test Run Results"
        Description = "Einzelergebnis pro Testfall in einem Testlauf"
        PrimaryField = @{ SchemaName = "itt_name"; DisplayName = "Name"; AutoNumber = "RE-{SEQNUM:6}" }
        Attributes = @(
            @{ SchemaName = "itt_testrunid"; Type = "Lookup"; Target = "itt_testrun"; DisplayName = "Test Run" }
            @{ SchemaName = "itt_testcaseid"; Type = "Lookup"; Target = "itt_testcase"; DisplayName = "Test Case" }
            @{ SchemaName = "itt_testid"; Type = "String"; MaxLength = 20; DisplayName = "Test ID"; Description = "Denormalized for subgrid display" }
            @{ SchemaName = "itt_outcome"; Type = "Picklist"; OptionSet = "itt_testoutcome"; DisplayName = "Outcome" }
            @{ SchemaName = "itt_duration_ms"; Type = "Integer"; DisplayName = "Duration (ms)"; Min = 0; Max = 600000 }
            @{ SchemaName = "itt_error_message"; Type = "Memo"; MaxLength = 100000; DisplayName = "Error Message" }
            @{ SchemaName = "itt_assertion_results_json"; Type = "Memo"; MaxLength = 1000000; DisplayName = "Assertion Results (JSON)" }
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
Write-Host "  5. Solution itt_testing erstellen und alle Komponenten hinzufügen"
