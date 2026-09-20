# Demo-Pakete

Das Integration Test Center bringt Demo-Pakete mit, die im Demo-Modus realistische Testdaten bereitstellen. Jedes Paket ist eine JSON-Datei unter `packs/`, registriert in `packs/manifest.json`; die Oberfläche lädt sie beim Start und füllt daraus den Pack-Selector. Drei Pakete decken unterschiedliche Szenarien ab.

## Übersicht der 3 Demo-Pakete

| # | Name | Beschreibung | Farbe | Testfälle | Läuft ohne Einrichtung |
|---|------|-------------|-------|-----------|------------------------|
| 1 | Standard CRM (Sales & Service) | Plattformverhalten, das jede Standardumgebung zeigt | Blau (#42a5f5) | 5 | ja, alle grün |
| 2 | Standard CRM mit Konfiguration | Testfälle, die eine eingerichtete Zielumgebung voraussetzen | Violett (#ab47bc) | 3 | nein, absichtlich |
| 3 | Leere Vorlage | Leere Umgebung mit einem Beispiel-Testfall | Grau (#78909c) | 1 | ja |

**Warum zwei Standard-Pakete?** Ein Demo hat zwei Aufgaben, die einander im Weg stehen: es soll zeigen,
dass das Werkzeug arbeitet, und es soll zeigen, wozu das Werkzeug da ist. Testfälle der zweiten Art prüfen
Kundenlogik und brauchen dafür eine eingerichtete Umgebung; auf einer frisch aufgesetzten Standardumgebung
schlagen sie fehl, obwohl mit ihnen und mit dem Werkzeug alles in Ordnung ist. Die beiden Aufgaben sind
deshalb auf zwei Pakete verteilt. Paket 1 ist das voreingestellte und läuft überall grün. Paket 2 wird
bewusst ausgewählt, und jeder seiner Testfälle nennt seine Voraussetzung im Titel.

## Pack 1: Standard CRM (Sales & Service)

**Beschreibung:** Nutzt ausschließlich Standardtabellen und Standardfelder aus Dynamics 365 Sales und
Service, benötigt keine Custom-Entities und keine Einrichtung. Alle fünf Testfälle prüfen Verhalten, das
die Plattform von sich aus zeigt.

**Genutzte CRM-Standard-Entities:** Account, Contact, Opportunity, Lead, Incident (Case), Task,
ActivityPointer, OpportunityClose

**User Stories:** PROJ-2001 (Sales Pipeline Automatisierung)

**Gemessen:** fünf von fünf bestanden gegen eine unkonfigurierte Standardumgebung, ohne Cleanup-Warnung,
in drei aufeinanderfolgenden Läufen.

### Testfälle im Detail

#### STD-TC01: Lead-Qualifizierung: Opportunity wird erstellt

- **Tags:** Lead, Opportunity, Sales
- **User Story:** PROJ-2001
- **Steps:**
  1. `CreateRecord` auf `lead` mit generiertem Vor-/Nachname, E-Mail, Firmenname, Betreff und Bewertung
  2. `ExecuteRequest` mit `QualifyLead` (`CreateOpportunity: true`) qualifiziert den Lead und lässt die
     Plattform die Opportunity anlegen
  3. `Wait` 5 Sekunden
  4. `FindRecord` auf die entstandene `opportunity` mit `trackForCleanup: true`, damit der Cleanup sie mit
     abräumt
  5. `Assert` auf `opportunity`: der Name enthält den Nachnamen aus dem Lead-Betreff
- **Was es zeigt:** die Plattform erzeugt beim Qualifizieren selbst einen Folgedatensatz, und der Test
  findet ihn wieder.

#### STD-TC02: Verkaufschance gewinnen: Status und Abschlussaktivität

- **Tags:** Opportunity, Account, Abschluss
- **Steps:**
  1. `CreateRecord` auf `account` (Alias `account1`)
  2. `CreateRecord` auf `opportunity` (Alias `opp1`) mit `customerid@odata.bind` auf den Account und
     `cleanupChildren` für die Abschlussaktivität
  3. `ExecuteRequest` mit `WinOpportunity` und einem `opportunityclose`; ein direktes Status-Update lehnt
     die Plattform ab
  4. `Wait` 5 Sekunden
  5. `Assert` auf die Opportunity: `statecode` ist 1 (gewonnen)
  6. `Assert` per Query auf `opportunityclose`: zur Opportunity existiert eine Abschlussaktivität
- **Was es zeigt:** eine Plattform-Message hat zwei Wirkungen, einen geänderten Status und einen neu
  entstandenen Datensatz, und der Test prüft beide.
- **Warum `cleanupChildren`:** die Abschlussaktivität hängt an der Opportunity und muss vor ihr gelöscht
  werden. Ohne die Deklaration scheiterte der Cleanup gemessen in einem von drei Läufen mit einem
  SQL-Constraint-Fehler und ließ einen Datensatz stehen.

#### STD-TC04: Kunde löschen: Kaskade auf verknüpfte Anfragen

- **Tags:** Account, Case, Kaskade
- **Steps:**
  1. `CreateRecord` auf `account` (Alias `account1`)
  2. `CreateRecord` auf `contact` (Alias `contact1`) unterhalb des Accounts
  3. `CreateRecord` auf `incident` (Alias `case1`) mit dem Account als Kunde und dem Contact als
     Ansprechpartner
  4. `Assert` per Query: vor dem Löschen hängt die Anfrage am Kunden
  5. `DeleteRecord` auf den Account
  6. `Wait` 5 Sekunden
  7. `Assert` per Query: nach dem Löschen ist die Anfrage mitgelöscht
- **Was es zeigt:** das Kaskaden-Löschverhalten der Standardbeziehung, in beide Richtungen belegt. Der
  `Exists`-Assert vor dem Löschen ist die Positivkontrolle: ohne ihn bestünde der `NotExists`-Assert auch
  dann, wenn die Anfrage nie angelegt worden wäre.

#### STD-TC05: Aufgabe anlegen: Eintrag im Aktivitätsverzeichnis

- **Tags:** Activity, Task, Aktivitäten
- **Steps:**
  1. `CreateRecord` auf `account`, danach auf `contact` unterhalb des Accounts
  2. `CreateRecord` auf `task` mit generiertem Betreff, Fälligkeit (+1 Stunde) und Bezug auf den Contact
  3. `Wait` 5 Sekunden
  4. `Assert` per Query auf `activitypointer`: der Betreff der Aufgabe steht im Aktivitätsverzeichnis
- **Was es zeigt:** die Plattform führt jede Aktivität zusätzlich in einem gemeinsamen Verzeichnis, das
  der Test über den Alias der Aufgabe wiederfindet.

#### STD-TC07: Opportunity-Pipeline: Phase-Wechsel Validierung

- **Tags:** Opportunity, BPF, Pipeline
- **User Story:** PROJ-2001
- **Steps:**
  1. `CreateRecord` auf `account` (Alias `account1`)
  2. `CreateRecord` auf `opportunity` (Alias `opp1`) mit `customerid@odata.bind` auf den Account
  3. `UpdateRecord` auf die Opportunity mit `stepname: "Propose"` und `salesstage: 2`
  4. `Wait` 5 Sekunden
  5. und 6. `Assert` auf die Opportunity: `salesstage` ist 2 und `stepname` ist "Propose"
- **Was es zeigt:** ein Update schlägt auf zwei zusammengehörige Felder durch.

## Pack 2: Standard CRM mit Konfiguration

**Beschreibung:** Drei Testfälle, die echte Kundenlogik prüfen und dafür eine eingerichtete Zielumgebung
voraussetzen. Ohne diese Einrichtung schlagen sie fehl, und das ist richtig so: sie messen dann die
Umgebung und nicht das Werkzeug.

**User Stories:** PROJ-2002 (Service Eskalation und SLA)

**Gemessen:** null von drei bestanden gegen eine unkonfigurierte Standardumgebung, ohne Abbruch und ohne
Fehler; jeder Assert wird ausgewertet und nennt in seiner Meldung, was fehlt.

| Testfall | Voraussetzung in der Zielumgebung |
|---|---|
| STD-TC03 | eine aktive Routingregel für Cases |
| STD-TC06 | eine veröffentlichte und aktive Duplikaterkennungsregel auf Account |
| STD-TC08 | ein aktives SLA für Cases |

#### STD-TC03: Case-Eskalation: Routing in eine Warteschlange

- **Tags:** Case, Routing, Service
- **Steps:** Account anlegen, Case mit Priorität "Hoch" anlegen, `Wait`, dann zwei Asserts: die Plattform
  hat eine Ticketnummer vergeben (läuft immer), und der Case liegt in einer Warteschlange (`queueitem`,
  braucht die Routingregel).
- **Ohne Routingregel:** der erste Assert besteht, der zweite meldet "Kein Record gefunden".

#### STD-TC06: Account-Deduplizierung: Warnung bei gleicher E-Mail

- **Tags:** Account, Duplikat, DQM
- **Steps:** zwei Accounts mit identischem Namen und identischer E-Mail anlegen (`{GENERATED:*}` liefert
  im selben Testfall denselben Wert), `Wait`, dann Assert per Query auf `duplicaterecord`.
- **Ohne Duplikaterkennungsregel:** 0 Treffer, der Assert meldet "Kein Record gefunden".

#### STD-TC08: Case-SLA: Timer startet bei Erstellung

- **Tags:** Case, SLA, Timer, Service
- **Steps:** Account anlegen, Case anlegen, `Wait`, dann Assert auf `slainvokedid` des Cases.
- **Ohne aktives SLA:** das Feld bleibt leer, der Assert meldet "Erwartet: nicht null, Aktuell: null".

## Pack 3: Leere Vorlage

**Beschreibung:** Leere Umgebung ohne vordefinierte Testdaten. Enthält einen einzigen Beispiel-Testfall als Ausgangspunkt für eigene Tests.

### Struktur

- Keine Testläufe, keine Testergebnisse, keine User Stories
- Ein einzelner Beispiel-Testfall mit ID `EXAMPLE-01`

### Beispiel-Testfall

```json
{
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc1",
      "fields": { "name": "Testfirma GmbH" } },
    { "stepNumber": 2, "action": "Wait", "waitSeconds": 3 },
    { "stepNumber": 3, "action": "Assert", "target": "Record", "recordRef": "{RECORD:acc1}",
      "field": "name", "operator": "Equals", "value": "Testfirma GmbH",
      "onError": "continue", "description": "Account-Name gesetzt" }
  ]
}
```

Dieser Testfall erstellt einen Account mit dem Namen "Testfirma GmbH", wartet 3 Sekunden und prüft anschließend, ob der Name korrekt gespeichert wurde. Ein Testfall ist seit ADR-0004 **eine einzige geordnete `steps`-Liste**; Vorbedingungen sind gewöhnliche `CreateRecord`-Schritte am Anfang, Prüfungen sind `Assert`-Schritte. Getrennte `preconditions`- oder `assertions`-Blöcke kennt die Engine nicht mehr, sie verwirft sie beim Einlesen still; `validate --pack` meldet sie als `PRECONDITIONS_OBSOLETE` beziehungsweise `ASSERTIONS_OBSOLETE`.

## Eigenes Demo-Paket erstellen

### Schritt-für-Schritt-Anleitung

1. **Pack-Datei anlegen:** Eine neue JSON-Datei unter `webresource/packs/` ablegen (z.B.
   `mein-pack.json`) und dieselbe Datei nach `solution/src/WebResources/jbe_/packs/` spiegeln. Beide Pfade
   müssen inhaltlich identisch bleiben; die erste ist die Quelle, die zweite geht in die Solution.

2. **Im Manifest registrieren:** In `packs/manifest.json` (ebenfalls in beiden Pfaden) einen Eintrag mit
   `packId` und `file` ergänzen. `"default": true` trägt genau ein Paket, es ist das beim Start
   ausgewählte.

3. **Pflichtfelder definieren:**
   - `name`: Anzeigename des Pakets
   - `description`: Kurzbeschreibung
   - `color`: Hex-Farbcode für den Pack-Indikator
   - `testCases`: Array der Testfälle
   - `testRuns`: Array oder Getter für Testläufe (kann leer sein)
   - `testRunResults`: Array oder Getter für Einzelergebnisse (kann leer sein)
   - `userStories`: Array der zugeordneten User Stories
   - `metadata`: Objekt mit `entities`, `attributes` und `optionsets`

Der Pack-Selector im Header muss **nicht** angefasst werden: er wird zur Laufzeit aus dem Manifest
befüllt (`PackLoader.getAvailablePacks`), und der angezeigte Name kommt aus dem `name`-Feld der
Pack-Datei.

### Template-JSON

```json
"mein-pack": {
    name: "Mein Demo-Paket",
    description: "Beschreibung des Pakets.",
    color: "#26a69a",

    testCases: [
        {
            jbe_testcaseid: "aaaaaaaa-0001-4000-8000-bbbbbbbbbbbb",
            jbe_testid: "MEIN-TC01",
            jbe_title: "Erster eigener Testfall",
            jbe_category: 100000005,
            jbe_tags: "Demo",
            jbe_userstories: "PROJ-9999",
            jbe_enabled: true,
            jbe_definitionjson: JSON.stringify({
                steps: [
                    {
                        stepNumber: 1,
                        action: "CreateRecord",
                        entity: "accounts",
                        alias: "acc1",
                        fields: { name: "Testfirma" }
                    },
                    { stepNumber: 2, action: "Wait", waitSeconds: 2 },
                    {
                        stepNumber: 3,
                        action: "Assert",
                        target: "Record",
                        recordRef: "{RECORD:acc1}",
                        field: "name",
                        operator: "Equals",
                        value: "Testfirma",
                        onError: "continue",
                        description: "Account-Name gesetzt"
                    }
                ]
            })
        }
    ],

    testRuns: [],
    testRunResults: [],

    userStories: [
        { key: "PROJ-9999", title: "Meine User Story" }
    ],

    metadata: {
        entities: [
            // Custom-Entities hier auflisten
        ],
        attributes: {
            // Attribute pro Entity hier auflisten
        },
        optionsets: [
            // OptionSets hier auflisten
        ]
    }
}
```

### Integration in DemoPacks-Objekt

Das neue Pack wird automatisch verfügbar, sobald es im Manifest steht. `PackLoader.loadAll()` lädt beim
Start jede dort genannte Datei, und der Selector bekommt seine Einträge aus derselben Liste. Der
`<option>`-Block im HTML trägt nur den Platzhalter "Packs werden geladen..." und wird beim Start
vollständig ersetzt.

### Testläufe mit Demo-Ergebnissen

Für realistische Demo-Daten können Testläufe als Getter definiert werden, die dynamische Zeitstempel erzeugen:

```javascript
get testRuns() {
    return [
        {
            jbe_testrunid: "...",
            jbe_teststatus: 100000002,  // Abgeschlossen
            jbe_passed: 5,
            jbe_failed: 1,
            jbe_total: 6,
            jbe_startedon: _makeDate(0, 10, 0),   // heute, 10:00
            jbe_completedon: _makeDate(0, 10, 8),  // heute, 10:08
            jbe_testcasefilter: "*",
            jbe_testsummary: "6 Tests ausgeführt.",
            jbe_fulllog: "..."
        }
    ];
}
```

Die Hilfsfunktion `_makeDate(daysAgo, hours, minutes)` erzeugt ein ISO-Datum relativ zum aktuellen Tag.

## Pack-Selector: Technische Funktionsweise

### UI-Element

Der Pack-Selector ist ein `<select>`-Element im Header der Anwendung (`#pack-selector`) mit einer `<option>` pro Demo-Paket. Der `value` jeder Option entspricht dem Schlüssel im `DemoPacks`-Objekt.

### Event-Handling

Der Event-Listener wird in der Funktion `activateDemoMode()` registriert:

```javascript
const packSel = document.getElementById("pack-selector");
if (packSel) {
    packSel.addEventListener("change", function() {
        MockAPI.switchPack(this.value);
    });
}
```

Bei Änderung des selektierten Packs wird `MockAPI.switchPack(packName)` aufgerufen.

### MockAPI.switchPack() im Detail

Die Methode führt folgende Schritte aus:

1. **Validierung:** Prüft ob das Pack in `DemoPacks` existiert.
2. **Pack-Wechsel:** Setzt `currentPack` auf den neuen Wert.
3. **Store-Neuinitialisierung:** Setzt `_store` auf `null` und ruft `_reinitStore()` auf.
4. **_reinitStore():** Klont die Testdaten des neuen Packs per `JSON.parse(JSON.stringify(...))` in den In-Memory-Store (testcases, testruns, testrunresults).
5. **DemoData-Alias:** Aktualisiert `DemoData` auf das neue Pack-Objekt.
6. **MetaCache leeren:** Setzt `MetaCache.entities`, `MetaCache.attributes`, `MetaCache.optionSets` und `MetaCache.customApis` auf `null`, damit beim nächsten Zugriff die Metadaten des neuen Packs geladen werden.
7. **Pack-Indikator aktualisieren:** Ruft `updatePackIndicator()` auf, das den farbigen Banner unter der Navigation aktualisiert (Hintergrundfarbe, Text, Border).
8. **View neu laden:** Ruft `handleRoute()` auf, damit die aktuelle Ansicht mit den neuen Daten gerendert wird.

### updatePackIndicator()

Erstellt oder aktualisiert ein `div#pack-indicator` unter der Navigation:

- Hintergrundfarbe: Pack-Farbe mit ca. 10% Deckkraft
- Textfarbe: Pack-Farbe
- Inhalt: Pack-Name (fett) und Beschreibung
- Synchronisiert den `#pack-selector` auf den aktuellen Pack-Wert
