# Demo-Pakete

Das Integration Test Center enthält ein modulares Demo-Paket-System (`DemoPacks`), das im Demo-Modus realistische Testdaten bereitstellt. Zwei vordefinierte Pakete decken unterschiedliche Szenarien ab.

## Übersicht der 2 Demo-Pakete

| # | Name | Beschreibung | Farbe | Testfälle | Entities |
|---|------|-------------|-------|-----------|----------|
| 1 | Standard CRM (Sales & Service) | Standardtabellen aus Dynamics 365 Sales und Service | Blau (#42a5f5) | 8 | Account, Contact, Opportunity, Lead, Incident, Task, ActivityPointer |
| 2 | Leere Vorlage | Leere Umgebung mit einem Beispiel-Testfall | Grau (#78909c) | 1 | (nach Bedarf) |

## Pack 1: Standard CRM (Sales & Service)

**Beschreibung:** Dieses Paket nutzt ausschließlich Standardtabellen und Standardfelder aus Dynamics 365 Sales und Service. Es benötigt keine Custom-Entities und eignet sich zum schnellen Ausprobieren des Test Centers.

**Genutzte CRM-Standard-Entities:** Account, Contact, Opportunity, Lead, Incident (Case), Task, ActivityPointer, DuplicateRecord

**Abgedeckte Szenarien:** Lead-Qualifizierung, Opportunity-Lifecycle (Won, Pipeline-Phasen), Case-Erstellung mit SLA und Routing, Contact-Adresskaskade, Task-Erstellung mit Workflow-Auslösung, Account-Deduplizierung

**User Stories:** PROJ-2001 (Sales Pipeline Automatisierung), PROJ-2002 (Service Eskalation und SLA)

### Testfälle im Detail

#### STD-TC01: Lead-Qualifizierung: Opportunity wird erstellt

- **Kategorie:** Bridge
- **Tags:** Lead, Opportunity, Sales
- **User Story:** PROJ-2001
- **Preconditions:** Account erstellen
- **Steps:**
  1. `CreateRecord` auf `lead` mit generiertem Vor-/Nachname, E-Mail, Firmenname und Bewertung "Heiß"
  2. `UpdateRecord` auf den Lead mit `statuscode: 3` (qualifiziert), wartet auf asynchrone Verarbeitung
- **Assertions:** Query auf `opportunity` gefiltert nach `originatingleadid` des Leads, prüft ob der Opportunity-Name den Nachnamen des Leads enthält

#### STD-TC02: Opportunity auf Won: Account-Umsatz aktualisiert

- **Kategorie:** Bridge
- **Tags:** Opportunity, Account, Revenue
- **Preconditions:** Account und Opportunity erstellen
- **Steps:** `UpdateRecord` auf Opportunity mit `statuscode: 3` (Won), `actualvalue: 50000` und Abschlussdatum
- **Assertions:** Prüft, ob `revenue` des Accounts größer als 0 ist

#### STD-TC03: Case-Eskalation: High Priority Routing

- **Kategorie:** Bridge
- **Tags:** Case, Routing, Service
- **User Story:** PROJ-2002
- **Steps:** `CreateRecord` auf `incident` mit Priorität 1 (Hoch), generiertem Titel und Kundenzuordnung
- **Assertions:** Prüft, ob der Owner des Cases nicht der aktuelle Benutzer ist (Routing hat gegriffen)

#### STD-TC04: Contact-Adresse: Kaskade auf verknüpfte Cases

- **Kategorie:** Bridge
- **Tags:** Contact, Case, Adresse
- **Preconditions:** Contact und Case erstellen
- **Steps:** `UpdateRecord` auf Contact mit `address1_city: "Berlin"` und PLZ
- **Assertions:** Query auf Incidents des Contacts, prüft ob die Kundenadresse "Berlin" enthält

#### STD-TC05: Task-Erinnerung: Workflow bei Fälligkeit

- **Kategorie:** Bridge
- **Tags:** Activity, Task, Workflow
- **Steps:** `CreateRecord` auf `task` mit generiertem Betreff, Fälligkeitsdatum (+1 Stunde) und Priorität
- **Assertions:** Query auf ActivityPointer, prüft ob der Statuscode nicht 5 (abgeschlossen) ist

#### STD-TC06: Account-Deduplizierung: Warnung bei gleicher Email

- **Kategorie:** Merge
- **Tags:** Account, Duplikat, DQM
- **Preconditions:** Account erstellen
- **Steps:** `CreateRecord` auf `account` mit identischem Namen und E-Mail wie der bestehende Account
- **Assertions:** Query auf `duplicaterecord`, prüft ob ein Duplikat-Eintrag existiert (`IsNotNull`)

#### STD-TC07: Opportunity-Pipeline: Phase-Wechsel Validierung

- **Kategorie:** Bridge
- **Tags:** Opportunity, BPF, Pipeline
- **User Story:** PROJ-2001
- **Preconditions:** Opportunity erstellen
- **Steps:** `UpdateRecord` auf Opportunity mit `stepname: "Propose"` und `salesstage: 2`
- **Assertions:** Prüft, ob `salesstage` den Wert 2 hat und `stepname` den Wert "Propose"

#### STD-TC08: Case-SLA: Timer startet bei Erstellung

- **Kategorie:** Bridge
- **Tags:** Case, SLA, Timer, Service
- **User Story:** PROJ-2002
- **Steps:** `CreateRecord` auf `incident` mit Priorität 2 und Kundenzuordnung, `waitForAsync: true`, max. 10 Sekunden
- **Assertions:** Prüft, ob `slainvokedid` nicht null ist (SLA wurde aktiviert)

## Pack 2: Leere Vorlage

**Beschreibung:** Leere Umgebung ohne vordefinierte Testdaten. Enthält einen einzigen Beispiel-Testfall als Ausgangspunkt für eigene Tests.

### Struktur

- Keine Testläufe, keine Testergebnisse, keine User Stories
- Ein einzelner Beispiel-Testfall mit ID `EXAMPLE-01`

### Beispiel-Testfall

```json
{
  "preconditions": {
    "createAccount": true,
    "createContact": true
  },
  "steps": [
    {
      "action": "CreateRecord",
      "entity": "account",
      "alias": "acc1",
      "fields": {
        "name": "Testfirma GmbH"
      }
    },
    {
      "action": "Wait",
      "waitSeconds": 3
    }
  ],
  "assertions": [
    {
      "target": "Record:acc1",
      "field": "name",
      "operator": "Equals",
      "value": "Testfirma GmbH"
    }
  ]
}
```

Dieser Testfall erstellt einen Account mit dem Namen "Testfirma GmbH", wartet 3 Sekunden und prüft anschließend, ob der Name korrekt gespeichert wurde.

## Eigenes Demo-Paket erstellen

### Schritt-für-Schritt-Anleitung

1. **Pack-Objekt anlegen:** Im `DemoPacks`-Objekt einen neuen Schlüssel hinzufügen (z.B. `"mein-pack"`).

2. **Pflichtfelder definieren:**
   - `name`: Anzeigename des Pakets
   - `description`: Kurzbeschreibung
   - `color`: Hex-Farbcode für den Pack-Indikator
   - `testCases`: Array der Testfälle
   - `testRuns`: Array oder Getter für Testläufe (kann leer sein)
   - `testRunResults`: Array oder Getter für Einzelergebnisse (kann leer sein)
   - `userStories`: Array der zugeordneten User Stories
   - `metadata`: Objekt mit `entities`, `attributes` und `optionsets`

3. **Pack-Selector erweitern:** Im HTML-Header eine neue `<option>` zum `#pack-selector` hinzufügen.

### Template-JSON

```javascript
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
                preconditions: { createAccount: true },
                steps: [
                    {
                        action: "CreateRecord",
                        entity: "account",
                        alias: "acc1",
                        fields: { name: "Testfirma" }
                    },
                    { action: "Wait", waitSeconds: 2 }
                ],
                assertions: [
                    {
                        target: "Record:acc1",
                        field: "name",
                        operator: "Equals",
                        value: "Testfirma"
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

Das neue Pack wird automatisch verfügbar, sobald es im `DemoPacks`-Objekt steht. Der Pack-Selector im HTML-Header muss manuell erweitert werden:

```html
<select id="pack-selector" class="form-select" style="width:250px">
    <option value="standard">Standard CRM (Sales & Service)</option>
    <option value="empty">Leere Vorlage</option>
    <option value="mein-pack">Mein Demo-Paket</option>
</select>
```

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
