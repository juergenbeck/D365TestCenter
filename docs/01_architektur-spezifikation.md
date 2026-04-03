# Integration Test Center: Architektur-Spezifikation

## 1. Überblick

### Zweck

Das Integration Test Center ist eine in Dynamics 365 integrierte Webanwendung zum Erstellen, Ausführen und Auswerten von Integrationstests. Es ermöglicht die Verwaltung von Testfällen, das Starten von Testläufen (einzeln, nach Kategorie, Tag oder User Story), die Echtzeit-Überwachung der Ausführung sowie die visuelle Auswertung mit Charts, Verlaufsanalyse und Flow-Visualisierung.

### Einsatzgebiet

- **Dynamics 365 / Dataverse:** Die Anwendung läuft als Web Resource innerhalb des CRM und nutzt die bestehende Session-Authentifizierung. Kein separater OAuth-Flow oder Login erforderlich.
- **Beliebige Umgebung:** Über den konfigurierbaren CONFIG-Block kann die Anwendung an jeden Dataverse-Publisher (Prefix, Entities, Fields, OptionSets) angepasst werden.
- **Standalone-Betrieb:** Außerhalb von Dynamics 365 aktiviert sich automatisch ein Demo-Modus mit simulierten Daten.

### Technische Eckdaten

- **Single-File HTML:** Alles (CSS, HTML, JavaScript) in einer einzigen Datei, ca. 3.900 Zeilen.
- **Kein Framework:** Kein React, Angular, Vue oder ähnliches. Reines Vanilla-JavaScript mit DOM-Manipulation.
- **Kein Build-Tool:** Kein Webpack, Vite, npm oder Transpiler. Die Datei ist direkt deploybar.
- **Sprache:** Deutsch (UI-Labels, Hilfe-Texte), Code-Kommentare teilweise Englisch.

### Herstellerunabhängigkeit

Der CONFIG-Block (Zeile 798 ff.) definiert sämtliche Entity-Namen, Feldnamen und OptionSet-Werte. Durch Anpassung dieses Blocks kann das Test Center auf einen anderen Publisher-Prefix umgestellt werden, ohne den restlichen Code zu ändern.

---

## 2. Architektur-Diagramm (ASCII)

```
+------------------------------------------------------------------+
|                        BROWSER (HTML)                            |
+------------------------------------------------------------------+
|                                                                  |
|  +------------------+   +------------------+   +---------------+ |
|  |   UI-Layer       |   |   CSS-Variablen  |   | HTML-Struktur | |
|  | (DOM-Rendering)  |   |   (Dark Theme)   |   | (Header, Nav) | |
|  +--------+---------+   +------------------+   +---------------+ |
|           |                                                      |
|  +--------v---------+                                            |
|  |     Router        |  hashchange-Event                         |
|  | handleRoute()     |  #dashboard, #cases, #run, #runs, ...    |
|  +--------+---------+                                            |
|           |                                                      |
|  +--------v-----------------------------------------+            |
|  |                    Views                          |            |
|  | renderDashboard()    renderTestCases()            |            |
|  | renderTestCaseEditor() renderNewRun()             |            |
|  | renderRunDetail()    renderRunHistory()            |            |
|  | renderTestHistory()  renderUserStories()           |            |
|  | renderStoryDetail()  renderMetadata()              |            |
|  | renderHelp()                                      |            |
|  +--------+-----------------------------------------+            |
|           |                                                      |
|  +--------v---------+                                            |
|  |    API-Layer      |                                           |
|  |    (API-Objekt)   |                                           |
|  +--------+---------+                                            |
|           |                                                      |
|     +-----+------+                                               |
|     |            |                                                |
|  +--v---+   +----v-------+                                       |
|  | Live |   | Demo-Modus |                                       |
|  +--+---+   +----+-------+                                       |
|     |            |                                                |
|  +--v--------+  +v-----------+                                   |
|  | Dataverse  |  | MockAPI    |                                   |
|  | Web API    |  | DemoPacks  |                                   |
|  | /api/data/ |  | localStorage|                                  |
|  | v9.2       |  | Store      |                                   |
|  +-----------+  +------------+                                   |
|                                                                  |
+------------------------------------------------------------------+

Datenfluss:
  User -- hashchange --> Router -- View-Funktion --> API-Aufrufe
       --> Dataverse Web API (Live) oder MockAPI/DemoPacks (Demo)
       --> JSON-Antwort --> DOM-Rendering --> User sieht Ergebnis
```

---

## 3. Modul-Übersicht

### 3.1 CONFIG

**Zweck:** Zentrale Konfiguration aller Dataverse-Schema-Namen und OptionSet-Werte.

**Properties:**
- `prefix` (String): Publisher-Prefix, Standard `"itt"`
- `optionSetBase` (Number): Basiswert für OptionSets, Standard `100000000`
- `entities` (Object): Logische Entity-Namen (`testcase`, `testrun`, `testrunresult`)
- `fields` (Object): Logische Feldnamen für alle drei Entities (z.B. `testid`, `title`, `category`, `tags`, `enabled`, `definition_json`, `userstories`, `teststatus`, `passed`, `failed`, `total`, `started_on`, `completed_on`, `testsummary`, `fulllog`, `testcasefilter`, `testresult_json`, `testrunid`, `testcaseid`, `outcome`, `duration_ms`, `error_message`, `assertion_results`)
- `optionSets` (Object): Numerische Werte für TestStatus (Planned/Running/Completed/Error), Outcome (Passed/Failed/Error/Skipped/NotImpl), Category (UpdateSource bis ErrorInjection)
- `actions` (Object): Custom Action-Name (`runTests`)

**Abhängigkeiten:** Keine. Wird von allen anderen Modulen referenziert.

### 3.2 API

**Zweck:** Abstraktion der Dataverse Web API. Im Live-Modus HTTP-Aufrufe, im Demo-Modus werden die Methoden durch MockAPI ersetzt.

**Öffentliche Methoden:**
- `fetch(url, options)`: Basis-HTTP-Aufruf gegen `/api/data/v9.2` mit OData-Headern
- `getMany(entity, query)`: Collection-GET mit OData-Query, gibt `value`-Array zurück
- `getOne(entity, id, select)`: Einzeldatensatz per GUID
- `create(entity, data)`: POST mit `Prefer: return=representation`
- `update(entity, id, data)`: PATCH
- `del(entity, id)`: DELETE
- `executeAction(actionName, params)`: Custom Action per POST ausführen
- `fetchXml(entity, xml)`: FetchXML-Abfrage
- `count(entity, filter)`: `$count=true&$top=0` für Zählung

**Properties:**
- `base` (String): API-Basispfad, Standard `"/api/data/v9.2"`

**Abhängigkeiten:** `window.fetch`

### 3.3 State

**Zweck:** Zentraler In-Memory-Zustand der Anwendung.

**Properties:**
- `currentView` (String): Aktive View, Standard `"dashboard"`
- `testCases` (Array): Geladene Testfälle
- `testRuns` (Array): Geladene Testläufe
- `currentRunId` (String|null): Aktuell betrachteter Testlauf
- `pollingTimer` (Number|null): Timer-ID für Live-Polling
- `prefillStory` (String|null): Vorausgefüllte User Story für Testlauf-Start
- `storyMap` (Object|null): Gruppierung von Testfällen nach User Story

**Abhängigkeiten:** Keine.

### 3.4 Charts

**Zweck:** SVG-basierte Chart-Generierung ohne externe Bibliotheken.

**Öffentliche Methoden:**
- `pie(data, size)`: Kreisdiagramm mit Legende. `data` ist Array von `{label, value, color}`. Gibt SVG+HTML als String zurück.
- `bar(data, width, height)`: Balkendiagramm. `data` ist Array von `{label, value, color}`.
- `timeline(data, width)`: Horizontales Balkendiagramm für Testdauer. `data` ist Array von `{label, duration, passed}`.

**Abhängigkeiten:** Keine.

### 3.5 MockAPI

**Zweck:** Fängt alle API-Aufrufe ab und liefert Daten aus DemoPacks, wenn die Anwendung außerhalb von Dynamics 365 läuft.

**Öffentliche Methoden:**
- `fetch(url, options)`: Haupt-Handler, parst URL und Method, delegiert an interne Handler
- `switchPack(packName)`: Wechselt das aktive Demo-Paket, leert MetaCache, rendert aktuelle View neu
- `getMany(entity, query)`, `getOne(entity, id, select)`, `create(entity, data)`, `update(entity, id, data)`, `del(entity, id)`, `executeAction(actionName, params)`, `fetchXml(entity, xml)`, `count(entity, filter)`: Wrapper-Methoden, die das API-Interface spiegeln

**Interne Methoden:**
- `_init()`, `_reinitStore()`: Initialisierung des In-Memory-Stores aus dem aktiven DemoPack
- `_uuid()`: GUID-Generierung
- `_parseFilter(filterStr, collection)`: OData-`$filter`-Parsing (contains, eq, Lookup, Mehrfachbedingungen)
- `_applyOrderBy(collection, orderBy)`: OData-`$orderby`-Sortierung
- `_getCollection(entitySet)`, `_getIdField(entitySet)`: Mapping EntitySet auf Store-Collection
- `_getOne()`, `_getMany()`, `_create()`, `_update()`, `_delete()`: CRUD-Operationen auf Store
- `_simulateTestRun(run, runsColl)`: Simuliert progressiven Testlauf mit Timer (1,5-2,5s pro Test, 90% Pass-Rate)
- `_handleEntityDefinitions(url)`: Metadata-API-Simulation für Entities und Attribute
- `_handleOptionSets(url)`: Metadata-API-Simulation für GlobalOptionSetDefinitions

**Properties:**
- `currentPack` (String): Aktives Pack, Standard `"standard"`
- `_store` (Object|null): In-Memory-Datenspeicher mit `testcases`, `testruns`, `testrunresults`

**Abhängigkeiten:** `DemoPacks`, `MetaCache`, `_sharedIttMeta`, `handleRoute()`, `updatePackIndicator()`

### 3.6 DemoPacks

**Zweck:** Modulares System mit austauschbaren Demo-Datenpaketen für verschiedene fachliche Szenarien.

**Vorhandene Pakete:**

| Pack-Key    | Name                                  | Farbe    | Testfälle |
|-------------|---------------------------------------|----------|-----------|
| `standard`  | Standard CRM (Sales & Service)        | `#42a5f5`| 8         |
| `field-governance` | Field Governance (Bridge)        | `#ab47bc`| 16        |
| `membership`| Membership & Rollen (Platform)        | `#ff9800`| 6         |
| `empty`     | Leere Vorlage                         | `#78909c`| 1         |

**Struktur pro Pack:**
- `name` (String): Anzeigename
- `description` (String): Kurzbeschreibung
- `color` (String): Akzentfarbe
- `testCases` (Array): Demo-Testfälle mit vollständiger JSON-Definition
- `testRuns` (Getter/Array): Historische Testläufe
- `testRunResults` (Getter/Array): Einzelergebnisse
- `userStories` (Array): Zugeordnete User Stories `{key, title}`
- `metadata` (Object): Simulierte Dataverse-Metadaten (`entities`, `attributes`, `optionsets`)

**Abhängigkeiten:** `_makeDate()`, `CONFIG`

### 3.7 HelpContent

**Zweck:** Integriertes Hilfesystem mit durchsuchbaren Hilfe-Seiten, gruppiert nach Themen und Rollen.

**Öffentliche Methoden:**
- `getGroups()`: Gibt Hilfe-Sektionen gruppiert nach `group`-Eigenschaft zurück
- `search(query)`: Volltextsuche in Titel und HTML-Inhalt aller Sektionen

**Properties:**
- `sections` (Array): Hilfe-Sektionen mit je `group`, `id`, `title`, `roles`, `content` (HTML-String)

**Vorhandene Gruppen und Sektionen:**
- **Erste Schritte:** Überblick (`intro`), Schnelleinstieg (`quickstart`)
- **Testfälle:** Testfall erstellen (`tc-create`), JSON-Struktur (`tc-json`), Platzhalter (`tc-placeholders`), User Stories (`tc-userstories`)
- **Testläufe:** Testlauf starten (`run-start`), Ergebnisse (`run-results`), Flow-Visualisierung (`flow-viz`)
- **FAQ:** Allgemein (`faq-general`), Testfälle (`faq-testcases`), Fehleranalyse (`faq-errors`)
- **Deployment:** Voraussetzungen (`deploy-prereq`), Installation (`deploy-install`)

**Rollen:** `tester`, `dev`, `admin` (als Badge pro Sektion angezeigt)

**Abhängigkeiten:** Keine.

### 3.8 FlowViz (FlowVisualizer)

**Zweck:** SVG-basierte Visualisierung von Testfall-Definitionen als Pipeline-Diagramm mit drei Phasen.

**Öffentliche Methoden:**
- `parseDefinition(json, results)`: Parst eine Testfall-JSON-Definition in ein Array von Flow-Nodes. Optional werden Assertion-Ergebnisse (passed/failed) aus `results` zugeordnet.
- `renderSvg(nodes, containerId)`: Rendert die Nodes als SVG-Diagramm mit Legende und Detail-Panel in den angegebenen Container.
- `showDetail(nodeId)`: Zeigt Detail-Panel für einen angeklickten Node (Entity, Alias, Felder, Operator, Erwartet/Tatsächlich).

**Interne Methoden:**
- `escSvg(s)`: HTML-Entity-Escaping für SVG-Texte

**Properties:**
- `colors` (Object): Farbschema pro Knotentyp (`create`, `update`, `wait`, `api`, `assert_passed`, `assert_failed`, `assert_pending`, `precondition`)
- `_currentNodes` (Object): Aktuell gerenderte Nodes für Detail-Zugriff

**Knotentypen:**

| Typ              | Farbe   | Icon | Beschreibung        |
|------------------|---------|------|---------------------|
| `precondition`   | Indigo  | ACC/CON/CS | Vorbedingung   |
| `create`         | Blau    | +    | Datensatz erstellen |
| `update`         | Orange  | U    | Datensatz ändern    |
| `wait`           | Grau    | W    | Wartezeit           |
| `api`            | Violett | API  | Custom API-Aufruf   |
| `assert_passed`  | Grün    | OK   | Assertion bestanden |
| `assert_failed`  | Rot     | X    | Assertion fehlgeschlagen |
| `assert_pending` | Grau    | ?    | Assertion noch offen |

**Abhängigkeiten:** DOM (`$()`, `escapeHtml()`)

### 3.9 MetaCache (MetadataExplorer)

**Zweck:** Cache für Metadaten-API-Abfragen (Entities, Attributes, OptionSets, Custom APIs), um Mehrfachabfragen zu vermeiden.

**Properties:**
- `entities` (Array|null): Gecachte Entity-Definitionen
- `attributes` (Object): Gecachte Attribute pro Entity-Name
- `optionSets` (Array|null): Gecachte GlobalOptionSetDefinitions
- `customApis` (Array|null): Gecachte Custom API-Definitionen

**Abhängigkeiten:** Wird durch `MockAPI.switchPack()` geleert.

### 3.10 Hilfsfunktionen

| Funktion              | Zweck                                                       |
|-----------------------|-------------------------------------------------------------|
| `$(sel)`              | `document.querySelector` Kurzform                           |
| `$$(sel)`             | `document.querySelectorAll` Kurzform                        |
| `el(tag, attrs, ...children)` | Element-Factory mit Event-Binding                  |
| `render(html)`        | Setzt `innerHTML` von `#app-content`                        |
| `formatDuration(ms)`  | Millisekunden in lesbare Dauer (ms/s/m)                     |
| `formatDate(d)`       | ISO-Datum in deutsches Format (DD.MM HH:MM)                 |
| `outcomeBadge(outcome)` | Badge-HTML für Testergebnis                               |
| `statusBadge(status)` | Badge-HTML für Testlauf-Status                              |
| `stopPolling()`       | Beendet aktives Live-Polling                                |
| `escapeHtml(s)`       | HTML-Entities escapen                                       |
| `copyToClipboard(text, btn)` | Text in Zwischenablage, visuelles Feedback            |
| `_makeDate(daysAgo, hours, minutes)` | Hilfsfunktion für Demo-Datumserzeugung      |
| `updatePackIndicator()` | Aktualisiert die Pack-Anzeige im Header                   |
| `activateDemoMode()`  | Ersetzt alle API-Methoden durch MockAPI, zeigt Banner       |
| `loadUserInfo()`      | Lädt aktuellen Benutzer über WhoAmI                         |
| `filterCases()`       | Filtert Testfall-Tabelle nach Suche/Kategorie/Story/Aktiv   |
| `filterRuns()`        | Filtert Testlauf-Tabelle nach Suche/Status/Ergebnis         |
| `prefillStoryRun(key)` | Setzt Story-Key für vorausgefüllten Testlauf-Start         |

---

## 4. Router und Navigation

### Hash-basiertes Routing

Die Navigation erfolgt über URL-Hashes. Der Router lauscht auf `hashchange`-Events und delegiert an die zuständige View-Funktion.

**Initialisierung:** `initRouter()` registriert den Event-Listener und ruft sofort `handleRoute()` auf.

### Route-Tabelle

| Hash                 | View-Funktion            | Beschreibung                           |
|----------------------|--------------------------|----------------------------------------|
| `#dashboard`         | `renderDashboard()`      | KPIs, letzte Läufe, Charts             |
| `#cases`             | `renderTestCases()`      | Testfall-Liste mit Filtern             |
| `#cases/{id}`        | `renderTestCaseEditor(id)` | Testfall bearbeiten oder neu erstellen |
| `#cases/new`         | `renderTestCaseEditor("new")` | Neuen Testfall anlegen            |
| `#run/new`           | `renderNewRun()`         | Testlauf konfigurieren und starten     |
| `#run/{id}`          | `renderRunDetail(id)`    | Live-Ansicht eines laufenden/fertigen Testlaufs |
| `#runs`              | `renderRunHistory()`     | Testlauf-Verlauf mit Filtern           |
| `#history/{testId}`  | `renderTestHistory(testId)` | Verlauf eines einzelnen Testfalls   |
| `#stories`           | `renderUserStories()`    | User Stories mit Testabdeckung         |
| `#stories/{key}`     | `renderStoryDetail(key)` | Detail zu einer User Story             |
| `#metadata`          | `renderMetadata()`       | Metadaten-Explorer (Entities)          |
| `#metadata/entities` | `renderMetadata("entities")` | Entity-Liste                      |
| `#metadata/entities/{name}` | `renderMetadata("entities", name)` | Entity-Attribute     |
| `#metadata/optionsets` | `renderMetadata("optionsets")` | OptionSet-Liste                |
| `#metadata/customapis` | `renderMetadata("customapis")` | Custom API-Liste               |
| `#help`              | `renderHelp()`           | Hilfe-System (Überblick)               |
| `#help/{sectionId}`  | `renderHelp(sectionId)`  | Hilfe-Sektion                          |

### Lifecycle

1. User klickt Navigation oder Link (ändert `location.hash`)
2. `hashchange`-Event feuert
3. `handleRoute()` wird aufgerufen:
   - Parst Hash in `view`, `param`, `param2`
   - Setzt aktiven Nav-Link (CSS-Klasse `active`)
   - Aktualisiert `State.currentView`
   - Stoppt laufendes Polling (`stopPolling()`)
   - Ruft die zuständige View-Funktion auf
4. View-Funktion:
   - Rendert initiales HTML-Gerüst über `render()`
   - Führt asynchrone API-Aufrufe aus
   - Füllt DOM-Bereiche mit Daten
   - Registriert Event-Listener für Interaktion

---

## 5. Views im Detail

### 5.1 Dashboard (`renderDashboard`)

**Render-Funktion:** `renderDashboard()`

**API-Aufrufe:**
- `API.count("jbe_testcases", "jbe_enabled eq true")`: Anzahl aktiver Testfälle
- `API.getMany("jbe_testruns", ...)`: Letzte 20 Testläufe
- `API.getMany("jbe_testrunresults", ...)`: Fehlgeschlagene Tests (outcome eq 100000001), letzte 10

**DOM-Elemente:**
- KPI-Grid: 4 Tiles (Testfälle, Pass Rate, Letzte Läufe, Offene Fehler)
- Grid-2: Letzte Testläufe (Tabelle) und Ergebnisverteilung (Pie-Chart)
- Fehlgeschlagene Tests: Tabelle mit Test-ID, Datum, Fehlermeldung

**User-Interaktionen:**
- Klick auf Testlauf-Zeile: Navigation zu `#run/{id}`
- Klick auf "Verlauf" bei fehlendem Test: Navigation zu `#history/{testId}`
- Fallback-Button "Demo-Ansicht laden" bei Verbindungsfehler

**Verlinkte Views:** `#run/{id}`, `#history/{testId}`, `#run/new`

### 5.2 Testfälle (`renderTestCases`)

**Render-Funktion:** `renderTestCases()`

**API-Aufrufe:**
- `API.getMany("jbe_testcases", ...)`: Alle Testfälle (max 500), sortiert nach TestId

**DOM-Elemente:**
- Toolbar: Suchfeld, Kategorie-Dropdown, User-Story-Filter, "Nur aktive"-Checkbox, "Neuer Testfall"-Button
- Tabelle: ID, Titel, Kategorie, Tags, User Stories, Status, Bearbeiten-Link

**User-Interaktionen:**
- Freitextsuche (filtert auf ID, Titel, Tags, User Stories)
- Kategorie-Filter (Dropdown mit 9 Kategorien)
- User-Story-Filter (Textfeld)
- "Nur aktive"-Checkbox
- Klick auf Tags: Navigation zu `#stories/{key}`
- "Bearbeiten"-Button: Navigation zu `#cases/{id}`
- "Neuer Testfall"-Button: Navigation zu `#cases/new`

**Verlinkte Views:** `#cases/{id}`, `#cases/new`, `#stories/{key}`

### 5.3 Testfall-Editor (`renderTestCaseEditor`)

**Render-Funktion:** `renderTestCaseEditor(id)`

**API-Aufrufe:**
- `API.getOne("jbe_testcases", id)`: Bestehenden Testfall laden (bei id != "new")
- `API.create("jbe_testcases", data)` oder `API.update("jbe_testcases", id, data)`: Speichern

**DOM-Elemente:**
- Split-View (Links/Rechts):
  - Links: Metadaten-Formular (Test ID, Titel, Kategorie, Tags, User Stories, Aktiv-Checkbox)
  - Rechts: Tab-Ansicht mit JSON-Editor und Flow-Visualisierung
- JSON-Editor mit Live-Validierung und Tab-Key-Unterstützung
- Speichern-Button

**User-Interaktionen:**
- Formularfelder bearbeiten
- JSON direkt im Textarea editieren (Live-Validierung zeigt "JSON gültig" oder Fehlermeldung)
- Tab-Wechsel zwischen JSON-Editor und Flow-Visualisierung
- Speichern (Create oder Update je nach Kontext)

**Verlinkte Views:** `#cases` (Zurück-Link)

### 5.4 Neuer Testlauf (`renderNewRun`)

**Render-Funktion:** `renderNewRun()`

**API-Aufrufe:**
- `API.create("jbe_testruns", {...})`: Testlauf erstellen
- `API.executeAction("jbe_RunIntegrationTests", {...})`: Custom Action starten

**DOM-Elemente:**
- Testauswahl-Dropdown (Alle, Kategorie, Tag, User Story, einzelne IDs)
- Filter-Detail-Eingabefeld (kontextabhängig)
- Pre-Flight-Diagnostics Checkbox
- Timeout-Eingabe (Standard: 120s)
- Start-Button

**User-Interaktionen:**
- Testauswahl-Typ wählen (zeigt/versteckt Detail-Feld)
- Filter-Wert eingeben
- Testlauf starten (Button wird deaktiviert, navigiert zu `#run/{id}`)
- Vorausfüllung über `State.prefillStory` (kommt von User Stories View)

**Verlinkte Views:** `#run/{id}` (nach Start)

### 5.5 Testlauf-Detail (`renderRunDetail`)

**Render-Funktion:** `renderRunDetail(runId)`

**API-Aufrufe:**
- `API.getOne("jbe_testruns", runId, ...)`: Testlauf-Daten laden
- `API.getMany("jbe_testrunresults", ...)`: Einzelergebnisse für diesen Lauf
- `API.getMany("jbe_testcases", ...)` und `API.getMany("jbe_testrunresults", ...)`: Für Flow-Anzeige pro Test

**DOM-Elemente:**
- Header: Status-Badge, Filter, Zeitraum
- Progress-Bar (prozentual, grün bei Erfolg, rot bei Fehlern)
- Passed/Failed/Gesamt-Zähler
- Grid-2: Pie-Chart und Zusammenfassung
- Einzelergebnisse-Tabelle: Test-ID, Ergebnis-Badge, Dauer, Fehler, "Flow anzeigen"-Button
- Log-Output (farbcodiert: grün=BESTANDEN, rot=FEHLGESCHLAGEN, gelb=FEHLER)
- Flow-Visualisierung (SVG) für ausgewählten Test

**User-Interaktionen:**
- "Flow anzeigen"-Button pro Testergebnis: Lädt Testfall-Definition und rendert SVG-Flow
- Klick auf Flow-Knoten: Zeigt Detail-Panel (Felder, Erwartet/Tatsächlich)
- Bei laufendem Testlauf: automatisches Polling alle 3 Sekunden

**Verlinkte Views:** `#runs` (Zurück-Link)

### 5.6 Testlauf-Verlauf (`renderRunHistory`)

**Render-Funktion:** `renderRunHistory()`

**API-Aufrufe:**
- `API.getMany("jbe_testruns", ...)`: Letzte 100 Testläufe

**DOM-Elemente:**
- Toolbar: Freitextsuche, Status-Dropdown, Ergebnis-Dropdown (fehlerfrei/mit Fehlern)
- Tabelle: Datum, Status, Filter, Passed, Failed, Gesamt, Dauer, Details-Link

**User-Interaktionen:**
- Filter auf Freitext (durchsucht testcasefilter)
- Filter auf Status (Geplant, Läuft, Abgeschlossen, Fehler)
- Filter auf Ergebnis (fehlerfrei oder mit Fehlern)
- Details-Link pro Zeile

**Verlinkte Views:** `#run/{id}`

### 5.7 Testverlauf (`renderTestHistory`)

**Render-Funktion:** `renderTestHistory(testId)`

**API-Aufrufe:**
- `API.getMany("jbe_testrunresults", ...)`: Letzte 20 Ergebnisse für eine Test-ID

**DOM-Elemente:**
- Grid-2: Balkendiagramm (Dauer über Zeit, farbcodiert) und Detail-Tabelle
- Tabelle: Datum, Ergebnis-Badge, Dauer, Fehlermeldung

**User-Interaktionen:** Keine speziellen (reine Auswertungsansicht)

**Verlinkte Views:** `#cases` (Zurück-Link)

### 5.8 User Stories (`renderUserStories`)

**Render-Funktion:** `renderUserStories()`

**API-Aufrufe:**
- `API.getMany("jbe_testcases", ...)`: Alle Testfälle laden, dann User Stories daraus extrahieren

**DOM-Elemente:**
- Toolbar: Suchfeld für Story-ID
- Tabelle: User Story Key, Anzahl Testfälle, aktive Tests, Abdeckungs-Fortschrittsbalken, "Testen"- und "Details"-Buttons

**User-Interaktionen:**
- Suche nach Story-ID
- "Testen"-Button: Setzt `State.prefillStory` und navigiert zu `#run/new`
- "Details"-Button: Navigation zu `#stories/{key}`

**Verlinkte Views:** `#stories/{key}`, `#run/new`

### 5.9 Story-Detail (`renderStoryDetail`)

**Render-Funktion:** `renderStoryDetail(storyKey)`

**API-Aufrufe:**
- `API.getMany("jbe_testcases", ...)`: Testfälle mit `contains(jbe_userstories, '{storyKey}')`
- `API.getMany("jbe_testrunresults", ...)`: Letzte 100 Ergebnisse, gefiltert auf relevante TestIDs

**DOM-Elemente:**
- Grid-2: Zugeordnete Testfälle (Tabelle) und Testergebnisse (KPI-Zeile + Tabelle)
- Story-KPIs: Passed/Failed/Andere/Gesamt/Story-Abdeckung in Prozent
- Button: "Alle Tests dieser Story starten"

**User-Interaktionen:**
- Bearbeiten-Link pro Testfall
- "Alle Tests starten"-Button

**Verlinkte Views:** `#stories` (Zurück), `#cases/{id}`, `#run/new`

### 5.10 Metadaten-Explorer (`renderMetadata`)

**Render-Funktion:** `renderMetadata(tab, entityId)`

**Tabs:**
- **Tabellen:** Entity-Liste mit Suche, "Nur Custom"- und "Nur jbe_"-Filter. Pro Entity: Schema-Name (kopierfähig), Anzeigename, Custom-Badge, EntitySet, Attribute-Link.
- **Attribute:** (nach Klick auf Entity) Attribut-Liste mit Suche und "Nur Custom"-Filter. Pro Attribut: Schema-Name (kopierfähig), Anzeigename, Typ-Badge, Pflicht-Badge, Snippet-Button. Bei PicklistType: OptionSet-Werte inline.
- **OptionSets:** GlobalOptionSetDefinitions mit Suche und "Nur jbe_"-Filter. Pro OptionSet: "Werte"-Button zeigt Werte-Tabelle.
- **Custom APIs:** Custom API-Liste mit Suche und "Nur jbe_"-Filter. Pro API: Name (kopierfähig), Anzeigename, Function/Action-Badge, Beschreibung, Snippet-Button.

**API-Aufrufe:**
- `API.fetch("/EntityDefinitions?...")`: Entity-Liste
- `API.fetch("/EntityDefinitions(LogicalName='...')/Attributes?...")`: Attribute einer Entity
- `API.fetch("/.../PicklistAttributeMetadata?...")`: OptionSet-Werte für Picklist-Attribute
- `API.fetch("/GlobalOptionSetDefinitions?...")`: Globale OptionSets
- `API.getMany("customapis", ...)`: Custom APIs

**Snippet-Generierung:** Pro Attribut werden JSON-Snippets für Create-Step, Update-Step und Assertion generiert. Pro Custom API wird ein ExecuteAction-Snippet erzeugt. Alle Snippets sind über "Kopieren"-Button in die Zwischenablage kopierbar.

**Verlinkte Views:** `#metadata/entities`, `#metadata/entities/{name}`, `#metadata/optionsets`, `#metadata/customapis`

### 5.11 Hilfe (`renderHelp`)

**Render-Funktion:** `renderHelp(sectionId)`

**DOM-Elemente:**
- Help-Layout (Grid 260px/1fr):
  - Sidebar: Suchfeld, gruppierte Navigation mit Rollen-Badges
  - Content: HTML-Inhalt der aktiven Sektion

**User-Interaktionen:**
- Volltextsuche filtert Sidebar-Navigation (blendet nicht passende Einträge aus)
- Klick auf Sektion lädt Inhalt

**Verlinkte Views:** `#help/{sectionId}`

---

## 6. CSS-Architektur

### CSS-Variablen (Dark Theme)

Alle Farben und Layout-Werte sind über CSS Custom Properties auf `:root` definiert:

**Hintergrund:**
- `--bg-primary`: `#1a1b2e` (Seitenhintergrund)
- `--bg-secondary`: `#242540` (Header, Navigation)
- `--bg-tertiary`: `#2d2e4a` (Hover-States, Toolbar)
- `--bg-card`: `#2a2b45` (Cards, KPI-Tiles)
- `--bg-input`: `#1e1f38` (Eingabefelder)

**Text:**
- `--text-primary`: `#e8e8f0`
- `--text-secondary`: `#a0a0b8`
- `--text-muted`: `#6c6c88`

**Akzentfarben:**
- `--accent-green`: `#4caf50` (Passed, Aktiv)
- `--accent-red`: `#f44336` (Failed, Fehler)
- `--accent-yellow`: `#ff9800` (Warning, Error)
- `--accent-blue`: `#42a5f5` (Links, Primary, Running)
- `--accent-purple`: `#ab47bc` (User Stories)

**Layout:**
- `--radius`: `8px`
- `--shadow`: `0 2px 8px rgba(0,0,0,0.3)`
- `--font`: System-Font-Stack (Segoe UI, Roboto, sans-serif)
- `--mono`: Cascadia Code, Fira Code, Consolas

### Komponentenklassen

| Klasse                 | Beschreibung                                      |
|------------------------|---------------------------------------------------|
| `.card`                | Container mit Hintergrund, Border, Schatten       |
| `.card-title`          | Uppercase-Überschrift in Card                     |
| `.card-tabs`           | Tab-Navigation innerhalb einer Card               |
| `.kpi-tile`            | KPI-Kachel mit Wert und Label                     |
| `.kpi-grid`            | CSS-Grid für KPI-Tiles (auto-fit, min 180px)      |
| `.data-table`          | Datentabelle mit Hover-Effekt                     |
| `.badge`               | Inline-Badge (Varianten: -passed, -failed, -error, -skipped, -running, -active, -disabled) |
| `.btn`                 | Button (Varianten: -primary, -danger, -sm)        |
| `.form-group`          | Formular-Wrapper                                  |
| `.form-label`          | Formular-Label                                    |
| `.form-input`          | Text-Eingabefeld                                  |
| `.form-select`         | Dropdown                                          |
| `.form-textarea`       | Mehrzeiliges Textfeld (Monospace)                 |
| `.progress-bar`        | Fortschrittsbalken (Varianten: .success, .error)  |
| `.log-output`          | Log-Anzeige (Monospace, farbcodiert)              |
| `.json-editor`         | JSON-Textarea (Monospace, dunkler Hintergrund)    |
| `.json-editor-wrapper` | Container für Editor mit Status-Zeile             |
| `.json-error`          | Fehleranzeige unter JSON-Editor                   |
| `.json-valid`          | Gültig-Anzeige unter JSON-Editor                  |
| `.split-view`          | Zwei-Spalten-Layout (350px / 1fr)                 |
| `.grid-2`, `.grid-3`   | Gleichmäßige Grid-Layouts                         |
| `.tag`                 | Inline-Tag                                        |
| `.toolbar`             | Flex-Toolbar mit Gap                              |
| `.checkbox-label`      | Checkbox mit Label                                |
| `.empty-state`         | Leere-Zustand-Anzeige                             |
| `.chart-container`     | SVG-Chart-Wrapper                                 |
| `.timeline-bar`        | Timeline-Balken                                   |
| `.assertion-row`       | Assertion-Zeile (Varianten: -passed, -failed)     |
| `.flow-container`      | Scrollbarer SVG-Flow-Wrapper                      |
| `.flow-node`           | Klickbarer Flow-Knoten                            |
| `.flow-detail-panel`   | Detail-Panel unter Flow-Diagramm                  |
| `.flow-legend`         | Legende für Flow-Farben                           |
| `.help-layout`         | Hilfe-Grid (260px Sidebar / 1fr Content)          |
| `.help-sidebar`        | Sticky-Sidebar mit Navigation                     |
| `.help-nav-group`      | Navigations-Gruppe in Sidebar                     |
| `.help-nav-item`       | Navigations-Eintrag                               |
| `.help-content`        | Hilfe-Inhalt (max-width 800px)                    |
| `.help-tip`            | Tipp-Box (blauer Rand)                            |
| `.help-warning`        | Warnung-Box (gelber Rand)                         |
| `.help-table`          | Tabelle in Hilfe-Inhalten                         |
| `.help-role-badge`     | Rollen-Badge (tester/dev/admin)                   |
| `.help-btn`            | Runder Hilfe-Button (?)                           |
| `.meta-tabs`           | Tab-Navigation im Metadaten-Explorer              |
| `.meta-detail`         | Detail-Panel im Metadaten-Explorer                |
| `.meta-type-badge`     | Typ-Badge für Attribute (string/int/bool/datetime/lookup/optionset/money/memo/other) |
| `.meta-snippet`        | Code-Snippet mit Kopier-Button                    |
| `.meta-optionset-list` | OptionSet-Werte-Liste                             |
| `.copy-btn`            | Kopier-Button (Monospace, mit "Kopiert!"-Feedback)|

### Responsive Breakpoints

| Breakpoint     | Anpassung                                           |
|----------------|-----------------------------------------------------|
| `max-width: 900px` | `.split-view`: Einspaltiges Layout              |
| `max-width: 900px` | `.help-layout`: Einspaltiges Layout             |
| `auto-fit`     | `.kpi-grid`: Automatische Spaltenanzahl (min 180px) |

---

## 7. Datenflüsse

### Live-Modus

```
User-Aktion (Klick, Navigation)
    |
    v
View-Funktion (z.B. renderDashboard)
    |
    v
API.fetch() / API.getMany() / API.create() / ...
    |
    v
window.fetch("/api/data/v9.2/...")
    |
    v
Dataverse Web API (OData v4, Session-Cookie)
    |
    v
JSON-Antwort (value-Array oder Einzelobjekt)
    |
    v
DOM-Rendering (innerHTML, Tabellen, Charts, Badges)
    |
    v
User sieht aktualisierte Ansicht
```

### Demo-Modus

```
User-Aktion (Klick, Navigation)
    |
    v
View-Funktion (z.B. renderDashboard)
    |
    v
API.fetch() [durch activateDemoMode() überschrieben]
    |
    v
MockAPI.fetch() [intercepted]
    |
    v
URL-Parsing (EntitySet, $filter, $orderby, $top, $count)
    |
    v
DemoPacks[currentPack] --> In-Memory Store (_store)
    |
    v
OData-kompatible JSON-Antwort
    |
    v
DOM-Rendering (identisch zum Live-Modus)
    |
    v
User sieht Demo-Daten
```

### Auto-Detection (Modus-Erkennung)

```
initApp()
    |
    v
window.fetch("/api/data/v9.2/WhoAmI")
    |
    +-- Erfolg (200 OK) --> Live-Modus bleibt aktiv
    |
    +-- Fehler (Network Error, 401, 404, ...) --> activateDemoMode()
            |
            v
        Alle API-Methoden werden durch MockAPI-Äquivalente ersetzt:
            API.fetch   = MockAPI.fetch.bind(MockAPI)
            API.getMany = MockAPI.getMany.bind(MockAPI)
            API.getOne  = MockAPI.getOne.bind(MockAPI)
            API.create  = MockAPI.create.bind(MockAPI)
            API.update  = MockAPI.update.bind(MockAPI)
            API.del     = MockAPI.del.bind(MockAPI)
            API.executeAction = MockAPI.executeAction.bind(MockAPI)
            API.fetchXml = MockAPI.fetchXml.bind(MockAPI)
            API.count   = MockAPI.count.bind(MockAPI)
            |
            v
        Demo-Banner wird unter Navigation eingefügt
        Pack-Indikator wird angezeigt
        Pack-Selector (Header) wird aktiviert
```

### Testlauf-Simulation (Demo-Modus)

```
User klickt "Testlauf starten"
    |
    v
API.create("jbe_testruns", ...) --> MockAPI._create()
    |
    v
MockAPI._simulateTestRun(run, collection)
    |
    v
Ermittlung der passenden Testfälle nach Filter:
    "*"              --> Alle aktiven Testfälle
    "category:XYZ"   --> Nach Kategorie filtern
    "tag:XYZ"        --> Nach Tag filtern
    "story:PROJ-1234"--> Nach User Story filtern
    "TC01,TC02"      --> Nach Test-IDs filtern
    |
    v
Status auf "Running" setzen
    |
    v
setTimeout-Kaskade (1,5-2,5s pro Test):
    - 90% Pass-Rate (TC05 immer fehlerhaft für Realismus)
    - TestRunResult-Datensatz in Store schreiben
    - Run-Zähler aktualisieren (passed/failed)
    - Log-Zeile hinzufügen
    |
    v
Nach letztem Test: Status auf "Completed", Log abschließen
    |
    v
View pollt alle 3 Sekunden und aktualisiert Anzeige
```

---

## 8. Konfiguration

### CONFIG-Block

Der CONFIG-Block (ab Zeile 798) definiert alle anpassbaren Schema-Namen:

| Property                     | Wert (Standard)             | Beschreibung                               |
|------------------------------|-----------------------------|--------------------------------------------|
| `prefix`                     | `"itt"`                     | Publisher-Prefix (ohne Unterstrich)        |
| `optionSetBase`              | `100000000`                 | Basiswert für alle Custom OptionSets       |
| `entities.testcase`          | `"jbe_testcase"`            | Entity für Testfälle                       |
| `entities.testrun`           | `"jbe_testrun"`             | Entity für Testläufe                       |
| `entities.testrunresult`     | `"jbe_testrunresult"`       | Entity für Einzelergebnisse                |
| `fields.testid`              | `"jbe_testid"`              | Test-ID (String, eindeutig)                |
| `fields.title`               | `"jbe_title"`               | Titel des Testfalls                        |
| `fields.category`            | `"jbe_category"`            | Kategorie (OptionSet)                      |
| `fields.tags`                | `"jbe_tags"`                | Tags (kommagetrennt)                       |
| `fields.enabled`             | `"jbe_enabled"`             | Aktiv-Flag (Boolean)                       |
| `fields.definition_json`     | `"jbe_definition_json"`     | JSON-Definition (Memo)                     |
| `fields.userstories`         | `"jbe_userstories"`         | User Stories (kommagetrennt)               |
| `fields.teststatus`          | `"jbe_teststatus"`          | Lauf-Status (OptionSet)                    |
| `fields.passed`              | `"jbe_passed"`              | Anzahl bestanden (Integer)                 |
| `fields.failed`              | `"jbe_failed"`              | Anzahl fehlgeschlagen (Integer)            |
| `fields.total`               | `"jbe_total"`               | Gesamtanzahl (Integer)                     |
| `fields.started_on`          | `"jbe_started_on"`          | Startzeit (DateTime)                       |
| `fields.completed_on`        | `"jbe_completed_on"`        | Endzeit (DateTime)                         |
| `fields.testsummary`         | `"jbe_testsummary"`         | Zusammenfassung (Memo)                     |
| `fields.fulllog`             | `"jbe_fulllog"`             | Vollständiges Log (Memo)                   |
| `fields.testcasefilter`      | `"jbe_testcasefilter"`      | Verwendeter Filter (String)                |
| `fields.outcome`             | `"jbe_outcome"`             | Ergebnis (OptionSet)                       |
| `fields.duration_ms`         | `"jbe_duration_ms"`         | Dauer in ms (Integer)                      |
| `fields.error_message`       | `"jbe_error_message"`       | Fehlermeldung (Memo)                       |
| `fields.assertion_results`   | `"jbe_assertion_results"`   | Assertion-Ergebnisse (Memo, JSON)          |
| `actions.runTests`           | `"jbe_RunIntegrationTests"` | Custom Action zum Starten                  |
| `optionSets.statusPlanned`   | `100000000`                 | Status: Geplant                            |
| `optionSets.statusRunning`   | `100000001`                 | Status: Läuft                              |
| `optionSets.statusCompleted` | `100000002`                 | Status: Abgeschlossen                      |
| `optionSets.statusError`     | `100000003`                 | Status: Fehler                             |
| `optionSets.outcomePassed`   | `100000000`                 | Ergebnis: Passed                           |
| `optionSets.outcomeFailed`   | `100000001`                 | Ergebnis: Failed                           |
| `optionSets.outcomeError`    | `100000002`                 | Ergebnis: Error                            |
| `optionSets.outcomeSkipped`  | `100000003`                 | Ergebnis: Skipped                          |
| `optionSets.outcomeNotImpl`  | `100000004`                 | Ergebnis: Not Implemented                  |
| `optionSets.catUpdateSource` | `100000000`                 | Kategorie: Update Source                   |
| `optionSets.catCreateSource` | `100000001`                 | Kategorie: Create Source                   |
| `optionSets.catPISA`         | `100000002`                 | Kategorie: PISA                            |
| `optionSets.catMultiSource`  | `100000003`                 | Kategorie: Multi-Source                    |
| `optionSets.catAdditionalFields` | `100000004`             | Kategorie: Additional Fields               |
| `optionSets.catBridge`       | `100000005`                 | Kategorie: Bridge                          |
| `optionSets.catMerge`        | `100000006`                 | Kategorie: Merge                           |
| `optionSets.catRecompute`    | `100000007`                 | Kategorie: Recompute                       |
| `optionSets.catErrorInjection` | `100000008`               | Kategorie: Error Injection                 |

### Prefix ändern

Um den Publisher-Prefix zu ändern (z.B. von `itt` auf `markant`):

1. `CONFIG.prefix` anpassen
2. Alle Werte in `CONFIG.entities` und `CONFIG.fields` anpassen (z.B. `jbe_testcase` zu `markant_testcase`)
3. `CONFIG.actions.runTests` anpassen
4. Die tatsächlichen Entities in Dataverse müssen mit dem neuen Prefix existieren

### OptionSet-Basiswerte

Alle Custom OptionSets nutzen den Dataverse-Standard-Basis `100000000`. Die konkreten Werte sind als Offset relativ zur Basis definiert:

- Status: 100000000 bis 100000003
- Outcome: 100000000 bis 100000004
- Category: 100000000 bis 100000008

Diese Werte müssen exakt mit den in Dataverse definierten OptionSet-Werten übereinstimmen.

---

## 9. Erweiterbarkeit

### Neue Views hinzufügen

1. **Route definieren:** In `handleRoute()` einen neuen `case`-Zweig im `switch`-Statement hinzufügen:
   ```javascript
   case "meinview": renderMeinView(param); break;
   ```

2. **Render-Funktion erstellen:** Asynchrone Funktion, die `render()` für das initiale HTML aufruft und dann API-Daten lädt:
   ```javascript
   async function renderMeinView(param) {
       render(`<h2>Meine View</h2><div id="mein-content">Lade...</div>`);
       const data = await API.getMany("jbe_testcases", "...");
       $("#mein-content").innerHTML = "...";
   }
   ```

3. **Navigation ergänzen:** Link in `<nav id="app-nav">` hinzufügen:
   ```html
   <a href="#meinview" data-view="meinview">Mein Bereich</a>
   ```

### Neue Demo-Pakete hinzufügen

1. Neues Paket im `DemoPacks`-Objekt definieren:
   ```javascript
   "mein-pack": {
       name: "Mein Demo-Paket",
       description: "Beschreibung des Pakets",
       color: "#e91e63",
       testCases: [...],
       get testRuns() { return [...]; },
       get testRunResults() { return [...]; },
       userStories: [...],
       metadata: { entities: [...], attributes: {...}, optionsets: [...] }
   }
   ```

2. Option im Pack-Selector (HTML, Zeile 758 ff.) hinzufügen:
   ```html
   <option value="mein-pack">Mein Demo-Paket</option>
   ```

### Neue Hilfe-Seiten hinzufügen

Neues Objekt in `HelpContent.sections` einfügen:
```javascript
{
    group: "Gruppenname",
    id: "eindeutige-id",
    title: "Seitentitel",
    roles: ["tester", "dev"],
    content: `<h2>Überschrift</h2><p>Inhalt als HTML...</p>`
}
```

Die Seite erscheint automatisch in der Sidebar-Navigation unter der angegebenen Gruppe.

### Neue Chart-Typen hinzufügen

Neue Methode im `Charts`-Objekt definieren:
```javascript
Charts.meinChart = function(data, width, height) {
    // SVG als String generieren und zurückgeben
    return `<svg ...>...</svg>`;
};
```

Aufruf in einer View:
```javascript
$("#container").innerHTML = Charts.meinChart(data, 400, 200);
```

### Neue Knotentypen im FlowVisualizer

1. Farbe in `FlowViz.colors` definieren:
   ```javascript
   meintyp: { bg: "#...", border: "#...", text: "#...", label: "Mein Typ" }
   ```

2. Erkennungslogik in `FlowViz.parseDefinition()` ergänzen (im Step-Abschnitt):
   ```javascript
   if (action.toLowerCase().includes("meinaction")) type = "meintyp";
   ```

### Neue Assertion-Operatoren

Die Operatoren (`Equals`, `NotEquals`, `Contains`, `IsNull`, `IsNotNull`, `GreaterThan`) werden im FlowVisualizer nur zur Anzeige verwendet. Die tatsächliche Auswertung erfolgt serverseitig in der Custom Action `jbe_RunIntegrationTests`. Für neue Operatoren muss daher sowohl die Custom Action als auch die Anzeige in `FlowViz.showDetail()` und in den HelpContent-Sektionen erweitert werden.
