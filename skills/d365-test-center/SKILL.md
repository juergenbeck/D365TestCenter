---
name: d365-test-center
description: >-
  D365 Test Center: Integrationstest-Framework für Dynamics 365 / Dataverse. Single-Page Web Resource plus
  C#-Core-Engine (ADR-0003 Single-Engine) mit drei Aufrufern (CRUD-Plugin, Custom API, CLI); ein Testfall ist
  seit ADR-0004 eine geordnete Action-Liste (Assert ist eine Action). USE FOR: D365TestCenter, Integration
  Test Center, jbe_testcase, jbe_testrun, jbe_testrunresult, jbe_teststep, Testfall-JSON, steps,
  Assert-Action, target Record, target Query, recordRef, CreateRecord, UpdateRecord, DeleteRecord, Wait,
  ExecuteAction, ExecuteRequest, RetrieveRecord, WaitForRecord, WaitForFieldValue, WaitForNotExists,
  TrackRecord, onError continue, onError stop, condition, GENERATED, TIMESTAMP, alias.outputs, outputAlias,
  Governance-Polling, waitForAsync, Pack-System, manifest.json, CONFIG.governance, AssertionEngine, Equals,
  IsNull, DateSetRecently, RecordCount, FindRecord, expectException, messageContains, errorCode,
  SetEnvironmentVariable, Negative-Path-Test, QualifyLead, SetState, Single-File, d365testcenter.html,
  BrowserAction, UI-Test, Playwright, evidence-dir, evidence-report, Testdokumentation, docSteps, docStep,
  caption, highlightFields, hideOverlays, hideSelectors, frameFallback, Belegbild, ADR-0003, ADR-0004,
  ADR-0006, D365TestCenter.Core, D365TestCenter.Cli, D365TestCenter.CrmPlugin, RunTestsOnStatusChange,
  jbe_RunIntegrationTests, jbe_fulllog, sync-docs, sync-results, report, sync-zephyr, build-pack,
  import-pack, inventory. DO NOT USE FOR: Dataverse Web API per PowerShell (use dataverse-web-api), C#
  Plugins allgemein (use dataverse-plugin-development), Xrm.WebApi Form Scripts (use webresources),
  Deployment per pac CLI allgemein (use deployment-alm), Jira- und Zephyr-Pflege (use jira-zephyr-scale).
---

# D365 Test Center

> **Quelle dieses Skills ist das Produkt-Repo D365TestCenter** (`skills/d365-test-center/`), versioniert
> mit dem Code, den er beschreibt (ADR 2026-09-17-2303). In den nutzenden Repos liegt eine byte-gleiche
> Kopie; dort wird der Text **nicht** geändert, sondern in der Quelle, danach wird neu gespiegelt.
>
> **Projektspezifisches steht nicht hier**, sondern in der `PROJEKT-KONTEXT.md` neben diesem Skill:
> Umgebungen und URLs, App- und Datensatz-Kennungen, Formular- und Feldnamen, Ablagepfade, Pack-Orte,
> Deployment- und Versionsstände. Diese Datei gehört dem jeweiligen Repo und wird beim Spiegeln nie
> überschrieben.

Dieser Hub trägt das Mentalmodell und das Routing. Das operative Detail steht in `references/`; bei
konkreter Arbeit wird die passende Referenz mitgeladen, nicht nur dieser Hub.

## Was es ist

Ein Testrunner für Ende-zu-Ende-Integrationsketten in einer lebenden Dataverse-Umgebung. Er führt
JSON-definierte Testfälle aus, wartet auf asynchrone Plugin-Ketten und prüft Ergebnisse automatisch.

**Lücke, die es füllt:** EasyRepro testet die Oberfläche, FakeXrmEasy testet C# in Isolation. Das Test
Center testet die Kette in einer echten Umgebung.

**Architektur (seit v5.3, ADR-0003 Single-Engine):** Eine C#-Core-Engine (`D365TestCenter.Core`), genutzt
von drei Aufrufern:

- **CRUD-Trigger-Plugin** (`RunTestsOnStatusChange`): asynchrone Batch-Kaskade, ausgelöst durch Anlage oder
  Statuswechsel eines `jbe_testrun`.
- **Custom API** (`jbe_RunIntegrationTests`): synchroner Aufruf für den Start aus dem Browser.
- **CLI** (`D365TestCenter.Cli`): headless für CI/CD und lange Läufe, kein Sandbox-Zeitlimit; nur hier gibt
  es UI-Schritte (`BrowserAction`, ADR-0006) und die Testdokumentation mit Belegbildern.

Die Web Resource `d365testcenter.html` (Vanilla JS, ohne Fremdbibliotheken) ist seit v5.3 ein reiner
UI-Client: Sie legt `jbe_testrun`-Datensätze an und pollt `jbe_testrunresult` für die Live-Ansicht.

## Aufbau des Produkt-Repos

```
D365TestCenter/
+-- webresource/          # d365testcenter.html plus generische Packs (manifest.json, standard.json, ...)
+-- backend/              # C#-Solution: Core (TestRunner, AssertionEngine, Models), CrmPlugin, Cli, Tests
+-- scripts/              # Deploy-Solution.ps1, deploy-config.json, Spiegel-Sync
+-- skills/               # Quelle dieses Skills
+-- docs/                 # Produktdokumentation (CLI-Referenz, Doku und Reporting, Handbuch)
```

Ein Projekt-Repo bringt seine eigenen Packs, Testfälle und Deploy-Konfigurationen mit; wo sie liegen,
sagt die `PROJEKT-KONTEXT.md`.

## Routing: welche Referenz wofür

| Aufgabe | Referenz |
|---|---|
| Testfall schreiben, Actions, Platzhalter, Asserts, Pitfalls | [`references/testfall-format.md`](references/testfall-format.md) |
| CONFIG-Block der Web Resource, Config-Profil `standard` | [`references/konfiguration.md`](references/konfiguration.md) |
| Engine, Ergebnis-Datensätze je Pfad, Pre-Run-Validation | [`references/execution-engine.md`](references/execution-engine.md) |
| UI-Schritte im CLI-Pfad (`BrowserAction`, Playwright, Anmeldezustand) | [`references/ui-tests-browseraction.md`](references/ui-tests-browseraction.md) |
| Testdokumentation mit Belegbildern je Schritt (HTML und DOCX) | [`references/ui-testdokumentation.md`](references/ui-testdokumentation.md) |
| `sync-docs`, `sync-results`, `report`, `sync-zephyr` | [`references/doku-und-reporting.md`](references/doku-und-reporting.md) |
| Packs bauen, importieren, Lokalisierung | [`references/pack-system.md`](references/pack-system.md) |
| Deployment von Engine, Plugin, CLI und Web Resource | [`references/deployment.md`](references/deployment.md) |
| API-Objekt im Browser, Test-Center-Tabellen und OptionSets | [`references/api-und-entities.md`](references/api-und-entities.md) |
| Authentifizierung, Rechte, Geheimnisse | [`references/auth-und-sicherheit.md`](references/auth-und-sicherheit.md) |

## Goldene Regeln (Kurzfassung)

1. **Keine Fremdbibliotheken:** kein jQuery, React oder D3. Alles nativ.
2. **Eine Datei:** alles in der einen HTML, Pack-JSONs sind die einzige Ausnahme.
3. **Deterministische Testdaten:** erkennbares Präfix, 555-Nummern, example.com.
4. **Keine magischen Zahlen:** alles über CONFIG-Konstanten.
5. **Idempotentes Deployment:** Existenzprüfung vor jedem Anlegen.
6. **Protokollieren:** jeder API-Aufruf, jeder Schritt, jede Prüfung.
7. **Aufräumen optional, Nachhalten Pflicht:** der RecordTracker dokumentiert immer.
8. **Fehler zeigen:** klare Meldung mit geparster Ursache.
9. **Echte Umlaute** in allen deutschen Texten.
10. **Dataverse fragen, nicht raten:** OptionSets und Schema per API abfragen.
11. **Bug-Hypothese zuerst als Test festnageln:** vor dem Fix die Hypothese als Unit-Test rot und danach grün
    zeigen, samt Schutz für die heute korrekten Pfade, statt den erstbesten Fix zu bauen.

## Typische Aufgaben

**Neuen Testfall schreiben:** ein bestehendes Pack des Projekts als Vorlage nehmen; den Fall als **eine
geordnete `steps`-Liste** schreiben (ADR-0004: kein getrenntes `preconditions`/`assertions`, Setup ist
`CreateRecord` am Anfang, `Assert` ist eine Action); Platzhalter für Testdaten verwenden (`{GENERATED:...}`,
`{alias.id}`, `{TIMESTAMP}`); auf asynchrone Effekte mit `WaitForRecord`, `WaitForFieldValue`,
`WaitForNotExists` oder `WaitForAsyncCompletion` warten statt mit festem `Wait`; das Pack registrieren und
per `import-pack` einspielen. Detail: `references/testfall-format.md`.

**Einen UI-Fall automatisieren:** nur im CLI-Pfad, mit Anmeldezustand (`--browser-state`). Soll daraus eine
Testdokumentation für ein Ticket entstehen, trägt der Fall `docSteps` und je geprüfter Erwartung einen
`screenshot` mit `caption` und `highlightFields`; gelaufen wird mit `--evidence-dir`. Detail:
`references/ui-tests-browseraction.md` und `references/ui-testdokumentation.md`.

**Web Resource ändern:** HTML bearbeiten, `CONFIG.version` hochzählen, lokal im Demo-Modus prüfen
(`python -m http.server` im Ordner `webresource/`), danach deployen. Detail: `references/deployment.md`.

**Neues Projekt einrichten:** Packs, Testfälle und Deploy-Konfiguration im Projekt-Repo anlegen und die
`PROJEKT-KONTEXT.md` neben diesem Skill füllen. Ein eigenes C#-Config-Profil gibt es nicht: die CLI kennt nur
`standard` (Default). Wer im Visual Editor Governance-Hilfen braucht, füllt den optionalen, leer ausgelieferten
Block `CONFIG.governance` in der eigenen Deploy-Kopie der Web Resource. Detail: `references/konfiguration.md`.

## Produktdokumentation

Im Produkt-Repo: `docs/08_cli-referenz.md` (Kommandos und Schalter), `docs/09_doku-und-reporting.md`
(Doku-Lebenszyklus, Testdokumentation), `docs/handbuch/02-testfall-schreiben/02-actions-referenz.md`
(Actions mit allen Feldern), `docs/05_deployment-handbuch.md`. Öffentlich:
`https://github.com/juergenbeck/D365TestCenter`.
