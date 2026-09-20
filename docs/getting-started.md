# Getting Started with D365 Test Center

This guide walks you through deploying D365 Test Center to your Dynamics 365 environment and running your first test case.

## Prerequisites

- A Dynamics 365 / Dataverse environment (any edition) and a user with the System Customizer or System
  Administrator role
- The Power Platform CLI (`pac`)
- A modern browser (Chrome, Edge, Firefox)

## Step 1: Sign in

```powershell
pac auth create --environment https://YOUR-ORG.crm4.dynamics.com
```

## Step 2: Build the solution package

```powershell
pac solution pack --zipfile solution/out/D365TestCenter.zip --folder solution/src --packagetype Unmanaged
```

## Step 3: Import

```powershell
pac solution import --path solution/out/D365TestCenter.zip --publish-changes --activate-plugins
```

The solution `D365TestCenter` contains everything the Test Center needs:

| Component | Content |
|-----------|---------|
| Publisher | "JBE" with prefix `jbe` |
| Tables | jbe_testcase, jbe_testrun, jbe_testrunresult, jbe_teststep, jbe_testchunk |
| Option sets | test status, outcome, category, step status, chunk status, lifecycle status |
| App | model-driven app `jbe_D365TestCenter` with forms and views |
| Web resources | `jbe_/testcenter.html`, `jbe_/handbuch.html`, demo packs |
| Plugin package | `jbe_D365TestCenter` with the engine, plugin steps and custom APIs |

The recurring recovery flow for stalled chunk runs is created per environment with
`scripts/Create-RecurrenceFlow.ps1` (optional).

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

Create a JSON file with one ordered list of actions. Setting up a record, changing it and
checking the result are all steps in the same list:

```json
{
  "testId": "MY-01",
  "title": "Set the website on a new account",
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "testAcc",
      "fields": { "name": "My Test Company {GENERATED:guid}" } },
    { "stepNumber": 2, "action": "UpdateRecord", "alias": "testAcc",
      "fields": { "websiteurl": "https://example.com" } },
    { "stepNumber": 3, "action": "Assert", "target": "Query", "entity": "accounts",
      "filter": { "accountid": "{testAcc.id}" },
      "field": "websiteurl", "operator": "Equals", "value": "https://example.com",
      "onError": "continue", "description": "Website was stored" }
  ]
}
```

### Placeholders

| Placeholder | Result |
|-------------|--------|
| `{GENERATED:firstname}` | Random first name ("JBE Test ...") |
| `{GENERATED:email}` | Random email @example.com |
| `{TIMESTAMP}` | Current UTC time as `yyyyMMdd_HHmmss_fff` (use `{TIMESTAMP_ISO}` for ISO 8601) |
| `{GENERATED:guid}` | Short random hex string, handy for unique names |
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

3. Copy the pack and the manifest to `solution/src/WebResources/jbe_/packs/`, then rebuild and re-import the solution

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
