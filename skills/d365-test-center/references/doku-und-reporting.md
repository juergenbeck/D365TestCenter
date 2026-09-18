# Doku- und Reporting-Kommandos (sync-docs, sync-results, report, sync-zephyr)

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Feldnamen) steht in der `PROJEKT-KONTEXT.md` des jeweiligen Repos.

## 8e. Doku/Reporting-Commands (ADR-0008): `sync-docs` + `sync-results` + `report`

Geschlossener Doku-Kreis um die Markdown-Test-Definitionen (Frontmatter +
Doku-Sektionen + eingebetteter JSON-Block, Format `definition-format.md`). Die
MD-Definition bleibt SSOT; das TestCenter zieht Ergebnisse zurück und erzeugt
Berichte. Render-/Parse-Logik im **Core** (`D365TestCenter.Core.Reporting`,
rein), IO/Dataverse in der **CLI**. Gemeinsamer Frontmatter-/Sektions-Parser:
`MarkdownDocument` (Normalize, TrySplitFrontmatter, ReadScalar mit
Quote-Stripping, SplitSections, ReadFirstHeading).

### E1: `sync-docs` (Doku-Durchreichung)

Schreibt die fachliche Doku (Pflicht-Sektionen Zweck/Datenkonstellation/Vorbedingungen/Ablauf/Erwartetes
Ergebnis) aus den MD-Definitionen nach `jbe_testcase.jbe_documentation`, damit der HTML-Client sie pro
Test zeigt. Matching `testId == frontmatter.id`. Nur die fachliche Whitelist (keine Ergebnis-Historie,
kein JSON-Block, kein Env-Scope).

```
sync-docs --defs <dir> [--config]
```

- Core `MarkdownReportGenerator.BuildDocumentation` (Whitelist), CLI `DocSync` (`CollectDocumentation`
  rein/testbar, `SyncDocumentation` Dataverse-Write auf **existierende** `jbe_testcase`, keine Neuanlage).
- **Schema:** Feld `jbe_documentation` (Memo/ntext) auf `jbe_testcase` nötig. HTML-Client rendert es im
  Detail-Tab "Dokumentation" via minimalem JS-Markdown-Renderer (`renderMarkdown`, spiegelt Core
  `MarkdownToHtml`). WebResource `webresource/d365testcenter.html` und `solution/src/WebResources/jbe_/
  testcenter.html` byte-identisch halten.
- **Schreibzugriff** auf `jbe_testcase` (DEV-Test-Daten frei, andere Envs Freigabe). Ob Feld und Web Resource
  auf einer Umgebung schon ausgerollt sind, steht im Umgebungsstand der `PROJEKT-KONTEXT.md`.

### E2: `sync-results` (Result-Round-Trip)

Schreibt Lauf-Ergebnisse in die `ergebnis_historie` der MD-Definitionen zurück
(Frontmatter ist SSOT, Body-Tabelle zwischen `<!-- AUTO:ergebnis-historie -->`-
Markern daraus gerendert). Matching `testId == frontmatter.id`.

```
sync-results --run <guid> --defs <dir> [--env] [--config]
run ... --sync-defs <dir> [--env]    # Komfort: synct direkt nach dem Lauf
```

Core `MarkdownResultSync` (rein, idempotent pro datum+env+modus), CLI
`ResultSync` (Directory-Walk, Datei-Schreiben UTF-8 ohne BOM). **Schreibt nur
lokale MD-Dateien, KEIN Dataverse-Write.** Für Smokes gegen eine temp-Kopie der
Definitionen, nie direkt gegen einen aktiven Sprint.

### E3/E4: `report` (Durchführungsbericht: Markdown / HTML / PDF)

Verheiratet Test-Doku (Zweck/Sektionen aus den lokalen MD) mit Ergebnis (aus
`jbe_testrun`/`-result`). **Ein Run = ein Bericht.** Liest die Doku direkt aus
den MD (unabhängig von E1).

```
report --run <guid> --defs <dir> [--out <datei>] [--detail compact|full] [--format md|html|pdf] [--env] [--config]
```

| Stufe | Inhalt |
|---|---|
| `compact` | Suite-Kopf (README erster Absatz) + KPI-Bilanz + Tabelle (ID, Titel, Zweck-Kurztext, Ergebnis, Dauer) |
| `full` (Default) | Suite-Kopf voll (Worum es geht + Träger-Modell) + KPI-Bilanz + pro Test alle Pflicht-Sektionen (Zweck, Datenkonstellation, Vorbedingungen, Ablauf, Erwartetes Ergebnis) + bei FAIL/ERROR die Fehlermeldung |

- **Suite-Kopf** aus `README.md` im `--defs`-Baum (`ParseReadme`).
- **Pro Test** aus der MD-Definition (`ParseDefinition`: titel + `## `-Sektionen);
  Ergebnis-Historie/JSON-Block sind bewusst NICHT im Bericht (Whitelist).
- **KPI-Bilanz** aus den gezeigten Items; **Dauer** Wall-Clock aus dem Run-Header
  (`jbe_startedon`/`jbe_completedon`, Fallback Summe der Test-Dauern); Datum/Filter
  aus dem Run. Core `MarkdownReportGenerator`, CLI `ReportBuilder`.
- **`--out`** schreibt die Datei (UTF-8 ohne BOM), ohne `--out` nach stdout.
- **`--format` (E4):** `md` (Default), `html` (self-contained, Inline-CSS, Regel 1) oder `pdf`
  (HTML via Playwright `PdfAsync`; braucht `--out` und Chromium `playwright install chromium`, das die
  CLI für UI-Tests ohnehin nutzt). Doku-Sektionen werden mit dem dependency-freien Core-Konverter
  `MarkdownToHtml` zu HTML (Absätze/Listen/Tabellen/inline). Core `HtmlReportRenderer`, CLI `PdfRenderer`.
- **Rein lesend gegen Dataverse** (Output ist die lokale `--out`-Datei). Smoke daher
  freigabefrei; `--defs` darf direkt auf die echten Definitionen zeigen (read-only).

### B5: `build-pack` + `import-pack` (Pack-Erzeugung + Import aus den MD-Definitionen)

Portiert ein projektseitiges PowerShell-Tooling (`Build-D365TC-Pack.ps1`/`Import-PackToDataverse.ps1`) generisch nach
Core/CLI. `build-pack` erzeugt aus einem Verzeichnisbaum von MD-Definitionen ein importierbares Suite-Pack
`{name, testCases:[{testId,title,tags,userStories,documentation,steps}]}`; `import-pack` schreibt es nach `jbe_testcase`.

```
build-pack  --defs <dir> --out <file> [--name] [--strict]      # offline, kein Dataverse
import-pack --pack <file> --org <url> ... --config <profil>     # Dataverse-Write auf jbe_testcase
```

- **build-pack** (Core `PackBuilder`, CLI `PackBuild`): Directory-Walk (Ausschluss `_`-Ordner, `archiv`/
  `archive`-Ordner, `README*`), pro Def den eingebetteten ```json-Block (`MarkdownDocument.ExtractJsonBlock`,
  letzter Block gewinnt) als Testfall, angereichert um `documentation` (Doku-Whitelist) und `userStories`
  (Frontmatter `ticket` + `weitere_tickets`). Lint-Codes JSON_BLOCK_MISSING/INVALID, JSON_ID_MISMATCH,
  FRONTMATTER_ID_MISSING, ARCHIVED_SKIPPED, DOC_SECTION_MISSING. Pack UTF-8 ohne BOM. `--strict` lässt auch Warnings fehlschlagen.
- **Archiv-Filter (Entscheidung 22, 2026-06-19):** Defs mit `status: archiviert` werden übersprungen
  (`ARCHIVED_SKIPPED`), `status: entwurf` ohne JSON-Block still. Hintergrund: Archivierte Defs können alte
  **Suiten** im Format `{suiteId, testCases:[...]}` sein (auf ADR-0004 nicht lauffähig, durch Standalone-Defs
  ersetzt). Suiten werden daher NICHT entpackt, sondern als Archiv ausgeschlossen. Eine aktive Def ist nie eine
  Suite.
- **import-pack** (CLI `ImportPack`): matched per `jbe_testid`, CREATE wenn neu, sonst UPDATE. Schreibt
  `jbe_title`, `jbe_definitionjson` (testId->id-Mapping, `userStories`/`documentation` raus, eigene Spalten),
  `jbe_tags`, `jbe_userstories`, `jbe_documentation`. **`jbe_enabled` wird NUR bei CREATE gesetzt** (neuer Test
  default-aktiv), bei UPDATE unangetastet - das Pack trägt keinen enabled-Status, ein Import darf den Aktiv-Status
  auf DEV nicht überschreiben (sonst reaktiviert er bewusst deaktivierte Tests, Befund S32). `jbe_category`
  (OptionSet) und `jbe_description` (existiert nicht auf der Entity) werden bewusst NICHT geschrieben
  (Stolperfallen S14/S16). Damit ist `sync-docs` der Alt-Weg (import-pack bringt die Doku gleich mit).
- **Zwei Doku-Vokabulare (Befund S31/S32):** Defs im generischen Format (`d365tc_format: generic`) dokumentieren unter
  `Zweck/Datenkonstellation/Vorbedingungen/Ablauf/Erwartetes Ergebnis` (= `FullSections`); Bridge-Defs
  (`fg-testtool`) füllen nur `Beschreibung` fachlich (Vorbereitung/Schritte/Erwartung sind Platzhalter
  "Siehe JSON unten"). Deshalb nutzt `BuildDocumentation` die Whitelist `DocSections` = `FullSections` +
  `Beschreibung` (additiv, bruchfrei für DSGVO); der compact-`report` fällt für die Zweck-Spalte auf
  `Beschreibung` zurück. `FullSections` (full-Report-Detail, E3/E4) bleibt das DSGVO-Vokabular.
- **Live verifiziert (2026-06-19):** ein Projekt-Baum im Format `fg-testtool` -> 120 Testfälle (18 archivierte
  raus, 0 Warnings/Errors); import-pack 120 UPDATE, 120/120 mit `jbe_documentation`, 5 deaktivierte bleiben
  deaktiviert.

### E6: `inventory` (Management-Inventar + Lauf-Trend)

Erzeugt aus einem `--defs`-Baum eine Management-Übersicht über die MD-Definitionen: statisches Inventar
(Status-/Domänen-Rollup + Tabelle pro Domäne, adoptiert von einem projektseitigen `Build-Inventar.ps1`), angereichert
um die Lauf-Sicht (letzter Lauf + Stabilitäts-Trend) aus der Frontmatter-`ergebnis_historie` (B4 Suite-Trend).

```
inventory --defs <dir> [--out <datei>] [--name <titel>]    # offline, kein Dataverse
```

- Core `InventoryBuilder` (rein/testbar), CLI `Inventory` (nutzt den build-pack-Walk, listet daher auch
  archivierte/Entwurf-Defs mit Status-Spalte - das Inventar zeigt die ganze Landschaft). Output Markdown
  (`--out` UTF-8 ohne BOM, sonst stdout).
- **Inhalt:** Gesamtzahl, Status-Verteilung, Domänen-Verteilung, Lauf-Status-Verteilung (`d365tc_lauf_status`,
  nur wenn mindestens eine Def einen trägt); pro Domäne Tabelle ID/Titel/**Stufe**/Status/Suite-Tags/Ticket/
  **Verantw.**/**Min**/**Quelle**/**Letzter Lauf**/**Trend**/**Datei** (Markdown-Link auf den relativen Def-Pfad).
- **Build-Inventar.ps1-Parität (S35):** `Stufe`/`Verantwortlich`/`geschaetzt_min`/`Quelle`/Datei-Link additiv
  ergänzt (leer wo Frontmatter-Feld fehlt, generisch optionale Metadaten nach ADR-0002). Damit ist `inventory`
  ein vollwertiger Ersatz für das projektseitige `Build-Inventar.ps1` (ADR-0007-Diff S35).
- **Trend** aus `ergebnis_historie` (wiederverwendet `MarkdownResultSync.ParseHistory`): "Nx PASS" (stabil) /
  "N Läufe, Xx nicht-PASS" (instabil) / "-" (keine Historie). "Letzter Lauf" = neuester Eintrag nach Datum
  (Ergebnis-Teil längen-gekappt gegen Freitext-Historie-Notizen).
- **Additiv (Befund S32):** Die `ergebnis_historie`-Trend-Quelle existiert nur im DSGVO-Baum
  (`d365tc_format: generic`, 24/24), nicht im Bridge-Baum (fg-testtool, 0/138). Trend-Spalten leer wo keine
  Historie; das Inventar-Grundgerüst (id/titel/status/domaene/suite_tags) ist in beiden Bäumen vorhanden.
- **Live verifiziert (2026-06-19):** Bridge-Baum 138 Defs, Status-/Domänen-Rollup deckungsgleich mit
  `Build-Inventar.ps1`; DSGVO-Baum 24 Defs mit befülltem Trend.

### E5: `sync-zephyr` (Result-Upload nach Zephyr Scale)

Lädt die Ergebnisse eines `jbe_testrun` nach **Zephyr Scale Data Center (ATM 1.0, NICHT Cloud-v2)** hoch.
Modell (Entscheidung 24): ein `jbe_testrun` -> ein **NEUER** Zephyr Test-Run (Cycle) pro Lauf, dann Bulk-Upload
der Ergebnisse. **Schreibt nach Zephyr -> freigabepflichtig.**

```
sync-zephyr --run <guid> --defs <dir> --org <url> <dataverse-auth> \
            --server <jira-url> --project <KEY> --zephyr-pat <pat> \
            [--env "<exakter Zephyr-environment-Name>"] [--script-results] [--cycle-name] [--config]
```

- Core `ZephyrResultBuilder` (rein/testbar): Outcome-Mapping (`Passed->Pass`, `Failed->Fail`, **`Error->Fail`**
  nicht `Blocked`, `Skipped->Not Executed`) + Payload-Bau (`BuildTestRunPayload` Cycle-Anlage mit `items[]`,
  `BuildResultsPayload` Bulk-Array). CLI `ZephyrSync`: liest die Lauf-Ergebnisse aus Dataverse
  (`ResultSync.LoadResultsFromRun`), walked die Defs für `zephyr_key` (`LoadZephyrKeys`, Frontmatter), matched
  per `testId == frontmatter.id` (`BuildPlan`), und macht zwei HTTP-POSTs:
  `POST /rest/atm/1.0/testrun` (Cycle) dann `POST /rest/atm/1.0/testrun/{key}/testresults` (Bulk).
- **Auth: `--zephyr-pat` als Bearer**, die CLI bleibt secret-agnostisch (wie `--client-secret`). Ein
  projektseitiger PowerShell-Wrapper holt den PAT aus dem Secret-Store des Projekts und reicht ihn durch;
  Dataverse-Lesen via Application-User-Secret (ENV). Der Secret-Store bleibt SSOT (kein C#-Binding,
  OE-9-konsistent).
- **Robust ohne `zephyr_key`:** Tests ohne `zephyr_key` im Frontmatter werden übersprungen + gemeldet; sind
  null Tests gemappt, wird **kein** Cycle angelegt (kein Leer-Upload). JSON-Body UTF-8 ohne BOM (Mojibake-Falle).
- **Datengrundlage:** Ein sinnvoller Live-Lauf braucht eine Def mit real existierendem Zephyr-Test-Case-Key
  (`<KEY>-T####`) im Frontmatter `zephyr_key`.
- **Phase 1 (umgesetzt S33, live durch S34):** Gesamt-Status pro Testfall (status, environment, executionTime
  ms, comment aus `jbe_errormessage`). Live verifiziert gegen eine Zephyr-Scale-DC-Instanz (neuer Cycle,
  Execution gegen einen Wegwerf-Test-Case).
- **Phase 2 (Code S35, opt-in `--script-results`, Default aus; Entscheidung 25):** per-Step `scriptResults[]`.
  Fachlicher Befund: Zephyr-`scriptResults[]` korrespondieren zu den **manuell im Zephyr-Test-Case definierten
  Script-Steps**, die D365TC-`jbe_teststep`-Records sind die **automatischen Ausführungs-Steps** (CreateRecord/
  Assert/Wait/Cleanup) - andere Zahl/Semantik. Ein 1:1-Index-Mapping ist nur korrekt, wenn der Zephyr-Case die
  D365TC-Steps spiegelt; bei groben manuellen Scripts im Zephyr-Case systematisch nicht gegeben. Daher
  Default Gesamt-Status; per-Step nur mit explizitem Flag. CLI `ZephyrSync.LoadStepResultsByTestId` lädt die
  Steps per LinkEntity-Query (`jbe_teststep` -> `jbe_testrunresult`, Filter `jbe_testrunid`, sortiert nach
  `jbe_stepnumber`), gruppiert nach `jbe_testid`, mappt auf `ScriptResultInput` (0-basierter Lauf-Index, Status
  aus `jbe_stepstatus`, Comment aus `jbe_errormessage`); `BuildPlan` hängt sie optional an.
  - **Live-Befund S35 (gegen einen Wegwerf-Test-Case mit 3 Script-Steps):** Die SmartBear-
    "nur-erster-Schritt"-Stolperfalle ist für die geprüfte ATM-1.0-DC-Instanz **widerlegt** - mehrere
    scriptResults landen. **Aber:** Zephyr matcht die gesendeten scriptResults per `index` auf die im
    Zephyr-Test-Case definierten Script-Steps; **überzählige (index >= Script-Step-Zahl) werden still
    verworfen**. Im Smoke wurden 5 gesendet (5 `jbe_teststep`), nur 3 landeten (3 Script-Steps des Test-Cases).
    Bestätigt: per-Step nur sinnvoll, wenn der Zephyr-Case die D365TC-Steps strukturell spiegelt (-> opt-in).
    `jbe_errormessage` trägt auch Erfolgs-Step-Messages (z.B. `OK: address1_city = "..."`), nicht nur Fehler.
- **environment-Feld (Befund S34, Fix S35, Entscheidung 26):** `environment` ist im Zephyr-Projekt
  **konfiguriert, NICHT Freitext** (die zulässigen Namen je Projekt liefert
  `GET /rest/atm/1.0/environments?projectKey=<KEY>`). Ein nicht passender Wert wirft HTTP 400. Die CLI hat
  **keinen** `DeriveEnv`-Default mehr für `sync-zephyr`: ohne `--env` wird das Feld **weggelassen** (sauberer
  als ein geratener Wert). Das org-URL -> environment-Mapping lebt im projektseitigen Wrapper, der `--env`
  setzt. `DeriveEnv` bleibt für `sync-results`/`report` (dort nur Freitext-Tag in der `ergebnis_historie`): es
  nimmt das Suffix des ersten Host-Labels nach dem letzten Bindestrich, sofern es ein Umgebungswort (`dev`,
  `test`, `prod`, `uat`, `qa`, `stag`, `sandbox`) enthält (`contoso-dev` ergibt `dev`, `contoso-uat` ergibt
  `uat`), sonst `test`/`prod`/`dev` nach Vorkommen im Host, sonst `unknown`.
- **Live-Verifikation:** Phase 1 durch (S34), **Phase 2 durch (S35)**: neuer Cycle mit Execution,
  3 scriptResults gelandet (Index-Matching belegt). E5/ADR-0008 code- + live-vollständig.
- **Audit-Kommentar (OE-10, S36):** Der Ergebnis-`comment` pro Testfall trägt jetzt einen Beleg
  "Angelegt: <Records mit Primary-Name> / Geprüft: <Asserts> / Fehler: ..." statt nur der Fehlermeldung (die bei
  PASS leer war, weshalb die Zephyr-Executions generisch wirkten). Quelle: `jbe_trackedrecords` +
  `jbe_assertionresults` auf dem `jbe_testrunresult`, gerendert vom pure `ZephyrResultBuilder.BuildAuditComment`
  (1500-Zeichen-Cap). **Die Primary-Namen werden nur im CLI-`run`-Pfad erfasst** (Flag `CaptureRecordNames`,
  sandbox-sicher; der CRUD-Trigger-/Plugin-Pfad lässt sie leer, Sandbox-Wächter-Regel), die **Asserts** kommen
  in jedem Pfad. Für aussagekräftige Belege den Run über CLI `run` erzeugen, nicht über den jbe_testrun-Trigger.
- **Beleg trägt ID UND Name; Zephyr-Lauf behält die Datensätze (OE 2026-06-23):**
  Der Audit-Kommentar rendert pro Record `<entity> "<Name>" [<alias>] (<id>)` -- die GUID-`id` ist Teil des
  Belegs (`ZephyrResultBuilder.BuildAuditComment`), damit der Zephyr-Eintrag eindeutig auf den Dataverse-Record
  verweist. Der Produkt-Default ist Cleanup (`keeprecords=false`); ein Zephyr-Lauf muss aber **mit Behalten**
  laufen (`run --keep-records`, ein projektseitiger Lauf-Wrapper kann das per Default setzen), sonst zeigt der Beleg auf einen
  gelöschten Record und ist wertlos. **Marker bewusst als Workflow-Konvention** (Lauf mit Behalten, dann
  `sync-zephyr`), keine Auto-Erkennung -- `zephyr_key`-getriebenes Auto-Behalten bleibt optionaler Backlog-Komfort.
  420 Unit-Tests grün, kein Deploy. Detail OE-10.

**Falle (alle Doku/Reporting-Commands):** Laufen über die published DLL (`backend/publish/cli`).
Nach Core/CLI-Änderung erst `dotnet publish D365TestCenter.Cli -c Release -o
backend/publish/cli`, sonst läuft eine alte DLL.

### E5-Pendant: DevOps-Reporting (lizenzfrei)

Wo ein Projekt per Test-Lizenz nach Zephyr Scale synct (E5), kann ein Projekt mit Azure DevOps **ohne
Test-Lizenz** die Testausführung in einen **Work-Item-Kommentar** schreiben. Gleiches Muster wie E5:
Run-Ergebnis aus Dataverse -> Beleg -> externe Senke.

**Status: GEBAUT + live-verifiziert (2026-06-25, ADR 2026-06-24 2347).**
Der generische CLI-Befehl `sync-devops` postet einen `jbe_testrun` als Azure-DevOps-Work-Item-Kommentar
(HTML-Audit-Fragment). End-to-End belegt an einem Run mit 4 Tests (3 PASS/1 ERROR): KPIs +
Angelegt/Geprüft/Fehler + Umlaute per GET-Roundtrip geprüft.

```
sync-devops --run <guid> --org <dataverse-url> [Auth] --work-item <id>
            --devops-org <org> --devops-project <proj> --devops-token <aad-bearer>
            [--devops-url https://dev.azure.com] [--env DEV] [--config standard]
```
Exit 0 = gepostet, 1 = nichts/Fehler, 2 = Argumentfehler. `--work-item` immer explizit (keine
Auto-Verknüpfung). Ein projektseitiger Wrapper holt den Token und ruft den Befehl auf.

**Architektur (ADR 2026-06-24):** geteilter `AuditCommentBuilder` im Core (neutrales `AuditModel` +
`RenderPlain` für sync-zephyr + `RenderHtml` für sync-devops). `ZephyrResultBuilder.BuildAuditComment`
delegiert daran (Signatur + Plain-Verhalten unverändert, Zephyr-Tests grün). `DevOpsCommentBuilder` baut den
KPI-Header + Per-Test-HTML-Block (escaped, 30k-Cap). Cli `DevOpsSync` (Run -> Fragment -> POST). 615 Tests grün.

**Auth:** Der DevOps-Bearer ist ein **AAD-Token, kein PAT** (OAuth mit Resource
`499b84ac-1321-427f-aa17-267ca6975798` = Azure DevOps, Device-Code-Flow, stille Erneuerung per Refresh-Token).
Azure DevOps REST akzeptiert ihn als `Authorization: Bearer <AAD-Token>` (belegt: HTTP 200, aud `499b84ac-…`,
scp `user_impersonation`). Ein projektseitiger Wrapper holt ihn aus dem Secret-Store des Projekts und reicht ihn
per `--devops-token` durch; welcher Store das System für Azure DevOps führt, steht in der `PROJEKT-KONTEXT.md`.

**C# vs. PowerShell:** Die DevOps-REST-PS-Stolperfallen (unten) gelten NUR für PS-Inline-Posts. Der
**C#-HttpClient-Pfad** (`DevOpsSync`) liest den Body als String und parst mit `JObject.Parse` -- kein Workaround
nötig, secret-agnostisch (`--devops-token`). HTML-Escaping der dynamischen Werte ist im `RenderHtml` Pflicht.

**Befunde aus der Entstehung (PoC 2026-06-24, weiter gültig):**

- **Senke:** `POST .../wit/workItems/{id}/comments?api-version=7.1-preview.4`, Body `{text: "<html>"}` als
  **UTF-8-Bytes** + `-ContentType 'application/json; charset=utf-8'` (sonst Umlaut-Mojibake).
- **Markdown wird NICHT gerendert.** Die Comment-API speichert Kommentare immer als `format=html`; ein
  `format: "markdown"` im Body wird ignoriert, Markdown-Syntax (`**fett**`) bleibt wörtlicher Text.
  **-> HTML-Fragment ist der Weg**, nicht `report --format md`.
- **Fragment vs. Volldokument:** `report --format html` erzeugt ein **volles HTML-Dokument**
  (`<!DOCTYPE><html><head><style>...`), das ein DevOps-Kommentar NICHT sauber rendert. `MarkdownToHtml.Convert`
  liefert zwar ein Fragment, aber **ohne Headings**. Der `<body>`-Extrakt ist nutzbar, verliert ohne `<style>`
  aber die Optik der CSS-Klassen (`badge`/`testblock`/`error-msg`). Deshalb das eigene Fragment im
  `BuildAuditComment`-Stil (nur `<b>`/`<br>`/`&nbsp;`), das als HTML gerendert wird (Tags erhalten, `"` zu
  `&quot;`, Umlaute sauber per GET-Roundtrip).
- **`report` zeigt KEINEN Ist-Beleg (angelegte Records / Asserts).** `RunResultLoader` lädt zwar
  `jbe_trackedrecords`/`jbe_assertionresults`, aber `MarkdownReportGenerator.BuildModel` überträgt sie **nicht**
  in `ReportItem`, und `RenderFullDetail` rendert nur Outcome + Soll-Doku-Sektionen + `ErrorMessage`. Die
  OE-10-Audit-Daten nutzt allein der `AuditCommentBuilder` (sync-zephyr, sync-devops).
- **CLI-`report` braucht lokale `.md`-Defs für Titel und Zweck.** `ReportBuilder` zieht Titel/Doku-Sektionen
  ausschließlich aus lokalen `*.md`-Defs (Match `testId == Frontmatter-id`). Ein Projekt nur mit JSON-Packs
  bekommt einen titellosen Report (Log `(keine Definition/Doku für …)`); `jbe_title` liest nur der
  Custom-API-Pfad (`DataverseReportSource.LoadDocs`). `--defs` muss nur existieren (sonst
  `DirectoryNotFoundException`); ein Verzeichnis ohne passende `.md` liefert einen validen, untitled Report.
  `--out` schreibt **UTF-8 ohne BOM** mit korrekten Umlauten.
- **Verknüpfung Lauf <-> Work-Item:** Zephyr nutzt `zephyr_key` (Frontmatter); DevOps braucht die numerische
  Work-Item-Id. `jbe_userstories` am Testfall wäre die Quelle, ist in der Praxis aber oft leer; die
  Ziel-Work-Item-Id wird daher **immer explizit** übergeben.
- **Encoding-Weg in PowerShell:** Fragment aus der **Datei** als UTF-8 lesen (umgeht die BOM-Falle der
  PS-String-Literale) + Body als `[Text.Encoding]::UTF8.GetBytes()` + `-ContentType 'application/json;
  charset=utf-8'`. Roundtrip: Kommentar per GET zurückholen, in utf8-BOM-Datei schreiben, Umlaut-`Contains`
  prüfen.
- **DevOps-REST-Stolperfallen (PowerShell 7):** (1) Antwort-Content-Type `application/json; charset=utf-8;
  api-version=7.1` hat ein Suffix, das `Invoke-RestMethod`s Auto-JSON-Parsing **umgeht** (Antwort kommt als
  String) -> `Invoke-WebRequest` + manuell `ConvertFrom-Json`. (2) `ConvertFrom-Json` wirft bei DevOps-Antworten
  "property whose name is an empty string" -> **`-AsHashtable`** nötig. (3) Work-Item anlegen:
  `POST .../workitems/$<Typ>` (Typ mit `$`-Prefix in der URL, z.B. `$Issue`), Body JSON-Patch-**Array**
  `[{op:add,path:/fields/System.Title,...}]`, ContentType `application/json-patch+json`. (4) Kommentar löschen:
  `DELETE .../comments/{id}`.

---
