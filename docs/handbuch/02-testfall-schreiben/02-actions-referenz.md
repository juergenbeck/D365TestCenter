# Actions-Referenz

Vollständige Liste aller Actions, ihrer Pflicht- und Optional-Felder,
typische Fallen, und wofür sie da sind.

## Übersicht

| Action | Zweck |
|---|---|
| [`CreateRecord`](#createrecord) | Neuen Record anlegen |
| [`UpdateRecord`](#updaterecord) | Bestehenden Record aktualisieren |
| [`DeleteRecord`](#deleterecord) | Record löschen |
| [`RetrieveRecord`](#retrieverecord) | Record neu laden (Aktualisiere Alias) |
| [`Wait`](#wait) | Feste Wartezeit |
| [`WaitForRecord` / `FindRecord`](#waitforrecord--findrecord) | Warten bis ein Record existiert (mit `orderBy`/`top` ab v5.3) |
| [`WaitForFieldValue`](#waitforfieldvalue) | Warten bis ein Feld einen Wert hat |
| [`ExecuteRequest`](#executerequest) | Beliebige SDK-Message (Merge, QualifyLead, ...) |
| [`ExecuteAction`](#executeaction) | Custom Action aufrufen |
| [`SetEnvironmentVariable`](#setenvironmentvariable-plugin-v53) | Environment-Variable setzen mit Auto-Restore (v5.3+) |
| [`RetrieveEnvironmentVariable`](#retrieveenvironmentvariable-plugin-v53) | Environment-Variable lesen (v5.3+) |
| [`Assert`](#assert) | Ergebnis prüfen |

**Negative-Path-Tests (v5.3+):** Für Steps die als erwartetes Ergebnis
einen Fehler werfen sollen, gibt es zwei optionale Felder
`expectFailure` und `expectException` — siehe [09-negative-path.md](09-negative-path.md).

## CreateRecord

Legt einen neuen Record an.

```json
{ "stepNumber": 1, "action": "CreateRecord",
  "entity": "accounts",
  "alias":  "acc",
  "fields": { "name": "JBE Test {TIMESTAMP}" },
  "columns": ["accountnumber", "createdon"]
}
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `entity` | ja | EntitySetName (Plural!): `accounts`, `contacts`, `leads`. |
| `fields` | ja | Feldwerte als Objekt. Inkl. Platzhalter. |
| `alias` | nein | Name zum späteren Referenzieren. Empfohlen. |
| `columns` | nein | Felder, die nach dem Create zurückgelesen werden sollen (für AutoNumber, Server-generierte Werte). |
| `description` | nein | Log-Kommentar. |

**Besonderheiten:**

- Ohne `alias` kannst du den Record später nicht mehr referenzieren.
- Lookup-Felder brauchen `@odata.bind`-Syntax — siehe
  [04-lookup-und-binding.md](04-lookup-und-binding.md).
- `columns` ist nützlich für Felder die du nicht setzt, aber im Test
  brauchst: z.B. `"columns": ["accountnumber"]` lädt die nach dem Create
  erzeugte AutoNumber, und du kommst per `{acc.fields.accountnumber}` an
  den Wert.
- Der Record wird automatisch im Record-Tracker registriert und am Ende
  des Tests gelöscht (außer `jbe_keeprecords=true` am Testrun).

## UpdateRecord

Aktualisiert einen bereits vorhandenen Record.

```json
{ "stepNumber": 5, "action": "UpdateRecord",
  "alias":  "acc",
  "fields": { "websiteurl": "https://new.example.com",
              "numberofemployees": 50 }
}
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `alias` | ja | Alias eines vorher angelegten Records. |
| `fields` | ja | Die zu ändernden Felder. |

**Alternative ohne Alias:** `recordRef` mit direkter GUID oder `{RECORD:...}`.
Selten nötig, meist arbeitet man mit Aliasen.

## DeleteRecord

Löscht einen Record.

```json
{ "stepNumber": 7, "action": "DeleteRecord", "alias": "tsk" }
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `alias` | ja | Alias des zu löschenden Records. |

Der Record wird aus dem Record-Tracker entfernt (wird am Test-Ende nicht
nochmal gelöscht).

## RetrieveRecord

Lädt einen Record neu. Sinnvoll wenn der Record durch ein Plugin
modifiziert wurde und du im nächsten Step die neuen Werte brauchst.

```json
{ "stepNumber": 4, "action": "RetrieveRecord",
  "alias":   "opp",
  "columns": ["statecode", "statuscode", "actualrevenue"]
}
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `alias` | ja | Vorhandener Alias. |
| `columns` | nein | Welche Felder neu laden. Ohne: alle aus dem letzten Create. |

**Wann brauchst du das?** Wenn `{alias.fields.xyz}` in einem späteren
Step einen aktuellen Wert liefern muss, der sich nach dem Create geändert
hat. Für reine Asserts ist `RetrieveRecord` nicht nötig — die
Assertion-Engine liest frisch aus der DB.

## Wait

Feste Wartezeit.

```json
{ "stepNumber": 3, "action": "Wait", "waitSeconds": 5,
  "description": "Plugin-Chain Zeit geben" }
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `waitSeconds` | ja | Wartezeit in Sekunden. |

**Nimm es nur wenn du musst.** `WaitForFieldValue` und `WaitForRecord`
sind fast immer die bessere Wahl, weil sie nicht länger warten als
nötig und bei Timeouts klar scheitern.

## WaitForRecord / FindRecord

Pollt bis ein Record mit bestimmten Kriterien existiert. `FindRecord`
ist ein Alias für denselben Step-Typ — semantisch passender wenn der
Record sicher schon existiert (statt darauf zu warten).

```json
{ "stepNumber": 4, "action": "WaitForRecord",
  "entity":         "tasks",
  "alias":          "triggered_task",
  "filter":         [
    { "field": "regardingobjectid", "operator": "eq", "value": "{opp.id}" }
  ],
  "columns":        ["subject", "statecode"],
  "timeoutSeconds": 30
}
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `entity` | ja | EntitySetName (Plural). |
| `filter` | ja | Filter-Array (siehe [05-assertions.md](05-assertions.md#filter-syntax)). |
| `alias` | nein | Wenn gesetzt: der gefundene Record bekommt diesen Alias. |
| `columns` | nein | Welche Felder laden. |
| `timeoutSeconds` | nein | Default 60. |
| `orderBy` | nein | Sortierung (Plugin v5.3+), siehe unten. |
| `top` | nein | Max-Treffer (Plugin v5.3+), Default 1. |

**Typisches Szenario:** Ein Plugin legt einen abhängigen Record an,
du weißt nicht wann, brauchst ihn aber für den nächsten Step.

Ohne `alias` dient der Step nur als "Barriere": der Test wartet bis der
Record da ist, aber du kannst ihn nicht weiter referenzieren.

### Sortierung mit `orderBy` (Plugin v5.3+)

Wenn ein Filter mehrere Kandidaten matcht und der Test einen
**deterministisch ausgewählten** Record braucht (ältester, neuester,
höchster Wert), benutze `orderBy`:

```json
{ "action": "FindRecord", "entity": "systemusers", "alias": "oldestDisabled",
  "filter": [
    { "field": "statecode",  "operator": "eq", "value": 1 },
    { "field": "isdisabled", "operator": "eq", "value": false }
  ],
  "orderBy": "modifiedon asc",
  "top": 1,
  "columns": ["firstname", "lastname", "internalemailaddress"] }
```

**Syntax:** Komma-separierter String im OData-Stil
`feldname asc|desc, feldname2 asc|desc`. Default-Richtung `asc` wenn
nur Feldname angegeben.

**Mehrere Sortkriterien:**

```json
"orderBy": "statecode asc, modifiedon desc"
```

**Häufige Use-Cases:**

- Ältesten Datensatz für Pseudonymize-Tests holen.
- Letzten verarbeiteten Bridge-Event finden.
- Contact mit höchstem Retry-Count zur Diagnose.

**`top`** ist standardmäßig 1, kann höher gesetzt werden — aber der
Standard-Alias-Mechanismus registriert nur den ersten Treffer. Höheres
`top` ohne weitere Logik liefert nicht mehr nutzbare Records (Stretch
für zukünftige Versionen).

## WaitForFieldValue

Pollt bis ein Feld des Alias-Records einen erwarteten Wert hat.

```json
{ "stepNumber": 4, "action": "WaitForFieldValue",
  "alias":          "opp",
  "fields":         { "statecode": 1 },
  "timeoutSeconds": 30
}
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `alias` | ja | Alias eines vorhandenen Records. |
| `fields` | ja | Feld-zu-Wert-Mapping. **Alle** Felder müssen den Wert haben. |
| `timeoutSeconds` | nein | Default 60. |

**Mehrere Felder gleichzeitig sind ein AND:**

```json
"fields": { "statecode": 1, "statuscode": 3 }
```

Wartet bis beide Felder gleichzeitig diese Werte haben.

## ExecuteRequest

Ruft eine Microsoft SDK-Message auf. Nicht für Custom APIs — dafür ist
[`ExecuteAction`](#executeaction).

```json
{ "stepNumber": 3, "action": "ExecuteRequest",
  "requestName": "Merge",
  "fields": {
    "Target":         { "$type": "EntityReference", "entity": "contact", "ref": "master" },
    "SubordinateId":  { "$type": "Guid", "ref": "duplicate" },
    "UpdateContent":  { "$type": "Entity", "entity": "contact", "fields": {} },
    "PerformParentingChecks": false
  },
  "waitSeconds": 5
}
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `requestName` | ja | SDK-Message-Name, z.B. `Merge`, `QualifyLead`, `WinOpportunity`, `Assign`. |
| `fields` | ja | Parameter der Message. Komplexe Typen mit `$type`. |
| `waitSeconds` | nein | Nachgelagertes Warten (einfacher als `Wait`-Step). |

**Die `$type`-Syntax für komplexe Parameter:**

| Parameter-Typ | JSON | Beispiel |
|---|---|---|
| Einfacher String/Bool/Int | direkter Wert | `"subject": "Won"`, `"CreateContact": true` |
| EntityReference | `{ "$type": "EntityReference", "entity": "<logicalname>", "ref": "<alias>" }` | `{ "$type": "EntityReference", "entity": "lead", "ref": "lead1" }` |
| Guid | `{ "$type": "Guid", "ref": "<alias>" }` | `{ "$type": "Guid", "ref": "lead1" }` |
| OptionSetValue | `{ "$type": "OptionSetValue", "value": <number> }` | `{ "$type": "OptionSetValue", "value": 3 }` |
| Money | `{ "$type": "Money", "value": <number> }` | `{ "$type": "Money", "value": 1500 }` |
| Entity | `{ "$type": "Entity", "entity": "<logicalname>", "fields": { ... } }` | Für Parameter wie `OpportunityClose` |
| EntityCollection | `{ "$type": "EntityCollection", "entities": [ ... ] }` | Selten, z.B. in `GrantAccess` |

**Häufige SDK-Messages:**

- `QualifyLead` — Lead qualifizieren
- `Merge` — zwei Records zusammenführen
- `WinOpportunity` / `LoseOpportunity` — Opportunity schließen
- `Assign` — Owner ändern
- `SetState` — statecode/statuscode ändern (einfacher als Update)
- `AddPrivilegesRole`, `RetrieveUserSettings`, ...

Die vollständige Liste steht in der Microsoft-Dokumentation unter
"Organization Service Messages".

## ExecuteAction

Für **Custom Actions** (selbst definierte Actions in der Solution).

```json
{ "stepNumber": 3, "action": "ExecuteAction",
  "actionName": "new_CalculatePriceList",
  "parameters": {
    "Target": "{opp.id}",
    "PriceListId": "stdprice"
  }
}
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `actionName` | ja | Schema-Name der Custom Action. |
| `parameters` | nein | Parameter-Map. |

Unterschied zu `ExecuteRequest`: `ExecuteAction` ist für Actions mit
einfachen Parametern. Für komplexe `$type`-Parameter nimm lieber
`ExecuteRequest`.

## SetEnvironmentVariable (Plugin v5.3+)

Setzt eine Environment-Variable für die Dauer des Tests. Mit `alias`
wird der Vorher-Zustand gemerkt und am Testende automatisch
wiederhergestellt — selbst wenn der Test in der Mitte fehlschlägt.

```json
{ "action": "SetEnvironmentVariable",
  "schemaName": "markant_GdprRetentionDays",
  "value": "1",
  "target": "effective",
  "alias": "envSnap" }
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `schemaName` | ja | `schemaname` der Definition (env-unabhängig). |
| `value` | ja | Neuer Wert als String. Max 2.000 Zeichen. |
| `target` | nein | `effective` (Default), `currentValue`, `defaultValue`. |
| `alias` | nein | Aktiviert Auto-Restore über RecordTracker. |

**Target-Semantik:**

- `effective` (Default, 99%-Fall): zur Laufzeit resolved. Wenn ein
  `environmentvariablevalue`-Record existiert, schreibe dort rein
  (CurrentValue); sonst schreibe in DefaultValue der Definition.
- `currentValue`: schreibt immer in `environmentvariablevalue`. Upsert,
  legt Record an wenn nicht vorhanden.
- `defaultValue`: schreibt in `environmentvariabledefinition.defaultvalue`.
  Auf Managed-Envs entsteht dabei ein Unmanaged Active Layer (in
  Dataverse normale Praxis).

**Typischer Use-Case:** Feature-Flag-gesteuerte DSGVO- oder Bridge-Tests.

```json
[
  { "action": "SetEnvironmentVariable",
    "schemaName": "markant_gdpr_sysadmin_is_gdpr_admin",
    "value": "false",
    "alias": "envSnap" },
  { "action": "CreateRecord", "entity": "contacts", "alias": "c", "fields": {} },
  { "action": "UpdateRecord", "alias": "c",
    "fields": { "markant_gdprstatuscode": 288260001 } }
]
```

Nach dem Test wird `markant_gdpr_sysadmin_is_gdpr_admin` automatisch auf
den Original-Wert zurückgesetzt.

## RetrieveEnvironmentVariable (Plugin v5.3+)

Liest eine Environment-Variable und legt das Ergebnis als virtuellen
Record im Alias-Store ab.

```json
{ "action": "RetrieveEnvironmentVariable",
  "schemaName": "markant_GdprRetentionDays",
  "source": "effective",
  "alias": "env" }
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `schemaName` | ja | `schemaname` der Definition. |
| `alias` | ja | Ergebnis-Alias (virtueller Record mit `value`-Feld). |
| `source` | nein | `effective` (Default), `currentValue`, `defaultValue`. |

**Source-Semantik:**

- `effective` (Default): wie Plugins lesen — CurrentValue wenn da,
  sonst DefaultValue.
- `currentValue`: nur aus `environmentvariablevalue`. Liefert `null`
  wenn kein Value-Record existiert. Nützlich für `IsNull`-Asserts:
  "ist die Variable überhaupt gesetzt?".
- `defaultValue`: nur aus der Definition.

**Verwendung des Ergebnisses:**

Der Wert ist über `{alias.fields.value}` ansprechbar:

```json
[
  { "action": "RetrieveEnvironmentVariable",
    "schemaName": "markant_max_retries", "alias": "envMax" },
  { "action": "Assert", "target": "Record", "recordRef": "{RECORD:envMax}",
    "field": "value", "operator": "GreaterThan", "value": "0",
    "description": "Max-Retries-Konfiguration ist > 0" }
]
```

## Assert

Prüft einen Wert oder die Existenz. Ausführlich dokumentiert in
[05-assertions.md](05-assertions.md). Hier nur die Kurzform:

```json
{ "stepNumber": 4, "action": "Assert",
  "target":    "Record",
  "recordRef": "{RECORD:acc}",
  "field":     "websiteurl",
  "operator":  "Equals",
  "value":     "https://example.com",
  "description": "Aussagekräftige Prüf-Beschreibung",
  "onError":   "continue"
}
```

Operatoren: `Equals`, `NotEquals`, `Contains`, `StartsWith`, `EndsWith`,
`GreaterThan`, `LessThan`, `IsNull`, `IsNotNull`, `Exists`, `NotExists`,
`RecordCount`, `DateSetRecently`.

Targets: `Record` (per `recordRef`), `Query` (per `entity` + `filter`).

---

Weiter mit den [Platzhaltern](03-platzhalter.md) oder
[Lookups/Bindings](04-lookup-und-binding.md).
