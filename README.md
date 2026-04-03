# D365 Test Center

**End-to-end integration testing for Dynamics 365 / Dataverse**

D365 Test Center is a browser-based test runner that executes JSON-defined test cases against a live Dynamics 365 environment. It fills the gap between UI testing (EasyRepro) and isolated unit testing (FakeXrmEasy) by validating complete plugin chains, async workflows, and field governance logic in real time.

## Key Features

- **JSON-defined test cases** with three phases: preconditions, steps, assertions
- **Live execution** against Dataverse Web API v9.2 (OData v4)
- **Async plugin polling** waits for plugin chains to complete before assertions
- **Visual test editor** with drag-drop, templates, alias autocomplete
- **Dashboard** with trend sparklines, regression detection, flaky test detection
- **Demo mode** works outside Dynamics 365 with generated mock data
- **Zero dependencies** pure Vanilla JS, single HTML file, no build process
- **10-minute deployment** to any Dynamics 365 environment worldwide

## Architecture

```
D365TestCenter/
+-- webresource/              # Single-file Web Resource + JSON test packs
+-- backend/                  # C# solution (Core, CRM Plugin, Tests)
+-- scripts/                  # PowerShell deployment scripts
+-- docs/                     # Product documentation
```

### Frontend

Single HTML file (~8700 lines) deployed as a Dataverse Web Resource. Contains the complete UI (dashboard, test runner, visual editor, metadata explorer) and execution engine (placeholder resolver, step executor, record tracker, governance polling).

### Backend

.NET Framework 4.7.2 solution with three projects:

| Project | Purpose |
|---------|---------|
| `D365TestCenter.Core` | Test runner, assertion engine, placeholder engine, models |
| `D365TestCenter.CrmPlugin` | Custom Action wrapper for server-side execution |
| `D365TestCenter.Tests` | Unit tests |

### Test Packs

Test cases are organized in JSON pack files. Each pack contains an array of test definitions with preconditions, steps, and assertions. Packs are loaded dynamically via a manifest.

## Quick Start

### 1. Deploy to your Dynamics 365 environment

```powershell
# Adjust deploy-config.json with your environment URL
.\scripts\Deploy-Solution.ps1
```

The script creates the publisher, solution, entities, option sets, and web resources idempotently.

### 2. Import test cases

```powershell
.\scripts\Import-TestCases.ps1
```

### 3. Open the Test Center

Navigate to your environment's Web Resources and open `d365testcenter.html`, or access it directly:

```
https://your-org.crm4.dynamics.com/WebResources/jbe_/testcenter.html
```

### 4. Try the Demo Mode

Open `webresource/d365testcenter.html` as a local file in your browser. The app auto-detects it's outside Dynamics 365 and switches to demo mode with mock data.

## Test Case Format

```json
{
  "preconditions": [
    { "entity": "accounts", "alias": "account1",
      "fields": { "name": "{GENERATED:company}" } }
  ],
  "steps": [
    { "action": "UpdateRecord", "alias": "account1",
      "fields": { "name": "New Name" } }
  ],
  "assertions": [
    { "target": "Contact", "field": "firstname",
      "operator": "Equals", "value": "Expected" }
  ]
}
```

### Placeholders

| Placeholder | Description |
|-------------|-------------|
| `{GENERATED:firstname}` | Random first name |
| `{GENERATED:email}` | Random email (example.com) |
| `{TIMESTAMP}` | Current ISO timestamp |
| `{alias.id}` | ID of an aliased record |
| `{alias.fields.xxx}` | Field value of an aliased record |

### Assertion Operators

`Equals`, `NotEquals`, `Contains`, `IsNull`, `IsNotNull`, `Exists`, `NotExists`, `GreaterThan`, `LessThan`, `StartsWith`, `EndsWith`, `DateSetRecently`

## Custom Entities

| Entity | Description |
|--------|-------------|
| `jbe_testcase` | Test case definition (JSON) |
| `jbe_testrun` | A test run |
| `jbe_testrunresult` | Result per test case in a run |
| `jbe_teststep` | Individual step log entry |

## License

[MIT](LICENSE)
