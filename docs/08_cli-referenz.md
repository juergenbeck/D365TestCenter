# CLI-Referenz (D365TestCenter.Cli)

Die `D365TestCenter.Cli` ist der headless Aufrufer der C#-Core-Engine (ADR-0003 Single-Engine):
dieselbe Engine, die im Plugin (Sync-Custom-API + Async-CRUD-Trigger) und hinter dem WebResource-Client
läuft, als Kommandozeilenwerkzeug für CI/CD, Projekt-Owner und den Doku-/Reporting-Lebenszyklus (ADR-0008).
Anders als der Async-Plugin-Pfad unterliegt die CLI keinem Sandbox-2-Minuten-Limit und kann UI-Tests
(Playwright, ADR-0006) ausführen.

> Der WebResource-Client und das [Entwickler-Handbuch](handbuch/README.md) decken die Arbeit **in der App**
> ab (Testfälle anlegen, starten, auswerten). Die CLI ist das Werkzeug für alles, was über die App
> hinausgeht: CI/CD-Läufe, der Doku-Round-Trip und der Report-/Inventar-Export. Den zusammenhängenden
> Doku-Lebenszyklus beschreibt [09_doku-und-reporting.md](09_doku-und-reporting.md).

## Build

Die CLI wird aus dem Backend-Solution gebaut:

```
dotnet publish backend/D365TestCenter.Cli -c Release -o backend/publish/cli
```

Aufruf danach (plattformneutral):

```
dotnet backend/publish/cli/D365TestCenter.Cli.dll <command> [optionen]
```

## Auth-Optionen (gemeinsam)

Alle Commands, die Dataverse lesen oder schreiben, teilen denselben Auth-Satz. Genau eine Variante wählen:

| Option | Beschreibung |
|---|---|
| `--org <url>` | Dataverse-Org-URL, z.B. `https://contoso.crm4.dynamics.com` |
| `--client-id` / `--client-secret` / `--tenant-id` | Service-Principal (Client Credentials), für CI/CD |
| `--token <bearer>` | Bereits vorhandenes Bearer-Token (Alternative zu Client Credentials) |
| `--interactive` | Interaktiver Browser-Login (lokal) |
| `--config <profil>` | Konfigurationsprofil: `standard` (Default) oder `markant` (CDH Field Governance) |

**Offline-Commands ohne Auth:** `build-pack` und `inventory` lesen nur lokale Markdown-Dateien.
`validate` ist offline, aktiviert aber mit optionalem `--org` die metadata-aware Regeln.

Die CLI ist **secret-agnostisch**: Secrets werden als Flags übergeben, nicht aus einem Vault gelesen.
In CI/CD setzt ein Wrapper-Skript die Secrets aus seiner Quelle und reicht sie durch.

---

## Ausführung

### `run` — Testfälle ausführen

Führt Testfälle gegen eine Dataverse-Umgebung aus (liest `jbe_testcase`, legt `jbe_testrun` an, schreibt
`jbe_testrunresult` + `jbe_teststep`).

```
run --org <url> <auth> [--filter <pattern>] [--keep-records] [--config <profil>]
    [--browser-state <pfad>] [--browser-headed] [--browser-locale <loc>] [--browser-trace <pfad>]
    [--sync-defs <dir>] [--env <label>]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--filter` | `*` | Auswahl: Wildcard auf Test-ID, `tag:...`, `category:...` oder Komma-getrennte IDs |
| `--keep-records` | `false` | Testdaten nach dem Lauf behalten (sonst Cleanup) |
| `--browser-state` | - | Playwright-Storage-State-JSON; aktiviert `BrowserAction`-Steps (UI-Tests). Ohne den Schalter werden BrowserAction-Steps übersprungen (wie im Plugin-Pfad). |
| `--browser-headed` | `false` | Browser sichtbar (Default headless) |
| `--browser-locale` | `de-DE` | Browser-Locale für UI-Tests |
| `--browser-trace` | - | Pfad für Playwright-`trace.zip` |
| `--sync-defs` | - | Nach dem Lauf die Ergebnisse in die Markdown-Definitionen unter diesem Verzeichnis zurückschreiben (E2-Round-Trip, Komfort-Variante zu `sync-results`) |
| `--env` | aus `--org`-Host abgeleitet | Env-Label für den zurückgeschriebenen Historien-Eintrag |

```
dotnet D365TestCenter.Cli.dll run --org https://contoso.crm4.dynamics.com \
    --client-id <id> --client-secret <secret> --tenant-id <tenant> \
    --filter "tag:smoke" --config markant
```

### `status` — letzte Läufe anzeigen

Zeigt die jüngsten `jbe_testrun`-Records mit KPI-Bilanz.

```
status --org <url> <auth> [--top <n>] [--config <profil>]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--top` | `5` | Anzahl der anzuzeigenden Läufe |

---

## Validierung

### `validate` — Pack statisch prüfen

Statische Validierung eines Pack-JSON gegen Schema- und Muster-Fehler (OE-6 PackValidator), ohne Dataverse-Aufruf.
Mit optionalem `--org` werden zusätzlich metadata-aware Regeln aktiv (Entity-/Feld-Existenz gegen die Ziel-Umgebung).

```
validate --pack <pfad> [--strict] [--org <url> <auth>]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--pack` | (Pflicht) | Pfad zu einem Pack-JSON (Suite-Wrapper mit `testCases` oder ein einzelner Testfall) |
| `--strict` | `false` | Exit-Code 1 auch bei reinen Warnungen (sonst nur Errors) |
| `--org` | - | Optionale Org-URL; aktiviert die metadata-aware Checks (`ENTITY_UNKNOWN`, `FIELD_UNKNOWN`, ...) |

Statische Regeln (Auswahl): `ACTION_UNKNOWN`, `FILTER_FIELD_NOT_LOGICAL`, `FILTER_OPERATOR_VALUE_NULL`,
`EXECUTEREQUEST_MISSING_NAME`, `LOOKUP_BIND_FORMAT`, `STATECODE_STATUSCODE_HINT`, `ASSERT_TARGET_INCOMPLETE`,
`STEP_NUMBER_DUPLICATE`, `ALIAS_UNDEFINED`. Eignet sich für IDE-Lint und CI-Pre-Commit.

---

## Doku-Lebenszyklus (ADR-0008)

Diese Commands schließen den Kreis zwischen der Markdown-Definition (Single Source of Truth), Dataverse und
den Berichten. Überblick: [09_doku-und-reporting.md](09_doku-und-reporting.md).

### `build-pack` — Suite-Pack aus Definitionen bauen (offline)

Erzeugt aus den Markdown-Test-Definitionen ein importierbares Suite-Pack, Dokumentation inklusive (B5).
Archivierte und Entwurf-Definitionen werden übersprungen.

```
build-pack --defs <dir> --out <datei> [--name <name>] [--strict]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--defs` | (Pflicht) | Verzeichnis mit den Markdown-Definitionen (rekursiv) |
| `--out` | (Pflicht) | Ausgabedatei des Pack-JSON (UTF-8 ohne BOM) |
| `--name` | Verzeichnisname | Pack-Name im Pack |
| `--strict` | `false` | Exit-Code 1 auch bei Warnungen |

Das Pack hat die Form `{ name, testCases: [ { testId, title, category, tags, userStories, documentation, steps } ] }`.
Die `documentation` stammt aus den fachlichen Pflicht-Sektionen der Definition (mit integriertem Lint).

### `import-pack` — Pack nach Dataverse importieren

Schreibt ein Suite-Pack idempotent nach `jbe_testcase` (CREATE+UPDATE per `jbe_testid`), inklusive
`jbe_documentation` (B5).

```
import-pack --org <url> <auth> --pack <datei> [--config <profil>]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--pack` | (Pflicht) | Pfad zum Pack-JSON (von `build-pack` erzeugt) |

### `sync-docs` — Doku durchreichen

Schreibt die fachliche Doku aus den Markdown-Definitionen nach `jbe_testcase.jbe_documentation`
(Matching `testId == frontmatter.id`), damit der HTML-Client sie im Doku-Tab rendert (E1).

```
sync-docs --org <url> <auth> --defs <dir> [--config <profil>]
```

### `sync-results` — Ergebnisse zurückschreiben (Round-Trip)

Schreibt die Ergebnisse eines abgeschlossenen Laufs zurück in die Markdown-Definitionen
(`ergebnis_historie` im Frontmatter ist SSOT; Body-Tabelle und README werden daraus gerendert) (E2).

```
sync-results --org <url> <auth> --run <guid> --defs <dir> [--env <label>] [--config <profil>]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--run` | (Pflicht) | `jbe_testrun`-GUID, dessen Ergebnisse synchronisiert werden |
| `--defs` | (Pflicht) | Verzeichnis mit den zu aktualisierenden Definitionen |
| `--env` | aus `--org`-Host abgeleitet | Env-Label für den Historien-Eintrag |

### `report` — Durchführungsbericht erzeugen

Erzeugt aus genau einem Lauf und den lokalen Definitionen einen Suite-Durchführungsbericht (E3/E4).
Rein lesend gegen Dataverse.

```
report --org <url> <auth> --run <guid> --defs <dir> [--out <datei>]
       [--detail compact|full] [--format md|html|pdf] [--env <label>] [--config <profil>]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--run` | (Pflicht) | `jbe_testrun`-GUID, über den berichtet wird |
| `--defs` | (Pflicht) | Verzeichnis mit den Markdown-Definitionen (Doku-Quelle) |
| `--out` | stdout | Ausgabedatei; ohne `--out` geht der Bericht nach stdout |
| `--detail` | `full` | `compact` (eine Tabelle) oder `full` (pro Test ein Abschnitt mit allen Pflicht-Sektionen) |
| `--format` | `md` | `md`, `html` oder `pdf`. `pdf` braucht `--out` und Chromium (`playwright install chromium`) |

### `sync-zephyr` — Ergebnisse nach Zephyr Scale hochladen

Lädt die Ergebnisse eines Laufs nach **Zephyr Scale Data Center (ATM 1.0)** hoch: legt pro Lauf einen neuen
Test-Run (Cycle) an und lädt die Ergebnisse als Bulk hoch (E5). **Schreibt in ein externes Test-Management-System.**

```
sync-zephyr --org <url> <auth> --run <guid> --defs <dir>
            --server <jira-url> --project <key> --zephyr-pat <pat>
            [--env "<exakter environment-Name>"] [--script-results] [--cycle-name <name>] [--config <profil>]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--run` | (Pflicht) | `jbe_testrun`-GUID, dessen Ergebnisse hochgeladen werden |
| `--defs` | (Pflicht) | Verzeichnis mit den Definitionen; Quelle des `zephyr_key`-Frontmatters (Matching `testId == id`) |
| `--server` | (Pflicht) | Jira/Zephyr-Server-Basis-URL (die ATM-1.0-Pfade werden angehängt) |
| `--project` | (Pflicht) | Jira/Zephyr-Projekt-Key |
| `--zephyr-pat` | (Pflicht) | Jira Personal Access Token (Bearer); von einem Wrapper aus dem Secret-Store gespeist |
| `--env` | (weggelassen) | Exakter, **im Zephyr-Projekt konfigurierter** environment-Name (kein Freitext). Ohne gültigen Wert wird das Feld weggelassen; ein nicht passender Wert wird mit HTTP 400 abgelehnt. |
| `--script-results` | aus | Lädt zusätzlich per-Step-Ergebnisse (`scriptResults[]`). Nur sinnvoll, wenn die Script-Steps des Zephyr-Test-Cases die Ausführungs-Steps spiegeln (siehe Hinweise). |
| `--cycle-name` | aus env + Datum + Run-Id | Name des anzulegenden Cycle |

**Hinweise / Stolperfallen:**
- **Outcome-Mapping:** `Passed -> Pass`, `Failed -> Fail`, `Error -> Fail`, `Skipped -> Not Executed`.
- **Tests ohne `zephyr_key`** werden übersprungen und gemeldet; sind null Tests gemappt, wird kein Cycle angelegt.
- **`--script-results` (per-Step):** Zephyr matcht die gesendeten `scriptResults` per **Index** auf die im
  Test-Case definierten Script-Steps; überzählige Einträge werden still verworfen. Die Ausführungs-Steps des
  Test Centers (CreateRecord/Assert/Wait/Cleanup) entsprechen nur dann den Zephyr-Script-Steps, wenn der
  Test-Case sie strukturell spiegelt - deshalb ist das Flag opt-in. Ohne das Flag wird der Gesamt-Status pro
  Testfall übertragen.
- **JSON-Body UTF-8 ohne BOM** (Umlaut-/Mojibake-Sicherheit).

---

## Inventar

### `inventory` — Management-Inventar erzeugen (offline)

Erzeugt aus einem Definitions-Baum eine Management-Übersicht: Status- und Domänen-Rollup plus eine Tabelle
pro Domäne, angereichert um Lauf-Trend aus der `ergebnis_historie` (E6). Rein lesend, kein Dataverse.

```
inventory --defs <dir> [--out <datei>] [--name <titel>]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--defs` | (Pflicht) | Verzeichnis mit den Markdown-Definitionen (rekursiv) |
| `--out` | stdout | Ausgabedatei (UTF-8 ohne BOM); ohne `--out` nach stdout |
| `--name` | `Inventar Integrationstests` | Titel des Inventars |

Tabellen-Spalten pro Domäne: ID, Titel, Stufe, Status, Suite-Tags, Ticket, Verantwortlich, geschätzte Minuten,
Quelle, letzter Lauf, Trend, Datei-Link. Spalten, für die eine Definition kein Frontmatter-Feld trägt, bleiben leer.

---

## UI-Tests

### `ui-setup` — Playwright-Storage-State anlegen

Erzeugt per interaktivem Login einen Playwright-Storage-State für UI-Tests (`BrowserAction` im `run`-Pfad,
ADR-0006). Harter DEV-Guard (nur Nicht-Produktiv-Umgebungen).

```
ui-setup --org <url> [--output <pfad>]
```

| Option | Default | Beschreibung |
|---|---|---|
| `--output` | `auth/<org>-<user>.json` | Ausgabepfad des Storage-State-JSON (in `.gitignore` halten) |

Der erzeugte State wird danach an `run --browser-state <pfad>` übergeben.

---

## Exit-Codes

| Code | Bedeutung |
|---|---|
| `0` | Erfolg (bzw. Lauf ohne Fehler; bei `validate`/`build-pack` keine Errors) |
| `1` | Fehler bzw. Findings (mit `--strict` auch Warnungen), oder nichts hochgeladen/erzeugt |
| `2` | Ungültige Argumente (z.B. fehlende Pflicht-Option, ungültige GUID) |
