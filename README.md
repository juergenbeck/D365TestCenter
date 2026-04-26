# D365 Test Center

**End-to-end integration testing for Dynamics 365 / Dataverse**

D365 Test Center is a test runner for Dynamics 365 environments that executes JSON-defined test cases against a live Dataverse organization. It fills the gap between UI-only testing (EasyRepro) and isolated unit testing (FakeXrmEasy) by validating complete plugin chains, async workflows, custom APIs, environment variables, and field governance logic against real data.

A single C# core engine (`D365TestCenter.Core`) is invoked from three callers (Single-Engine architecture, ADR-0003): the in-product Web Resource UI, a CRM plugin (sync custom API + async CRUD trigger), and a headless command-line client.

## Key Features

- **JSON-defined test cases** as a flat list of actions, with assertions inline (ADR-0004)
- **Live execution** against the Dataverse Web API v9.2 (OData v4)
- **Three execution flows** sharing one engine: Web Resource UI, CRM plugin, headless CLI (ADR-0003)
- **Async plugin polling** waits for plugin chains to complete before assertions
- **Negative-path tests** with `expectFailure` / `expectException` and sandbox-safe error capture (ADR-0005)
- **UI tests via Microsoft.Playwright** as `BrowserAction` steps in the CLI flow (ADR-0006)
- **Visual test editor** with drag-drop, templates, alias autocomplete
- **Dashboard** with trend sparklines, regression detection, flaky test detection
- **Demo mode** works outside Dynamics 365 with generated mock data
- **Zero dependencies** in the Web Resource (pure Vanilla JS, single HTML file, no build process)

## Architecture

```
D365TestCenter/
+-- webresource/              # Single-file Web Resource + JSON test packs
+-- backend/                  # C# solution (Core, Cli, CRM Plugin, Tests)
+-- scripts/                  # PowerShell deployment + UI smoke scripts
+-- docs/                     # Product documentation
```

### Frontend

Single HTML file (~8100 lines) deployed as a Dataverse Web Resource. Contains the complete UI (dashboard, test runner, visual editor, metadata explorer). Since v5.3 it is a thin client: it creates `jbe_testrun` records and polls `jbe_testrunresult` for the live view; the test execution itself happens in the backend.

### Backend

| Project | Target Framework | Purpose |
|---------|-----------------|---------|
| `D365TestCenter.Core` | netstandard2.0 | Test runner, assertion engine, placeholder engine, models |
| `D365TestCenter.Cli` | net8.0 | Headless CLI for CI/CD, no sandbox time limit, hosts Microsoft.Playwright for UI tests |
| `D365TestCenter.CrmPlugin` | net462 | CRM plugin (`RunTestsOnStatusChange` async CRUD trigger + `jbe_RunIntegrationTests` sync custom API) |
| `D365TestCenter.Tests` | net10.0 | Unit tests |

`Core` targets `netstandard2.0` so that the same engine runs in the .NET Framework 4.6.2 plugin sandbox and in the modern .NET 8+ CLI host.

### Test Packs

Test cases are organized in JSON pack files. Each pack contains an array of test definitions. Packs are loaded dynamically via a manifest and deployed as Dataverse Web Resources.

## Quick Start

### 1. Deploy to your Dynamics 365 environment

```powershell
# Adjust deploy-config.json with your environment URL
.\scripts\Deploy-Solution.ps1
```

The script creates the publisher, solution, entities, option sets, web resources and the plugin package idempotently.

### 2. Import test cases

```powershell
.\scripts\Import-TestCases.ps1
```

### 3. Open the Test Center

Navigate to your environment's Web Resources and open `d365testcenter.html`, or access it directly:

```
https://your-org.crm4.dynamics.com/WebResources/jbe_/testcenter.html
```

### 4. Run headless via the CLI (CI/CD)

```powershell
dotnet backend\D365TestCenter.Cli\bin\Debug\net8.0\D365TestCenter.Cli.dll run `
    --org https://your-org.crm4.dynamics.com `
    --client-id <app-id> --client-secret <secret> --tenant-id <tenant> `
    --filter "MGR*"
```

The CLI does not have the 2-minute plugin sandbox limit, so it is the recommended path for large suites (>30 tests).

### 5. Try the Demo Mode

Open `webresource/d365testcenter.html` as a local file in your browser. The app auto-detects it is outside Dynamics 365 and switches to demo mode with mock data.

## Test Case Format

Since ADR-0004, a test case is a single ordered list of actions. There is no separate `preconditions` / `assertions` block; setup steps are normal `CreateRecord` actions at the start, and `Assert` is just another action.

```json
{
  "testId": "DEMO-01",
  "title": "Account update + assert",
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc1",
      "fields": { "name": "{GENERATED:company}" } },
    { "stepNumber": 2, "action": "UpdateRecord", "alias": "acc1",
      "fields": { "websiteurl": "https://example.com" } },
    { "stepNumber": 3, "action": "Assert", "target": "Record",
      "recordRef": "{RECORD:acc1}",
      "field": "websiteurl", "operator": "Equals", "value": "https://example.com" }
  ]
}
```

### Action Types

| Action | Purpose |
|--------|---------|
| `CreateRecord` / `UpdateRecord` / `DeleteRecord` | Standard CRUD |
| `RetrieveRecord` / `FindRecord` / `WaitForRecord` | Read and polling on existence/values |
| `Wait` | Fixed delay |
| `ExecuteAction` | Invoke a custom API |
| `ExecuteRequest` | Invoke any SDK message (e.g. `QualifyLead`), with optional `outputAlias` for result substitution |
| `SetEnvironmentVariable` / `RetrieveEnvironmentVariable` | Manipulate environment variables with auto-restore |
| `BrowserAction` | UI step driven by Microsoft.Playwright (CLI only, see UI Tests below) |
| `Assert` | Assertion (with `target: Record` or `target: Query`, plus operators) |

### Negative-path tests (ADR-0005)

Steps may set `expectFailure: true` or `expectException: { messageContains, errorCode, ... }` to assert that an action throws. Sandbox-safe via `ExecuteMultipleRequest` with `ContinueOnError=true` since plugin v5.3.3.

### Placeholders

| Placeholder | Description |
|-------------|-------------|
| `{GENERATED:firstname}` | Random first name (with `JBE Test` prefix for cleanup) |
| `{GENERATED:email}` | Random email at `example.com` |
| `{TIMESTAMP}` | Current ISO timestamp |
| `{alias.id}` | ID of an aliased record |
| `{alias.fields.xxx}` | Field value of an aliased record |
| `{alias.outputs.xxx}` | Result value from `ExecuteRequest` with `outputAlias` |

### Assertion Operators

`Equals`, `NotEquals`, `Contains`, `IsNull`, `IsNotNull`, `Exists`, `NotExists`, `RecordCount`, `GreaterThan`, `LessThan`, `StartsWith`, `EndsWith`, `DateSetRecently`

## UI Tests (ADR-0006)

UI tests run as `BrowserAction` steps in the CLI flow only. The plugin flows do not inject a browser executor, so the test runner skips those steps with the message `BrowserAction skipped: not supported in the Plugin-Sandbox path. Use the CLI path with --browser-state for UI tests.` and continues. A pack can therefore contain mixed API + UI steps and still execute fully via the CLI while the API portion runs anywhere.

### Operations

| operation | Required | Optional |
|---|---|---|
| `navigate` | `url` | `waitForSelector`, `timeoutSeconds`, `assertNoLoginRedirect` |
| `click` / `doubleClick` | `selector` | `fallbackSelector`, `waitForSelector` |
| `fill` | `selector`, `value` | |
| `delay` | `delayMs` | |
| `waitFor` | `selector` | `timeoutSeconds` |
| `screenshot` | | `name` |
| `evaluate` | `expression` | `value` (inline assert: result must equal `value`), `outputAlias` |

### Storage-state setup

```powershell
dotnet backend\D365TestCenter.Cli\bin\Debug\net8.0\D365TestCenter.Cli.dll ui-setup `
    --org https://your-dev-org.crm4.dynamics.com `
    --output auth\storage-state.json
```

Headed Chromium opens, you log in interactively (incl. MFA), the resulting browser state is persisted as JSON. The `ui-setup` sub-command has a hard guard against non-DEV subdomains. Storage-state files are short-lived (~24h for SPA flows) and must not be committed to source control.

### Running UI tests

```powershell
dotnet backend\D365TestCenter.Cli\bin\Debug\net8.0\D365TestCenter.Cli.dll run `
    --org https://your-dev-org.crm4.dynamics.com `
    --client-id <app-id> --client-secret <secret> --tenant-id <tenant> `
    --filter "UI-*" `
    --browser-state auth\storage-state.json
```

Useful flags: `--browser-headed` (visible browser for local debug), `--browser-locale <locale>` (default `de-DE`), `--browser-trace <path>` (Playwright trace zip).

### Pre-deploy smoke gate

`scripts/Run-UiSmokes.ps1` is a generic wrapper around the CLI with retry logic and exit codes intended to be invoked as a pre-deploy gate from project-specific deployment scripts. If any UI smoke fails after all retries, the script exits non-zero so the calling deploy pipeline aborts before exporting or importing the solution.

```powershell
$env:D365TC_UISMOKE_CLIENT_SECRET = "<secret>"
.\scripts\Run-UiSmokes.ps1 `
    -Org "https://your-dev-org.crm4.dynamics.com" `
    -ClientId "<app-id>" -TenantId "<tenant>" `
    -StorageState "auth\storage-state.json" `
    -Filter "UI-*"
```

### Diagnostics on failure

When a `BrowserAction` step fails (selector timeout, inline assert mismatch), the CLI automatically uploads:

- A PNG screenshot to `jbe_testrunresult.jbe_screenshot` (5 MB file column)
- The Playwright trace zip to `jbe_testrunresult.jbe_uitrace` (30 MB file column, when `--browser-trace` was provided)

Both are retrievable via the Web API `/jbe_testrunresults(<id>)/jbe_screenshot/$value` and `/jbe_uitrace/$value` respectively.

## Custom Entities

| Entity | Description |
|--------|-------------|
| `jbe_testcase` | Test case definition (JSON in `jbe_definitionjson`) |
| `jbe_testrun` | A test run; `jbe_fulllog` carries engine + plugin trace logs |
| `jbe_testrunresult` | Result per test case; `jbe_trackedrecords`, `jbe_screenshot`, `jbe_uitrace` |
| `jbe_teststep` | Individual step log entry |

## License

[MIT](LICENSE)
