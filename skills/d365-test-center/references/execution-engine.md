# Ausführung: Engine, Ergebnis-Datensätze, Pre-Run-Validation

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Feldnamen) steht in der `PROJEKT-KONTEXT.md` des jeweiligen Repos.

## 8. Execution Engine: Wie Tests laufen

Alle drei Aufrufer-Pfade (CLI, Custom-API `jbe_RunIntegrationTests`, CRUD-Trigger-Plugin
`RunTestsOnStatusChange`) führen denselben `TestRunner` aus `backend/D365TestCenter.Core` aus (ADR-0003
Single-Engine). Pro Testfall:

1. **Pre-Run-Validation:** `PackValidator.ValidateOne` prüft den Testfall statisch, ohne Service-Call. Ein
   Error-Befund bricht den Test mit `Outcome=Error` ab, bevor ein Step läuft (siehe 8d).
2. **Steps** laufen sequentiell in JSON-Reihenfolge; es gibt nur die eine `steps[]`-Liste, keine getrennten
   Preconditions oder Assertions (ADR-0004). Je Step: optionale `condition` prüfen (ADR-0011, nicht erfüllt =
   Step übersprungen), Platzhalter in den Feldwerten auflösen (`TestDataFactory.ResolveTemplateData`,
   `PlaceholderEngine`), dann die Action ausführen.
3. **Fehlerverhalten:** `onError="stop"` (Default für alle Actions außer `Assert`) beendet den Test mit
   `Outcome=Error`; `onError="continue"` (Default für `Assert`) lässt ihn weiterlaufen.
4. **Asynchrone Effekte** wartet der Testfall selbst ab, die Engine pollt nichts implizit:
   `WaitForRecord`, `WaitForFieldValue`, `WaitForNotExists`, `WaitForAsyncCompletion` (nur CLI-Pfad, im
   Plugin-Sandbox-Pfad übersprungen) oder fest per `Wait`/`Delay`.
5. **Outcome:** `Failed`, sobald ein nicht übersprungener Step `Success=false` hat; `Skipped`, wenn der Test
   Assert-Steps definiert, aber alle per `condition` übersprungen wurden; sonst `Passed`.
6. **Cleanup** (`TestRunner.Cleanup`, im `finally`, also auch nach Fehlern): zuerst Environment-Variable-
   Snapshots zurückschreiben, dann die erzeugten und getrackten Records in umgekehrter Reihenfolge (LIFO)
   löschen, jeweils nach den deklarierten `cleanupChildren`. Mit `jbe_keeprecords=true` wird nichts gelöscht.
   Die Cleanup-Phase erscheint als ein eigener StepResult (StepNumber 9000).
7. **Persistenz:** Der `TestCenterOrchestrator` schreibt die Ergebnisse als `jbe_testrunresult` je Testfall und
   `jbe_teststep` je StepResult; welcher Pfad was schreibt, steht in 8c.

### Historisch: die frühere HTML-Engine

Bis zur Single-Engine-Umstellung (ADR-0003, v5.3) lief die Ausführung in der HTML-Web-Resource. Diese kannte
zwei Mechanismen, die es im C#-Core **nicht** gibt (gemessen 2026-09-18 im Code):

- **AutoSetDateFields:** setzte bei ContactSource-Operationen automatisch die zugehörigen Timestamp-Felder
  (`CONFIG.governance.autoDateFields`, Muster z.B. `contoso_<field>_modifiedon`).
- **Governance-Polling per `waitForAsync`:** nach ContactSource-Creates 1,5 s Initial Delay, dann alle 2 s eine
  Query auf die Governance-Logging-Entity (`CONFIG.governance.loggingEntitySet`) mit ContactId- und
  Timestamp-Filter, Timeout 120 s.

`ITestCenterConfig` trägt keine Governance-Eigenschaften mehr, und `CONFIG.governance` wirkt nur noch im Visual
Editor und im Demo-Modus (`references/konfiguration.md`). `waitForAsync` ist kein Feld von `TestStep`: ein Step
mit diesem Key wird beim Laden verworfen und von der Pre-Run-Validation als `STEP_KEY_UNKNOWN` (Warning)
gemeldet. Ein Test, der früher auf das Polling vertraute, braucht heute einen expliziten Wait-Step (Punkt 4).

### Einen Lauf bewerten (false-green-Schutz, Betriebs-Checkliste)

Ein „X/X grün" ist erst dann ein Beweis, wenn vier Dinge stimmen (Betriebsregel aus einem Nutzerprojekt, N34):

1. **`ERR` und `Skipped` zählen als defekt/unvollständig**, nicht als „nicht so schlimm". Nur reine
   PASSED-Läufe ohne Error/Skipped sind grün.
2. **„geladen == ausgeführt" prüfen**: ein still übersprungener Filter (z.B. ein Tippfehler in der ID, der
   0 Fälle lädt) sieht aus wie „nichts kaputt", testet aber nichts. (`tag:A,tag:B` lädt seit ADR 2026-06-30
   1432 A ODER B, nicht mehr 0 -- die alte Falle ist behoben.)
3. **Assert-Vorhandensein verifizieren**: ein Test ohne Assertion ist „false green". Aggregat-Query:
   `jbe_testrunresults?$filter=_jbe_testrunid_value eq <runId>&$select=jbe_testid,jbe_assertionresults`,
   dann zählen, wie viele `jbe_assertionresults` >= 1 Eintrag haben (die Property heißt `passed`, nicht
   `success`, siehe 8c). `withAsserts == Gesamtzahl` => kein Test ohne Assertion.
4. **CLI-Pfad für Vollläufe**, nicht den Trigger-Teillauf (2-min-Sandbox-Limit; der Sync-Pfad liefert zudem
   keine Per-Test-Asserts, siehe 8c).

---

## 8c. jbe_testrunresult-Detail-Records: was welcher Pfad schreibt

Die drei Run-Pfade (CRUD-Trigger, Custom-API, CLI) erzeugen unterschiedlich tiefe Result-Daten. Wer pro Test-Case Assertion-Details als JSON-Beleg braucht (z.B. zur Forensik bei FieldConfig-Wert-Drift oder als versioniertes Lauf-Inventar), muss den richtigen Pfad wählen.

| Run-Pfad | jbe_testrun | jbe_testrunresult (pro Test) | jbe_assertionresults (JSON) | jbe_teststep |
|---|---|---|---|---|
| **CRUD-Trigger (`RunTestsOnStatusChange`, Async)** | ja, mit `jbe_passed`/`jbe_failed`/`jbe_total`-Aggregat | **ja, pro Test ein Record** | **ja, mit allen Assert-Detail-JSONs** | ja |
| **Custom-API (`jbe_RunIntegrationTests`, Sync)** | ja, mit Aggregat | **nein**, nur `jbe_testsummary` (Memo) mit OK-Liste | **nein** | (nicht durchgängig verifiziert) |
| **CLI (`D365TestCenter.Cli`)** | ja, mit Aggregat | **ja, pro Test ein Record** | **ja**, vollständig | ja |

**Konsequenz für die Diagnose-Strategie:**

- Wenn ein Sync-Custom-API-Lauf 4/4 PASS meldet, sind das Aggregat-Belege ohne Per-Test-Detail. Für die Pro-Test-Assertion-Verifikation (z.B. "welcher der 17 Asserts hat tatsächlich PASS gemeldet") braucht es einen Async-CRUD-Trigger- oder CLI-Lauf desselben Filters.
- Bei Failure-Wurzelanalyse (siehe `PROJEKT-KONTEXT.md` Sektion „Failure-Diagnose via Web-API") ist `jbe_assertionresults` die zentrale Quelle. Wer im Sync-Pfad einen Failure hat, sieht nur das Aggregat, ohne Detail-Records kein Drill-Down möglich.
- Für versioniertes Inventar pro Lauf (z.B. „TR-001085 hatte 73 PASS, hier ist der JSON-Beleg pro Test") sind Async-CRUD-Trigger oder CLI verbindlich. Sync-Pfad dokumentiert nur „passed/failed-Bilanz".
- **Sync-Pfad-Sweetspot:** schnelle Aggregat-Verifikation bei kleinen Filtern (1 bis 5 Tests), wenn klar ist, dass alle PASS sind und kein Drill-Down nötig wird.

Belegt 2026-05-15: ein Sync-Custom-API-Lauf mit Wildcard-Filter über 4 Tests lieferte 4/4 PASS in 41 s, aber kein `jbe_testrunresult.jbe_assertionresults`-Detail pro Test. Für die Per-Test-Verifikation der 10 neuen Wert-Asserts wäre ein Async-CRUD-Trigger-Lauf nötig gewesen.

### Schema-Detail: AssertionResults-JSON-Property heißt `passed`, nicht `success`

Beim Parsen von `jbe_assertionresults` aus dem Web-API-Response: jeder Eintrag ist ein Objekt mit den Properties `description`, **`passed`** (Bool), `message`, `expectedDisplay`, `actualDisplay`. Häufige Fehlannahme: Property heißt `success`, das gibt es nicht, der Lookup liefert `null`, und alle Asserts wirken fälschlich als FAIL.

```powershell
$ar = $result.jbe_assertionresults | ConvertFrom-Json
foreach ($a in $ar) {
    $status = if ($a.passed) { "OK" } else { "FAIL" }   # passed, nicht success!
    Write-Host "[$status] $($a.description): $($a.message)"
}
```

Beispiel-Raw-JSON aus `jbe_testrunresult.jbe_assertionresults`:

```json
[
  {
    "description": "Account-name pseudonymisiert",
    "passed": true,
    "message": "OK: name = \"PSEUDO_cd2c9283\"",
    "expectedDisplay": "PSEUDO_",
    "actualDisplay": "\"PSEUDO_cd2c9283\""
  },
  {
    "description": "Sub-Pseudomap für Account-Activities",
    "passed": false,
    "message": "Query-Assertion: Kein Record in 'contoso_gdprpseudonymmap' gefunden.",
    "expectedDisplay": null,
    "actualDisplay": null
  }
]
```

Die `message`-Property beginnt bei PASSED-Asserts mit `OK:` (das ist Bestandteil der Engine-Output-Formatierung, kein Hinweis auf den Status). Nur `passed` ist verbindlich.

Belegt in Session 17 (D3-GDPR-Diagnose, 2026-05-16): erstes Diagnose-Skript las `$a.success`, lieferte falsches FAIL-Bild bei allen 3 Cascade-Tests (jeweils 5 von 6 Asserts wurden falsch als FAIL angezeigt). Raw-JSON-Inspektion zeigte den Property-Namen, Re-Run der Diagnose lieferte das korrekte 1-von-6-FAIL-Bild.

**Pitfall: vereinzelter ERROR mit Infra-/SOAP-Meldung = transienter Throttle, kein Defekt (belegt bei einem Retest 2026-06-17):** Der CLI-`run`-Pfad hat **keinen Auto-Retry** (nur der UI-Smoke-Runner kennt `-MaxAttempts`). Ein **einzelner** `Outcome=Error` mit `jbe_errormessage` = „Es war kein an …/XRMServices/2011/Organization.svc/web … lauschender Endpunkt vorhanden …" (ServiceClient-SOAP-Endpoint-Blip) bei gleichzeitig **abnorm langer** `jbe_durationms` (Beispiel: ERR-Test 78 s, Nachbar-Test 121 s vs. Median ~12 s) ist ein **transienter Konnektivitäts-/Throttle-Fehler der Ziel-Org**, kein Plugin-/Calc-Defekt, erkennbar an `jbe_assertionresults = []` (es lief gar kein Assert). Diagnose: `jbe_testrunresults?$filter=_jbe_testrunid_value eq <runId>` -> `jbe_errormessage`/`jbe_durationms`/`jbe_outcome` lesen. **Behandlung:** den **einzelnen** Test erneut laufen (`--filter <ID>`); kommt er grün und schnell zurück, war es der Blip. Nicht als Produktfehler triagieren und nicht das Pack „fixen". Gegenprobe gegen einen fachlich benachbarten Test, der grün blieb (im Beispiel deckte ein grüner Nachbartest dieselbe Berechnung ab).

---

## 8d. Pre-Run-Validation (OE-6, Plugin v5.3.8)

Seit Plugin v5.3.8 läuft vor jedem Testfall der `PackValidator` aus
`D365TestCenter.Core.Validation`. Statisch (kein Dataverse-Call), pro Testfall
ein `ValidationReport`. Findings je Severity:

- **Error:** Test wird mit `Outcome=Error` abgebrochen, kein Step läuft, der Befund landet in `errorMessage` und `fullLog`.
- **Warning** und **Info:** Test läuft normal weiter, Findings nur in `fullLog`.

Die Validation lebt im Core und greift in allen drei Aufrufer-Pfaden
(ADR-0003): Cli `run`, Custom-API `jbe_RunIntegrationTests`,
CRUD-Trigger-Plugin `RunTestsOnStatusChange`. Cli-Sub-Command `validate`
exponiert dieselbe Logik ohne Run-Execution für IDE-Lint oder CI-Pre-Commit.

### Regel-Codes Phase 1

| Code | Severity | Wann |
|---|---|---|
| `ACTION_UNKNOWN` | Error | Action nicht in Whitelist (CreateRecord, UpdateRecord, DeleteRecord, ExecuteRequest, CallCustomApi, ExecuteAction, RetrieveRecord, WaitForRecord, FindRecord, TrackRecord, WaitForFieldValue, WaitForNotExists, WaitForAsyncCompletion, AssertEnvironment, Assert, Wait, Delay, SetEnvironmentVariable, RetrieveEnvironmentVariable, BrowserAction). Levenshtein-Vorschlag bei Distance <=2. |
| `TRACKRECORD_MISSING_FIELDS` | Error | `TrackRecord` ohne `entity` oder `recordId` (FB-54): der Runner würde zur Laufzeit werfen. |
| `FILTER_FIELD_NOT_LOGICAL` | Error | Filter-`field` im OData-Format `_xxx_value` statt Logical-Name. Würde im Async-Pfad `'<entity>' entity doesn't contain attribute with Name = '_xxx_value' and NameMapping = 'Logical'` werfen. |
| `FILTER_OPERATOR_VALUE_NULL` | Error | Operator `eq`/`ne` mit `value: null`. Plattform-Async-Pfad cast`t DBNull nicht zu Guid/Int. |
| `EXECUTEREQUEST_MISSING_NAME` | Error | `ExecuteRequest`/`CallCustomApi`/`ExecuteAction` ohne `requestName`/`actionName`/`apiName`/`entity` (alle vier ADR-0007-Fallback-Quellen leer). |
| `LOOKUP_BIND_FORMAT` | Warning | Field-Key mit `@odata.bind` hat Pre-Teil `_xxx_value`. Sollte Logical-Name sein. |
| `STATECODE_STATUSCODE_HINT` | Warning | Create/Update setzt `statuscode` ohne `statecode`. Plattform-(state, status)-Validierung lehnt mismatched Kombinationen mit `"<n> is not a valid status code for state code <S>"` ab. |
| `ASSERT_TARGET_INCOMPLETE` | Error | Assert mit `target=Query` ohne `entity`+`filter`, oder `target=Record` ohne `recordRef`. |
| `STEP_NUMBER_DUPLICATE` | Warning | Mehrere Steps mit derselben `stepNumber > 0` im selben Test. `stepNumber=0` wird ignoriert (Default für legacy-Schema-Packs). |
| `PRECONDITIONS_OBSOLETE` | Error | Obsoletes Top-Level-`preconditions[]`-Array (Pre-ADR-0004). Wird still ignoriert, Test prüft nichts. Via `[JsonExtensionData]` auf `TestCase` gefangen. Als `CreateRecord`-Steps migrieren. (v5.3.12) |
| `ASSERTIONS_OBSOLETE` | Error | Obsoletes Top-Level-`assertions[]`-Array (Pre-ADR-0004). Wird still ignoriert, Test prüft nichts. Als `Assert`-Steps migrieren. (v5.3.12) |
| `STEP_KEY_UNKNOWN` | Warning | Step trägt einen Key, der nicht im `TestStep`-Schema steht (z.B. `withinSeconds`, `timeoutMs`, `ms`). Newtonsoft verwirft ihn still (`MissingMemberHandling` Default Ignore), der Wert wirkt nicht (es greift der Schema-Default, FB-45). `[JsonExtensionData]` auf `TestStep` macht ihn sichtbar. Annotation-Keys (`comment`/`note`) sind erlaubt. Levenshtein-Vorschlag bei nahem Tippfehler. (v5.3.13, Backlog N) |
| `CONDITION_MALFORMED` | Error / Warning | Step-`condition` nicht wohlgeformt: leer, gemischte Formen (Einfachklausel + `all`/`any`, oder `all` + `any`), leeres `all`/`any`, unbekannter/fehlender Operator (Laufzeit wirft -> `Outcome=Error`) oder fehlendes `left` (alle **Error**); wertbasierter Operator ohne `right` (**Warning**, Vergleich gegen Leerstring). Operatoren aus `ValueComparator.SupportedOperators` (case-insensitiv), Levenshtein-Vorschlag bei nahem Tippfehler. Undefinierte condition-Aliasse fängt zusätzlich `ALIAS_UNDEFINED`. (ADR-0011 Phase 4) |

### Pitfall: stale Schema auf eingefrorener Sekundärumgebung

Eine eingefrorene Sekundärumgebung (z.B. ein Cutover-Ziel wie eine TEST-Umgebung) kann `jbe_testcase`-Records im
**veralteten pre-ADR-0004-Schema** (`preconditions[]`/`assertions[]`) tragen, während die Hauptumgebung (DEV)
längst auf `steps[]` migriert ist. Beim CLI-`run` gegen die Sekundärumgebung erscheinen genau diese Fälle als
`Outcome=Error` mit **0 ms** Laufzeit und `PRECONDITIONS_OBSOLETE`/`ASSERTIONS_OBSOLETE`, das ist **kein
Plugin-/Deploy-Defekt**, sondern Test-Daten-Drift. Diagnose: dieselben Test-IDs auf der Hauptumgebung prüfen
(dort neu-Schema und grün?). Fix: `jbe_definitionjson` der betroffenen IDs von der Hauptumgebung auf die
Sekundärumgebung patchen (die Hauptumgebung ist die Wahrheitsquelle auch für Testdefinitionen). Belegt
bei einem Cutover 2026-06-20: 9 `tag:Plugin`-Fälle auf TEST alt-Schema, nach DEV->TEST-Sync grün (31/32).

**Zweite Dimension: `jbe_enabled`:** Dieselbe Snapshot-Staleness betrifft das Enabled-Flag. Auf der
Hauptumgebung bewusst **disabled**-e WIP-/known-failing-Tests können auf der eingefrorenen Sekundärumgebung
noch **enabled** sein und beim Volllauf rot werden, obwohl sie nie Teil der aktiven Suite waren. Diagnose:
`jbe_enabled`-Delta Sekundär-vs-Haupt erheben und den Enabled-Zustand von der Hauptumgebung übernehmen. Belegt
2026-06-20: 8 Records (7 FunctionCall-Tests + 1 API-Test) auf TEST enabled, auf DEV disabled
-> nach Angleich aktive Suite 52/53 grün.

**Dritte Dimension: das Repo-Pack kann der Hauptumgebung VORAUSEILEN (Vorsicht bei `Export-PacksFromDev`):**
Die „DEV = Wahrheitsquelle"-Regel gilt im Normalfall, aber nicht ausnahmslos. Wird ein Pack-Fix direkt im Repo
committet und NICHT per projektseitigem Reimport-Skript (Repo->DEV) zurückgespielt, hängt DEV hinter dem Repo. Ein blinder
`Export-PacksFromDev` (DEV->Repo) **über alle Packs** überschriebe solche lokalen Fixes mit dem alten DEV-Stand;
der Alt-Schema-Skip schützt NICHT, weil beide Stände sauberes neu-Schema sind. Daher vor jedem Export pro
`testId` die Drift-Richtung prüfen (Repo-Canon vs `jbe_definitionjson`, rekursiv key-normalisiert, Array-Reihenfolge
erhalten). Eilt das Repo voraus, ist die korrekte Richtung der **gezielte Reimport (Repo->DEV)**, am sichersten
ein chirurgischer PATCH nur des `jbe_definitionjson` der driftenden IDs (so bleiben `jbe_enabled`/Tags der übrigen
unangetastet, kein versehentliches Reaktivieren). Belegt 2026-06-20: 4 testIds (drei mit Tilde-Fix `contoso_bezeichnung~`, einer mit
Produkt-Bindung) im Repo gefixt, DEV trug den pre-Fix-Stand ->
chirurgischer Repo->DEV-PATCH statt Voll-Export.

### Cli-Verwendung

```bash
dotnet D365TestCenter.Cli.dll validate \
    --pack <projekt>/packs/<pack>.json
# Exit 0 = keine Errors. Exit 1 = mind. ein Error.
# --strict: Warning macht ebenfalls Exit 1 (CI-Modus).
```

### Cli-DLL aktuell halten (Stolperfalle: lokale Engine, nicht das Plugin)

Die Cli führt die **lokal gebaute** `D365TestCenter.Core`-Engine aus (die publizierte
`D365TestCenter.Cli.dll`), NICHT das in Dataverse deployte Plugin. Ein Lauf testet also
den Stand der lokalen DLL. Ist sie älter als der Engine-Quellcode (z.B. nach einem
Engine-Release wie `WaitForNotExists` v5.3.12: neue Action in `TestRunner.cs`, DLL aber
noch vom Vortag), kennt die Cli die Action nicht und der Verify schlägt mit
`ACTION_UNKNOWN` fehl oder testet veralteten Code. Vor dem Verify nach jeder
Engine-Änderung neu publishen:

```bash
dotnet publish D365TestCenter.Cli -c Release -o backend/publish/cli
```

Ein projektseitiger CLI-Wrapper kann das automatisieren: DLL-Datum gegen die jüngste `.cs` in
`D365TestCenter.Core`/`.Cli` vergleichen und bei Drift rebuilden (mit Opt-out-Schalter). Belegt
am `WaitForNotExists`-Release (2026-06-14): die DLL vom Vortag kannte die Action nicht, erst nach
Rebuild liefen die beiden betroffenen Tests grün.

### Pack-Format-Auto-Mapping im Cli

Der Cli-`validate`-Lader unterstützt das Workspace-Pack-Wrapper-Format
(`{ name, testCases: [...] }`) und mappt das Pack-Feld `testId` automatisch
auf `TestCase.Id`, damit Findings die korrekte Test-ID tragen.

### Bekannte False-Positives vermeiden

- **stepNumber=0** wird bei `STEP_NUMBER_DUPLICATE` ignoriert, weil
  Newtonsoft-Default für nicht-gesetzte `stepNumber`-Properties 0 ist und
  alte Pack-Files (vor ADR-0004) keine `stepNumber` setzten. Bei modernen
  Packs mit explizit nummerierten Steps greift die Regel normal.

### Phase 2 (separate Entscheidung OE-8)

Aufgeteilt in zwei Hälften, beide UMGESETZT:

**Symbol-Table-Validation (statisch, v5.3.15, Backlog J).** Regel `ALIAS_UNDEFINED` (Warning): flaggt
`{alias.id}`/`{alias.fields.X}`/`{alias.outputs.X}`/`{RECORD:alias}`/`{RESULT:alias.X}`, deren Alias kein
vorheriger Step über `alias`/`outputAlias` definiert. Konservativ: Tests mit `sharedContext` oder
`dependsOn` werden übersprungen (Cross-Test-Aliase statisch nicht auflösbar). 0 False Positives über
21 aktive Packs. Läuft **immer** (auch in der service-call-freien Pre-Validierung).

**Metadata-aware-Validation (org-gebunden, CLI-only).** Läuft **ausschließlich** über
`validate --org <url> --client-id ... --client-secret ... --tenant-id ...`, NICHT in der
TestRunner-Pre-Validierung (die bleibt service-call-frei, OE-6-Wert; Pin-Test `AbortsOnError`
mit `svc.ExecuteCallCount == 0` erzwingt das). Aktiviert über die Overload
`IPackValidator.ValidateOne(tc, EntityMetadataCache?)`.

| Code | Severity | Wann | Stand |
|---|---|---|---|
| `ENTITY_UNKNOWN` | Warning | Entity hat keine ladbaren Metadaten auf der Ziel-Env | v5.3.16 (Session 27) |
| `FIELD_UNKNOWN` | Warning | Feld ist kein Attribut der Entity (Levenshtein-Vorschlag, `@odata.bind`-Keys + Platzhalter übersprungen) | v5.3.16 (Session 27) |
| `OPTIONSET_VALUE_IMPLAUSIBLE` | Warning | Create/Update schreibt einen numerischen Wert auf ein Picklist/State/Status/MultiSelect-Feld, der kein definierter Options-Wert ist. Nur Create/Update-Field-Values (Filter/Assert-Werte können Labels vergleichen); Platzhalter + nicht-numerische Labels übersprungen | Session 28 (Backlog J Rest) |
| `POLYMORPH_TARGET_INVALID` | Warning | `@odata.bind`-Ziel liegt außerhalb der erlaubten Lookup-Targets: (1) Nav-Property-Suffix nennt ein ungültiges Target, (2) Suffix widerspricht dem gebundenen Datensatz (z.B. `customerid_account` an `/contacts`), (3) gebundenes Entity nicht in den Targets. Konservativ: unbekannte Nav-Property oder Platzhalter-Bind -> kein Finding | Session 28 (Backlog J Rest) |

Metadata-Quelle: derselbe `EntityMetadataCache`, den die Engine zur Laufzeit nutzt. **Performance:** die
Option-Werte (`EnumAttributeMetadata.OptionSet.Options`) und alle Lookup-Targets (`LookupAttributeMetadata.Targets`)
kommen aus dem **bestehenden** `RetrieveEntity`-Call (`EntityFilters.Attributes`) - kein zusätzlicher
Service-Call. Org-verifiziert gegen eine DEV-Org (Session 28): synthetischer Negativ-Pack feuert alle drei
Polymorph-Varianten + OptionSet-out-of-range (`statecode=999` -> "defined option values: 0, 1"), 0 False
Positives über alle 20 aktiven Projekt-Packs zweier Nutzerprojekte. Boolean (TwoOptions) bewusst ausgeklammert (Packs setzen
true/false, kein numerischer Wert-Check). Siehe OE-8.

### Praxis-Beleg

Session 18 (2026-05-16) erster Lauf des `validate`-Sub-Commands gegen
ein Coverage-Pack eines Nutzerprojekts fand zwei echte Bugs: zwei Tests
filterten auf das OData-Lookup-Format `_contoso_bestellungid_value`.

Beide würden bei einem regulären Lauf in `WaitForRecord`-
Timeouts laufen (~44 s jeweils) und als FAILED enden, ohne dass die
Ursache aus der Step-Trace klar wird. Mit OE-6 sind diese Tests sofort
`Outcome=Error` mit dem konkreten Fix-Vorschlag `Use 'contoso_bestellungid'`.

---
