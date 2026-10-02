# Deployment-Handbuch: Integration Test Center

Schritt-für-Schritt-Anleitung zur Einrichtung des Integration Test Centers in einer Dynamics 365 / Dataverse-Umgebung.

## Voraussetzungen

- **Dynamics 365 / Dataverse-Umgebung** (z.B. DEV oder TEST) und ein Benutzer mit der Rolle System Customizer
  oder System Administrator für den Import
- **Power Platform CLI** (`pac`), angemeldet an der Zielumgebung (`pac auth create --environment <url>`)
- **Browser** mit aktivem CRM-Login (für die Web Resource)

Die Einrichtung läuft ausschließlich über den Import der Solution `D365TestCenter`. Die Solution bringt alle
Bestandteile mit: Publisher `JBE` (Prefix `jbe`), die Tabellen `jbe_testcase`, `jbe_testrun`, `jbe_testrunresult`,
`jbe_teststep` und `jbe_testchunk`, die globalen OptionSets, Formulare, Ansichten, die App `jbe_D365TestCenter`,
die Web Resources (`jbe_/testcenter.html`, `jbe_/handbuch.html`, Demo-Packs), das Plugin-Paket
`jbe_D365TestCenter`, die Custom APIs und die Plugin-Steps. Tabellen, Web Resources oder Custom APIs werden
nicht von Hand oder per Skript angelegt.

## Schritt 1: Solution bauen und importieren

Aus dem Quellstand `solution/src` ein Paket bauen und in die Zielumgebung importieren:

```powershell
pac solution pack --zipfile solution/out/D365TestCenter.zip --folder solution/src --packagetype Unmanaged
pac solution import --path solution/out/D365TestCenter.zip --publish-changes --activate-plugins
```

`solution/out/` ist per `.gitignore` ausgenommen. Für Test- und Produktivumgebungen gilt der Weg über eine
managed Solution aus der Entwicklungsumgebung (Export mit `pac solution export --managed true`, Import mit
`--stage-and-upgrade`); Details im Skill `d365-test-center`, Referenz `deployment.md`.

Änderungen an der Oberfläche entstehen in `webresource/d365testcenter.html`; vor dem Paketbau die Datei nach
`solution/src/WebResources/jbe_/testcenter.html` übernehmen, beide Stände sind byte-gleich zu halten.

## Schritt 2: Recovery-Flow anlegen (optional)

Der zeitgesteuerte Flow, der hängengebliebene Teilläufe über die Custom API `jbe_RecoverStaleChunks` wieder
anstößt, ist nicht Teil der Solution, weil er eine umgebungsspezifische Connection Reference braucht. Er wird je
Umgebung angelegt:

```powershell
pwsh ./scripts/Create-RecurrenceFlow.ps1 -OrgUrl https://<umgebung>.crm4.dynamics.com `
    -ClientId <id> -ClientSecret <secret> -TenantId <tenant> `
    -ConnectionReferenceLogicalName <connection-reference-der-umgebung>
```

## Schritt 3: Security Roles

### Benötigte Rechte

| Entity | Tester | Entwickler | Admin |
|--------|--------|------------|-------|
| `jbe_testcase` | Lesen | Lesen, Schreiben, Erstellen | Vollzugriff |
| `jbe_testrun` | Lesen, Erstellen | Lesen, Schreiben, Erstellen | Vollzugriff |
| `jbe_testrunresult` | Lesen | Lesen, Schreiben, Erstellen | Vollzugriff |
| Getestete Entities (z.B. Contact, Account) | Lesen, Schreiben, Erstellen | Lesen, Schreiben, Erstellen, Löschen | Vollzugriff |

### Rollen-Empfehlung

**JBE Tester:**
- `jbe_testcase`: Lesen (Organization)
- `jbe_testrun`: Lesen, Erstellen, Schreiben (Organization)
- `jbe_testrunresult`: Lesen (Organization)
- Kann Testläufe starten und Ergebnisse einsehen, aber keine Testfälle bearbeiten.

**ITT Entwickler:**
- Alle Rechte des Testers, zusätzlich:
- `jbe_testcase`: Erstellen, Schreiben, Löschen (Organization)
- `jbe_testrunresult`: Erstellen, Schreiben (Organization)
- Kann Testfälle erstellen, bearbeiten und Ergebnisse korrigieren.

**ITT Admin:**
- Vollzugriff auf alle drei Entities.
- Zusätzlich: Zugriff auf Custom API-Registrierungen, Web Resource Management, Solution-Export.

## Schritt 4: Testen

### 4.1 URL aufrufen

```
https://<umgebung>.crm4.dynamics.com/WebResources/jbe_/testcenter.html
```

### 4.2 Demo-Modus prüfen

- Ist die Solution noch nicht importiert, erscheint ein Verbindungsfehler mit dem Button "Demo-Ansicht laden".
- Im Demo-Modus sind alle Funktionen verfügbar, Daten werden lokal simuliert.

### 4.3 Ersten Testlauf starten

1. Navigiere zu **Testlauf**.
2. Wähle **Alle aktiven Tests**.
3. Klicke **Testlauf starten**.
4. Beobachte den Live-Fortschritt: Progress-Bar, Passed/Failed-Zähler, Log.
5. Nach Abschluss: Pie-Chart, Einzelergebnisse, Flow-Visualisierung pro Test.

### 4.4 Testfall erstellen (Verifizierung)

1. Navigiere zu **Testfälle**.
2. Klicke **+ Neuer Testfall**.
3. Fülle Metadaten aus (ID, Titel, Kategorie).
4. Gib eine JSON-Definition ein (oder nutze den Beispiel-Testfall aus Pack 4).
5. Prüfe die **Flow-Visualisierung** (Tab-Wechsel im Editor).
6. Klicke **Speichern**.

### 4.5 Metadaten-Explorer testen

- Navigiere zu **Metadaten**.
- Tabs: Tabellen, OptionSets, Custom APIs.
- Prüfe, ob die drei ITT-Entities und deren Attribute korrekt angezeigt werden.
- Nutze die "Snippet"-Funktion, um JSON-Vorlagen für Testfälle zu generieren.

### 4.6 Demo-Modus

Wenn die Web Resource außerhalb von Dynamics 365 geöffnet wird (oder die CRM-API nicht erreichbar ist), aktiviert sich automatisch der **Demo-Modus**:

- Alle Daten werden lokal simuliert (kein API-Zugriff nötig).
- Ein gelbes Banner "Demo-Modus: Alle Daten sind simuliert" wird angezeigt.
- Der Pack-Selector im Header ermöglicht den Wechsel zwischen Demo-Paketen.
- Testläufe werden mit simulierten Ergebnissen ausgeführt (ca. 90% Pass-Rate, 1.5-2.5s pro Test).

## Anhang: Schema-Referenz

Die Solution legt die Tabellen beim Import an. Die folgenden Tabellen beschreiben die drei Kern-Tabellen;
`jbe_teststep` (ein Datensatz je Schritt) und `jbe_testchunk` (Teilläufe im Worker-Modell) kommen hinzu.

### Tabelle 1: Testfall (`jbe_testcase`)

| Eigenschaft | Wert |
|-------------|------|
| Anzeigename | Testfall |
| Schema-Name | `jbe_testcase` |
| Pluralname | Testfälle |
| EntitySet-Name | `jbe_testcases` |
| Primäres Namensfeld | `jbe_name` (AutoNumber empfohlen: `TC-{SEQNUM:8}`) |

**Felder:**

| Schema-Name | Anzeigename | Typ | Pflicht | Beschreibung |
|-------------|-------------|-----|---------|--------------|
| `jbe_testid` | Test ID | String (100) | Pflicht (ApplicationRequired) | Eindeutige Test-ID (z.B. TC01, BTC01) |
| `jbe_title` | Titel | String (300) | Pflicht (ApplicationRequired) | Beschreibender Titel |
| `jbe_category` | Kategorie | OptionSet (Picklist) | Empfohlen | Testkategorie (siehe OptionSets) |
| `jbe_tags` | Tags | String (500) | Optional | Kommagetrennte Tags |
| `jbe_userstories` | User Stories | String (500) | Optional | Kommagetrennte Jira-Keys |
| `jbe_enabled` | Aktiv | Boolean | Optional | Testfall aktiv/deaktiviert (Standard: true) |
| `jbe_definitionjson` | Definition (JSON) | Memo (Multiline) | Optional | JSON-Definition des Testfalls |

**Alternate Key:** `jbe_testid` (ermöglicht Upsert bei Import).

### Tabelle 2: Testlauf (`jbe_testrun`)

| Eigenschaft | Wert |
|-------------|------|
| Anzeigename | Testlauf |
| Schema-Name | `jbe_testrun` |
| Pluralname | Testläufe |
| EntitySet-Name | `jbe_testruns` |
| Primäres Namensfeld | `jbe_name` (AutoNumber empfohlen: `RUN-{SEQNUM:8}`) |

**Felder:**

| Schema-Name | Anzeigename | Typ | Pflicht | Beschreibung |
|-------------|-------------|-----|---------|--------------|
| `jbe_teststatus` | Status | OptionSet (Picklist) | Optional | Teststatus (siehe OptionSets) |
| `jbe_passed` | Bestanden | Integer | Optional | Anzahl bestandener Tests |
| `jbe_failed` | Fehlgeschlagen | Integer | Optional | Anzahl fehlgeschlagener Tests |
| `jbe_total` | Gesamt | Integer | Optional | Gesamtanzahl Tests im Lauf |
| `jbe_startedon` | Gestartet | DateTime | Optional | Startzeitpunkt |
| `jbe_completedon` | Abgeschlossen | DateTime | Optional | Endzeitpunkt |
| `jbe_testcasefilter` | Testfall-Filter | String (500) | Optional | Angewendeter Filter (z.B. `"*"`, `"story:PROJ-1234"`) |
| `jbe_testsummary` | Zusammenfassung | Memo (Multiline) | Optional | Textuelle Zusammenfassung |
| `jbe_fulllog` | Vollständiges Log | Memo (Multiline) | Optional | Komplettes Ausführungslog |
| `jbe_testresult_json` | Ergebnis (JSON) | Memo (Multiline) | Optional | Strukturiertes Ergebnis als JSON |

### Tabelle 3: Testergebnis (`jbe_testrunresult`)

| Eigenschaft | Wert |
|-------------|------|
| Anzeigename | Testergebnis |
| Schema-Name | `jbe_testrunresult` |
| Pluralname | Testergebnisse |
| EntitySet-Name | `jbe_testrunresults` |
| Primäres Namensfeld | `jbe_name` (AutoNumber empfohlen: `RES-{SEQNUM:8}`) |

**Felder:**

| Schema-Name | Anzeigename | Typ | Pflicht | Beschreibung |
|-------------|-------------|-----|---------|--------------|
| `jbe_testrunid` | Testlauf | Lookup auf `jbe_testrun` | Optional | Zugehöriger Testlauf |
| `jbe_testcaseid` | Testfall | Lookup auf `jbe_testcase` | Optional | Zugehöriger Testfall |
| `jbe_testid` | Test ID | String (100) | Optional | Test-ID (redundant für schnelle Abfragen) |
| `jbe_outcome` | Ergebnis | OptionSet (Picklist) | Optional | Testergebnis (siehe OptionSets) |
| `jbe_durationms` | Dauer (ms) | Integer | Optional | Ausführungsdauer in Millisekunden |
| `jbe_errormessage` | Fehlermeldung | Memo (Multiline) | Optional | Fehlerbeschreibung bei Failed/Error |
| `jbe_assertionresults` | Assertion-Ergebnisse | Memo (Multiline) | Optional | JSON-Array der einzelnen Assertion-Ergebnisse |

### OptionSets

Die Solution bringt folgende globale OptionSets mit (Wertebereich ab `105710000`, Quelle: `solution/src/OptionSets/*.xml`):

**jbe_teststatus (Teststatus):**

| Wert | Label |
|------|-------|
| 105710000 | Ausstehend |
| 105710001 | Läuft |
| 105710002 | Abgeschlossen |
| 105710003 | Fehler |
| 105710004 | Aufteilung läuft |

**jbe_testoutcome (Testergebnis):**

| Wert | Label |
|------|-------|
| 105710000 | Passed |
| 105710001 | Failed |
| 105710002 | Skipped |
| 105710003 | Error |

Die UI-Konstante `CONFIG.optionSets.outcomeNotImpl` (`105710004`) hat im OptionSet keine Entsprechung.

Die OptionSet-Werte stehen fest in `solution/src/OptionSets/*.xml` (Bereich 10571xxxx) und werden beim
Import unverändert übernommen. `Solution.xml` nennt für den Publisher `JBE` den Options-Präfix `39507`;
der gilt nur für Optionen, die jemand später im Maker Portal neu anlegt, und ändert keinen bestehenden Wert.

**jbe_testcategory (Testkategorie):**

| Wert | Label |
|------|-------|
| 105710000 | Update Source |
| 105710001 | Create Source |
| 105710002 | Delete Source |
| 105710003 | Multi-Source |
| 105710004 | Merge |
| 105710005 | Custom API |
| 105710006 | Config |
| 105710007 | End-to-End |
| 105710008 | Error Handling |

**Wichtig:** Die OptionSet-Werte müssen exakt mit den im JavaScript definierten Werten übereinstimmen. Bei abweichenden Werten zeigt die UI falsche Labels an.
