# Getting Started with D365 Test Center

This guide walks you through deploying D365 Test Center to your Dynamics 365 environment and running your first test case.

## Prerequisites

- A Dynamics 365 / Dataverse environment (any edition)
- PowerShell 5.1+ (Windows) or PowerShell 7+ (cross-platform)
- A valid Bearer token for your Dataverse environment (MSAL, Client Credentials, or interactive login)
- A modern browser (Chrome, Edge, Firefox)

## Step 1: Configure your environment

Edit `scripts/deploy-config.json`:

```json
{
    "publisherPrefix": "jbe",
    "publisherOptionValuePrefix": 10571,
    "publisherUniqueName": "jbe",
    "resource": "https://YOUR-ORG.crm4.dynamics.com/",
    "solutionUniqueName": "D365TestCenter"
}
```

Keep `publisherPrefix` and `publisherOptionValuePrefix`: the web resource and the engine expect the `jbe_`
schema names and option set values from 105710000 upwards (`publisherOptionValuePrefix` x 10000).

Replace `YOUR-ORG` with your Dataverse organization name.

## Step 2: Authenticate

Set the `$headers` variable with your Bearer token before running the deployment:

```powershell
# Option A: MSAL interactive login
$token = (Get-MsalToken -ClientId "YOUR-APP-ID" -TenantId "YOUR-TENANT" -Scopes "https://YOUR-ORG.crm4.dynamics.com/.default").AccessToken
$headers = @{ "Authorization" = "Bearer $token"; "Content-Type" = "application/json" }

# Option B: Client credentials (service principal)
$token = (Get-MsalToken -ClientId "APP-ID" -ClientSecret (ConvertTo-SecureString "SECRET" -AsPlainText -Force) -TenantId "TENANT" -Scopes "https://YOUR-ORG.crm4.dynamics.com/.default").AccessToken
$headers = @{ "Authorization" = "Bearer $token"; "Content-Type" = "application/json" }

# Option C: Azure CLI (if already logged in)
$token = (az account get-access-token --resource "https://YOUR-ORG.crm4.dynamics.com" --query accessToken -o tsv)
$headers = @{ "Authorization" = "Bearer $token"; "Content-Type" = "application/json" }
```

## Step 3: Deploy

```powershell
cd scripts
.\Deploy-Solution.ps1
```

The script creates everything idempotently (safe to run multiple times):

| Component | What gets created |
|-----------|------------------|
| Publisher | "JBE" with prefix `jbe` |
| Solution | "IntegrationTestCenter" |
| 5 OptionSets | Test status, outcome, category, step phase, step status |
| 4 Entities | jbe_testcase, jbe_testrun, jbe_testrunresult, jbe_teststep |
| All attributes | On all 4 entities |
| 2 Relationships | testrunresult to testrun, teststep to testrunresult |
| Web Resources | HTML app + JSON packs |
| PublishAllXml | Makes everything visible |

## Step 4: Open the Test Center

Navigate to:

```
https://YOUR-ORG.crm4.dynamics.com/WebResources/jbe_/testcenter.html
```

You should see the dashboard with sample test cases from the "Standard CRM (Sales & Service)" pack.

## Step 5: Try the Demo Mode

Open the HTML file locally to explore without a Dynamics 365 connection:

```powershell
# Option A: Direct file
start ..\webresource\d365testcenter.html

# Option B: Local HTTP server (needed for pack loading)
cd ..\webresource
python -m http.server 8765
# Open http://localhost:8765/d365testcenter.html
```

The app auto-detects it's outside Dynamics 365 and shows demo data.

## Step 6: Write your first test case

Create a JSON file with three phases:

```json
{
  "preconditions": [
    {
      "entity": "accounts",
      "alias": "testAcc",
      "fields": { "name": "My Test Company {TIMESTAMP}" }
    }
  ],
  "steps": [
    {
      "action": "UpdateRecord",
      "alias": "testAcc",
      "fields": { "websiteurl": "https://example.com" }
    }
  ],
  "assertions": [
    {
      "target": "Query",
      "entity": "accounts",
      "filter": { "accountid": "{testAcc.id}" },
      "field": "websiteurl",
      "operator": "Equals",
      "value": "https://example.com"
    }
  ]
}
```

### Placeholders

| Placeholder | Result |
|-------------|--------|
| `{GENERATED:firstname}` | Random first name ("JBE Test ...") |
| `{GENERATED:email}` | Random email @example.com |
| `{TIMESTAMP}` | Current ISO timestamp |
| `{alias.id}` | ID of a previously created record |
| `{alias.fields.xxx}` | Field value from a previously created record |

### Assertion Operators

`Equals`, `NotEquals`, `Contains`, `IsNull`, `IsNotNull`, `Exists`, `NotExists`, `GreaterThan`, `LessThan`, `StartsWith`, `EndsWith`, `DateSetRecently`

## Step 7: Create a test pack

1. Save your test cases as a JSON file in `webresource/packs/`
2. Register it in `webresource/packs/manifest.json`:

```json
{
  "packs": [
    { "packId": "my-tests", "file": "my-tests.json" }
  ]
}
```

3. Re-run `Deploy-Solution.ps1` (it will update the web resources)

## Waiting for async plugins

Test runs execute in the C# engine, which does not read any polling settings from the web resource.
To wait for asynchronous plugins, use the test case actions `WaitForRecord` / `WaitForFieldValue`
(poll until a record or value appears) or `WaitForAsyncCompletion`, and assert the result afterwards.

The optional `CONFIG.governance` block inside `d365testcenter.html` only feeds the Visual Editor
(field suggestions, source systems, date-field mapping) and ships empty:

```javascript
CONFIG.governance = {
    sourceEntity: "", sourceEntitySet: "", loggingEntitySet: "", governanceApi: "",
    sourceSystemField: "", externalIdField: "",
    sourceSystems: [],       // e.g. [{ value: 1, label: "CRM", alias: "crm" }]
    autoDateFields: {}       // e.g. { "contoso_firstname": "contoso_firstname_modifiedon" }
};
```

## Next steps

- Read the full [API Reference](03_api-referenz.md)
- Explore the [Test Case Specification](04_testfall-spezifikation.md)
- Check the [Deployment Guide](05_deployment-handbuch.md)
- See [Customization Guide](07_customization.md) for adapting demo metadata
- Use the [CLI Reference](08_cli-referenz.md) for all headless commands (run, report, sync, build/import-pack, inventory)
- Understand the [documentation & reporting lifecycle](09_doku-und-reporting.md) (ADR-0008)
