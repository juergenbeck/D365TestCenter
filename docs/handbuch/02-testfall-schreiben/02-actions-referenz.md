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
| [`WaitForNotExists`](#waitfornotexists) | Warten bis ein Record nicht mehr existiert (async-Lösch-Tests, v5.3.12) |
| [`ExecuteRequest`](#executerequest) | Beliebige SDK-Message (Merge, QualifyLead, Custom Actions, Custom APIs) |
| [`SetEnvironmentVariable`](#setenvironmentvariable-plugin-v53) | Environment-Variable setzen mit Auto-Restore (v5.3+) |
| [`RetrieveEnvironmentVariable`](#retrieveenvironmentvariable-plugin-v53) | Environment-Variable lesen (v5.3+) |
| [`Assert`](#assert) | Ergebnis prüfen |

**Negative-Path-Tests (v5.3+):** Für Steps die als erwartetes Ergebnis
einen Fehler werfen sollen, gibt es zwei optionale Felder
`expectFailure` und `expectException` — siehe [09-negative-path.md](09-negative-path.md).

**Konditionale Steps (ADR-0011):** Jeder Step kann ein optionales Feld
`condition` tragen, das ihn überspringt, wenn eine Laufzeit-Bedingung nicht
zutrifft — siehe Abschnitt [condition](#condition).

## condition

> Konditionale Step-Ausführung (ADR-0011). Kein eigener Action-Typ, sondern
> ein optionales Feld auf **jedem** Step.

Ist die `condition` zur Laufzeit **nicht** erfüllt, wird der Step
**übersprungen** (Step-Status `Skipped`, kein Failure). Ist sie erfüllt, läuft
der Step normal (inkl. `onError`/`expectFailure`). Zwei Steps mit
gegensätzlicher `condition` bilden ein if/else. Typischer Einsatz: ein Test,
der sich ohne Re-Edit an die Ziel-Konfiguration anpasst (z.B. ein Feature an/aus
je Umgebung) und nur im zutreffenden Zweig die echte Datensatz-Änderung prüft.

**Genau EINE von drei Formen pro `condition`** (die Laufzeit löst `all` vor
`any` vor Einfachklausel):

```jsonc
// Einfachklausel auf einem beliebigen Step:
"condition": { "left": "{cfg.fields.feature_enabled}", "operator": "Equals", "right": "true" }

// all (UND) - alle Klauseln müssen zutreffen:
"condition": { "all": [
  { "left": "{cfg.fields.a}", "operator": "Equals", "right": "1" },
  { "left": "{cfg.fields.b}", "operator": "IsNotNull" }
] }

// any (ODER) - mindestens eine Klausel:
"condition": { "any": [
  { "left": "{cfg.fields.a}", "operator": "Equals", "right": "x" },
  { "left": "{cfg.fields.a}", "operator": "Equals", "right": "y" }
] }
```

Eine Klausel ist `left` `operator` `right`. Mische die Formen nicht (eine
Einfachklausel zusammen mit `all`/`any`, oder `all` mit `any`); der
Pre-Run-Validator meldet das als `CONDITION_MALFORMED` (siehe
[11-pre-run-validation.md](11-pre-run-validation.md)).

**Operatoren** (geteilter Comparator, identisch zur `Assert`-Action,
**case-insensitiv**): `Equals`, `NotEquals`, `IsNull`, `IsNotNull`, `Contains`,
`StartsWith`, `EndsWith`, `GreaterThan`, `LessThan`. `IsNull`/`IsNotNull`
brauchen kein `right`. Nicht enthalten: `DateSetRecently`/`Exists`/`NotExists`/
`RecordCount` (Assert-/Query-only) und `In`/`NotIn` (nur im Filter-Set).

**Fallstricke:**

- **Unaufgelöster Platzhalter -> harter Fehler (`Outcome=Error`), kein stiller
  Skip.** Ein falsch geschriebener Alias lässt `{x.fields.y}` wörtlich stehen;
  die Auswertung erkennt das und wirft, damit ein Tippfehler nicht zu einem
  falsch-grünen Skip führt.
- **Boolean-Vergleich case-insensitiv halten.** `{alias.fields.<bool>}` liefert
  `"True"`/`"False"` (groß); der Comparator vergleicht case-insensitiv, also
  matcht `right: "true"`.
- **Test-Outcome bei lauter Skips:** Ein Test, dessen Asserts **alle**
  condition-übersprungen wurden, wird `Skipped` (nicht Passed), ehrlich sichtbar
  statt falsch-grün.

**Abgrenzung zu `AssertEnvironment`:** `AssertEnvironment` **scheitert** (Fail),
wenn eine Umgebungs-Vorbedingung nicht stimmt; `condition` **überspringt** den
Step (Skip). Scheitern vs. Überspringen.

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
| `timeoutSeconds` | nein | Default 120. |
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

Pollt bis **ein** Feld des Alias-Records einen erwarteten Wert hat.

```json
{ "stepNumber": 4, "action": "WaitForFieldValue",
  "alias":          "opp",
  "fields":         { "statecode": null },
  "expectedValue":  1,
  "timeoutSeconds": 30
}
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `alias` (oder `recordRef`) | ja | Alias eines vorhandenen Records. |
| `fields` | ja | Single-Key-Map. Der **Key** ist der zu beobachtende Feldname; der Wert wird ignoriert (Konvention: `null` setzen). Mehrere Keys werden nicht unterstützt — nur der erste wird ausgewertet. |
| `expectedValue` | ja | Der erwartete Wert für das Feld. Platzhalter wie `{alias.id}` werden aufgelöst. |
| `entity` | nein | Logical-Name der Entity. Default: aus dem Alias-Record. |
| `timeoutSeconds` | nein | Default 120. |
| `pollingIntervalMs` | nein | Default 2000. |

Schema-Hintergrund: WaitForFieldValue ist als Single-Field-Polling konzipiert
(`GenericRecordWaiter.WaitForFieldValue(entityName, recordId, fieldName, expectedValue, ...)`).
Multi-Field-AND ist heute nicht implementiert — bei Bedarf zwei separate
WaitForFieldValue-Steps oder einen FindRecord/Assert-Schritt verwenden.

## WaitForNotExists

Pollt bis **kein** Record mehr auf den Filter matcht. Das Polling-Pendant zu
`WaitForFieldValue`, aber für Record-Abwesenheit statt Feldwert. Gemacht für
async-Lösch-Tests: ein asynchrones Plugin (Post-Operation) löscht einen Record,
und der Test soll robust auf die Löschung warten, statt mit einem fixen `Wait`
zu raten.

```json
{ "stepNumber": 6, "action": "WaitForNotExists",
  "entity":         "contacts",
  "filter":         [
    { "field": "lastname", "operator": "eq", "value": "Composite Address" }
  ],
  "timeoutSeconds": 90
}
```

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `entity` | ja | EntitySetName (Plural). |
| `filter` | ja | Filter-Array (siehe [05-assertions.md](05-assertions.md#filter-syntax)). |
| `timeoutSeconds` | nein | Default 120. **Empfohlen 90** für async-Delete: Puffer über die typische Job-Laufzeit, klar unter dem 2-min-Sandbox-Limit. |
| `pollingIntervalMs` | nein | Default 2000. |
| `maxDurationMs` | nein | Performance-Assertion: wirft, wenn die Löschung länger als X ms dauert. |

**Warum eigene Action und nicht ein Flag auf `WaitForRecord`?** Bei Abwesenheit
gibt es keinen Record zum Registrieren, keinen Alias, keine `columns`/`orderBy`.
Eine eigene Action hält den Vertrag sauber, symmetrisch zu `WaitForFieldValue`.

**Verhalten bei Timeout:** existiert der Record nach `timeoutSeconds` noch,
wirft der Step (`Outcome=Error`), analog `WaitForRecord`.

**Sandbox-Grenze:** im async-CRUD-Trigger-Plugin-Pfad gilt das 2-min-Sandbox-Limit
für den gesamten Testlauf. Für lange async-Lösch-Ketten den CLI-Pfad bevorzugen
(kein Sandbox-Timeout).

**Migration von fixem Wait:** Statt `Wait` plus `Assert ... NotExists` (flaky,
wenn der async-Job mal länger braucht als der Wait) ein einzelner
`WaitForNotExists`-Step. Er scheitert von selbst, wenn der Record innerhalb des
Timeouts nicht verschwindet, ein separater `Assert NotExists` ist dann nicht
mehr nötig.

## ExecuteRequest

Ruft eine SDK-Message auf. Seit Plugin v5.3.7 (ADR-0007) **die einzige
kanonische Aktion für alle SDK-Message-Aufrufe** — Microsoft-Standard-Messages
(Merge, QualifyLead, Assign, SetState, ...) **und** Custom Actions / Custom APIs.
Legacy-Verben `CallCustomApi` und `ExecuteAction` werden als Aliasse durchgereicht
(siehe ["Legacy-Aliasse (ADR-0007)"](#legacy-aliasse-adr-0007) unten).

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

### Pitfall: QualifyLead-Target-Drift bei PreOperation-Plugins

Beim `ExecuteRequest` mit `requestName: "QualifyLead"` weicht das interne
Account-Target leicht vom Web-API-Pendant `POST /leads(<id>)/Microsoft.Dynamics.CRM.QualifyLead` ab:

| Eintritts-Pfad | Verhalten im PreOperation-Account-Create-Target |
|---|---|
| Web-API-Action `POST /leads(<id>)/Microsoft.Dynamics.CRM.QualifyLead` | Nur die in der Lead-zu-Account-AttributeMap gelisteten Felder sind im Target enthalten. Custom-Felder ohne AttributeMap-Eintrag (z.B. `za_accounttype`) sind **nicht** in `target.Contains(...)`. |
| `ExecuteRequest QualifyLead` aus dem Test Center | Manche Felder ohne AttributeMap-Eintrag landen als `null` im Target. `target.Contains("za_accounttype")` ist **true**, aber `target["za_accounttype"]` ist **null**. |

**Konsequenz für PreOperation-Plugins:** Wenn dein Plugin `target.Contains(field)` als
"User-hat-Wert-explizit-gesetzt"-Indikator nutzt, prüfe zusätzlich auf `target[field] != null`.

```csharp
// Statt: if (target.Contains("za_accounttype")) { ... user wins ... }
// Sauber:
if (target.Contains("za_accounttype") && target["za_accounttype"] != null)
{
    // User hat den Wert explizit gesetzt, behalten.
}
else
{
    // Plugin füllt den Wert.
}
```

**Verifizierter Workaround in Tests:** Wenn der Test ausschließlich den Web-API-Pfad
exerzieren soll, statt `ExecuteRequest QualifyLead` einen direkten `CreateRecord` auf
`accounts` mit `originatingleadid@odata.bind` verwenden (Single-Target-Lookup,
Bind ohne `_target`-Suffix). Dann läuft der Plugin-Pfad identisch zum HTTP-API-Aufruf.

Belegt durch das ZP-LeadQualifyMapping-Befund (April 2026, Suite 1 v1.0.0.10):
ZP-LQM-01/02/07 (ExecuteRequest-Pfad) FAILED an `za_accounttype = <null>`,
ZP-LQM-04/06 (Direct-Create-Pfad) PASSED.

### Custom Actions und Custom APIs via ExecuteRequest

Custom Actions und Custom APIs sind aus SDK-Sicht ebenfalls SDK-Messages
(per `OrganizationRequest(uniquename)` aufgerufen). Sie verwenden
denselben `ExecuteRequest`-Pfad wie Microsoft-Standard-Messages.

```json
{ "stepNumber": 3, "action": "ExecuteRequest",
  "requestName": "new_CalculatePriceList",
  "fields": {
    "Target":      { "$type": "EntityReference", "entity": "opportunity", "ref": "opp" },
    "PriceListId": "stdprice"
  }
}
```

```json
{ "stepNumber": 4, "action": "ExecuteRequest",
  "requestName": "markant_RunFieldGovernanceForContact",
  "fields": { "ContactId": "{contact.id}" }
}
```

Einfache Parameter (String/Int/Bool/Guid) werden direkt als Wert
gemappt, komplexe Parameter mit `$type` wie bei jeder anderen SDK-Message.

### Legacy-Aliasse (ADR-0007)

Bis Plugin v5.3.6 gab es zwei zusätzliche Action-Verben (`CallCustomApi`,
`ExecuteAction`) mit eigenen Schemata. Ab v5.3.7 werden diese als Aliasse
zu `ExecuteRequest` behandelt (Konsolidierung, siehe ADR-0007):

| Legacy-Verb | Legacy-Schema | Mapping zu kanonisch |
|---|---|---|
| `CallCustomApi` | `entity` + `fields` | Verb → `ExecuteRequest`; `entity` → `requestName` |
| `ExecuteAction` | `actionName` + `parameters` | Verb → `ExecuteRequest`; `actionName` → `requestName`; `parameters` → `fields` |
| `ExecuteAction` | `apiName` + `parameters` | wie oben, `apiName` ist Synonym zu `actionName` |
| `ExecuteAction` | `entity` + `fields` | wie oben (Mischform, z.B. Markant SW03) |

Bestehende Tests mit Legacy-Verben/-Schemas laufen unverändert. Empfehlung
für neue Tests: kanonisch `ExecuteRequest` + `requestName` + `fields`.
Aliasse bleiben für mindestens zwei Plugin-Major-Versionen erhalten.

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
| `alias` | nein | Optional, nur für explizites Referenzieren des Snapshot-Objekts. **Auto-Restore ist immer aktiv** (Plugin v5.3.1+). |

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
