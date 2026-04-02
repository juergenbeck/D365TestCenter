# Demo-Pakete

Das Integration Test Center enthält ein modulares Demo-Paket-System (`DemoPacks`), das im Demo-Modus realistische Testdaten bereitstellt. Vier vordefinierte Pakete decken unterschiedliche Szenarien ab.

## Übersicht der 4 Demo-Pakete

| # | Name | Beschreibung | Farbe | Testfälle | Entities |
|---|------|-------------|-------|-----------|----------|
| 1 | Standard CRM (Sales & Service) | Standardtabellen aus Dynamics 365 Sales und Service | Blau (#42a5f5) | 8 | Account, Contact, Opportunity, Lead, Incident, Task, ActivityPointer |
| 2 | Field Governance (Bridge) | ContactSource, PlatformBridge, Field Governance und Logging | Violett (#ab47bc) | 16 | Contact, ContactSource, PlatformBridge, Audit Logging, Account |
| 3 | Membership & Rollen (Platform) | Rollenzuweisung, Mitgliedschaften und Buchungen | Orange (#ff9800) | 6 | Contact, PlatformBridge, RoleAssignment, Membership, Booking |
| 4 | Leere Vorlage | Leere Umgebung mit einem Beispiel-Testfall | Grau (#78909c) | 1 | (nach Bedarf) |

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

## Pack 2: Field Governance (Bridge)

**Beschreibung:** Testet ContactSource, PlatformBridge, Field Governance und Audit Logging. Dieses Paket benötigt die zugehörigen Custom-Entities.

**Benötigte Custom-Entities:** `markant_fg_contactsource` (Contact Source), `markant_bridge_pf_record` (Platform Bridge), `markant_fg_logging` (Audit Logging)

**User Stories:** DYN-8621 (Field Governance LUW-Verarbeitung), DYN-8768 (ContactSource Create und Priority), DYN-5522 (PlatformBridge E2E Integration), DYN-8888 (Booking- und Generic-Events)

### Testfälle im Detail

#### TC01: LUW Single Source - Update Firstname

- **Kategorie:** UpdateSource
- **Tags:** LUW, SingleSource
- **User Story:** DYN-8621
- **Preconditions:** Account, Contact und eine ContactSource (PISA, alias "pisa1")
- **Steps:** UpdateContactSource mit neuem Vornamen, Wait 5s, CallGovernanceApiContact
- **Assertions:** Contact.firstname und ContactSource.itt_firstname prüfen

#### TC02: LUW All Fields Update

- **Kategorie:** UpdateSource
- **Tags:** LUW, AllFields
- **User Story:** DYN-8621
- **Preconditions:** Account, Contact und eine ContactSource (PISA)
- **Steps:** UpdateContactSource mit Vorname, Nachname, E-Mail, Telefon, Wait 5s, GovernanceAPI
- **Assertions:** Alle vier Felder am Contact prüfen

#### TC03: Priority Order Single Source

- **Kategorie:** UpdateSource
- **Tags:** PriorityOrder
- **User Story:** DYN-8768
- **Preconditions:** Account, Contact und zwei ContactSources (PISA + Plattform)
- **Steps:** UpdateContactSource "plat1" mit "LowPrio", dann "pisa1" mit "HighPrio", Wait 5s, GovernanceAPI
- **Assertions:** Contact.firstname muss "HighPrio" sein (PISA hat höhere Priorität)

#### TC05: Multi-Source LUW - 3 Sources

- **Kategorie:** MultiSource
- **Tags:** LUW, MultiSource
- **User Stories:** DYN-8621, DYN-5522
- **Preconditions:** Account, Contact und drei ContactSources (PISA + 2x Platform)
- **Steps:** Alle drei Sources aktualisieren, Wait 8s, GovernanceAPI
- **Assertions:** Contact.firstname = "PisaName" (höchste Priorität), statecode aller Sources = 0

#### TC06: No-Op Same Value

- **Kategorie:** UpdateSource
- **Tags:** NoOp
- **Preconditions:** Contact mit firstname "Bestand", ContactSource mit identischem Wert
- **Steps:** UpdateContactSource mit unverändertem Wert, Wait 3s, GovernanceAPI
- **Assertions:** Contact.firstname bleibt "Bestand", AuditLog enthält "NoChange"

#### TC08: Conditional Mapping Salutation

- **Kategorie:** UpdateSource
- **Tags:** Mapping
- **Steps:** UpdateContactSource mit Anrede "Herr" und Vorname "Hans", Wait 5s, GovernanceAPI
- **Assertions:** Contact.salutation = "Herr", Contact.firstname = "Hans"

#### TCC01: Create New ContactSource

- **Kategorie:** CreateSource
- **Tags:** Create
- **User Story:** DYN-8768
- **Preconditions:** Account und Contact (ohne ContactSource)
- **Steps:** CreateContactSource mit Vorname, Nachname und Quellsystem PISA, Wait 5s, GovernanceAPI
- **Assertions:** ContactSource.statecode = 0, Contact.firstname = "Neu"

#### TCC02: Create With All Fields

- **Kategorie:** CreateSource
- **Tags:** Create, AllFields
- **User Story:** DYN-8768
- **Steps:** CreateContactSource mit allen Feldern (Vorname, Nachname, E-Mail, Telefon, Quellsystem Platform)
- **Assertions:** Alle Felder am Contact prüfen

#### TCMF01: Multi-Field Update Batch

- **Kategorie:** AdditionalFields
- **Tags:** MultiField
- **Steps:** UpdateContactSource mit drei Feldern gleichzeitig, Wait 5s, GovernanceAPI
- **Assertions:** Alle drei Felder am Contact prüfen

#### BTC01: Bridge E2E - UserCreatedEvent

- **Kategorie:** Bridge
- **Tags:** Bridge, E2E
- **User Story:** DYN-5522
- **Steps:** CreateRecord auf `markant_bridge_pf_record` mit EventType "UserCreatedEvent" und JSON-Payload, Wait 10s
- **Assertions:** BridgeStatus = 100000002 (Completed), Contact.firstname = "BridgeUser"

#### BTC02: Bridge E2E - UserUpdatedEvent

- **Kategorie:** Bridge
- **Tags:** Bridge, E2E
- **User Story:** DYN-5522
- **Preconditions:** Account und Contact
- **Steps:** CreateRecord auf PlatformBridge mit "UserUpdatedEvent", Wait 10s
- **Assertions:** BridgeStatus = Completed, Contact.firstname = "UpdatedBridge"

#### BTC03: Bridge E2E - BookingCreatedEvent

- **Kategorie:** Bridge
- **Tags:** Bridge, Booking
- **User Story:** DYN-8888
- **Steps:** CreateRecord auf PlatformBridge mit "BookingCreatedEvent" und Booking-Daten, Wait 10s
- **Assertions:** BridgeStatus = Completed

#### ERR01: Ungültiger EventType

- **Kategorie:** ErrorInjection
- **Tags:** Error
- **Steps:** CreateRecord auf PlatformBridge mit ungültigem EventType "InvalidEventType_XYZ"
- **Assertions:** BridgeStatus = 100000003 (Error)

#### ERR03: Account Not Found

- **Kategorie:** ErrorInjection
- **Tags:** Error, AccountLookup
- **Preconditions:** Kein Account (createAccount: false), aber Contact
- **Steps:** CreateRecord auf PlatformBridge mit nicht existierender AccountPlatformId
- **Assertions:** BridgeStatus = Error, AuditLog enthält "AccountNotFound"

#### ERR05: Malformed JSON Payload

- **Kategorie:** ErrorInjection
- **Tags:** Error, JSON
- **Steps:** CreateRecord auf PlatformBridge mit ungültigem JSON-String als EventData
- **Assertions:** BridgeStatus = Error

#### GEN01: Generic Bridge Record E2E

- **Kategorie:** Bridge
- **Tags:** Generic
- **User Story:** DYN-8888
- **Steps:** CreateRecord auf PlatformBridge mit "GenericSyncEvent", Wait 10s
- **Assertions:** BridgeStatus = Completed

## Pack 3: Membership & Rollen (Platform)

**Beschreibung:** Testet die Rollenverwaltung, Mitgliedschafts-Synchronisation, Buchungen und Platform-Interface Events über die PlatformBridge.

**Benötigte Custom-Entities:** `markant_bridge_pf_record` (Platform Bridge), `markant_roleassignment` (Rollenzuweisung), `markant_membership` (Mitgliedschaft), `markant_booking` (Buchung)

**User Stories:** PROJ-3001 (Rollenverwaltung und Membership-Sync), PROJ-3002 (Booking-Integration und Fehlerbehandlung)

### Testfälle im Detail

#### MEM-TC01: Rollenzuweisung: UserRolesChangedEvent verarbeiten

- **Kategorie:** Bridge
- **Tags:** Rolle, Event, Delta
- **User Story:** PROJ-3001
- **Preconditions:** Account und Contact
- **Steps:** CreateRecord auf PlatformBridge mit "UserRolesChangedEvent", Payload enthält AddedRoles ["Admin", "Editor"], Wait 10s
- **Assertions:** BridgeStatus = Completed, Query auf RoleAssignment prüft ob "Admin"-Rolle vorhanden

#### MEM-TC02: Membership erstellen: OrganizationMembershipCreated

- **Kategorie:** Bridge
- **Tags:** Membership, Event
- **User Story:** PROJ-3001
- **Steps:** CreateRecord auf PlatformBridge mit "OrganizationMembershipCreated" und OrganizationId, Wait 10s
- **Assertions:** BridgeStatus = Completed

#### MEM-TC03: Membership löschen: Cleanup RoleAssignments

- **Kategorie:** Bridge
- **Tags:** Membership, Delete, Cascade
- **User Story:** PROJ-3002
- **Steps:** CreateRecord auf PlatformBridge mit "OrganizationMembershipDeleted", Wait 10s
- **Assertions:** BridgeStatus = Completed, RoleAssignment.statecode = 1 (deaktiviert)

#### MEM-TC04: Booking erstellen: CartridgeVariant-Zuordnung

- **Kategorie:** Bridge
- **Tags:** Booking, Cartridge
- **User Story:** PROJ-3002
- **Steps:** CreateRecord auf PlatformBridge mit "BookingCreatedEvent" und CartridgeVariantId, Wait 10s
- **Assertions:** BridgeStatus = Completed

#### MEM-TC05: Rollen-Init-Load: 1000 Rollen Batch-Import

- **Kategorie:** Bridge
- **Tags:** Rolle, Init, Batch, Performance
- **User Story:** PROJ-3001
- **Steps:** CreateRecord auf PlatformBridge mit "BulkRoleInitEvent", BatchSize 1000, waitForAsync mit 60s Timeout
- **Assertions:** BridgeStatus = Completed

#### MEM-TC06: Fehler: Membership vor User-Create (Race Condition)

- **Kategorie:** ErrorInjection
- **Tags:** Membership, Error, Timing
- **User Story:** PROJ-3002
- **Preconditions:** Nur Account (kein Contact)
- **Steps:** CreateRecord auf PlatformBridge mit "OrganizationMembershipCreated" und nicht existierender UserID
- **Assertions:** BridgeStatus = Error (Kontakt existiert noch nicht)

## Pack 4: Leere Vorlage

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
            itt_testcaseid: "aaaaaaaa-0001-4000-8000-bbbbbbbbbbbb",
            itt_testid: "MEIN-TC01",
            itt_title: "Erster eigener Testfall",
            itt_category: 100000005,
            itt_tags: "Demo",
            itt_userstories: "PROJ-9999",
            itt_enabled: true,
            itt_definition_json: JSON.stringify({
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
    <option value="field-governance">Field Governance (Bridge)</option>
    <option value="membership">Membership & Rollen (Platform)</option>
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
            itt_testrunid: "...",
            itt_teststatus: 100000002,  // Abgeschlossen
            itt_passed: 5,
            itt_failed: 1,
            itt_total: 6,
            itt_started_on: _makeDate(0, 10, 0),   // heute, 10:00
            itt_completed_on: _makeDate(0, 10, 8),  // heute, 10:08
            itt_testcasefilter: "*",
            itt_testsummary: "6 Tests ausgeführt.",
            itt_fulllog: "..."
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
