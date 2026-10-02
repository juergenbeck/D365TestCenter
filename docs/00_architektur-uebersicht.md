# Integration Test Center: Architekturübersicht

## 1. Systemüberblick

Das Integration Test Center (ITT) ist eine **Single-File Web Application** (HTML + CSS + JavaScript), die als Dataverse Web Resource deployed wird. Sie testet beliebige CRM-Automatisierungen Ende-zu-Ende in einer lebenden Dynamics-365-Umgebung.

### Architekturprinzipien

- **Zero Dependencies:** Keine externen Libraries (kein jQuery, React, D3, Moment.js). Alles nativ.
- **Single-File Deployment:** Eine HTML-Datei, die als Web Resource hochgeladen wird.
- **Dual-Mode:** Automatische Erkennung ob CRM-Umgebung oder Standalone (Demo-Modus).
- **Pack-basiert:** Testszenarien als externe JSON-Dateien, dynamisch geladen.

```
+---------------------------------------------------------------+
|                    Dynamics 365 / Browser                      |
|                                                                |
|  +----------------------------------------------------------+ |
|  |              jbe_testcenter.html                          | |
|  |                                                           | |
|  |  +--------+  +--------+  +---------+  +---------------+  | |
|  |  |  UI    |  | State  |  |   API   |  |   PackLoader  |  | |
|  |  | Views  |->| Mgmt   |->| Layer   |  |   (JSON)      |  | |
|  |  +--------+  +--------+  +----+----+  +-------+-------+  | |
|  |                               |                |          | |
|  |                          +----+----+           |          | |
|  |                          |         |           |          | |
|  |                    Execution    +--+--------+  |          | |
|  |                    Engine       |  MockAPI  |<-+          | |
|  |                      |          | (In-Mem.) |             | |
|  |                      |          | (Demo)    |             | |
|  |              +-------+------+   +-----------+             | |
|  |              |              |                              | |
|  |        +-----+------+ +----+-------+                      | |
|  |        | Dataverse  | | ITTLog     |                      | |
|  |        |  Web API   | | (Console)  |                      | |
|  |        | (Live)     | +------------+                      | |
|  |        +-----+------+                                     | |
|  +----------------------------------------------------------+ |
|                 |                                              |
|        +--------+--------+                                    |
|        | Dataverse        |                                   |
|        | jbe_testcase     |                                   |
|        | jbe_testrun      |                                   |
|        | jbe_testrunresult|                                   |
|        | jbe_teststep     |                                   |
|        +-----------------+                                    |
+---------------------------------------------------------------+

Execution Engine:
  TestRunner         -- Orchestrierung (execute, Filter, progressive Updates)
  StepExecutor       -- 4 Phasen (Preconditions, Steps, Assertions, Cleanup)
  PlaceholderResolver-- 16 Patterns (GENERATED, TIMESTAMP, Alias, ENV)
  RecordTracker      -- track/cleanup/getTrackedRecords
  StepLogger         -- Schreibt jbe_teststep Records
```

---

## 2. Komponenten

### 2.1 UI-Layer (Views)

Hash-basiertes Routing ohne externe Library. Jeder View ist eine `render*()`-Funktion, die `#app-content` per innerHTML setzt.

| Route | View | Zweck |
|-------|------|-------|
| `#dashboard` | Dashboard | KPI-Tiles (Testfälle, Pass Rate, Läufe, offene Fehler), Pie-Chart, fehlgeschlagene Tests |
| `#cases` | Testfälle | Übersicht mit Filter (Suche, Kategorie, Tag, Story, Aktiv). Play-Button pro Zeile. Checkboxen für Multi-Select. |
| `#cases/{id}` | Testfall-Editor | Split-View: Metadaten links, JSON-Editor + Flow-Visualisierung rechts |
| `#run/new` | Testlauf starten | Filter eingeben (Kategorie/Tag/Story/IDs), "Test starten"-Button |
| `#run/{id}` | Testlauf-Details | Live-Polling (3s), Progress-Bar, Log-Output, Ergebnis-Tabelle, Schritte-Tab |
| `#runs` | Verlauf | Historische Testläufe mit Datum/Status-Filter |
| `#stories` | User Stories | Testabdeckung pro Story, Drill-Down in Story-Details |
| `#metadata` | Metadaten-Explorer | Tabellen, Attribute, OptionSets, Custom APIs aus Dataverse |
| `#help` | Hilfe | Durchsuchbare Dokumentation mit Rollen-Badges |

**UI-Erweiterungen:**

- **Play-Button** pro Testfall-Zeile (einzelner Testfall direkt starten)
- **Multi-Select-Toolbar:** Checkboxen auf Testfall-Zeilen, Toolbar zeigt "X ausgewählt", Buttons "Testlauf starten" und "Starten + Daten beibehalten"
- **Schritte-Tab** in Testlauf-Details: aufklappbare Baumstruktur pro Testfall mit Phase, Aktion, Entity, Alias, Status, Dauer
- **Button "Testdaten aufräumen"** bei keeprecords=true (ruft RecordTracker.cleanupManual auf)
- **showError()** statt alert() für Fehlermeldungen (rote Fehlerbox im UI)

### 2.2 State Management

Globales State-Objekt, kein Framework:

```
State
  currentView      -- aktive View ("dashboard", "cases", ...)
  testCases[]      -- gepufferte Testfall-Liste
  testRuns[]       -- gepufferte Testlauf-Liste
  currentRunId     -- aktiver Testlauf (für Polling)
  pollingTimer     -- setInterval-Handle (wird bei View-Wechsel gestoppt)
```

### 2.3 API-Layer

Abstraktionsschicht über der Dataverse Web API v9.2. Methoden:

| Methode | HTTP | Endpoint-Muster |
|---------|------|-----------------|
| `getMany(entity, query)` | GET | `/{entity}?$select=...&$filter=...&$orderby=...&$top=N` |
| `getOne(entity, id, select)` | GET | `/{entity}({id})?$select=...` |
| `create(entity, data)` | POST | `/{entity}` |
| `update(entity, id, data)` | PATCH | `/{entity}({id})` |
| `del(entity, id)` | DELETE | `/{entity}({id})` |
| `executeAction(name, params)` | POST | `/{actionName}` |
| `fetchXml(entity, xml)` | GET | `/{entity}?fetchXml=...` |

**Im Live-Modus:** Direkte REST-Aufrufe an `/api/data/v9.2/` mit OData v4.0 Headers.

**Im Demo-Modus:** Alle Methoden werden per `Function.bind()` auf MockAPI umgeleitet. Der aufrufende Code merkt keinen Unterschied.

### 2.4 MockAPI (Demo-Modus)

Vollständige In-Memory-Datenbank, die alle API-Aufrufe simuliert:

```
MockAPI
  _store
    testcases[]          -- aus aktivem Pack geladen
    testruns[]           -- Testläufe (werden bei Create simuliert)
    testrunresults[]     -- Testergebnisse (progressiv erzeugt)

  fetch(url, options)    -- Hauptrouter: URL-Pattern-Matching
  _parseFilter(expr)     -- OData $filter Parser (contains, eq, and)
  _applyOrderBy(data)    -- OData $orderby Implementierung
  _simulateTestRun()     -- Asynchrone Testausführung (1.5-2.5s pro Test)
  _handleEntityDefs()    -- Metadaten: Entities + Attribute
  _handleOptionSets()    -- Metadaten: Globale OptionSets
  switchPack(packId)     -- Pack wechseln, Store zurücksetzen
```

### 2.5 PackLoader

Lädt Testszenarien aus externen JSON-Dateien:

```
PackLoader
  _basePath              -- Relativer Pfad zu packs/ (auto-detected)
  _manifest              -- Geladenes manifest.json
  _packs{}               -- Cache: packId -> Pack-Daten

  loadManifest()          -- Fetcht packs/manifest.json
  loadPack(packId)        -- Fetcht packs/{packId}.json, hydratisiert
  loadAll()               -- Lädt alle Packs aus Manifest
  _hydrate(pack)          -- JSON-Objekte zu Strings, Datum-Templates auflösen
  _resolveDates(obj)      -- $date:daysAgo,hour,minute -> ISO-Timestamp
```

### 2.6 Charts (SVG)

Eigene SVG-basierte Chart-Engine, keine externe Library:

- `Charts.pie(data, container)` -- Kreisdiagramm mit Legende
- `Charts.bar(data, container)` -- Balkendiagramm
- `Charts.timeline(data, container)` -- Horizontale Balken-Timeline (Testdauer)

### 2.7 FlowViz (Testfall-Visualisierung)

SVG-basierte Visualisierung der Testfall-Definition:

```
FlowViz
  parseDefinition(json, results)    -- JSON -> Flow-Nodes
  renderSvg(nodes, containerId)     -- Nodes -> SVG mit Phasen, Pfeilen, Icons

  Phasen:
    1. Preconditions (Account, Contact, ContactSources erstellen)
    2. Steps (Create/Update/Wait/API-Aufrufe)
    3. Assertions (Entity.Field vs. erwarteter Wert)
    4. Cleanup (Testdaten aufräumen, optional)

  Farben:
    Blau   = CreateRecord
    Orange = UpdateRecord
    Lila   = Custom API
    Grün   = Assertion bestanden
    Rot    = Assertion fehlgeschlagen
```

### 2.8 Execution Engine

Die Execution Engine führt Testfälle direkt im Browser gegen die Dataverse Web API aus. Sie besteht aus fünf Untermodulen:

#### PlaceholderResolver

Löst dynamische Platzhalter in Testfall-Definitionen auf, in vier Kategorien. Die
vollständige Liste steht in
[03-platzhalter.md](handbuch/02-testfall-schreiben/03-platzhalter.md).

| Kategorie | Patterns | Beispiel |
|-----------|----------|---------|
| GENERATED | firstname, lastname, email, phone, mobile, phone_international, company, text, guid, number, city, street, zip, jobtitle, website | `{GENERATED:email}` ergibt zufällige Adresse auf example.com |
| Zeit | TIMESTAMP, TIMESTAMP_COMPACT, TIMESTAMP_ISO, TIMESTAMP_PLUS_1H/_2H/_1D, TIMESTAMP_MINUS_30M/_1H/_2H/_3H/_1D | `{TIMESTAMP}` ergibt `yyyyMMdd_HHmmss_fff`, ISO 8601 liefert `{TIMESTAMP_ISO}` |
| Kontext | TESTID, GUID, PREFIX, CONTACT_ID, ACCOUNT_ID | `{TESTID}` ergibt die ID des laufenden Testfalls |
| Alias | alias.id, alias.fields.X, RECORD:alias, RESULT:alias.X, alias.outputs.X, ROW:X | `{myContact.id}` ergibt die ID des Alias "myContact" |

#### RecordTracker

Verwaltet alle während eines Testlaufs erstellten Dataverse-Records für späteres Cleanup:

| Methode | Zweck |
|---------|-------|
| `track(entity, id, alias)` | Record registrieren |
| `cleanup()` | Alle registrierten Records löschen (umgekehrte Reihenfolge) |
| `cleanupManual()` | Manuelles Aufräumen bei keeprecords=true |
| `getTrackedRecords()` | Liste aller registrierten Records |

Cleanup ist optional: bei `keeprecords=true` bleiben Records bestehen und können über den Button "Testdaten aufräumen" manuell entfernt werden.

#### StepExecutor

Führt einen einzelnen Testfall in vier Phasen aus:

```
StepExecutor.runTestCase(definition, context, keepRecords)
  |
  +-> Phase 1: Preconditions (generisches Array oder Legacy-Format)
  |     CreateRecord, Alias zuweisen, RecordTracker.track()
  |
  +-> Phase 2: Steps
  |     CreateRecord / UpdateRecord / DeleteRecord / Wait / ExecuteAction
  |
  +-> Phase 3: Assertions
  |     Target auflösen, Feld abfragen, Operator anwenden
  |
  +-> Phase 4: Cleanup (nur wenn keeprecords=false)
        RecordTracker.cleanup()
```

**Assertion-Operatoren (11):**

| Operator | Prüfung |
|----------|---------|
| Equals | Wert == Erwartung |
| NotEquals | Wert != Erwartung |
| Contains | Wert enthält Erwartung |
| IsNull | Wert ist null/undefined |
| IsNotNull | Wert ist nicht null/undefined |
| GreaterThan | Wert > Erwartung |
| LessThan | Wert < Erwartung |
| GreaterThanOrEqual | Wert >= Erwartung |
| LessThanOrEqual | Wert <= Erwartung |
| StartsWith | Wert beginnt mit Erwartung |
| EndsWith | Wert endet mit Erwartung |

**Abwärtskompatibilität:** Das alte Precondition-Format (Objekt statt Array) wird weiterhin unterstützt und intern in das generische Array-Format konvertiert.

#### StepLogger

Schreibt für jeden ausgeführten Schritt einen `jbe_teststep`-Record nach Dataverse:

| Feld | Inhalt |
|------|--------|
| Phase | Precondition / Step / Assertion / Cleanup |
| Action | CreateRecord, UpdateRecord, Assert, usw. |
| Entity, Alias | Betroffene Entity und Alias-Name |
| RecordId, RecordUrl | ID und Deep-Link des Records |
| InputData, OutputData | Gesendete/empfangene Daten (JSON) |
| Duration | Dauer in Millisekunden |
| Status | Success / Failed / Skipped |
| AssertionField/Operator/Expected/Actual | Assertion-Details (nur bei Phase=Assertion) |

#### TestRunner

Orchestriert die Ausführung eines kompletten Testlaufs:

```
TestRunner.execute(runId)
  |
  +-> Run laden (API.getOne)
  +-> Testfälle filtern (category: / tag: / story: / IDs)
  +-> Status auf "Läuft" setzen
  +-> Für jeden Testfall (sequenziell):
  |     StepExecutor.runTestCase(definition, context, keepRecords)
  |     Ergebnis als jbe_testrunresult speichern
  |     Step-Protokoll als jbe_teststep Records speichern (StepLogger)
  |     Run-Zähler aktualisieren (progressive Updates für Polling)
  +-> Run auf "Abgeschlossen" setzen
```

Die Ausführung erfolgt **fire-and-forget**: Der Aufrufer wartet nicht auf das Ergebnis, das Frontend pollt den Fortschritt.

### 2.9 MetaCache

In-Memory-Cache für Dataverse-Metadaten:

```
MetaCache
  entities       -- EntityDefinitions (LogicalName, DisplayName, etc.)
  attributes{}   -- Pro Entity: Attribut-Liste mit Typ, Pflicht, Custom
  optionSets     -- Globale OptionSets mit Optionen
  customApis     -- Registrierte Custom APIs
```

### 2.10 Hilfe-System

Integrierte, durchsuchbare Dokumentation mit:
- Rollen-Badges (Tester, Entwickler, Admin)
- Abschnitte: Überblick, Testfälle, Testläufe, User Stories, Metadaten, JSON-Syntax
- Keyboard-Suche über alle Abschnitte

### 2.11 ITTLog (Console-Logging)

Strukturiertes Logging für Diagnose und Nachvollziehbarkeit:

```
ITTLog
  Prefix: [ITT]
  Zeitstempel: ISO-Format
  Bereiche: App, TestRunner, StepExecutor, API

  info(bereich, nachricht)     -- Normale Information
  warn(bereich, nachricht)     -- Warnung
  error(bereich, nachricht)    -- Fehler
  group(titel)                 -- Gruppierung starten (console.group)
  groupEnd()                   -- Gruppierung beenden
  table(daten)                 -- Tabellarische Ausgabe (console.table)
```

Beispielausgabe: `[ITT] 2026-03-29T14:30:00Z [TestRunner] Starte Testlauf TR-00000042`

### 2.12 Fehlerbehandlung

Einheitliche Fehlerbehandlung im gesamten ITT:

| Komponente | Verhalten |
|------------|-----------|
| `showError(titel, details)` | Rote Fehlerbox im UI (ersetzt `alert()`). Technische Details aufklappbar. |
| `_parseDataverseError(response)` | Extrahiert `message` und `code` aus JSON-Fehler-Response der Dataverse Web API. |
| Testlauf-Details | Status-abhängige Meldungen: Fehlgeschlagen (rot), Übersprungen (grau), Bestanden (grün). |
| API-Fehler | HTTP-Statuscode und Fehlermeldung werden im Log und im UI angezeigt. |

---

## 3. Datenmodell (Dataverse)

### 3.1 Entities

```
jbe_testcase                    jbe_testrun                     jbe_testrunresult
+-----------------------+       +-----------------------+       +---------------------------+
| jbe_testcaseid (PK)   |       | jbe_testrunid (PK)    |       | jbe_testrunresultid (PK)   |
| jbe_name (AutoNumber) |       | jbe_name (AutoNumber) |       | jbe_name (AutoNumber)      |
| jbe_testid            |       | jbe_teststatus (OS)   |       | jbe_testid                 |
| jbe_title             |       | jbe_passed            |       | jbe_outcome (OS)           |
| jbe_category (OS)     |       | jbe_failed            |       | jbe_durationms            |
| jbe_tags              |       | jbe_total             |       | jbe_errormessage          |
| jbe_userstories       |       | jbe_startedon        |       | jbe_assertionresults      |
| jbe_enabled           |       | jbe_completedon      |       | jbe_testrunid (FK) --------+
| jbe_definitionjson   |       | jbe_testcasefilter    |       +---------------------------+
+-----------------------+       | jbe_testsummary       |
                                | jbe_fulllog           |                    ^
                                | jbe_keeprecords (Bool)|                    |
                                +-----------------------+       jbe_teststep |
                                                                +--------------------------+
                                                                | jbe_teststepid (PK)      |
                                                                | jbe_name (AutoNumber     |
                                                                |   TS-{SEQNUM:8})         |
                                                                | jbe_testrunresultid (FK)-+
                                                                | jbe_stepnumber (Int)     |
                                                                | jbe_phase (OS)           |
                                                                | jbe_action (String)      |
                                                                | jbe_entity (String)      |
                                                                | jbe_alias (String)       |
                                                                | jbe_recordid (String)    |
                                                                | jbe_recordurl (String)   |
                                                                | jbe_inputdata (Memo)     |
                                                                | jbe_outputdata (Memo)    |
                                                                | jbe_errormessage (Memo)  |
                                                                | jbe_durationms (Int)     |
                                                                | jbe_stepstatus (OS)      |
                                                                | jbe_assertionfield (Str) |
                                                                | jbe_assertionoperator(Str)|
                                                                | jbe_expectedvalue (Str)  |
                                                                | jbe_actualvalue (Str)    |
                                                                +--------------------------+
```

### 3.2 Globale OptionSets

| OptionSet | Werte |
|-----------|-------|
| `jbe_teststatus` | 0: Ausstehend, 1: Läuft, 2: Abgeschlossen, 3: Fehler, 4: Aufteilung läuft (Offset auf 105710000) |
| `jbe_testoutcome` | 0: Bestanden, 1: Fehlgeschlagen, 2: Übersprungen, 3: Fehler |
| `jbe_testcategory` | 0: Update Source, 1: Create Source, 2: Delete Source, 3: Multi-Source, 4: Merge, 5: Custom API, 6: Config, 7: End-to-End, 8: Error Handling |
| `jbe_stepstatus` | 0: Success, 1: Failed, 2: Skipped |
| `jbe_chunkstatus` | 0: Neu, 1: Läuft, 2: Fortsetzen, 3: Verarbeitet, 4: Fehler |
| `jbe_lifecyclestatus` | 0: Entwurf, 1: Aktiv, 2: Instabil, 3: Historisch, 4: Archiviert |

Alle Werte sind Offsets auf 105710000 (Quelle: `solution/src/OptionSets/*.xml`). Das frühere OptionSet
`jbe_stepphase` gehört nicht mehr zur Solution (ADR-0004: eine Step-Liste statt Phasen).

### 3.3 Relationships

| Relationship | Typ | Von | Zu | Verhalten |
|---|---|---|---|---|
| `jbe_testrunresult_testrun` | N:1 | jbe_testrunresult | jbe_testrun | |
| `jbe_teststep_testrunresult` | N:1 | jbe_teststep | jbe_testrunresult | Cascade Delete |

---

## 4. Datenflüsse

### 4.1 Modus-Erkennung (Startup)

```
Browser öffnet jbe_testcenter.html
        |
        v
  initApp() aufgerufen
        |
        v
  fetch("/api/data/v9.2/WhoAmI")
        |
   +----+----+
   |         |
  200       Fehler
   |         |
   v         v
Live-Modus  activateDemoMode()
   |         |
   |         +-> PackLoader.loadAll()
   |         +-> API.* = MockAPI.* (bind)
   |         +-> Demo-Banner anzeigen
   |         +-> Pack-Selector befüllen
   |
   v
loadUserInfo()
        |
        v
  initRouter()
        |
        v
  handleRoute() -> renderDashboard()
```

### 4.2 Testlauf starten und ausführen

```
User: Play-Button (einzeln) oder Multi-Select "Testlauf starten"
        |
        v
  _startRunWithFilter(filter, keepRecords)
        |
        v
  API.create("jbe_testruns", { status: Ausstehend, filter, keeprecords })
        |
        v
  TestRunner.execute(runId)    [fire-and-forget]
        |
        v
  Run laden (API.getOne)
        |
        v
  Testfälle filtern (category: / tag: / story: / IDs)
        |
        v
  Status auf "Läuft" setzen (API.update)
        |
        v
  +---> Für jeden Testfall (sequenziell):
  |       |
  |       v
  |     StepExecutor.runTestCase(definition, context, keepRecords)
  |       |
  |       +-> Phase 1: Preconditions (generisch Array oder Legacy-Format)
  |       |     PlaceholderResolver: Platzhalter auflösen
  |       |     API.create(): Records anlegen
  |       |     RecordTracker.track(): Records registrieren
  |       |     StepLogger: jbe_teststep Records schreiben
  |       |
  |       +-> Phase 2: Steps
  |       |     CreateRecord / UpdateRecord / DeleteRecord / Wait / ExecuteAction
  |       |     PlaceholderResolver: Platzhalter auflösen
  |       |     StepLogger: jbe_teststep Records schreiben
  |       |
  |       +-> Phase 3: Assertions
  |       |     Target auflösen (Alias oder Entity+ID)
  |       |     Feld von Dataverse abfragen (API.getOne)
  |       |     Operator anwenden (11 Operatoren)
  |       |     StepLogger: Assertion-Details schreiben
  |       |
  |       +-> Phase 4: Cleanup (nur wenn keeprecords=false)
  |             RecordTracker.cleanup() (umgekehrte Reihenfolge)
  |             StepLogger: Cleanup-Schritte schreiben
  |       |
  |       v
  |     Ergebnis als jbe_testrunresult speichern (API.create)
  |     Run-Zähler aktualisieren (progressive Updates, API.update)
  |       |
  +------+
        |
        v
  Run auf "Abgeschlossen" setzen (API.update)

--- Frontend (parallel) ---

  Redirect: #run/{runId}
        |
        v
  renderRunDetail(runId)
        |
        v
  Polling starten (alle 3 Sekunden):
        |
  +---> API.getOne("jbe_testruns", runId)
  |     API.getMany("jbe_testrunresults", filter=runId)
  |     API.getMany("jbe_teststeps", filter=resultIds)
  |           |
  |     UI aktualisieren:
  |       Progress-Bar, Zähler, Log, Ergebnis-Tabelle, Schritte-Tab
  |           |
  |     Status == "Abgeschlossen" oder "Fehler"?
  |      Nein -> weiter polling
  +------+
         |
        Ja -> Polling stoppen, finale Anzeige
              Bei keeprecords=true: Button "Testdaten aufräumen" anzeigen
```

### 4.3 Pack laden und wechseln

```
activateDemoMode()
        |
        v
  PackLoader.loadManifest()
        |
        v
  fetch("packs/manifest.json")
        |
        v
  Für jeden Pack im Manifest:
        |
        v
  PackLoader.loadPack(packId)
        |
        v
  fetch("packs/{packId}.json")
        |
        v
  _hydrate(rawPack):
    - jbe_definitionjson: Object -> JSON.stringify()
    - $date:daysAgo,hour,minute -> ISO-Timestamp
        |
        v
  DemoPacks[packId] = hydratedPack
        |
        v
  MockAPI.switchPack(defaultPackId)
        |
        v
  _store = deep-copy(pack.testCases, testRuns, testRunResults)
  MetaCache = null (Reset)

--- Bei Pack-Wechsel durch User ---

  Pack-Selector: onChange
        |
        v
  MockAPI.switchPack(newPackId)
        |
        v
  Store neu laden, UI re-rendern
```

### 4.4 Metadaten laden

```
User: #metadata -> Tabellen-Tab
        |
        v
  Metadaten aus Cache oder API laden
        |
   +----+-----+
   |           |
  Live        Demo (MockAPI)
   |           |
   |     _handleEntityDefinitions():
   |       _sharedIttMeta.entities    (15 Entities, 361 Attribute)
   |       + pack.metadata.entities   (pack-spezifische)
   |       -> merged, dedupliziert
   |
   v
  Entity-Liste anzeigen
        |
        v
  User klickt Entity -> Attribute laden
        |
        v
  allAttributes[entityName]
    = shared + pack (merged, keine Duplikate)
        |
        v
  Attribut-Tabelle: Schema-Name, Anzeigename, Typ, Pflicht
  Filter: "Nur Custom" Checkbox, Suchfeld
```

---

## 5. Deployment-Architektur

### 5.1 Komponenten auf Dataverse

| Komponente | Typ | Dataverse-Name |
|---|---|---|
| Publisher | Publisher | `JBE` (Prefix: `jbe`; OptionSet-Werte 10571xxxx, siehe unten) |
| Solution | Solution | `D365TestCenter` |
| Entity | Entity | `jbe_testcase` |
| Entity | Entity | `jbe_testrun` |
| Entity | Entity | `jbe_testrunresult` |
| Entity | Entity | `jbe_teststep` |
| OptionSet | Global OptionSet | `jbe_teststatus` |
| OptionSet | Global OptionSet | `jbe_testoutcome` |
| OptionSet | Global OptionSet | `jbe_testcategory` |
| OptionSet | Global OptionSet | `jbe_stepstatus` |
| OptionSet | Global OptionSet | `jbe_chunkstatus` |
| OptionSet | Global OptionSet | `jbe_lifecyclestatus` |
| Relationship | N:1 | `jbe_testrunresult_testrun` |
| Relationship | N:1 (Cascade Delete) | `jbe_teststep_testrunresult` |
| Web Resource | HTML | `jbe_/testcenter.html` |
| Web Resource | JSON | `jbe_/packs/manifest.json` |
| Web Resource | JSON | `jbe_/packs/standard.json` |
| Web Resource | JSON | `jbe_/packs/empty.json` |

### 5.2 Deployment-Ablauf

```
pac solution pack   (solution/src -> solution/out/D365TestCenter.zip)
        |
        v
pac solution import --publish-changes --activate-plugins
        |
        v
  optional: scripts/Create-RecurrenceFlow.ps1 (Recovery-Flow je Umgebung)
        |
        v
  URL: https://{env}.crm4.dynamics.com/WebResources/jbe_/testcenter.html
```

Die Einrichtung läuft ausschließlich über den Solution-Import; die früheren Skripte, die Tabellen und
OptionSets einzeln per Web API anlegten, sind stillgelegt (ADR-2026-09-18-1258). Die OptionSet-Werte stehen fest in `solution/src/OptionSets/*.xml` (Bereich 10571xxxx) und werden beim
Import unverändert übernommen. `Solution.xml` nennt für den Publisher `JBE` den Options-Präfix `39507`;
der gilt nur für Optionen, die jemand später im Maker Portal neu anlegt, und ändert keinen bestehenden Wert.

---

## 6. Sicherheit und Zugriff

- **Authentifizierung:** Die Web Resource erbt den CRM-Session-Cookie. Kein eigener Login.
- **Autorisierung:** Dataverse-Sicherheitsrollen steuern den Zugriff auf die ITT-Entities.
- **Demo-Modus:** Kein Zugriff auf echte Daten. Alles simuliert.
- **Keine Secrets:** Kein API-Key, kein Token, kein Passwort in der HTML-Datei.

---

## 7. Erweiterungspunkte

| Erweiterung | Mechanismus |
|---|---|
| Neue Testszenarien | JSON-Pack-Datei erstellen, in manifest.json eintragen |
| Neue Entity-Typen | Attribute in `_sharedIttMeta.attributes` ergänzen |
| Neue OptionSets | In `_sharedIttMeta.optionsets` ergänzen |
| Neue Custom APIs | In `CONFIG.customapis` registrieren |
| Neuer Test-Kategorie-Typ | OptionSet `jbe_testcategory` erweitern |
| Neuer Assertion-Operator | In StepExecutor Operator-Liste erweitern |
| Neuer View | `render*()` Funktion + Route in `handleRoute()` |
| Neue Step-Aktion | In StepExecutor Phase-2-Handler ergänzen |
| Lokalisierung | Alle Strings sind hardcoded (de-DE), Refactoring zu i18n möglich |

---

## 8. Projektorganisation

Dieses Repo enthält das Produkt: Code, Solution, Skripte und die Dokumentation unter `docs/`.
Entscheidungsprotokolle (ADRs), Umsetzungspläne und Arbeitsstände werden außerhalb dieses Repos geführt;
wo die Doku eine ADR-Kennung nennt, dient sie nur als Herkunftsangabe.
