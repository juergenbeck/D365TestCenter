# Testfall-Spezifikation

Dieses Dokument beschreibt das JSON-Schema eines Testfalls, alle verfügbaren Actions, Assertions, Platzhalter und den Testfall-Lebenszyklus im Integration Test Center.

## JSON-Schema eines Testfalls

Jeder Testfall wird als JSON-Objekt im Feld `itt_definition_json` gespeichert. Das Objekt hat drei Hauptabschnitte:

```json
{
  "preconditions": { ... },
  "steps": [ ... ],
  "assertions": [ ... ]
}
```

### Feldübersicht

| Feld | Typ | Pflicht | Beschreibung |
|------|-----|---------|--------------|
| `preconditions` | Object | Optional | Vorbedingungen: welche Datensätze vor dem Test existieren müssen |
| `preconditions.createAccount` | Boolean | Optional | Wenn `true`, wird ein Account als Testdatum erstellt (Standard: `true`) |
| `preconditions.createContact` | Boolean | Optional | Wenn `true`, wird ein Contact als Testdatum erstellt (Standard: `true`) |
| `preconditions.createOpportunity` | Boolean | Optional | Wenn `true`, wird eine Opportunity erstellt |
| `preconditions.createCase` | Boolean | Optional | Wenn `true`, wird ein Case (Incident) erstellt |
| `preconditions.accountFields` | Object | Optional | Feldwerte für den erstellten Account |
| `preconditions.contactFields` | Object | Optional | Feldwerte für den erstellten Contact |
| `preconditions.existingContactSources` | Array | Optional | Liste vorab existierender ContactSources (Field-Governance-Szenario) |
| `preconditions.existingContactSources[].alias` | String | Pflicht | Alias-Name der ContactSource (z.B. `"pisa1"`) |
| `preconditions.existingContactSources[].sourceSystem` | Integer | Pflicht | OptionSet-Wert des Quellsystems (1=Platform, 3=Plattform, 4=PISA) |
| `preconditions.existingContactSources[].fields` | Object | Optional | Initialwerte für Felder der ContactSource |
| `steps` | Array | Pflicht | Geordnete Liste der Testschritte (Actions) |
| `steps[].action` | String | Pflicht | Name der auszuführenden Action |
| `steps[].alias` | String | Optional | Alias für den erstellten/aktualisierten Datensatz |
| `steps[].entity` | String | Bedingt | Logischer Entity-Name (Pflicht bei `CreateRecord`) |
| `steps[].fields` | Object | Optional | Feldwerte als Key-Value-Paare |
| `steps[].waitSeconds` | Integer | Bedingt | Wartezeit in Sekunden (Pflicht bei `Wait`) |
| `steps[].waitForAsync` | Boolean | Optional | Wenn `true`, wartet der Schritt auf asynchrone Verarbeitung |
| `steps[].maxDurationMs` | Integer | Optional | Maximale Wartezeit in Millisekunden für diesen Schritt |
| `assertions` | Array | Pflicht | Liste der erwarteten Ergebnisse |
| `assertions[].target` | String | Pflicht | Ziel der Assertion (z.B. `"Contact"`, `"Record:alias"`) |
| `assertions[].field` | String | Pflicht | Feldname, der geprüft wird |
| `assertions[].operator` | String | Pflicht | Vergleichsoperator (z.B. `"Equals"`, `"Contains"`) |
| `assertions[].value` | String | Bedingt | Erwarteter Wert (nicht bei `IsNull`/`IsNotNull`) |
| `assertions[].entity` | String | Bedingt | Entity-Name bei `Query`-Target |
| `assertions[].filter` | Object | Bedingt | Filterkriterien bei `Query`-Target |

## Actions

Der TestRunner unterstützt die folgenden Actions als Testschritte:

### CreateRecord

Erstellt einen beliebigen Datensatz in Dataverse.

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"CreateRecord"` |
| `entity` | String | Pflicht | Logischer Entity-Name (z.B. `"account"`, `"lead"`, `"markant_bridge_pf_record"`) |
| `alias` | String | Optional | Alias zur späteren Referenzierung per `{alias.id}` oder `Record:alias` |
| `fields` | Object | Pflicht | Feldwerte als Key-Value-Paare |
| `waitForAsync` | Boolean | Optional | Wartet auf asynchrone Verarbeitung nach dem Create |
| `maxDurationMs` | Integer | Optional | Maximale Wartezeit in ms |

```json
{
  "action": "CreateRecord",
  "entity": "lead",
  "alias": "lead1",
  "fields": {
    "firstname": "{GENERATED:firstname}",
    "lastname": "{GENERATED:lastname}",
    "emailaddress1": "{GENERATED:email}",
    "companyname": "Firma Alpha GmbH",
    "leadqualitycode": 1
  }
}
```

### UpdateRecord

Aktualisiert einen bestehenden Datensatz über seinen Alias.

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"UpdateRecord"` |
| `alias` | String | Pflicht | Alias des zuvor erstellten Datensatzes |
| `fields` | Object | Pflicht | Zu aktualisierende Feldwerte |
| `waitForAsync` | Boolean | Optional | Wartet auf asynchrone Verarbeitung |

```json
{
  "action": "UpdateRecord",
  "alias": "lead1",
  "fields": {
    "statuscode": 3
  },
  "waitForAsync": true
}
```

### DeleteRecord

Löscht einen Datensatz aus Dataverse.

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"DeleteRecord"` |
| `alias` | String | Pflicht | Alias des zu löschenden Datensatzes |

```json
{
  "action": "DeleteRecord",
  "alias": "acc_dup"
}
```

### CreateContactSource (Field-Governance-spezifisch)

Erstellt eine neue ContactSource-Entität im Field-Governance-Kontext.

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"CreateContactSource"` |
| `alias` | String | Optional | Alias für die neue ContactSource |
| `fields` | Object | Pflicht | Feldwerte inkl. `itt_sourcesystem` |

```json
{
  "action": "CreateContactSource",
  "alias": "newcs",
  "fields": {
    "itt_firstname": "Neu",
    "itt_lastname": "Kontakt",
    "itt_sourcesystem": 4
  }
}
```

### UpdateContactSource (Field-Governance-spezifisch)

Aktualisiert eine bestehende ContactSource.

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"UpdateContactSource"` |
| `alias` | String | Pflicht | Alias der ContactSource (aus Preconditions oder vorherigem Create) |
| `fields` | Object | Pflicht | Zu aktualisierende Feldwerte |

```json
{
  "action": "UpdateContactSource",
  "alias": "pisa1",
  "fields": {
    "itt_firstname": "Maximilian"
  }
}
```

### CallGovernanceApiContact (Field-Governance-spezifisch)

Ruft die Governance-API für den aktuellen Contact auf. Löst die Field-Governance-Berechnung aus.

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"CallGovernanceApiContact"` |
| `fields` | Object | Optional | Zusätzliche Parameter für den API-Aufruf |

```json
{
  "action": "CallGovernanceApiContact",
  "fields": {}
}
```

### ExecuteAction

Ruft eine Custom API (Action oder Function) in Dataverse auf.

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"ExecuteAction"` |
| `apiName` | String | Pflicht | Vollständiger Name der Custom API (z.B. `"itt_RunIntegrationTests"`) |
| `parameters` | Object | Optional | Eingabeparameter für die Custom API |

```json
{
  "action": "ExecuteAction",
  "apiName": "itt_RunIntegrationTests",
  "parameters": {}
}
```

### Wait

Pausiert die Testausführung für eine feste Zeitdauer.

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"Wait"` |
| `waitSeconds` | Integer | Pflicht | Wartezeit in Sekunden |

```json
{
  "action": "Wait",
  "waitSeconds": 5
}
```

### Delay

Funktional identisch mit `Wait`, alternative Benennung.

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"Delay"` |
| `waitSeconds` | Integer | Pflicht | Wartezeit in Sekunden |

### AssertEnvironment

Prüft Umgebungsvoraussetzungen vor dem eigentlichen Testlauf (Pre-Flight-Diagnostics).

| Parameter | Typ | Pflicht | Beschreibung |
|-----------|-----|---------|--------------|
| `action` | String | Pflicht | `"AssertEnvironment"` |

Die Custom API `itt_AssertEnvironment` ist als Function registriert und validiert, ob die notwendigen Entities und Konfigurationen vorhanden sind.

## Assertions

### Assertion-Struktur

Jede Assertion prüft einen einzelnen Feldwert an einem bestimmten Ziel.

```json
{
  "target": "Record:lead1",
  "field": "statuscode",
  "operator": "Equals",
  "value": "3"
}
```

| Feld | Typ | Pflicht | Beschreibung |
|------|-----|---------|--------------|
| `target` | String | Pflicht | Ziel-Referenz (siehe unten) |
| `field` | String | Pflicht | Logischer Feldname |
| `operator` | String | Pflicht | Vergleichsoperator |
| `value` | String | Bedingt | Erwarteter Wert (nicht bei IsNull/IsNotNull) |
| `entity` | String | Bedingt | Entity-Name bei Query-Target |
| `filter` | Object | Bedingt | Filterkriterien bei Query-Target |

### Assertion-Targets

| Target | Syntax | Beschreibung |
|--------|--------|--------------|
| Record-Alias | `Record:<alias>` | Prüft ein Feld des per Alias referenzierten Datensatzes (z.B. `Record:lead1`, `Record:bridge1`) |
| Contact | `Contact` | Prüft ein Feld des im Test erstellten Contacts |
| ContactSource | `ContactSource:<alias>` | Prüft ein Feld einer spezifischen ContactSource (z.B. `ContactSource:pisa1`) |
| AuditLog | `AuditLog` | Prüft Felder im Audit Logging (z.B. `markant_decisions`) |
| Query | `Query` | Führt eine Abfrage aus und prüft das Ergebnis. Erfordert `entity` und `filter` |
| PlatformBridge | `PlatformBridge` | Prüft den Status eines PlatformBridge-Datensatzes |

### Query-Target im Detail

Bei `Query`-Assertions wird eine OData-Abfrage ausgeführt:

```json
{
  "target": "Query",
  "entity": "opportunity",
  "filter": {
    "originatingleadid": "{lead1.id}"
  },
  "field": "name",
  "operator": "Contains",
  "value": "{lead1.lastname}"
}
```

Das `filter`-Objekt wird in einen OData-`$filter`-Ausdruck übersetzt. Platzhalter werden dabei aufgelöst.

### Operatoren

| Operator | Beschreibung | Beispiel |
|----------|--------------|---------|
| `Equals` | Exakte Übereinstimmung | `{ "operator": "Equals", "value": "Maximilian" }` |
| `NotEquals` | Ungleichheit | `{ "operator": "NotEquals", "value": "{CURRENT_USER}" }` |
| `Contains` | Teilstring-Prüfung | `{ "operator": "Contains", "value": "NoChange" }` |
| `IsNull` | Feld ist leer/null | `{ "operator": "IsNull" }` |
| `IsNotNull` | Feld hat einen Wert | `{ "operator": "IsNotNull" }` |
| `GreaterThan` | Numerisch größer als | `{ "operator": "GreaterThan", "value": "0" }` |
| `LessThan` | Numerisch kleiner als | `{ "operator": "LessThan", "value": "1000" }` |

## Platzhalter-Engine

Platzhalter werden vor der Ausführung eines Testschritts oder einer Assertion durch konkrete Werte ersetzt.

### Verfügbare Platzhalter

| Platzhalter | Beschreibung | Auflösungszeitpunkt | Wiederverwendung |
|-------------|--------------|---------------------|------------------|
| `{GENERATED:firstname}` | Generierter Vorname | Beim ersten Auftreten im Testfall | Ja, gleicher Wert im gesamten Testfall |
| `{GENERATED:lastname}` | Generierter Nachname | Beim ersten Auftreten im Testfall | Ja, gleicher Wert im gesamten Testfall |
| `{GENERATED:email}` | Generierte E-Mail-Adresse | Beim ersten Auftreten im Testfall | Ja, gleicher Wert im gesamten Testfall |
| `{GENERATED:text}` | Generierter Freitext | Beim ersten Auftreten im Testfall | Ja, gleicher Wert im gesamten Testfall |
| `{TIMESTAMP}` | Aktueller UTC-Zeitstempel (ISO 8601) | Zum Zeitpunkt der Auflösung | Nein, jeder Aufruf erzeugt neuen Wert |
| `{TIMESTAMP_PLUS_1H}` | Zeitstempel + 1 Stunde | Zum Zeitpunkt der Auflösung | Nein |
| `{NEW_GUID}` / `{GUID}` | Neue zufällige GUID | Zum Zeitpunkt der Auflösung | Nein, jeder Aufruf erzeugt neue GUID |
| `{CONTACT_ID}` | ID des im Testfall erstellten Contacts | Nach Precondition-Phase | Ja |
| `{ACCOUNT_ID}` | ID des im Testfall erstellten Accounts | Nach Precondition-Phase | Ja |
| `{CURRENT_USER}` | ID des aktuell angemeldeten Benutzers | Bei Testlauf-Start | Ja |
| `{alias.id}` | ID eines per Alias referenzierten Datensatzes (z.B. `{lead1.id}`) | Nach Erstellung des Datensatzes | Ja |
| `{alias.fieldname}` | Feldwert eines per Alias referenzierten Datensatzes (z.B. `{lead1.lastname}`) | Nach Erstellung des Datensatzes | Ja |
| `{CS:alias}` | ID einer ContactSource | Nach Erstellung/Bereitstellung der ContactSource | Ja |
| `{ROW:feldname}` | Wert aus einer Datenzeile (für datengetriebene Tests) | Bei Testschritt-Ausführung | Ja, innerhalb der Zeile |

### Auflösungsregeln

- `{GENERATED:*}`-Platzhalter werden beim ersten Auftreten im Testfall generiert und danach wiederverwendet. So ist z.B. `{GENERATED:lastname}` in Steps und Assertions identisch.
- `{alias.id}` und `{alias.fieldname}` setzen voraus, dass der Datensatz mit dem angegebenen Alias bereits in einem vorherigen Schritt erstellt wurde.
- `{CONTACT_ID}` und `{ACCOUNT_ID}` werden aus den Preconditions befüllt und sind in allen Steps und Assertions verfügbar.
- `{TIMESTAMP}` wird bei jeder Verwendung neu erzeugt, daher sind zwei Vorkommen im selben Testfall nicht zwingend identisch.

## WaitForRecord

Der Polling-Mechanismus wird über die Step-Eigenschaften `waitForAsync` und `maxDurationMs` gesteuert.

### Funktionsweise

Wenn `waitForAsync: true` auf einem Step gesetzt ist, wartet der TestRunner nach der Ausführung der Action darauf, dass asynchrone Plugins, Workflows oder Custom Actions abgeschlossen sind.

### Konfiguration

| Parameter | Typ | Standard | Beschreibung |
|-----------|-----|----------|--------------|
| `waitForAsync` | Boolean | `false` | Aktiviert das Polling nach asynchroner Verarbeitung |
| `maxDurationMs` | Integer | `120000` (120s) | Maximale Wartezeit in Millisekunden |
| `pollingIntervalMs` | Integer | `1500` - `2500` | Intervall zwischen Polling-Anfragen (variiert leicht für Realismus) |

### Timeout-Verhalten

- Innerhalb der `maxDurationMs` wird in regelmäßigen Intervallen geprüft, ob die erwartete Zustandsänderung eingetreten ist.
- Bei Überschreitung der maximalen Wartezeit wird der Testschritt mit dem Outcome `Failed` und der Fehlermeldung `Timeout` abgebrochen.
- Der Gesamttestfall läuft weiter, die nachfolgenden Assertions werden gegen den aktuellen (möglicherweise unvollständigen) Zustand geprüft.
- Im Demo-Modus simuliert der TestRunner die Verarbeitung mit `setTimeout`-Aufrufen im Intervall von 1500 bis 2500 ms pro Testfall.

### Konfiguration auf Testlauf-Ebene

Beim Starten eines Testlaufs kann ein globaler Timeout pro Testfall eingestellt werden (Standardwert: 120 Sekunden). Dieser wird als Obergrenze verwendet, wenn ein Step kein eigenes `maxDurationMs` definiert.

## Testfall-Lebenszyklus

### 1. Erstellen

- Testfall wird über den JSON-Editor im Test Center erstellt oder per PowerShell-Import.
- Metadaten (Test ID, Titel, Kategorie, Tags, User Stories) werden als Felder des `itt_testcase`-Datensatzes gespeichert.
- Die JSON-Definition wird im Memo-Feld `itt_definition_json` abgelegt.

### 2. Validieren

- Beim Speichern wird die JSON-Definition clientseitig mit `JSON.parse()` validiert.
- Ungültiges JSON führt zu einer Fehlermeldung und verhindert das Speichern.
- Die Flow-Visualisierung (SVG-Pipeline) zeigt die Teststruktur in drei Spalten: Vorbedingungen, Testschritte, Assertions.

### 3. Ausführen

- Der Testlauf wird über die UI gestartet (Filteroptionen: alle, Kategorie, Tag, User Story, einzelne IDs).
- Ein `itt_testrun`-Datensatz wird erstellt (Status: `Geplant`, dann `Läuft`).
- Optional wird die Custom API `itt_RunIntegrationTests` aufgerufen.
- Jeder Testfall wird sequentiell verarbeitet:
  1. Preconditions werden aufgebaut (Account, Contact, ContactSources).
  2. Steps werden der Reihe nach ausgeführt, Platzhalter aufgelöst.
  3. Assertions werden gegen den resultierenden Zustand geprüft.
- Pro Testfall wird ein `itt_testrunresult`-Datensatz mit Outcome, Dauer und Fehlermeldung erstellt.
- Der `itt_testrun`-Datensatz wird laufend aktualisiert (Passed/Failed-Zähler, Log).
- Die UI pollt alle 3 Sekunden den aktuellen Zustand und zeigt Live-Fortschritt.

### 4. Ergebnis

- Nach Abschluss: Status wechselt auf `Abgeschlossen` (oder `Fehler` bei kritischem Abbruch).
- Ergebnis umfasst: Pie-Chart (Passed/Failed/Andere), Zusammenfassung, vollständiges Log, Einzelergebnisse pro Testfall.
- Flow-Visualisierung zeigt Assertions mit farblicher Kodierung: Grün (Passed), Rot (Failed).
- Der Testverlauf pro Testfall-ID ist über die History-Ansicht einsehbar (Balkenchart der Dauer, Ergebnistabelle).
