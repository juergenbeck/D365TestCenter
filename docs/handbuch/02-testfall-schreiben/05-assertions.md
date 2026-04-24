# Assertions im Detail

Assertions sind der Kern jedes Tests: sie pruefen ob der erwartete Zustand
erreicht wurde. Dieses Dokument erklaert alle Varianten von
`Assert`-Actions, ihre Operatoren und typische Muster.

## Grundform

```json
{ "stepNumber": 4, "action": "Assert",
  "target":   "Record",
  "recordRef":"{RECORD:acc}",
  "field":    "websiteurl",
  "operator": "Equals",
  "value":    "https://example.com",
  "description": "Website wurde korrekt gesetzt",
  "onError":  "continue"
}
```

## Zwei Targets: Record vs Query

**`target: "Record"`** — prueft ein Feld eines bekannten Records.

```json
{ "action": "Assert",
  "target":    "Record",
  "recordRef": "{RECORD:acc}",
  "field":     "websiteurl",
  "operator":  "Equals",
  "value":     "https://example.com"
}
```

- Schnell (eine Single-Record-Abfrage auf Basis einer GUID).
- Braucht einen Alias oder eine GUID in `recordRef`.
- Funktioniert nur wenn der Record existiert.

**`target: "Query"`** — fuehrt eine Query aus und prueft das Ergebnis.

```json
{ "action": "Assert",
  "target":  "Query",
  "entity":  "contacts",
  "filter":  [
    { "field": "emailaddress1", "operator": "eq", "value": "anna@example.com" }
  ],
  "field":    "firstname",
  "operator": "Equals",
  "value":    "Anna"
}
```

- Flexibler: findet Records anhand beliebiger Kriterien.
- Funktioniert auch wenn der Record indirekt erzeugt wurde (Plugin, async).
- Pflicht fuer `Exists`, `NotExists`, `RecordCount`.

**Entscheidungs-Regel:**

| Situation | Nimm |
|---|---|
| Record wurde per `CreateRecord` / `WaitForRecord` unter Alias registriert | `target: "Record"` |
| Record wurde durch ein Plugin erzeugt, du kennst die GUID nicht | `target: "Query"` |
| Du willst pruefen dass etwas NICHT existiert | `target: "Query"` mit `NotExists` |
| Du willst die Anzahl pruefen | `target: "Query"` mit `RecordCount` |

## Operatoren im Detail

### String-Operatoren

| Operator | Erklaerung | Beispiel |
|---|---|---|
| `Equals` | Exakt gleich | `"operator": "Equals", "value": "Anna"` |
| `NotEquals` | Ungleich | `"operator": "NotEquals", "value": ""` |
| `Contains` | Teilstring enthalten | `"operator": "Contains", "value": "Test"` |
| `StartsWith` | Beginnt mit | `"operator": "StartsWith", "value": "JBE"` |
| `EndsWith` | Endet mit | `"operator": "EndsWith", "value": "@example.com"` |

Alle Vergleiche sind case-sensitive.

### Numerische Operatoren

| Operator | Erklaerung |
|---|---|
| `Equals` | Gleich (auch fuer Zahlen) |
| `NotEquals` | Ungleich |
| `GreaterThan` | Groesser als |
| `LessThan` | Kleiner als |

Beispiel:

```json
{ "action": "Assert",
  "target":    "Record", "recordRef": "{RECORD:opp}",
  "field":     "estimatedvalue",
  "operator":  "GreaterThan",
  "value":     "10000"
}
```

### Null-Pruefungen

| Operator | Erklaerung |
|---|---|
| `IsNull` | Feld ist leer / null |
| `IsNotNull` | Feld hat irgendeinen Wert |

```json
{ "action": "Assert",
  "target": "Record", "recordRef": "{RECORD:con}",
  "field":  "jobtitle",
  "operator": "IsNull",
  "description": "Jobtitle wurde geloescht"
}
```

`IsNull`/`IsNotNull` brauchen **keinen** `value`.

### Existenz-Operatoren (nur mit `target: Query`)

| Operator | Erklaerung |
|---|---|
| `Exists` | Query liefert mindestens 1 Record |
| `NotExists` | Query liefert 0 Records |
| `RecordCount` | Genaue Anzahl pruefen, `value` ist die Zahl |

Beispiele:

```json
{ "action": "Assert", "target": "Query",
  "entity": "opportunities",
  "filter": [ { "field": "customerid", "operator": "eq", "value": "{acc.id}" } ],
  "operator": "Exists",
  "description": "Account hat eine Opportunity"
}

{ "action": "Assert", "target": "Query",
  "entity": "tasks",
  "filter": [ { "field": "regardingobjectid", "operator": "eq", "value": "{opp.id}" } ],
  "operator": "RecordCount",
  "value":    "3",
  "description": "Es wurden genau 3 Follow-up-Tasks erzeugt"
}
```

### Datum-Operatoren

| Operator | Erklaerung |
|---|---|
| `DateSetRecently` | Feld wurde in den letzten X Sekunden gesetzt, `value` ist die Sekundenzahl |

```json
{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:opp}",
  "field":    "modifiedon",
  "operator": "DateSetRecently",
  "value":    "30",
  "description": "Plugin hat Opportunity in den letzten 30s modifiziert"
}
```

## Filter-Syntax (target: Query)

Der `filter` ist ein Array aus Bedingungen. Alle werden **AND** verknuepft:

```json
"filter": [
  { "field": "statecode",     "operator": "eq", "value": "0" },
  { "field": "customerid",    "operator": "eq", "value": "{acc.id}" },
  { "field": "estimatedvalue","operator": "gt", "value": "1000" }
]
```

### Filter-Operatoren (OData)

| Operator | Bedeutung |
|---|---|
| `eq` | gleich |
| `ne` | ungleich |
| `gt` | groesser als |
| `ge` | groesser oder gleich |
| `lt` | kleiner als |
| `le` | kleiner oder gleich |

Achtung: die Filter-Operatoren sind in **lowercase** (`eq`, nicht `Equals`).
Das ist OData-Standard.

### String-Funktionen im Filter

```json
"filter": [
  { "field": "name",         "operator": "contains",   "value": "Test" },
  { "field": "emailaddress1","operator": "startswith", "value": "anna." },
  { "field": "description",  "operator": "endswith",   "value": "wichtig." },
  { "field": "jobtitle",     "operator": "like",       "value": "%Leiter%" }
]
```

### Null im Filter

```json
"filter": [
  { "field": "websiteurl", "operator": "eq", "value": null }
]
```

### Lookup-Filter

Wichtig: im Filter nimmst du den LogicalName **ohne** `_` und **ohne**
`_value`:

```json
// RICHTIG
{ "field": "parentcustomerid", "operator": "eq", "value": "{acc.id}" }

// FALSCH
{ "field": "_parentcustomerid_value", "operator": "eq", "value": "{acc.id}" }
```

Die Engine uebersetzt automatisch.

## recordRef-Varianten

```json
"recordRef": "{RECORD:acc}"                               // empfohlen, mit Alias
"recordRef": "{acc.id}"                                   // gleicher Effekt
"recordRef": "3f2a1b4e-9c27-40d1-b9a2-0e5fa2c4a1d3"       // Direkt-GUID (selten)
```

## description — warum sie wichtig ist

Jede Assert sollte eine `description` haben. Im Steps-Tab und im
`jbe_errormessage` taucht sie auf, wenn die Assert fehlschlaegt. Ohne
description weisst du hinterher nur "irgendein Assert auf `markant_x` ist
gescheitert, erwartet war `1`, bekommen habe ich `0`". Mit description
weisst du direkt: "Merge-Plugin hat die Deaktivierung nicht durchgefuehrt".

**Guter Stil:**

```json
"description": "Sub-Contact ist nach Merge deaktiviert"
"description": "Website wurde korrekt vom Plugin gesetzt"
"description": "Neue Follow-up-Task an die Opportunity gehaengt"
```

**Schlechter Stil:**

```json
"description": "statecode Equals 1"             // sagt nur was im Feld steht, nicht warum
"description": "Check"                          // nichtssagend
"description": ""                               // leer
```

## onError — Default ist "continue"

Fuer Asserts ist `onError` automatisch `continue`: wenn eine Assert
fehlschlaegt, laufen die naechsten trotzdem. Am Ende ist der Test
`Failed`, aber du hast **alle** Pruefungen gesehen.

Wenn du willst dass der Test beim ersten Assert-Fehlschlag abbricht:

```json
{ "action": "Assert", ..., "onError": "stop" }
```

Das ist selten sinnvoll. Meistens willst du alle Failures auf einen Blick.

## Beispiele typischer Muster

### Pruefen dass ein Create zu korrektem Zustand fuehrt

```json
{ "action": "CreateRecord", "entity": "leads", "alias": "lead1",
  "fields": { "firstname": "Anna", "lastname": "Meier",
              "subject": "Anfrage" } },

{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:lead1}",
  "field": "statecode", "operator": "Equals", "value": "0",
  "description": "Lead ist nach Create aktiv", "onError": "continue" },

{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:lead1}",
  "field": "subject", "operator": "Equals", "value": "Anfrage",
  "description": "Subject korrekt gespeichert", "onError": "continue" }
```

### Pruefen dass ein Update eine Plugin-Kette ausloest

```json
{ "action": "UpdateRecord", "alias": "opp",
  "fields": { "estimatedvalue": 100000 } },

{ "action": "Wait", "waitSeconds": 3 },

{ "action": "Assert", "target": "Query", "entity": "tasks",
  "filter": [ { "field": "regardingobjectid", "operator": "eq", "value": "{opp.id}" },
              { "field": "subject",           "operator": "contains", "value": "Genehmigung" } ],
  "operator": "Exists",
  "description": "Approval-Task wurde vom Plugin erzeugt",
  "onError": "continue" }
```

### Pruefen dass eine Action negative Seiteneffekte hat

```json
{ "action": "ExecuteRequest", "requestName": "Merge",
  "fields": { "Target": { "$type": "EntityReference", "entity": "contact", "ref": "master" },
              "SubordinateId": { "$type": "Guid", "ref": "duplicate" },
              "UpdateContent": { "$type": "Entity", "entity": "contact", "fields": {} },
              "PerformParentingChecks": false },
  "waitSeconds": 5 },

{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:duplicate}",
  "field": "statecode", "operator": "Equals", "value": "1",
  "description": "Subordinate ist nach Merge deaktiviert",
  "onError": "continue" },

{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:master}",
  "field": "statecode", "operator": "Equals", "value": "0",
  "description": "Master bleibt aktiv",
  "onError": "continue" }
```

Weiter mit [06-coverage-regeln.md](06-coverage-regeln.md) — wie viele
Asserts sind genug?
