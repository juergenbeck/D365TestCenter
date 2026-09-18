# Testfall-Format: JSON, Actions, Platzhalter

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Feldnamen) steht in der `PROJEKT-KONTEXT.md` des jeweiligen Repos.

## 3. Testfall schreiben: Das JSON-Format

Seit ADR-0004 (`02_decisions/adr/ADR-0004-actions-statt-phasen.md` im Repo D365TestCenter-Workspace) ist ein Testfall **eine einzige geordnete Liste von Actions**. Kein getrenntes `preconditions`-Array, kein getrenntes `assertions`-Array. Die JSON-Reihenfolge ist die Ausführungsreihenfolge. Assert ist eine Action wie jede andere.

```json
{
  "testId": "DEMO-01",
  "title": "Beispiel",
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc1", "fields": { "name": "Demo GmbH" } },
    { "stepNumber": 2, "action": "UpdateRecord", "alias": "acc1", "fields": { "websiteurl": "https://example.com" } },
    { "stepNumber": 3, "action": "Assert", "target": "Record", "recordRef": "{RECORD:acc1}", "field": "websiteurl", "operator": "Equals", "value": "https://example.com" }
  ]
}
```

### 3.1 Setup via CreateRecord (ehemals Preconditions)

Setup-Schritte sind ganz normale `CreateRecord`-Actions am Anfang der `steps`-Liste.

```json
{ "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "account1",
  "fields": { "name": "{GENERATED:company}" } }
```

- `entity`: EntitySetName (Plural, z.B. `accounts`, `contacts`, `leads`)
- `alias`: Name zum späteren Referenzieren (z.B. `{account1.id}`)
- `fields`: Feldwerte, können Platzhalter enthalten
- `columns`: Optional, Auto-Retrieve nach dem Create (für Server-generierte Felder wie AutoNumber)

### 3.2 Weitere Actions

| action | Beschreibung | Pflichtfelder |
|--------|-------------|--------------|
| `CreateRecord` | Datensatz erstellen | `entity`, `fields`, optional `alias`, optional `columns` (Auto-Retrieve), optional **`cleanupChildren`** (ADR 2026-07-23 0808, s.u.) |
| `UpdateRecord` | Datensatz aktualisieren | `alias`, `fields` |
| `DeleteRecord` | Datensatz löschen | `alias` |
| `Wait` | Wartezeit | `waitSeconds` |
| `ExecuteRequest` | Beliebige SDK-Message (v5.2): Merge, QualifyLead, SetState, Assign, **Custom Actions, Custom APIs** (ADR-0007 ab v5.3.7) | `requestName`, `fields` mit `$type`-System, optional **`outputAlias`** (v5.3.5) für `{alias.outputs.X}`-Platzhalter. Legacy-Verben `CallCustomApi`/`ExecuteAction` sind Aliasse (ADR-0007). |
| `RetrieveRecord` | Record neu laden (v5.2) | `alias`, optional `columns` |
| `WaitForRecord` / `FindRecord` | Auf Record-Existenz warten | `entity`, `filter`, optional `alias`, `columns`, **`orderBy`** (v5.3), **`top`** (v5.3), **`trackForCleanup`** (FB-54, Default false: gefundener Record ist Bestand; `true` NUR für während des Laufs von der getesteten API SERVERSEITIG erzeugte Records, dann räumt der Cleanup sie mit ab), optional **`cleanupChildren`** (nur mit `trackForCleanup:true` wirksam) |
| **`TrackRecord`** (FB-54, ADR 2026-07-17 1801 (`02_decisions/adr/ADR-2026-07-17-1801-cleanup-serverseitig-erzeugte-records.md` im Repo D365TestCenter-Workspace)) | Bekannten, serverseitig erzeugten Record ohne Query in Registry + Cleanup-Löschliste aufnehmen (z.B. Custom-API-Output-ID). Unaufgelöster Platzhalter = Error (kein stilles Nichts-Tracken); Dedup gegen bereits getrackte Records. | `entity`, `recordId` (Platzhalter erlaubt, z.B. `{result.outputs.InvoiceId}`), optional `alias`, optional **`cleanupChildren`** |
| `WaitForFieldValue` | Auf Feldwert warten | `alias`, `fields`, `expectedValue` |
| **`WaitForNotExists`** (v5.3.12) | Auf Record-Abwesenheit warten (async-Lösch-Tests), Polling-Pendant zu `WaitForFieldValue`. Pollt bis 0 Treffer oder Timeout. | `entity`, `filter`, optional `timeoutSeconds` (Default 120, empfohlen 90), `pollingIntervalMs`, `maxDurationMs` |
| **`WaitForAsyncCompletion`** (ADR 2026-06-28, **verifiziert**) | Wartet auf das **Ende einer mehrstufigen async-Plugin-Kette** (asyncoperation-Quiescence) statt auf einen geratenen Endzustand mit festem Timeout. **CLI-/Core-only** (Plugin-Sandbox skippt, 2-min-Limit). Siehe Pitfall 3.4d. | optional `aliases` (regardingobjectid-Verengung), `timeoutSeconds` (Sicherheits-Obergrenze, KEIN Rate-Wert), `pollingIntervalMs`, `stableChecks` (Default 3), `lookbackSeconds` (Default 20), `initialWaitMs` (Default 2000) |
| **`SetEnvironmentVariable`** (v5.3 / Auto-Restore immer aktiv ab 5.3.1) | EnvironmentVariable setzen mit Auto-Restore | `schemaName`, `value`, optional `target` (effective/currentValue/defaultValue), optional `alias` für Snapshot-Referenz |
| **`RetrieveEnvironmentVariable`** (v5.3) | EnvironmentVariable lesen, Ergebnis als virtueller Record im Alias-Store | `schemaName`, `alias`, optional `source` (effective/currentValue/defaultValue) |
| **`BrowserAction`** (Phase 1, ADR-0006) | UI-Test-Step über Microsoft.Playwright .NET im Cli-Pfad. Plugin skipt mit klarer Skip-Message. | `operation` (navigate/click/doubleClick/fill/delay/waitFor/screenshot/evaluate), je nach Operation: `url`, `selector`, `fallbackSelector`, `waitForSelector`, `value`, `expression`, `name`, `assertNoLoginRedirect` |

```json
{ "action": "UpdateRecord", "alias": "account1",
  "fields": { "websiteurl": "https://updated.example.com" } }
```

**`WaitForRecord`/`FindRecord` mit Sortierung (v5.3):**

```json
{ "action": "FindRecord", "entity": "systemuser", "alias": "oldestDisabled",
  "filter": [
    { "field": "isdisabled",            "operator": "eq",        "value": true },
    { "field": "contoso_gdprstatuscode","operator": "isnull" },
    { "field": "applicationid",         "operator": "isnull" },
    { "field": "firstname",             "operator": "isnotnull" }
  ],
  "orderBy": "modifiedon asc",
  "top": 1,
  "columns": ["firstname", "lastname", "internalemailaddress"] }
```

`orderBy` ist ein komma-separierter String OData-Stil. Default-Richtung
asc wenn nur Feldname angegeben. Mehrere Felder per Komma:
`"modifiedon asc, createdon desc"`.

**`WaitForNotExists` (v5.3.12) für async-Lösch-Tests:**

Polling-Pendant zu `WaitForFieldValue`, aber für Record-Abwesenheit. Pollt die
Query bis 0 Treffer oder Timeout. Löst das Flaky-Muster "async-Plugin löscht
einen Record, fixer `Wait` ist mal zu kurz" (gleiche Klasse wie der
`WaitForFieldValue`-Fix für async-Statusänderungen). Eigene Action statt Flag
auf `WaitForRecord`, weil bei Abwesenheit kein Record zum Registrieren oder
Zurückgeben bleibt (symmetrisch zu `WaitForFieldValue`).

```json
{ "action": "WaitForNotExists", "entity": "contacts",
  "filter": [ { "field": "lastname", "operator": "eq", "value": "Composite Address" } ],
  "timeoutSeconds": 90 }
```

`timeoutSeconds` Default 120, empfohlen 90 für async-Delete (Puffer über die
typische Job-Laufzeit, klar unter dem 2-min-Sandbox-Limit). Optional
`pollingIntervalMs` (Default 2000) und `maxDurationMs` (Lösch-Frist als
Performance-Assertion). Kein `alias`/`columns`/`orderBy` (es gibt keinen
Record). Bei Timeout wirft der Step (Outcome=Error), analog `WaitForRecord`.
Im Async-CRUD-Trigger-Plugin-Pfad gilt das Sandbox-2-min-Limit; für lange
async-Lösch-Ketten den CLI-Pfad bevorzugen.

**Filter-Wahl: Record-PK statt Merkmal (Best Practice).** Den Filter auf den
Primary Key des erwartet-gelöschten Records legen (`contactid`/`activityid` =
`{alias.id}`), nicht auf ein fachliches Merkmal wie `lastname` oder `subject`.
Ein Merkmalsfilter wird falsch-rot, sobald ein Parallel-Lauf oder eine Altlast
einen zweiten Record mit demselben Merkmal stehen lässt; der PK-Filter wartet
exakt auf das Verschwinden des eigenen Records und entspricht 1:1 dem vorherigen
`Assert ... NotExists`-Filter (minimaler, deterministischer Übergang). Der
GUID-Platzhalter wird im Filter-Value aufgelöst und typkorrekt auf das
Uniqueidentifier-Feld konvertiert. Belegt 2026-06-14 (Kontakt-Test mit Filter
`contactid={con.id}`, Termin-Test mit Filter `activityid={appt.id}`, CLI 2 PASSED;
Appointment-Delete 37s, klar unter 90s, 60s wäre für den Pfad zu knapp gewesen).

```json
{ "action": "WaitForNotExists", "entity": "contacts",
  "filter": [ { "field": "contactid", "operator": "eq", "value": "{con.id}" } ],
  "timeoutSeconds": 90 }
```

**Filter-Operatoren** (zentral in `GenericRecordWaiter.ResolveOperator`,
gilt für FindRecord, WaitForRecord und Assert mit `target: "Query"`):

| Pack-Operator | C#-Pendant | Wert |
|---|---|---|
| `eq`, `equals` | Equal | erforderlich |
| `ne`, `notequals` | NotEqual | erforderlich |
| `gt`/`ge`/`lt`/`le` | GreaterThan / GreaterEqual / LessThan / LessEqual | erforderlich |
| `like`, `contains`, `beginswith`/`startswith`, `endswith` | Like / Contains / BeginsWith / EndsWith | erforderlich |
| `null`, `isnull` | Null | **kein Wert** (oder ignoriert) |
| `notnull`, `isnotnull` | NotNull | **kein Wert** (oder ignoriert) |
| `in`, `notin` | In / NotIn | Array |

**KRITISCH:** Für Null-Vergleiche immer `isnull` / `isnotnull` verwenden,
nicht `eq` / `ne` mit `value: null`. Letzteres wirft im Async-Pfad
`Condition for attribute 'X': expected argument(s) of type 'System.Guid'
but received 'System.DBNull'`, weil der ConditionExpression-Builder
DBNull nicht als gültigen Wert für typed Felder akzeptiert.

**Lookup-Felder im Filter:** als Filter-`field` immer den Logical-Name
des Lookups verwenden (z.B. `contoso_contactid`), nicht das
OData-Lookup-Format (`_contoso_contactid_value`). Letzteres ist nur in
Web-API-OData-Queries gültig, nicht im Test-Center-Filter, der intern
über `QueryExpression` läuft. Falsches Format wirft
`'<entity>' entity doesn't contain attribute with Name =
'_xxx_value' and NameMapping = 'Logical'`.

```json
// FALSCH — OData-Format
{ "field": "_contoso_contactid_value", "operator": "eq", "value": "{c.id}" }

// RICHTIG — Logical-Name
{ "field": "contoso_contactid", "operator": "eq", "value": "{c.id}" }
```

**EnvironmentVariable-Actions (v5.3):**

```json
{ "action": "SetEnvironmentVariable",
  "schemaName": "contoso_GdprRetentionDays",
  "value": "1",
  "target": "effective",
  "alias": "envSnap" }
```

`target: "effective"` (Default) resolved zur Laufzeit: wenn ein
`environmentvariablevalue`-Record existiert, schreibe CurrentValue;
sonst schreibe DefaultValue. **Auto-Restore ist seit Plugin 5.3.1
immer aktiv** (FB-30-Fix), der Vorher-Zustand wird automatisch
gemerkt und im Cleanup-Pass wiederhergestellt, unabhängig davon ob
`alias` gesetzt ist. `alias` ist nur für explizites Referenzieren des
Snapshot-Objekts in nachfolgenden Steps relevant.

```json
{ "action": "RetrieveEnvironmentVariable",
  "schemaName": "contoso_GdprRetentionDays",
  "source": "effective",
  "alias": "env" }
```

`source: "effective"` (Default) liefert wie Plugins lesen: CurrentValue
falls da, sonst DefaultValue. Ergebnis als virtueller Record im
Alias-Store mit Feldern `value`, `schemaname`, `resolvedsource`,
nutzbar als `{env.fields.value}` in nachfolgenden Steps.

Detail-Doku: `D365TestCenter-Workspace/03_implementation/envvar-handling-in-tests.md`.

### 3.2c BrowserAction für UI-Tests (ADR-0006, Phase 1)

UI-Steps gegen die Model-Driven-App laufen über Microsoft.Playwright .NET im Cli-Pfad. Plugin (Sync-Custom-API + Async-CRUD-Trigger) ignoriert BrowserAction-Steps mit `Skipped`-Verhalten, kein Failure, kein gebrochener Test. Aktivierung im Cli via `--browser-state <path>`.

**Operations:**

| operation | Pflichtfelder | Optionale Felder |
|---|---|---|
| `navigate` | `url` | `waitForSelector`, `timeoutSeconds`, `assertNoLoginRedirect` (Default true) |
| `click` / `doubleClick` | `selector` | `fallbackSelector`, `waitForSelector` |
| `fill` | `selector`, `value` | |
| `delay` | `delayMs` | |
| `waitFor` | `selector` | `timeoutSeconds` |
| `screenshot` | | `name` (Datei-Name ohne Endung) |
| `evaluate` | `expression` | `value` (inline-Assert: result == value oder Step failt), `outputAlias` (Resultat im Test-Context) |

**Inline-Assert via `value`-Feld in `evaluate`:** Statt separatem Assert-Step prüft der `evaluate`-Step direkt das Resultat. Wenn `value` gesetzt: bei Mismatch wirft der Step. Eleganter als separater Assert-Step (`Assert target=Output` ist nicht implementiert).

**Beispiel-Smoke (Contact-Form-Customization eines Projekts):**

```json
{
  "id": "CONTOSO-UI-CONTACT-FORM-CUSTOMIZATION-01",
  "title": "Contoso Contact-Form-Customization aktiv",
  "tags": ["smoke", "ui", "contoso", "tier-1"],
  "steps": [
    { "stepNumber": 1, "action": "BrowserAction", "operation": "navigate",
      "url": "https://contoso-dev.crm4.dynamics.com/main.aspx?pagetype=entitylist&etn=contact",
      "waitForSelector": "[data-id*='HomePageGrid']", "timeoutSeconds": 60 },
    { "stepNumber": 2, "action": "BrowserAction", "operation": "waitFor",
      "selector": "[role='row']", "timeoutSeconds": 30 },
    { "stepNumber": 3, "action": "BrowserAction", "operation": "doubleClick",
      "selector": "[role='row'][aria-rowindex='2']",
      "fallbackSelector": "[role='row']:has([role='gridcell'])",
      "waitForSelector": "[data-id='editFormRoot']", "timeoutSeconds": 60 },
    { "stepNumber": 4, "action": "BrowserAction", "operation": "delay", "delayMs": 4000 },
    { "stepNumber": 5, "action": "BrowserAction", "operation": "evaluate",
      "expression": "() => localStorage.getItem('roleType')",
      "value": "Position",
      "description": "Contoso Custom-Form-Customization verifiziert" }
  ]
}
```

**Diagnostik bei Failure:** Phase 1d schreibt automatisch Screenshot in `jbe_testrunresult.jbe_screenshot` (5 MB File-Feld) und optional Trace-Zip in `jbe_uitrace` (30 MB), abrufbar via Web API `/jbe_screenshot/$value`.

**Mehr im Skill `ui-automation`:** Selektor-Strategie, Frame-Fallback-Pattern, Strict-Mode-Verhalten, Latenz-Variabilität auf DEV-Orgs.

### 3.3 Prüfungen via Assert-Action (ehemals Assertions)

Assert ist eine Action wie jede andere. Sie steht irgendwo in der `steps`-Liste, oft am Ende, aber **Zwischen-Asserts sind erlaubt und erwünscht** (create -> assert -> update -> assert).

| Feld | Beschreibung |
|------|-------------|
| `action` | `"Assert"` |
| `target` | `Record` (per recordRef), `Query` (per entity+filter), `Contact`, `Account`, etc. |
| `field` | Feldname zum Prüfen |
| `operator` | Vergleichsoperator (siehe unten) |
| `value` | Erwarteter Wert (kann Platzhalter enthalten) |
| `entity` | Bei `Query`: EntitySetName |
| `filter` | Bei `Query`: Filter-Array `[{field, operator, value}]` oder Kurzform `{field: value}` |
| `description` | Was die Assertion inhaltlich prüft |
| `onError` | `"continue"` (Default für Assert) oder `"stop"` |

**Operatoren (C#-AssertionEngine, Stand Plugin v5.3.22 / FB-52):**

- Feldbasiert (für `target: "Record"` und `target: "Query"`):
  - `Equals`/`NotEquals`: **numerisch** für native Zahl-Typen (Money/Decimal/Double/Float/OptionSet/Integer),
    sonst String-Vergleich (case-insensitiv, getrimmt). Numerisch heißt: `1000`, `1000.00` und der aus Dataverse
    **retrievte** `1000.0000000000` (Money trägt die Feld-Precision als decimal-Scale) matchen alle den Pack-Wert
    `"1000"`. **Pack-Werte invariant mit Dezimal-Punkt schreiben** (`"1234.56"`, nicht `"1234,56"`); der Vergleich ist
    kulturunabhängig (FB-52, vorher brach jeder Money-Equals mit Nachkommaanteil unter de-DE/de-CH). String-actuals
    (Text-Felder, Condition-Pfad) bleiben bewusst textuell -- `"00123"` ist nicht `"123"`.
  - `Contains`, `StartsWith`, `EndsWith`: String-Vergleich, case-insensitiv; numerische Felder werden invariant
    (Dezimal-Punkt) als String extrahiert.
  - Null: `IsNull`, `IsNotNull`.
  - Ordered: `GreaterThan`, `LessThan` (versuchen nacheinander: numerisch via decimal invariant, dann DateTime, dann
    String; Money/OptionSetValue/Int werden numerisch behandelt). Teilen die Zahl-Extraktion (`TryExtractNumber`)
    mit `Equals`/`NotEquals` -- EINE kulturunabhängige Zahl-Quelle (FB-52).
    **Falle (DateTime):** Der DateTime-Zweig normalisiert `Kind=Unspecified` als UTC (wie Falle 2 bei DateSetRecently). Bis v5.3.16 fehlte das hier (nur DateSetRecently war v5.3.11 gefixt); Fix 2026-06-18 via gemeinsamem `NormalizeToUtc`-Helper (FB-44 Nachtrag, PV-07). Betrifft nur DateTime-Felder, nicht die heute üblichen numerischen GreaterThan/LessThan.
  - Zeit-Toleranz: `DateSetRecently` (Value = Sekunden-Toleranz, Default 120).
    **Falle 1:** Die Toleranz steht ausschließlich im `value`-Key (als String, z.B. `"160"`).
    Einen Key `withinSeconds` gibt es nicht; unbekannte Keys verwirft Json.NET still und
    der Default 120s greift (FB-45). Das value-Parsing existiert seit v5.3 (Commit 6635440),
    wirkt also auch auf deploytem v5.3.10.
    **Falle 2 (vor v5.3.11):** Kind=Unspecified-Werte wurden per `ToUniversalTime()` als
    Lokalzeit interpretiert, Diff um den TZ-Offset zu groß (CEST +7200s), jede Toleranz
    unter 7200s unerfüllbar. Fix in v5.3.11 (FB-44): Unspecified wird als UTC behandelt.
    Auf Envs mit Plugin <= v5.3.10 zeitzonen-neutral asserten (Referenzwert per
    RetrieveRecord in Alias einfrieren, NotEquals/GreaterThan gegen `{alias.fields.<feld>}`).
- Query-only (ohne `field`): `Exists`, `NotExists`, `RecordCount` (Value = erwartete Anzahl).

Unbekannter Operator liefert Assert-Failure mit Message "Unbekannter Operator: X". Siehe FB-27 für Plugin-Step-Probleme nach Stage-and-Upgrade.

```json
{ "stepNumber": 5, "action": "Assert", "target": "Query", "entity": "accounts",
  "filter": [ { "field": "accountid", "operator": "eq", "value": "{account1.id}" } ],
  "field": "websiteurl", "operator": "Equals",
  "value": "https://updated.example.com",
  "description": "Website wurde korrekt gesetzt" }
```

### 3.4 Fehlerverhalten (`onError`)

- `Assert`-Actions laufen bei Failure **weiter** (Default `onError: "continue"`). Am Testende ist der Test `Failed`, wenn mindestens ein Assert nicht `Success=true` war.
- Alle anderen Actions brechen bei Exception den Test **ab** (Default `onError: "stop"`, Outcome=`Error`). Per explizitem `"onError": "continue"` kann man das je Schritt übersteuern.

### 3.4g Konditionale Step-Ausführung (`condition`, ADR-0011)

Ein Step kann eine optionale `condition` tragen. Ist sie zur Laufzeit **nicht** erfüllt, wird der Step
**übersprungen** (Step-Status `Skipped`, kein Failure); ist sie erfüllt, läuft der Step normal (inkl.
`onError`/`expectFailure`). Zwei Steps mit gegensätzlicher `condition` bilden ein if/else. Use Case: ein
Test, der sich ohne Re-Edit an die Ziel-Config anpasst (z.B. eine Field-Governance mit Writeback an/aus je
Umgebung) und im "starken" Zweig die **echte** Datensatz-Änderung prüft statt nur einen Log-Eintrag.

**Drei Formen, genau EINE pro condition** (Laufzeit löst `all` vor `any` vor Einfachklausel):

```jsonc
// Einfachklausel
"condition": { "left": "{wbcfg.fields.contoso_writebacktocontact}", "operator": "Equals", "right": "true" }

// all (AND) - alle Klauseln müssen erfüllt sein
"condition": { "all": [ { "left": "{a.fields.x}", "operator": "Equals", "right": "1" }, { "left": "...", "operator": "IsNotNull" } ] }

// any (OR) - mindestens eine Klausel
"condition": { "any": [ { "left": "...", "operator": "Equals", "right": "..." }, { "left": "...", "operator": "Equals", "right": "..." } ] }
```

**Operatoren** (geteilter `ValueComparator`, identisch zur Assert-Action, **case-insensitiv**):
`Equals`, `NotEquals`, `IsNull`, `IsNotNull`, `Contains`, `StartsWith`, `EndsWith`, `GreaterThan`,
`LessThan`. `IsNull`/`IsNotNull` brauchen kein `right`. NICHT enthalten (bewusst):
`DateSetRecently`/`Exists`/`NotExists`/`RecordCount` (Assert-/Query-only) und `In`/`NotIn` (nur im
Filter-Set des `GenericRecordWaiter`).

**`left`/`right` laufen durch die PlaceholderEngine.** Zwei Fallstricke (verifiziert, siehe auch 3.5):

- **Boolean-Vergleich bleibt case-insensitiv.** `{alias.fields.<bool>}` liefert `"True"`/`"False"`
  (groß); der Comparator vergleicht case-insensitiv, `right: "true"` matcht also. Ein selbstgebauter
  case-sensitiver Vergleich würde brechen.
- **Unaufgelöster Platzhalter -> harter Step-Fehler (Outcome `Error`), kein stiller Skip.** Ein
  unbekannter/falsch geschriebener Alias lässt `{x.fields.y}` wörtlich stehen (die PlaceholderEngine
  wirft dort nicht). Die Condition-Auswertung prüft genau das und wirft, damit ein Alias-Tippfehler
  nicht zu einem falsch-grünen Skip wird.

**Test-Outcome bei lauter Skips:** Ein Test, dessen Asserts **alle** condition-übersprungen wurden, wird
**`Skipped`** (nicht Passed) - ehrlich sichtbar, nicht falsch-grün.

**Abgrenzung zu `AssertEnvironment`:** `AssertEnvironment` prüft Umgebungs-Vorbedingungen und lässt den
Test **scheitern** (Fail), wenn sie nicht stimmen. `condition` ist orthogonal: sie **überspringt** den
Step (Skip), wenn die Bedingung nicht zutrifft. Scheitern vs. Überspringen.

**PackValidator-Lint `CONDITION_MALFORMED`** (Pre-Run, bricht bei Error ab, siehe Regel-Tabelle in
Abschnitt 11): leere condition, gemischte Formen, leeres `all`/`any`, unbekannter/fehlender Operator
oder fehlendes `left` (Error); wertbasierter Operator ohne `right` (Warning). Ein undefinierter Alias in
der condition wird zusätzlich von `ALIAS_UNDEFINED` erfasst.

### 3.4d Sandbox-sichere expectFailure (ADR-0005, Plugin v5.3.3)

Steps mit `expectFailure: true` oder `expectException: {...}` auf primitiven
Service-Aktionen (Create/Update/Delete/ExecuteRequest) werden ab Plugin
v5.3.3 via `ExecuteMultipleRequest` mit `ContinueOnError=true` als
Sub-Transaktions-Boundary ausgeführt. Faults landen als
`OrganizationServiceFault` in `stepResult.ActualDisplay` (z.B.
`"OrganizationServiceFault [0x80040217]: ..."`). Vor v5.3.3 brach der
Sync-Custom-API-Pfad mit `0x80040265` ab.

Aktionen die NICHT im Scope sind (Stretch v2):
- WaitForRecord/FindRecord mit expectFailure (Multi-Call-Polling)
- RetrieveRecord, SetEnvironmentVariable, RetrieveEnvironmentVariable
- Variante 2b (FindRecord auf systemuser ohne expectFailure crasht
  weiterhin, siehe FB-31b)

Im Async-CRUD-Trigger-Pfad ist FB-31 nicht relevant (Async-Plugins haben
eigene Transaktions-Boundary). Tests die nur dort laufen können, brauchen
keinen FB-31-Workaround.

**ABER:** Im Async-Pfad scheinen Plugin-Exceptions im Async-Worker-
Kontext stillgelegt zu werden. Beobachtet 2026-04-25 an einem
Negativtest gegen ein Restore-Plugin: das Plugin wirft eine
`InvalidPluginExecutionException("...disabled in Entra...")`, im
Sync-Pfad würde der Update mit dieser Exception fehlschlagen, im
Async-Pfad geht das Update durch (der Status landet trotzdem auf
dem neuen Wert), und der `expectException`-Step bekommt
`"Expected exception but action succeeded"`. Plugin-Trace zeigt
trotzdem den Throw.

**Folge:** `expectException` ist im Async-Pfad **nicht zuverlässig**
für Plugin-Exceptions. Negative-Path-Tests die auf Plugin-Throws
prüfen, sollten:

1. Im Sync-Pfad laufen (projektseitiger Sync-Runner oder
   `API.executeAction("jbe_RunIntegrationTests", ...)`), wo
   ADR-0005-Fix den Fault sauber als `OrganizationServiceFault` im
   `ActualDisplay` durchreicht.
2. Oder: das Plugin-Verhalten per Plugin-Unit-Test in der jeweiligen
   Plugin-Test-Suite abdecken.

Plattform-Exceptions (nicht Plugin-Exceptions) verhalten sich
auch im Async-Pfad wie erwartet (Update bricht ab, Step `Failed`
mit Exception-Match).

### 3.4e Custom-API Pattern 1 Stage 30 MainOperation Fault-Propagation (v5.3.9 Fix)

Ein Nutzerprojekt hat 2026-05-16 einen Pfad-asymmetrischen Bug gemeldet:

`expectException`-Tests gegen Custom-APIs mit **Pattern 1** (PluginType direkt am `customapi.plugintypeid` verknüpft, Stage 30 MainOperation) wurden im CLI-Pfad als `Outcome=Errored` gewertet, obwohl die Plugin-Exception-Message exakt das `messageContains`-Pattern enthielt. Im Plugin-CRUD-Trigger-Pfad lief derselbe Test korrekt grün.

**Plattform-Empirie (nicht offiziell in Microsoft Learn dokumentiert):**

| Plugin-Pattern | Stage | Fault-Propagation in ExecuteMultipleRequest |
|---|---|---|
| Custom-API Pattern 1 | 30 MainOperation | `FaultException<OrganizationServiceFault>` am Endpoint |
| Pre/PostOp-Plugin | 20 / 40 | `Responses[0].Fault`-Slot |
| Async-Worker-Sandbox | beliebig | `Responses[0].Fault`-Slot |

Damit verhält sich `ContinueOnError = true` bei MainOperation-Custom-APIs **nicht** wie bei Pre/PostOp-Plugins.

**Fix in Plugin v5.3.9 (ADR-0005 Stufe 3):** `TestRunner.ExecuteSandboxSafe` fängt `FaultException<OrganizationServiceFault>` aus dem `_service.Execute(emReq)`-Aufruf und mappt sie auf den normalen Fault-Slot-Pfad (`return (null, faultEx.Detail)`). Damit ruft `ExecuteStepInSandboxBoundary` `EvaluateExpectException` auch in dieser Plattform-Variante korrekt auf. Andere Exception-Typen (Netzwerk, Timeout) propagieren weiter und werden als echter Test-Error behandelt.

**Verifikation:** Vier Mock-Unit-Tests in `ExpectExceptionCustomApiTests.cs` pinnen beide Plattform-Varianten plus Pre/PostOp-Regress. Vor Fix: zwei Tests rot. Nach Fix: 234/234 grün.

**Folge für Test-Autoren:** Ab Plugin v5.3.9 verhält sich `expectException` mit `messageContains`/`messageMatches` gegen Custom-APIs identisch im CLI-Pfad und im Plugin-Pfad. Bestehende Negative-Path-Tests gegen Pre/PostOp-Plugins sind unverändert grün.

### 3.4c Plattform-Pitfalls bei Negative-Path-Tests (Stand v5.3.2)

Wenn ein Test mit `expectException` `FAILED` ist, ist die Ursache oft
**nicht** dass das Plugin die falsche Message hat, sondern dass eine
**Plattform-Exception** vor dem Plugin geworfen wurde. Häufige Fälle:

- **State-locked Entity-Creation:** `task`, `email`, `quote`,
  `salesorder`, `invoice` erlauben `CreateRecord` nur in Default-State.
  Endstatus per nachfolgendem `UpdateRecord` setzen. Sonst kommt:
  `"6 is not a valid status code for state code TaskState.Open"`,
  das matcht nicht deine Plugin-Message, Test ist FAILED mit
  Plattform-Exception statt Plugin-Exception.
- **Read-Only-Field-Update:** PK-Felder (`accountid`), `createdon`,
  AutoNumber-Felder können nicht per Update gesetzt werden. Plattform-
  Exception kommt vor jedem Plugin.
- **Required-Field-Missing:** Pflichtfelder werden vor jedem Plugin
  von der Plattform geprüft.

**Diagnose:** Ab Plugin v5.3.2 enthält der `actualDisplay` des
StepResults Type + ErrorCode der gefangenen Exception. Wenn dort
`FaultException [0x80048408]` o.ä. steht und nicht der erwartete
Plugin-Code, ist es eine Plattform-Exception. Voll-Liste der Pitfalls
im Handbuch unter `02-testfall-schreiben/10-pitfalls.md`.

### 3.4d Verifizierte Test-Schreib-Pitfalls (Praxisbefunde ab 2026-06-25)

Beim Bau von 14 Umsatzverteilungs- und Pipeline-Coverage-Tests in einem Nutzerprojekt empirisch belegt; alle generisch (nicht projektspezifisch):

- **EntitySetName mit `-ies`-Plural (`contoso_countries`) ist im `entity`-Feld UND im `@odata.bind`-Pfad NICHT auflösbar (belegt 2026-07-20).** `EntityMetadataCache.ResolveLogicalName` probiert bei einem unbekannten Namen nur `strip "es"` und `strip "s"`; aus `contoso_countries` wird `contoso_countri`/`contoso_countrie`, beide existieren nicht -> `entity ... not found in MetadataCache`. Einfache `-s`-Plurale (`contoso_country_allocations` -> `...allocation`, `contoso_dataaccessgrants` -> `...grant`, `teams` -> `team`) lösen sauber auf, nur `-ies`/`-y` bricht. **Lösung: den Logical-Namen angeben** -- im `entity`-Feld von FindRecord/WaitForRecord/Assert `"entity": "contoso_country"`, und im Bind-VALUE `"...@odata.bind": "/contoso_country({land.id})"` (`ExtractEntityFromBindValue` liest den EntitySet aus dem VALUE und jagt ihn durch dieselbe Auflösung). Der Feldname bleibt korrekt, weil der `_contoso_country`-Suffix bei `contoso_countryid`/`contoso_address1_countryid` nicht greift.
- **Env-portabler Test ohne Cross-Env-GUID-Umschreiben (Pattern, belegt DEV+TEST 2026-07-20):** Statt Land-/Team-GUIDs hart zu binden (Muster mit TEST-Umschreib-Skript), die Stammdaten-Records zur Laufzeit **per Name** auflösen: `FindRecord contoso_country` (filter `contoso_name eq 'Italien'`) bzw. `FindRecord teams` (filter `name eq '<Teamname>'`) -> Alias, dann `{alias.id}` im Bind/Filter. Namen sind DEV/TEST-stabil, GUIDs nicht; derselbe Pack läuft ohne Anpassung auf beiden Envs (DEV+TEST je 1/1 PASS bei komplett verschiedenen TEST-GUIDs).
- **`@odata.bind` erwartet den Attribut-Logical-Namen (lowercase), NICHT die OData-Navigation-Property.**
  Bei einem Lookup mit PascalCase-SchemaName (z.B. `contoso_ProjektServicemanager`, dessen Navigation-Property
  ebenfalls `contoso_ProjektServicemanager` heißt) muss im Bind der **lowercase Logical-Name** stehen:
  `"contoso_projektservicemanager@odata.bind": "/contacts({c.id})"`. Mit der Navigation-Property (PascalCase)
  kommt zur **Laufzeit** (nicht in der Pre-run-Validierung, Lücke in `LOOKUP_BIND_FORMAT`, die nur den
  `_xxx_value`-Fall prüft): `'<entity>' entity doesn't contain attribute with Name = '<PascalCase>' and
  NameMapping = 'Logical' (look up attribute by name is case-sensitive)`. Die `…id`-Suffix-Lookups
  (`contoso_leistungid`, `contoso_bestellungid`, `pricelevelid`) sind unkritisch, weil dort logical == nav.
- **Lookup-Filter UND Assert-`field`: Logical-Name, nicht `_xxx_value`.** `WaitForRecord`/`Assert` auf einen
  Lookup nutzen den Logical-Namen (`contoso_umsatzverantwortlich`), nicht das OData-`_contoso_umsatzverantwortlich_value`
  (sonst `FILTER_FIELD_NOT_LOGICAL` im Pre-run). Ein Assert `field=<lookup-logical>` `Equals` `<GUID>` ist
  zulässig, die Engine vergleicht `EntityReference.Id` gegen die GUID.
- **Nie geschriebene Money-Felder sind `null`, nicht `0`.** Ein Plugin, das ein Feld mangels Wert nie setzt,
  hinterlässt `null`. `Assert … Equals "0"` failt dann mit `act=<null>`. Für "kein Wert erwartet" `IsNull`
  statt `Equals 0`.
- **Async-Plugins löschen sequenziell mit Einzel-Commit (keine Sammel-Transaktion).** Ein `WaitForNotExists`,
  das nur auf EINEN von N zu räumenden Records filtert, kann zu früh durchlaufen (dieser eine weg, andere noch
  da) und ein nachfolgender Mengen-`Assert NotExists` failt. Filter auf die **gemeinsame Quelle** (alle N,
  z.B. nur `contoso_opportunityid`), nicht auf ein diskriminierendes Feld (`contoso_monat`).
- **`transactioncurrencyid@odata.bind` auf `/transactioncurrencies(...)` scheitert** mit
  `entity ... 'transactioncurrencies' ... not found in MetadataCache` (System-Entity nicht im Lazy-Cache).
  Wenn möglich weglassen -> Org-Default-Währung; bei quote+Preisliste reicht das, sofern die Preisliste die
  Org-Base-Currency trägt.
- **Async Job-Queue-Latenz:** Money-Asserts ohne pollbaren Status-Indikator brauchen grosszügige `Wait`
  (30s ist unter Last grenzwertig, 50s sicher; belegt am EK-Actual-Plugin, das pro Ereignis zwei
  Aktualisierungen fährt). Wo ein Status-/Picklist-Wechsel den Abschluss markiert, stattdessen `WaitForRecord`
  mit dem Zielwert im Filter (culture-/scale-unabhängig, kein Money-Polling).
- **Mehrstufige async-Verteilung: ein Status-Wait auf den ERSTEN Teilschritt reicht NICHT (belegt 2026-06-26).** Wenn ein Status-Wechsel nur EINEN Teilschritt einer Plugin-Kette markiert und der nächste
  (Money-)Schritt nicht-atomar folgt, läuft ein `WaitForRecord` auf nur den Status-Marker zu früh durch. Konkret
  bei der Nach-vorn-Regel: der Test wartete auf `contoso_monat=3 contoso_status=Abgerechnet` (Einrasten), aber die
  nachgelagerte Money-Neuverteilung der anderen Monate (Jan/Feb -> 0, April -> Rest) folgt minimal später ->
  isoliert + auf DEV grün, im 80er-Volllauf unter Last **rot** (Asserts trafen den Erstverteilungs-Zustand
  je 1000). **Fix nach Indikator-Typ:**
  - **Nachgelagerter Money-Wert (Neuverteilung):** Wurzel ist die **variable async-Latenz unter Volllauf-Last**,
    die Neuverteilung der anderen Monate kann **>50s, im Batch >90s** nach dem Status-Einrasten dauern
    (Job-Queue-Stau bei ~80 parallel laufenden Tests). **Money-Polling per `WaitForRecord` FUNKTIONIERT** (der
    `eq`-Filter matcht den Wert, per direktem OData-`$filter` + KeepRecords-Inspektion belegt); ein früher
    beobachteter 90s-Timeout war **echtes Last-Timing** (Wert zu dem Zeitpunkt noch nicht geschrieben), KEIN
    Money-Polling-Defekt. Ein **fixer `Wait`** ist nur **grenzwertig**: 50s reichten für einen Test, aber
    NICHT für einen zweiten mit längerer Restverteilung (Volllauf-FAIL, Restverteilung nach 50s noch nicht durch). **Robust: pollender
    `WaitForRecord` auf den Endwert mit hohem `timeoutSeconds` (150s)**, wartet genau so lange, wie die Last
    erfordert, statt fix zu raten. (Belegt 2026-06-26: der zweite Test, **umgestellt** auf pollenden
    `WaitForRecord` (`contoso_monat=7`/`contoso_forecast_vk=800`, `timeoutSeconds=150`, pollingIntervalMs=5000),
    lief isoliert per CLI auf DEV **grün** (57s) -> der Money-`eq`-Filter matcht, der „Money-Polling timeoutet
    trotz korrektem Wert"-Verdacht ist damit endgültig widerlegt.
    **2026-06-27 im 138er-DEV-Volllauf grün verifiziert** (OK in 14s, großer Puffer zu 150s; reale
    Volllauf-Last durch 46-74s Laufzeit der Nachbartests belegt) -> Flake geschlossen.)
  - **Nachgelagerter Record/Task (Existenz):** `WaitForRecord` auf die **Existenz** des async erzeugten Records
    ist zuverlässig (kein Money-Polling), z.B. die Differenz-Aufgabe (`entity:"tasks"`,
    `filter regardingobjectid eq {pos.id}`, polymorpher Lookup wird akzeptiert).
  - **Abgrenzung:** Prüft der Test nach dem Status-Wait nur Felder **desselben** Records (Status + Money im
    selben Plugin-Update, z.B. Monatsstatus + Monatsdifferenz), ist KEIN Zusatz-Wait nötig, der
    Status-Wait deckt die Asserts ab. Nicht „verschlimmbessern" (ein Money-Filter im Wait würde nur Flake einführen).
  - **Engine-Primitiv `WaitForAsyncCompletion` (ADR 2026-06-28, verifiziert + Volllauf-getestet):**
    Statt den geratenen Money-Endwert mit festem `timeoutSeconds` zu pollen, wartet dieses Primitiv auf
    **async-Job-Quiescence**: es pollt `asyncoperation`, bis über ein Stabilitätsfenster kein offener Job
    (statecode 0/1/2) mehr existiert. Plattform-verifiziert (DEV-Org 2026-06-28): der App-User darf asyncoperation
    lesen; statecode 3 = fertig; `primaryentitytype` ist bei Plugin-Jobs leer und `correlationid` bündelt eine
    fachliche Kette **nicht** zuverlässig -> Korrelation über **`regardingobjectid`-Set (`aliases`)** und/oder ein
    **Zeitfenster (`createdon`)**. CLI-/Core-only (im Plugin-Sandbox-Pfad sauber geskippt). **Hybrid (Richtung C)
    empfohlen:** WaitForAsyncCompletion als Haupt-Sync + abschließender `WaitForRecord`-Wert-Backstop.
    **STATUS 2026-06-28 (verifiziert + Last-getestet):** Engine + 628 Unit-Tests grün (inkl. Gegenprobe), ADR
    2026-06-28 0015 (`02_decisions/adr/ADR-2026-06-28-0015-engine-waitforasynccompletion.md` im Repo D365TestCenter-Workspace). **Finale
    Korrelations-Wahl: regobj-Set `aliases:["pos","detail"]` + 20s-Zeitfenster + 90s-WaitForRecord-Wert-Backstop
    (Hybrid).** Der Referenztest lief gegen die erholte DEV-Umgebung **8x grün in Folge** (5x isoliert + 3x im
    138er-Volllauf unter Parallel-Last), **kein Falsch-Grün**, Quiescence schnell (19-26s, weit unter der
    240s-Sicherheits-Obergrenze). Das reine Zeitfenster (Plan B) war nicht nötig. **Gate-Lehre:** der
    offene-Jobs-Zähler ist **nicht** an der rohen Gesamtzahl zu messen, dauerhaft suspendierte uralte System-Jobs
    (statecode 1, altes createdon) blockieren frische Kette-Jobs nicht (regobj- UND Zeitfenster-Korrelation
    ignorieren sie); maßgeblich sind aktive (statecode 0/2) + frische (<=5min) Jobs. Volle Empirie:
    `03_implementation/vorgaenge/2026-06-27-engine-waitforasynccompletion/` (plattform-verifikation + korrelation-zeitfenster).
- **Query-Assert-Filter matcht exakt (`eq`), nicht startswith: bricht bei `{TIMESTAMP}`-Namen (belegt 2026-06-27).** Ein `Assert target:Query` mit `filter:{contoso_bezeichnung:"JBE X"}` sucht den Wert **exakt**; Records,
  die mit `"JBE X {TIMESTAMP}"` angelegt wurden, werden NICHT gefunden -> 0 Records -> FAIL, obwohl die API/das Plugin
  korrekt lief (an einem Kopier-Test beweisbar: sogar der in Step 2 erzeugte Quell-Record wird nicht gefunden). Wurzel der
  betroffenen Fälle war ein Drift-„Fix" `contoso_bezeichnung~`->`contoso_bezeichnung`, der einen früheren startswith-Match (`~`) zu exakt
  machte. Robust: `operator: StartsWith`/`Contains` statt `Exists`+exaktem Filter, oder den vollen erzeugten Namen
  referenzieren. (Trifft alle Tests, die einen mit `{TIMESTAMP}` angelegten Record per exaktem Namensfilter suchen.)
- **activityparty-Partylist im `CreateRecord`/`UpdateRecord` via `$type` EntityCollection (seit 2026-06-27, ADR 2026-06-27 2020).**
  `ApplyFields` löst `$type`-Feldwerte über denselben `ResolveTypedValue` auf wie der ExecuteRequest-Pfad. Eine Partylist
  (z.B. appointment `requiredattendees`, email `to`/`cc`/`bcc`) wird damit im Create gesetzt:
  `"requiredattendees": { "$type": "EntityCollection", "entities": [ { "$type": "Entity", "entity": "activityparty", "fields": { "partyid": { "$type": "EntityReference", "entity": "contact", "ref": "con" } } } ] }`.
  Der `appointment` behält seinen regulären `alias` (sauberer Record für Assert/Cleanup); `participationtypemask` ist nicht
  nötig. Ein **rohes** `appointment_activity_parties`-Array (ohne `$type`) bleibt unsupported (Engine behandelt es als Attribut
  -> `'appointment' entity doesn't contain attribute with Name = 'appointment_activity_parties'`) -> immer das
  `$type`-EntityCollection-Format nutzen. Belegt an einem Termin-Integrationstest (DEV-Org, CLI grün).
- **Lookup-Feld im Assert: SDK-Name (`contoso_contactid`), NICHT Web-API-Notation (`_contoso_contactid_value`).** Der CLI-/Core-Pfad
  asserted über die SDK-QueryExpression; ein `field:"_contoso_contactid_value"` wirft `'appointment' entity doesn't contain
  attribute with Name = '_contoso_contactid_value'`. Das `_x_value`-Format ist nur Web-API/OData. Lookup-Wert prüfen mit
  `field:"contoso_contactid"`, `operator:"Equals"`, `value:"{alias.id}"` (der ValueComparator extrahiert die EntityReference zur
  GUID). Belegt am selben Test (mit `_contoso_contactid_value` FAIL, mit `contoso_contactid` PASS). Trifft alle CLI-Tests, die ein
  Lookup-Value asserten.
- **`Assert target:"Result"` (Output-Assert) ist totes Ziel** (Engine kennt nur `Record`/`Query`) -> reine Output-APIs nur
  als Aufrufbarkeits-Smoke prüfen.
- **Ein Wait auf den ERSTEN Record einer Multi-Record-Erstverteilung reicht NICHT, wenn danach sofort eine
  Struktur-Änderung folgt (belegt 2026-07-02, DEV-Org, zwei Revert-Tests).** Erzeugt ein
  Positions-Create eine mehrmonatige Erstverteilung (mehrere `contoso_umsatzplan`-Records async), reicht ein
  `WaitForRecord` auf NUR den ersten Monat (z.B. Monat 1) NICHT als Beleg „Erstverteilung fertig", dieselbe
  Falle wie die bereits dokumentierte „Status-Wait auf den ERSTEN Teilschritt" (siehe oben), hier
  aber auf der Erstverteilung selbst statt auf einer Reverteilung. Symptom: wird direkt danach ein Actual
  gesetzt und sofort eine Struktur-Änderung (z.B. Positions-Ende verlängert) getriggert, kann die noch **laufende**
  Erstverteilung der übrigen Monate NACH der bereits korrekten Reverteilung fertig werden und sie mit einer
  frischen, actual-unwissenden Gleichverteilung überschreiben, Endergebnis: alle Monate (inklusive des
  bereits „eingerasteten") tragen denselben uniformen Wert (Positions-Gesamtbetrag / Monatsanzahl), der Actual-Effekt ist
  spurlos weg. Reproduzierbar auch **isoliert** (nicht nur unter Volllast), also eine echte Race und kein
  reiner Last-Flake. Fix: explizit auf **alle** Monate der Erstverteilung warten (`WaitForRecord` je Monat),
  erst danach den Actual setzen; zusätzlich vor jeder Struktur-Änderung ein `WaitForAsyncCompletion` (scope
  `aliases:[<positions-alias>]`) als zweite Absicherung. Beide Maßnahmen zusammen (Hybrid) waren im Nachtest 3x in
  Folge stabil grün, isoliert und unter Volllauf.
- **CLI-Filter (ab ADR 2026-06-30 1432, Filter-Negation):** Der Filter wird komma-getrennt in Terme zerlegt,
  jeder Term optional negiert per `!`-Präfix (Exclude). `tag:A,tag:B` lädt jetzt **A ODER B** (vorher 0 -- der
  alte Parser behandelte den ganzen Rest als einen Tag "A,B"). Negation: `*,!ITEM-STATUS-01` = alle ausser einem;
  `!tag:known-flaky` = alle ausser den so getaggten; `ORD*,!ORD-STATUS-01` = Gruppe minus einen. Semantik:
  Include = Vereinigung der positiven Terme (kein positiver Term -> alle aktivierten), minus alle Exclude-Treffer.
  Implementiert zentral in `TestCaseFilter.Apply` (geteilt von CLI- und Plugin-/Coordinator-Pfad).
- **Harte Bestandsdaten-IDs sind NICHT umgebungsportabel.** Ein `@odata.bind` auf eine feste GUID von
  Stammdaten (Saisonmuster, Preisliste, Produkt, UoM) failt auf einer anderen Umgebung mit
  `Entity '<X>' With Id = <guid> Does Not Exist`, schon im Setup-`CreateRecord`, vor jeder Plugin-Logik
  (Outcome ERR, ~1-2s). Belegt beim DEV->TEST-Transfer (ein Saisonmuster, eine Preisliste).
  Für portable Tests die Referenzdaten **im Test selbst anlegen** (`CreateRecord`), nicht hart per GUID binden.
  **`FindRecord` by name reicht NICHT**: der Name variiert ebenfalls zwischen Umgebungen (DEV-Preisliste
  `Standardpreisliste` existiert auf TEST gar nicht; FindRecord läuft dann in den Timeout). Verifiziert behoben
  (2026-06-25): der Verteilungstest legt sein Saisonmuster selbst an, der Pipeline-Test seine Preisliste, beide danach auf
  DEV **und** TEST grün. **Spezialfall quote/Pipeline:** eine quote braucht zwingend eine Preisliste, sonst
  bleibt `totalamount` 0 (kein Pricing) und das Pipeline-Plugin erzeugt nichts; eine Preisliste **ohne**
  `transactioncurrencyid` erbt die Org-Default-Währung (umgeht den nicht auflösbaren `transactioncurrencies`-Bind),
  die quote daran erbt sie ebenfalls -> Währungen matchen automatisch (kein `0x80048cf6`). Die Angebotsposition
  als Freitext (`isproductoverridden:true`) mit explizitem `extendedamount` braucht kein Produkt/UoM.
- **Cleanup räumt NUR von Test-Steps getrackte Records -- serverseitig (von der getesteten API/dem Plugin)
  erzeugte Records bleiben liegen und können als Abhängige den Delete der getrackten Eltern-Records blockieren
  (FB-54 (`06_referenzkataloge/fehlerbildkatalog.md` im Repo D365TestCenter-Workspace), belegt 2026-07-17 auf einer DEV-Org: 19 Account-Waisen nach
  einem Beleg-API-Pack, Belege an `customerid` blockierten den Account-Delete; Lauf blieb GRÜN, der
  Delete-Fehler steht nur im Steps-Tab/`jbe_fulllog`).** In die Cleanup-Löschliste kommt automatisch nur
  `CreateRecord`; `FindRecord`/`WaitForRecord` trackt per Default nie (Stammdaten-Schutz 2026-06-23),
  Query-Asserts und `CallCustomApi`-Outputs tracken nie. **Fix (umgesetzt + live-verifiziert 2026-07-17,
  ADR 2026-07-17 1801 (`02_decisions/adr/ADR-2026-07-17-1801-cleanup-serverseitig-erzeugte-records.md` im Repo D365TestCenter-Workspace)):
  Tests, deren getestete API Records erzeugt, deklarieren diese explizit** -- per `WaitForRecord` mit
  `trackForCleanup: true` (gefundenen Beleg in die Löschliste; LIFO löscht ihn VOR dem Eltern-Record) oder
  per `TrackRecord` (`entity` + `recordId` aus dem API-Output, ohne Query). Zusätzlich weist der Lauf
  Cleanup-Fehler jetzt aggregiert aus (Banner `CLEANUP-WARNUNG`, Run-Summary, Audit-Kommentar
  sync-zephyr/sync-devops via `jbe_assertionresults`-Cleanup-Eintrag) -- ein grüner Lauf mit Datenleck ist
  nicht mehr still. Live-Beleg DEV-Org: Repro ohne Tracking = Warnung + Waisen (Read-back 1/1); mit
  `trackForCleanup` = restlos sauber (Read-back 0/0). CLI-Pfad sofort wirksam (Core + neue CLI); der
  Plugin-/Worker-Pfad übernimmt beim nächsten Plugin-Deploy. Nach `keeprecords=false`-Serienläufen mit
  Beleg-erzeugenden APIs trotzdem den Waisen-Check per Namenspräfix-Query fahren (Goldene Regel 3),
  solange nicht alle Packs nachgezogen sind.
- **Plugin-erzeugte Kind-MENGEN dynamischer Größe: `cleanupChildren` deklarieren (2026-07-23,
  ADR 2026-07-23 0808 (`02_decisions/adr/ADR-2026-07-23-0808-cleanup-kind-deklaration.md` im Repo D365TestCenter-Workspace)).**
  `trackForCleanup`/`TrackRecord` deklarieren EINZELNE bekannte Records, für N asynchron entstehende,
  im Testverlauf wandernde Kinder (z.B. `contoso_umsatzplan`-Monatszeilen an Test-Positionen, Restrict-Delete-Härtung)
  nicht abbildbar. Deshalb deklarativ am Step des Parents:
  `"cleanupChildren": [ { "entity": "contoso_umsatzplans", "lookupField": "contoso_bestellungid" } ]`
  (CreateRecord; getrackte FindRecord/WaitForRecord; TrackRecord). Der Cleanup fragt die Kinder ZUR
  CLEANUP-ZEIT ab (finale Menge) und löscht sie VOR dem Parent. Bewusst KEIN Metadaten-Discovery und
  genau EINE Ebene (Fortführung der deterministischen A-Linie, nicht das verworfene Voll-B).
  Dazu zwei Toleranzen im Cleanup: **404 zählt als „bereits geräumt"** (Plattform-Cascade hat den
  getrackten Record schon mitgenommen, die CLEANUP-WARNUNG-Zahl ist damit wieder ein echter
  Rest-Indikator, keine Doppel-Delete-Verfälschung mehr) und der Plattform-Konflikt **„More than one
  concurrent Delete requests detected"** (ein async-Plugin-Job löscht dieselben Kinder parallel, live
  belegt) wird per Folgerunden-Nachprüfung rekonziliert statt blind geschluckt (max 10 Runden a 1s,
  Amok-Schutz 10.000 Kinder je Beziehung). Suite 657 grün; Wirkbeleg 2026-07-23: 5 Umsatzverteilungs-/
  Budget-Tests 5/5 PASS ohne CLEANUP-WARNUNG, Read-back 0 (vorher dauerhaft 1-2 Restrict-Fehler je Test).

### 3.4b Negative-Path-Tests via `expectFailure` (v5.3)

Für Tests bei denen das **erwartete Ergebnis ein Fehler ist** (Plugin-
Guard blockiert, ReadOnly-Update wird abgelehnt, OC-Conflict, Plugin-
Validation-Failure):

```json
{ "action": "UpdateRecord", "alias": "contact1",
  "fields": { "statecode": 0 },
  "expectFailure": true,
  "description": "Reaktivierung muss durch GDPR-Guard blockiert werden" }
```

**Semantik:**

| Laufzeit | Ohne expectFailure | Mit expectFailure |
|---|---|---|
| Step wirft Exception | Step `Failed`, Test `Error` (oder `Failed` bei onError=continue) | Step **Passed** |
| Step läuft ohne Exception | Step `Passed` | Step `Failed` mit "Expected exception but action succeeded" |

Erweiterte Variante mit Match auf die konkrete Exception:

```json
{ "action": "UpdateRecord", "alias": "contact1",
  "fields": { "statecode": 0 },
  "expectException": {
    "messageContains": "can only be reactivated by a GDPR admin",
    "errorCode": "0x80040227"
  } }
```

**Felder unter `expectException`** (alle optional, AND-verknüpft):

| Feld | Semantik |
|---|---|
| `messageContains` | Substring case-insensitive im Message-Text |
| `messageMatches` | Regex case-insensitive (exklusiv zu `messageContains`) |
| `errorCode` | Dataverse-Error-Code z.B. `0x80040227` (aus FaultException oder Message-Regex) |
| `httpStatus` | HTTP-Status z.B. 400 (per Reflection/Message-Scan) |

`expectException` impliziert `expectFailure: true`. Nur für Non-Assert-
Actions (Assert hat eigene Negativ-Operatoren wie `NotEquals`,
`IsNull`, `NotExists`).

Detail-Doku: `D365TestCenter-Workspace/03_implementation/expectfailure-feature.md`.

### 3.5 Platzhalter

| Platzhalter | Ergebnis |
|-------------|----------|
| `{GENERATED:firstname}` | Zufälliger Vorname mit "JBE Test" Prefix |
| `{GENERATED:lastname}` | Zufälliger Nachname |
| `{GENERATED:email}` | E-Mail @example.com |
| `{GENERATED:phone}` | Telefon 555-xxxx |
| `{GENERATED:mobile}` | Mobil 555-xxxx |
| `{GENERATED:company}` | Firmenname |
| `{GENERATED:text}` | Zufallstext |
| `{GENERATED:guid}` | Neue GUID, 8 Zeichen (Hex-Suffix) |
| `{TIMESTAMP}` | ISO-Timestamp (jetzt), 19 Zeichen (`20260424_110403_012`) |
| `{TIMESTAMP_MINUS_1H}` | ISO-Timestamp (vor 1 Stunde), 19 Zeichen |
| `{alias.id}` | ID eines per Alias referenzierten Records |
| `{alias.fields.xxx}` | Feldwert eines Alias-Records |
| `{alias.outputs.xxx}` (v5.3.5) | OrganizationResponse-Wert aus ExecuteRequest mit `outputAlias` |
| `{alias.outputs.xxx[type=Y]}` (v5.3.5) | EntityReferenceCollection-Filter (z.B. CreatedEntityReferences) |

**Platzhalter-Auflösung: verifiziertes Engine-Verhalten (S43, `PlaceholderEngine.cs`).** Zwei
Fallstricke, die bei `condition`-/Assert-Vergleichen auf gelesene Feldwerte zählen:

- **Boolean-Format hängt vom Platzhalter-Pfad ab.** `{alias.fields.<feld>}` formatiert einen Boolean
  über `val.ToString()` zu **`"True"`/`"False"` (Großschreibung)** -- NICHT lowercase. Nur
  `{alias.outputs.x}` und `{RESULT:alias.field}` laufen über `FormatValueForPlaceholder` und liefern
  `"true"`/`"false"`. OptionSetValue wird zur Zahl, EntityReference zur GUID (beide Pfade). Konsequenz:
  Vergleiche auf gelesene Boolean-Felder **müssen case-insensitiv** sein (die Assert-Operatoren
  `Equals`/`NotEquals` sind es). Ein case-sensitiver Eigenvergleich gegen `"true"` schlägt fehl.
- **Unbekannter Alias wirft NICHT, er bleibt wörtlich stehen.** `{xyz.fields.f}` mit unbekanntem Alias
  `xyz` lässt den Platzhalter **unverändert** im String (kein Fehler). Nur ein **bekannter** Alias mit
  nicht-geladenem Feld (oder Alias in `Records`, aber nicht in `FoundRecords`) wirft. Konsequenz: ein
  **Tippfehler im Alias** wird nicht als Fehler erkannt -- der Literaltext fließt in den Vergleich und
  matcht typischerweise nicht, was bei einer `condition` zu stillem Skip und damit falsch-grünem Test
  führen kann. Wer auf aufgelöste Werte verzweigt, muss "Platzhalter nicht aufgelöst (enthält noch
  `{...}`)" selbst als harten Fehler behandeln.

**Length-Constraint bei aggregier-betroffenen externalIds.** Wenn ein
Testfall eine Source-Entity mit `contoso_externalid` (oder einem
Äquivalent in einem anderen Projekt) anlegt und dieser Wert später
durch einen Aggregator-Plugin auf ein typisiertes Contact-Feld
gespiegelt wird, muss die externalId-Länge der MaxLength des
Ziel-Felds entsprechen. Sonst crasht der Plugin nach Merge oder
Aggregation. `{TIMESTAMP}` (19 Zeichen) sprengt typischerweise
24-Zeichen-Felder, sobald Prefix oder Suffix dazukommt. Empfehlung:
`{GENERATED:guid}` (8 Zeichen) oder `{PREFIX}{GENERATED:guid}` mit
Prefix passend zur MaxLength des Ziel-Felds. Projekt-spezifische
Ziel-Felder siehe `PROJEKT-KONTEXT.md`.

**`outputAlias` bei ExecuteRequest (v5.3.5, A4):**

```json
{ "stepNumber": 2, "action": "ExecuteRequest",
  "requestName": "QualifyLead",
  "outputAlias": "qres",
  "fields": { "LeadId": { "$type": "EntityReference", "entity": "lead", "ref": "lead1" },
              "Status": { "$type": "OptionSetValue", "value": 3 },
              "CreateAccount": true } },

{ "stepNumber": 3, "action": "Assert", "target": "Record",
  "recordRef": "{qres.outputs.CreatedEntityReferences[type=account]}",
  "field": "za_accounttype", "operator": "Equals", "value": "105710001",
  "description": "Account-Type wurde durch QualifyLead-Plugin gesetzt" }
```

Spart einen `FindRecord`-Step nach `ExecuteRequest`. Der `outputAlias`-Store
(`ctx.OutputAliases`) hält die nativen Typen (EntityReference,
EntityReferenceCollection, OptionSetValue, Money, Guid, primitive), der
Platzhalter-Resolver formatiert sie für die Substitution. `[type=...]`-
Filter funktioniert auf `EntityReferenceCollection` (häufig in
`CreatedEntityReferences` bei QualifyLead).

**Pitfall: QualifyLead-Target-Drift bei PreOperation-Plugins.** `ExecuteRequest QualifyLead` erzeugt im Account-Create-PreOperation-Target andere Contains-Belegung als das Web-API-Pendant `POST /leads(<id>)/Microsoft.Dynamics.CRM.QualifyLead`: Felder ohne AttributeMap-Eintrag (z.B. `za_accounttype`) landen über `ExecuteRequest` als `null` im Target (`target.Contains == true`, `target[field] == null`), beim Web-API-Aufruf sind sie überhaupt nicht im Target enthalten. PreOperation-Plugins, die `target.Contains(field)` als „User-hat-Wert-explizit-gesetzt"-Indikator nutzen, müssen zusätzlich auf `target[field] != null` prüfen, sonst greifen User-Wert-Guards falsch. Workaround in Tests: statt `ExecuteRequest QualifyLead` einen direkten `CreateRecord accounts` mit `originatingleadid@odata.bind` verwenden. Detail in Handbuch `02-actions-referenz.md` Sektion „Pitfall: QualifyLead-Target-Drift". Belegt durch ZP-LeadQualifyMapping-Befund April 2026.

**`Merge` über `ExecuteRequest` (kanonisches Beispiel für SDK-Messages):**

Es gibt keine eigene `MergeContacts`-Action. `MergeRequest` wird über
`ExecuteRequest` mit `requestName: "Merge"` und typisierten `$type`-Parametern
abgebildet. Dasselbe Muster gilt für jede andere SDK-Message ohne native
Action (z.B. `Assign`, `SetParentBusinessUnit`, `Book`).

```json
{ "stepNumber": 4, "action": "ExecuteRequest", "requestName": "Merge",
  "description": "Sub-Kontakt in Master mergen",
  "fields": {
    "Target":                 { "$type": "EntityReference", "entity": "contact", "ref": "MASTER" },
    "SubordinateId":          { "$type": "Guid", "ref": "SUB" },
    "UpdateContent":          { "$type": "Entity", "entity": "contact", "fields": {} },
    "PerformParentingChecks": false
  } }
```

Mapping zu den SDK-Properties von `MergeRequest`:

| SDK-Property | `$type` im fields-Block | Zweck |
|---|---|---|
| `Target` | `EntityReference` mit `entity` + `ref` (Alias) oder `id` (GUID) | Master-Record nach dem Merge |
| `SubordinateId` | `Guid` mit `ref` oder `value` | Sub-Record, der deaktiviert wird |
| `UpdateContent` | `Entity` mit `entity` + `fields`-Block | Optionale UI-Override-Werte; leer wenn nicht benötigt (`fields: {}`) |
| `PerformParentingChecks` | primitiver bool | Default `false`; `true` blockiert Merge bei unterschiedlichen Parent-Accounts |

`MergeRequest` läuft synchron. Wenn das Coalesce- oder Reparenting-Plugin in
PreOp Sync sitzt, sieht die nächste `RetrieveRecord`-Action auf den Master
bereits die neuen Werte. Async-Plugins (z.B. Reparenting in PostOp Async)
werden separat per `WaitForFieldValue` oder `WaitForRecord` abgewartet, wenn
die Assertion auf deren Ergebnis zielt. Wer auf ContactSources oder
MembershipSources der reparentierten Records assertiert, lädt diese per
`FindRecord` mit Filter auf den Master-Contact (statt auf einen Alias, weil
neue Records vom Plugin angelegt wurden).

**Record-Sharing über `ExecuteRequest` (`GrantAccess`/`ModifyAccess`/`RevokeAccess`):**

Seit ADR-2026-07-23 (Backlog O) kennt das `$type`-System
`PrincipalAccess` und macht damit Share-Setups im Pack baubar:

```json
{ "stepNumber": 3, "action": "ExecuteRequest", "requestName": "GrantAccess",
  "fields": {
    "Target":          { "$type": "EntityReference", "entity": "contact", "ref": "con" },
    "PrincipalAccess": {
      "$type": "PrincipalAccess",
      "principal":  { "$type": "EntityReference", "entity": "team", "ref": "teamAT" },
      "accessMask": "ReadAccess,WriteAccess,AppendAccess"
    }
  } }
```

- `principal` ist ein normales `$type EntityReference`-Objekt (typisch `team`/`systemuser`;
  beide Plural-Formen `teams`/`systemusers` löst die Engine auch offline auf).
- `accessMask` = SDK-`AccessRights`-Flags als Komma-String, **case-insensitiv**: `None`,
  `ReadAccess`, `WriteAccess`, `AppendAccess`, `AppendToAccess`, `CreateAccess`,
  `DeleteAccess`, `ShareAccess`, `AssignAccess`. Ungültiger Wert = Step-Error mit
  Flag-Liste in der Meldung.
- `ModifyAccess` nutzt dieselbe Form (`Target` + `PrincipalAccess`); `RevokeAccess`
  braucht kein `PrincipalAccess`, nur zwei EntityReferences (`Target` + `Revokee`).
- Core-only-Feature: CLI sofort, Plugin-/Worker-Pfad erst nach Plugin-Deploy mit
  Core >= Commit vom 2026-07-23.

### 3.6 Vollständiges Beispiel

```json
{
  "testId": "DEMO-01",
  "title": "Contact-Update + Assert",
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc1",
      "fields": { "name": "{GENERATED:company}" } },
    { "stepNumber": 2, "action": "CreateRecord", "entity": "contacts", "alias": "con1",
      "fields": {
        "firstname": "{GENERATED:firstname}",
        "lastname": "{GENERATED:lastname}",
        "parentcustomerid_account@odata.bind": "/accounts({acc1.id})"
      }
    },
    { "stepNumber": 3, "action": "UpdateRecord", "alias": "con1",
      "fields": { "jobtitle": "CEO" } },
    { "stepNumber": 4, "action": "Assert", "target": "Query", "entity": "contacts",
      "filter": [ { "field": "contactid", "operator": "eq", "value": "{con1.id}" } ],
      "field": "jobtitle", "operator": "Equals", "value": "CEO",
      "description": "Jobtitle wurde gesetzt" }
  ]
}
```

### 3.6 Test-Qualität: Assertion-Targets, Coverage, Symmetrie

Ein Test ist nur dann nützlich wenn er alle Erwartungen prüft. Diese Regeln gelten für JEDEN Test.

#### Regel 1: target="Record" vs. target="Query"

| Situation | Target | Warum |
|-----------|--------|-------|
| Record wurde per `CreateRecord` oder `WaitForRecord` unter einem Alias registriert, Asserts auf Felder die der Test selbst gesetzt hat | `"target": "Record"` mit `"recordRef": "{RECORD:alias}"` | Schneller (kein Query nötig), leserlicher, Assertion-Engine cached bereits bekannte Records |
| Asserts auf Felder die ein **Plugin/Workflow als Side-Effect** geändert hat (z.B. nach Status-Update durch Plugin angefasste Felder) | `"target": "Query"` mit Filter auf den PK | Der Record-Snapshot wird beim `UpdateRecord` nur **partiell** aktualisiert (nur die Felder im Update-Body). Plugin-Side-Effects landen nicht im Snapshot. Live-Query liest den aktuellen DB-Stand |
| Ergebnis ist implizit (z.B. Plugin erzeugt Records die wir nicht explizit anlegen) | `"target": "Query"` mit Filter | Query findet Records unabhängig vom Alias-Registry |
| Prüfung ob Records NICHT existieren (`NotExists`) oder Anzahl (`RecordCount`) | `"target": "Query"` | Diese Operatoren funktionieren nur auf Query |

**Regel:** target=Record ist Default für Felder die der Test selbst geschrieben hat. Sobald ein Plugin Felder hinter dem Snapshot ändert (z.B. der Anonymize-Plugin überschreibt firstname auf ANONYM), MUSS target=Query auf den PK verwendet werden. Verifiziert an einem Anonymisierungs-Test: nach `UpdateRecord` mit nur dem Status-Feld (`contoso_gdprstatuscode` auf den Anonymisieren-Wert) im Body sieht der Snapshot weiter den Original-Vornamen, obwohl das Plugin firstname auf ANONYM gesetzt hat.

```json
// Plugin-Side-Effect-Assert: target=Query gegen die aktuelle DB
{ "stepNumber": 6, "action": "Assert", "target": "Query",
  "entity": "systemuser",
  "filter": [ { "field": "systemuserid", "operator": "eq", "value": "{targetUser.id}" } ],
  "field": "firstname", "operator": "Equals", "value": "ANONYM",
  "description": "Plugin hat firstname anonymisiert (Snapshot würde noch Original sehen)" }
```

#### Regel 1b: Es gibt KEIN Output-Assert-Ziel (skalare Custom-API-Outputs nicht direkt prüfbar)

Die Assertion-Engine (`AssertionEngine.Evaluate`) kennt **nur** `target: "Record"` und `target: "Query"`.
Ein `target: "Result"`/`"Output"` existiert **nicht** -> Outcome FAIL mit `"Unbekanntes Assertion-Ziel:
Result"` (empirisch 2026-06-20, DEV-Org; im CLI-Core verifiziert: AssertionEngine nur RECORD/QUERY, kein
`EVALUATE`-Step im TestRunner-Switch). Ein `target: "Result"`-Block in einem Pack ist damit **toter Code**
(failt).

Konsequenz für `CallCustomApi`/`ExecuteRequest`-Tests:
- **Output ist nur Platzhalter, kein Assert-Wert.** Mit `outputAlias` landet der OrganizationResponse als
  `{alias.outputs.X}` im Context (native Typen). Das taugt als **recordRef/value in einem FOLGE-`target:
  "Query"`/`"Record"`-Assert** (z.B. `recordRef: "{qres.outputs.CreatedEntityReferences[type=account]}"`),
  aber ein **skalarer** Output (String/Int, z.B. `contoso_GetCompositeAddress.Composite`) kann NICHT direkt
  geprüft werden, es gibt keinen Record dahinter.
- **Custom API MIT Record-Seiteneffekt** -> via `target: "Query"` auf den PK prüfen (z.B.
  `contoso_LockInvoicePricing` -> `invoices.ispricelocked`), das ist der vollwertige Integrationstest.
- **Custom API mit NUR skalarem Output** (kein Record-Seiteneffekt, z.B. `contoso_GetCompositeAddress`) ->
  nur als **Aufrufbarkeits-/Parameter-Paritäts-Smoke** sinnvoll (CallCustomApi-Step ohne Assert; ein Test
  ohne fehlgeschlagenen Step gilt als PASSED, TestRunner.cs „kein Step Success=false -> Passed"). Fängt
  fehlende API-Registrierung/Parameter (Cutover-Parität). Die **Output-Korrektheit gehört in den Unit-Test**
  (FakeXrmEasy), nicht in den Integrationstest. Belegt 2026-06-20 in einem Nutzerprojekt: ein Adress-API-Smoke und ein
  Query-Assert auf eine Preissperre.

#### Regel 2: Coverage-Checkliste pro Test

Jeder Test muss eine systematische Coverage haben. Für CRUD-Operationen gelten diese Mindest-Assertions:

| Operation | Mindest-Assertions |
|-----------|-------------------|
| CreateRecord | 2-3: (a) Record existiert (`IsNotNull` auf Primary Name oder Query `Exists`), (b) wichtigste Feldwerte korrekt |
| UpdateRecord | 1 pro geändertem Feld: Feld hat neuen Wert. Plus Felder die NICHT geändert werden sollten: alter Wert (`Equals`) oder `IsNull`-Stabilität |
| DeleteRecord | 1: Record existiert nicht mehr (`NotExists` per Query) |
| Status-Change (deaktivieren/aktivieren) | 2: (a) `statecode Equals` neuer Wert, (b) `statuscode Equals` neuer Wert |
| Plugin-Kette (Create triggert Logik) | 1 pro erwartetem Side-Effect + 1 pro Feld das NICHT geändert werden sollte |
| Merge | siehe Merge-Szenarien (projektspezifisch) |

**Leitfrage:** "Was würde ich manuell im Browser prüfen um sicher zu sein dass der Test erfolgreich war?" Jede manuelle Prüfung muss eine Assertion sein.

#### Regel 3: Positive UND negative Erwartungen prüfen

Nicht nur prüfen dass etwas passiert, sondern auch dass andere Dinge NICHT passieren:

```json
// SCHLECHT: Nur positive Erwartung
[
  { "target": "Record", "recordRef": "{RECORD:con1}", "field": "contoso_goldenrecordid", "operator": "Equals", "value": "GR-42" }
]

// GUT: Positive + negative Erwartungen
[
  { "target": "Record", "recordRef": "{RECORD:con1}", "field": "contoso_goldenrecordid", "operator": "Equals", "value": "GR-42" },
  { "target": "Record", "recordRef": "{RECORD:con1}", "field": "contoso_foundmaster", "operator": "IsNull", "description": "FoundMaster geleert nach Merge" },
  { "target": "Record", "recordRef": "{RECORD:con2}", "field": "statecode", "operator": "Equals", "value": "1", "description": "Duplikat deaktiviert" }
]
```

#### Regel 4: Symmetrie bei umgekehrten Szenarien

Wenn Test B die Umkehrung von Test A ist, müssen die Assertions **strukturell identisch** sein - nur mit vertauschten Rollen. Asymmetrische Coverage ist ein Test-Design-Bug.

**Beispiel:**
- Test A: MGR04 hat 7 Assertions (GR_A Master, GR_B Subordinate)
- Test B: MGR05 ist Umkehrung -> MUSS auch 7 Assertions haben (GR_B Master, GR_A Subordinate)

#### Regel 5: `description` auf jeder Assertion

Jede Assertion braucht eine `description`. Das ist nicht Kosmetik sondern Dokumentation:
- Wer den Test liest versteht warum die Assertion existiert
- Im Ergebnis-Report steht die description im Fehlerfall
- Bei Test-Failure ist sofort klar welche Erwartung verletzt wurde

```json
// SCHLECHT:
{ "target": "Record", "recordRef": "{RECORD:con1}", "field": "contoso_ismaster", "operator": "Equals", "value": "1" }

// GUT:
{ "target": "Record", "recordRef": "{RECORD:con1}", "field": "contoso_ismaster", "operator": "Equals", "value": "1", "description": "Survivor ist nach Merge Master-Kontakt" }
```

#### Regel 6: Record-Alias konsequent nutzen

Wenn ein Record unter einem Alias bekannt ist:
- Steps referenzieren ihn per `alias` oder `recordRef`
- Assertions nutzen `target: "Record"` mit `recordRef: "{RECORD:alias}"`
- Platzhalter wie `{alias.id}` in Lookup-Bindings

**Nicht vermischen:** Mal Alias, mal hard-coded GUID, mal Query-Filter auf Primary Name. Ein Record hat einen Alias, dieser Alias wird überall verwendet.

#### Regel 7: Precondition-Vollständigkeit

Ein Test muss **alle notwendigen Vorbedingungen explizit** anlegen. Nicht auf "ist bestimmt schon da" verlassen:
- Alle referenzierten Parent-Records (Accounts, Contacts) als Precondition
- Alle Lookup-Ziele als Precondition
- Alle Sub-Records die Plugin-Logik erwartet
- Alle Feld-Vorbedingungen die das Szenario voraussetzt (z.B. "Contact ist als Golden Record markiert")

**Konsequenz:** Der Test muss idempotent und eigenständig sein. Bei `keeprecords=false` räumt er sich selbst auf.

#### Regel 8: Test-Title-Genauigkeit (Lesson Session 16)

Der Test-Title verspricht **exakt das, was der Verifikations-Marker tatsächlich prüft**. Nicht mehr, nicht weniger. Title-Übersprechen ist ein Test-Design-Bug, weil ein grüner Test dann mehr Sicherheit suggeriert als er liefert.

**Konkrete Falle:** Bei Marker-Verifikation auf D365-Forms wird oft etwas Indirektes geprüft (z.B. "Custom-Control existiert im DOM"), während der Title etwas Direktes verspricht (z.B. "Form-Library wurde geladen"). Ein Custom-Control im DOM beweist nur die aktive Custom-Form, nicht aber die erfolgreiche Ausführung der Library (Library könnte beim OnLoad einen Syntax-Error werfen, das Control wäre trotzdem da).

| Schlecht | Gut |
|---|---|
| Title: "Contoso Form.js aktiv" + Marker: `Xrm.Page.getControl('contoso_X')` Existenz | Title: "Contoso Custom-Form aktiv" + Marker: `Xrm.Page.getControl('contoso_X')` Existenz |
| Title: "Plugin-Cascade lief erfolgreich" + Marker: ein einzelnes Side-Effect-Feld | Title: "Plugin setzt Feld X" + Marker: dieses eine Feld |

**Regel:** Bei jedem Smoke vor dem Schreiben formulieren: "Was verifiziert der Marker GENAU?" Diese Antwort ist der Title. Wenn die ehrliche Antwort schwächer klingt als der ursprüngliche Title, entweder den Title schwächen oder einen stärkeren Marker (zusätzlicher Step) hinzufügen.

**Description bleibt der Ort für die Begründung der Marker-Wahl.** Der Title ist die Aussage, die Description erklärt warum dieser Marker den Aussagegehalt liefert (oder warum es ein indirekter Marker ist und welche Annahmen dahinterstehen).

---
