# Auth und Sicherheit

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Feldnamen) steht in der `PROJEKT-KONTEXT.md` des jeweiligen Repos.

## 13. Auth und Sicherheit

- **Im CRM (Live-Modus):** Die HTML läuft als Web Resource im CRM-iframe. Der Browser hat eine Entra-ID-Session. Alle API-Aufrufe nutzen automatisch das Session-Cookie. Kein eigener Auth-Code nötig.
- **Im Demo-Modus:** Keine echte API, MockAPI mit generierten Daten (seit v5.3 mit robustem Fallback bei fehlenden Pack-Dateien).
- **Deployment-Skripte:** Brauchen einen Bearer-Token. Woher er kommt (Secret-Store des Projekts, Application User, interaktive Anmeldung), regelt das Projekt; Geheimnisse stehen nie im Produkt-Repo.
- **UI-Anmeldezustand (`ui-setup`):** Der Playwright-Storage-State für UI-Tests wird nur für Nicht-Produktions-Hosts angelegt. Ohne Zusatz gelten Hosts mit `-dev.` oder `-test.` im Hostnamen, weitere Umgebungen nur per `--allow-env <marker>` (z.B. `--allow-env uat` für `contoso-uat.crm4.dynamics.com`); Produktionsmarker (`prod`, `prd`, `production`, `live`) sind nie freigebbar. Geprüft wird nur der Host, nicht Pfad oder Query (`StorageStateSetup.TryResolveEnvironment`). Detail: `references/ui-tests-browseraction.md`.

### WICHTIG: Browser immer über Dynamics-Portal öffnen

**Richtig** (Live-Modus mit Session):
1. Dynamics-Portal öffnen: `https://{env}.crm4.dynamics.com/`
2. Zur Model-Driven App navigieren (oder direkt in einem zweiten Tab: `https://{env}.crm4.dynamics.com/WebResources/jbe_/testcenter.html`, dann funktioniert die Entra-Session)

**Falsch** (führt zu Demo-Modus):
- Testcenter-URL direkt im Inkognito-Fenster öffnen -> WhoAmI schlägt fehl (kein Session-Cookie) -> Demo-Modus wird aktiviert -> gemockte Daten statt echter Tests.

Erkennungsmerkmal für falsch-gestartet: Gelbes Banner "Demo Mode: All data is simulated" oben im UI.

### Test-Ausführungsflüsse (v5.3)

| Flow | Aufrufer | Tests |
|------|----------|-------|
| **Browser Start -> CRUD-Trigger** | `API.create("jbe_testruns", status=Geplant)` | Plugin `RunTestsOnStatusChange` (Async Batch-Cascade), `BatchSize 5 * Depth 8 ≈ 40 Tests` (seit v5.3.14, vorher 12/≈96; der HTML-Kommentar in `_startRunWithFilter` trägt noch den alten Wert -> Drift) |
| **Browser Start -> Custom API** | `API.executeAction("jbe_RunIntegrationTests", ...)` | Sync im Plugin (2-Min-Sandbox-Limit) |
| **Headless CLI** | `dotnet D365TestCenter.Cli.dll run --org ... --filter "<TestId-Muster>"` | Kein Sandbox-Limit, beliebig viele Tests |

Für große Test-Suites (> ~40 Tests) oder langsame/lange async-Ketten (2-Min-Sandbox je Batch): **CLI nutzen** (kein Sandbox-Limit). Siehe FB-19.

#### Reiner UI-Anwender: was fehlt gegenüber der CLI

Der Browser startet Läufe **ausschließlich** über den Async-CRUD-Trigger
(`_startRunWithFilter` -> `API.create("jbe_testruns", status=Geplant)`,
`d365testcenter.html:5046`); die Sync-Custom-API `jbe_RunIntegrationTests` ruft er
**nicht** zum Starten (`executeAction` wird dafür nirgends aufgerufen). Beide
Plugin-Pfade instanziieren `TestRunner`/Orchestrator ohne Browser-Executor und ohne
`CaptureRecordNames`. Damit fehlt dem reinen UI-Anwender gegenüber dem CLI-Pfad:

**Im Lauf selbst:**
- **UI-/BrowserAction-Steps** werden geskippt (nur CLI `run --browser-state`). Der Test läuft, die UI-Steps fehlen ohne Failure. Siehe Sektion 8b (`TestRunner.cs` `_browser == null`).
- **Primary-Namen der angelegten Records** im `jbe_trackedrecords` fehlen (`CaptureRecordNames` nur im CLI-`run`, OE-10 Variante A). Geschrieben werden entity/id/alias **ohne** Name; die Asserts (`jbe_assertionresults`) kommen vollständig. Folge: der Zephyr-Audit-Kommentar trägt Record-Namen nur über die CLI.
- **Skalierung/Dauer:** 2-Min-Sandbox je Batch, BatchSize 5 * Depth 8 ≈ **40 Tests/Lauf**; langsame oder lange async-Ketten (z.B. `WaitForNotExists` mit hohem Timeout) reißen das Batch-Limit, der Run bleibt auf "Running". Die CLI hat kein Limit.
- **`expectException` auf Plugin-Throws** ist im Async-Pfad unzuverlässig (Sektion 3.4d). Sauber nur im Sync-Custom-API-Pfad (ADR-0005) oder per Plugin-Unit-Test -- die UI nutzt den Sync-Pfad aber nicht.

**Vor/nach dem Lauf (keine UI-Buttons, alles CLI-Subcommands):**
- `validate --org` (metadata-aware, OE-6/OE-8). Der UI-Editor hat nur eine client-seitige Static-Validation (`EditorModel.validate`), keine Entity/Field-Existenzprüfung gegen die Org.
- `sync-docs` (E1), `sync-results` / `run --sync-defs` (E2), `report` md/html/pdf (E3), `sync-zephyr` (E5), `inventory` (E6), `build-pack`/`import-pack` (B5). Die UI hat nur **"Copy Jira Report"** (`_copyJiraReport`, Clipboard, pro Testfall, kein Upload, kein Run-Report).
- **Headless/CI, Service-Principal-Auth, Exit-Codes** (CLI-only).

**Was die UI dafür kann:** Testfall-Editor (visual/JSON/flow, Templates, Static-Validation), Filter/Tag/Category-Auswahl, Live-Polling + Trend, Copy-Jira-Report. Und weil sie den Async-Pfad nutzt, bekommt sie **volle Per-Test-`jbe_assertionresults`** (anders als der Sync-Custom-API-Pfad, Sektion 8c).

#### Pack-Filter-Format (`jbe_testcasefilter`)

Der Filter beim CRUD-Trigger und Custom-API-Aufruf ist **kein OData**,
sondern ein eigenes Wildcard/CSV-Format. Seit ADR 2026-06-30 1432 zentral
in `TestCaseFilter.Apply` (geteilt von CLI- und Plugin-/Coordinator-Pfad;
`TestCenterOrchestrator.ApplyFilter` delegiert darauf):

| Filter-Wert | Trifft |
|---|---|
| `*` oder leer | alle aktivierten Tests |
| `TC01` | exakt diese ID |
| `TC*` / `*01` / `*BC*` | Wildcard-Prefix/Suffix/Contains |
| `TC01,TC02,TC03` | mehrere IDs als Liste |
| `tag:merge` | alle Tests mit dem Tag `merge` |
| `tag:A,tag:B` | Tag A **ODER** B (Vereinigung; ab ADR 2026-06-30, vorher 0) |
| `category:Bridge` | alle Tests in der Kategorie `Bridge` |
| `*,!TC01` | **alle ausser** TC01 (Negation per `!`-Präfix) |
| `!tag:known-flaky` | alle ausser den so getaggten (Gate-Aufruf für known-flaky) |
| `ORD*,!ORD-STATUS-01` | Gruppe minus einen |

**Negation (ADR 2026-06-30 1432):** Jeder komma-getrennte Term darf ein
führendes `!` tragen = Exclude. Include = Vereinigung der positiven Terme
(ohne positiven Term -> alle aktivierten), minus alle Exclude-Treffer. Der
Volllauf-Gate-Aufruf für known-flaky-Tests ist `-Filter "*,!tag:known-flaky"`.

OData-Syntax wie `jbe_testid eq 'GDPR-A-S01'` funktioniert NICHT, der
Plugin liest das wörtlich und findet keine TestId, die so heißt
(Outcome=Completed, Total=0). Korrekt ist nur die TestId selbst:
`-Filter 'GDPR-A-S01'` oder Wildcard `'GDPR-A-*'`.

Filter-Auswertung passiert nach dem Laden aller `jbe_enabled=true`-
Records. `jbe_enabled=false`-Records werden vor dem Filter aussortiert.

---
