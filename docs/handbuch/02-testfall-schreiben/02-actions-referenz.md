# Actions-Referenz

Vollstaendige Liste aller Actions, ihrer Pflicht- und Optional-Felder,
typische Fallen, und wofuer sie da sind.

## Uebersicht

| Action | Zweck |
|---|---|
| [`CreateRecord`](#createrecord) | Neuen Record anlegen |
| [`UpdateRecord`](#updaterecord) | Bestehenden Record aktualisieren |
| [`DeleteRecord`](#deleterecord) | Record loeschen |
| [`RetrieveRecord`](#retrieverecord) | Record neu laden (Aktualisiere Alias) |
| [`Wait`](#wait) | Feste Wartezeit |
| [`WaitForRecord`](#waitforrecord) | Warten bis ein Record existiert |
| [`WaitForFieldValue`](#waitforfieldvalue) | Warten bis ein Feld einen Wert hat |
| [`ExecuteRequest`](#executerequest) | Beliebige SDK-Message (Merge, QualifyLead, ...) |
| [`ExecuteAction`](#executeaction) | Custom Action aufrufen |
| [`Assert`](#assert) | Ergebnis pruefen |

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
| `alias` | nein | Name zum spaeteren Referenzieren. Empfohlen. |
| `columns` | nein | Felder, die nach dem Create zurueckgelesen werden sollen (fuer AutoNumber, Server-generierte Werte). |
| `description` | nein | Log-Kommentar. |

**Besonderheiten:**

- Ohne `alias` kannst du den Record spaeter nicht mehr referenzieren.
- Lookup-Felder brauchen `@odata.bind`-Syntax — siehe
  [04-lookup-und-binding.md](04-lookup-und-binding.md).
- `columns` ist nuetzlich fuer Felder die du nicht setzt, aber im Test
  brauchst: z.B. `"columns": ["accountnumber"]` laedt die nach dem Create
  erzeugte AutoNumber, und du kommst per `{acc.fields.accountnumber}` an
  den Wert.
- Der Record wird automatisch im Record-Tracker registriert und am Ende
  des Tests geloescht (ausser `jbe_keeprecords=true` am Testrun).

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
| `fields` | ja | Die zu aendernden Felder. |

**Alternative ohne Alias:** `recordRef` mit direkter GUID oder `{RECORD:...}`.
Selten noetig, meist arbeitet man mit Aliasen.

## DeleteRecord

Loescht einen Record.

```json
{ "stepNumber": 7, "action": "DeleteRecord", "alias": "tsk" }
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `alias` | ja | Alias des zu loeschenden Records. |

Der Record wird aus dem Record-Tracker entfernt (wird am Test-Ende nicht
nochmal geloescht).

## RetrieveRecord

Laedt einen Record neu. Sinnvoll wenn der Record durch ein Plugin
modifiziert wurde und du im naechsten Step die neuen Werte brauchst.

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

**Wann brauchst du das?** Wenn `{alias.fields.xyz}` in einem spaeteren
Step einen aktuellen Wert liefern muss, der sich nach dem Create geaendert
hat. Fuer reine Asserts ist `RetrieveRecord` nicht noetig — die
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
sind fast immer die bessere Wahl, weil sie nicht laenger warten als
noetig und bei Timeouts klar scheitern.

## WaitForRecord

Pollt bis ein Record mit bestimmten Kriterien existiert.

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

**Typisches Szenario:** Ein Plugin legt einen abhaengigen Record an,
du weisst nicht wann, brauchst ihn aber fuer den naechsten Step.

Ohne `alias` dient der Step nur als "Barriere": der Test wartet bis der
Record da ist, aber du kannst ihn nicht weiter referenzieren.

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
| `fields` | ja | Feld-zu-Wert-Mapping. **Alle** Felder muessen den Wert haben. |
| `timeoutSeconds` | nein | Default 60. |

**Mehrere Felder gleichzeitig sind ein AND:**

```json
"fields": { "statecode": 1, "statuscode": 3 }
```

Wartet bis beide Felder gleichzeitig diese Werte haben.

## ExecuteRequest

Ruft eine Microsoft SDK-Message auf. Nicht fuer Custom APIs — dafuer ist
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

**Die `$type`-Syntax fuer komplexe Parameter:**

| Parameter-Typ | JSON | Beispiel |
|---|---|---|
| Einfacher String/Bool/Int | direkter Wert | `"subject": "Won"`, `"CreateContact": true` |
| EntityReference | `{ "$type": "EntityReference", "entity": "<logicalname>", "ref": "<alias>" }` | `{ "$type": "EntityReference", "entity": "lead", "ref": "lead1" }` |
| Guid | `{ "$type": "Guid", "ref": "<alias>" }` | `{ "$type": "Guid", "ref": "lead1" }` |
| OptionSetValue | `{ "$type": "OptionSetValue", "value": <number> }` | `{ "$type": "OptionSetValue", "value": 3 }` |
| Money | `{ "$type": "Money", "value": <number> }` | `{ "$type": "Money", "value": 1500 }` |
| Entity | `{ "$type": "Entity", "entity": "<logicalname>", "fields": { ... } }` | Fuer Parameter wie `OpportunityClose` |
| EntityCollection | `{ "$type": "EntityCollection", "entities": [ ... ] }` | Selten, z.B. in `GrantAccess` |

**Haeufige SDK-Messages:**

- `QualifyLead` — Lead qualifizieren
- `Merge` — zwei Records zusammenfuehren
- `WinOpportunity` / `LoseOpportunity` — Opportunity schliessen
- `Assign` — Owner aendern
- `SetState` — statecode/statuscode aendern (einfacher als Update)
- `AddPrivilegesRole`, `RetrieveUserSettings`, ...

Die vollstaendige Liste steht in der Microsoft-Dokumentation unter
"Organization Service Messages".

## ExecuteAction

Fuer **Custom Actions** (selbst definierte Actions in der Solution).

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

Unterschied zu `ExecuteRequest`: `ExecuteAction` ist fuer Actions mit
einfachen Parametern. Fuer komplexe `$type`-Parameter nimm lieber
`ExecuteRequest`.

## Assert

Prueft einen Wert oder die Existenz. Ausfuehrlich dokumentiert in
[05-assertions.md](05-assertions.md). Hier nur die Kurzform:

```json
{ "stepNumber": 4, "action": "Assert",
  "target":    "Record",
  "recordRef": "{RECORD:acc}",
  "field":     "websiteurl",
  "operator":  "Equals",
  "value":     "https://example.com",
  "description": "Aussagekraeftige Pruef-Beschreibung",
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
