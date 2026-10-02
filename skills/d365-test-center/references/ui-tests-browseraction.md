# UI-Tests mit BrowserAction (CLI, Playwright)

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Feldnamen) steht in der `PROJEKT-KONTEXT.md` des jeweiligen Repos.

## 8b. UI-Tests via Cli (ADR-0006)

UI-Tests laufen ausschließlich im **Cli-Pfad** mit Microsoft.Playwright .NET 1.59
(ADR-0003 Single-Engine + ADR-0005 Sandbox-safe). Der Async-CRUD-Trigger und der
Sync-Custom-API-Pfad übergeben `null` als BrowserActionExecutor; der TestRunner
skipt den Step ohne Failure. Damit sind Test-Suites mit gemischten API+UI-Steps
möglich (API-Anteil läuft überall, UI-Anteil nur im Cli).

### Cli-Aktivierung

Die UI-Tests werden im Cli über `--browser-state <pfad>` aktiviert. Vollständige
Cli-Optionen-Tabelle, `ui-setup`-Sub-Command, Storage-State-Lebensdauer (24h SPA),
Selektor-Strategie und Frame-Fallback-Pattern: siehe den Skill `ui-automation` des Projekt-Repos
(Sektion "Produktiver Pfad") und dessen `PROJEKT-KONTEXT.md`, sofern vorhanden.

### Anmeldezustand anlegen: `ui-setup`

`ui-setup` öffnet ein sichtbares Chromium, wartet bis zu 5 Minuten auf die manuelle Anmeldung (mit MFA) und
speichert Cookies und localStorage als Storage-State-JSON (Lebensdauer rund 24 h im SPA-Flow).

```powershell
& scripts/Setup-PlaywrightStorageState.ps1 `
    -Org "https://contoso-dev.crm4.dynamics.com" `
    -Output "auth/storage-state.json"
# weitere Nicht-Produktions-Umgebung, z.B. contoso-uat.crm4.dynamics.com:
& scripts/Setup-PlaywrightStorageState.ps1 -Org "https://contoso-uat.crm4.dynamics.com" -AllowEnv uat
```

| Aspekt | Wert |
|---|---|
| `-Org` | Pflicht (CLI: `--org`) |
| `-Output` | Default `auth/storage-state.json` (relativ zum CLI-Arbeitsverzeichnis oder absolut) |
| `-AllowEnv` | zusätzliche Umgebungsmarker, je Marker ein `--allow-env <marker>` an die CLI |
| Ohne Zusatz zugelassen | Hosts mit `-dev.` oder `-test.` |
| Nie zulassbar | Produktionsmarker `prod`, `prd`, `production`, `live` |

Harte Sperre in `StorageStateSetup.TryResolveEnvironment`: Geprüft wird nur der Host (ein Marker in Pfad oder
Query öffnet nichts), und zwar als `-<marker>.`. Ein Host `-perftest.` enthält `-test.` also nicht und bleibt
gesperrt, bis `perftest` ausdrücklich freigegeben ist. Welche Umgebungen ein Projekt freigibt, entscheidet das
Projekt, nicht das Produkt. Für jede Umgebung außer DEV zeigt die CLI vor dem Browserstart einen Hinweis: die
zulässigen Lese- und Schreibschritte regelt das beauftragende Projekt, der Anmeldezustand erweitert den
Testumfang nicht. Fehlercodes: 1 bei fehlendem `--org`, 2 bei nicht zugelassenem Host.

### Pre-Deploy-Hook: `Run-UiSmokes.ps1`

Generischer PowerShell-Wrapper im Produkt-Repo `scripts/Run-UiSmokes.ps1`. Wird in
projekt-spezifischen Deploy-Skripten als **Step 0 vor Solution-Export** eingehängt.
Bei rotem Smoke nach allen Versuchen: throw VOR dem Solution-Export, kein Touch der
Ziel-Umgebungen (TEST, weitere Test-Umgebungen, PROD).

```powershell
$env:D365TC_UISMOKE_CLIENT_SECRET = "<secret>"
& scripts/Run-UiSmokes.ps1 `
    -Org "https://contoso-dev.crm4.dynamics.com" `
    -ClientId "<app-id>" -TenantId "<tenant>" `
    -StorageState "<pfad>/storage-state.json"
```

Verifizierte Eigenschaften (aus dem Skript-Code, nicht aus Doku abgeleitet):

| Aspekt | Wert |
|---|---|
| Default `-Filter` | `*-UI-*` (TestId-Konvention `<PROJEKT>-UI-<n>`) |
| Default `-Config` | `standard` |
| Default `-MaxAttempts` | `2` (1 = kein Retry) |
| Sleep zwischen Retries | 5 Sekunden (hardcoded) |
| Trace-Suffix bei mehreren Attempts | `<base>-attempt<N>.<ext>` (z.B. `trace.zip` -> `trace-attempt1.zip`) |
| Client-Secret-Quelle | Parameter `-ClientSecret` ODER ENV `D365TC_UISMOKE_CLIENT_SECRET` |
| Default `-CliDll` | `<repo>/backend/D365TestCenter.Cli/bin/Debug/net8.0/D365TestCenter.Cli.dll` |
| Exit `0` | Alle Smokes grün (Deploy darf weitermachen) |
| Exit `1` | Mindestens ein Smoke ROT nach allen Versuchen (Deploy muss abbrechen) |
| Exit `2` | Setup-Fehler (Storage-State fehlt, Cli-DLL fehlt, ClientSecret leer) |

### Plugin-Pfad (Skip-Verhalten)

Im Sync-Custom-API-Pfad (`jbe_RunIntegrationTests`) und im Async-CRUD-Trigger-Pfad
(`RunTestsOnStatusChange`) wird kein `IBrowserActionExecutor` injiziert. Der
TestRunner detected `_browser == null` und setzt für den Step:

- StepResult-Message (exakter Wortlaut aus `TestRunner.cs:484`):
  `BrowserAction skipped: not supported in the Plugin-Sandbox path. Use the CLI path with --browser-state for UI tests.`
- Log-Message: `SKIP BrowserAction (operation=<op>): not supported in this execution path (likely Plugin-Sandbox). See ADR-0006.`
- `stepResult.Success` bleibt `true` (Default), Outcome-Berechnung sieht keinen Failure.

Konsequenz: TestCase-Outcome ist `Passed` wenn alle übrigen Steps grün sind, oder
`Failed` wenn ein anderer Step (z.B. Assert) bricht. UI-Steps blockieren den
Plugin-Pfad nicht.

### Diagnostik bei BrowserAction-Failure (Cli-Pfad)

Phase 1d Diagnostics-Pipeline:

- Screenshot wird automatisch in `jbe_testrunresult.jbe_screenshot` hochgeladen (FileType-Attribut, MaxSizeInKB=5120 = 5 MB)
- Trace-Zip (wenn `--browser-trace` gesetzt) in `jbe_testrunresult.jbe_uitrace` (MaxSizeInKB=30720 = 30 MB)
- Abruf via Web API: `GET /jbe_testrunresults(<id>)/jbe_screenshot/$value` und `/jbe_uitrace/$value` (real verifiziert Session 16: HTTP 200, 143673 Bytes)
- 4-MB-Chunking via SDK `InitializeFileBlocksUploadRequest`/`UploadBlockRequest`/`CommitFileBlocksUploadRequest`

### Querverweise

- Skill `ui-automation` des Projekt-Repos, Selektor-Strategie, Frame-Fallback, Strict-Mode, MCP-Integration
- dessen `PROJEKT-KONTEXT.md`, projektspezifische Selektoren, Lessons-Learned (Listen-First-Row vs. Direct-URL+formid, Modul-Scope-Stolperfalle, jbe_testcase-Schema-Stolperfalle)
- ADR-0006 (UI-Automation-Architektur), Entscheidungs-Kontext

---
