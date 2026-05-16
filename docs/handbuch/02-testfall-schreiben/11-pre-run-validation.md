# Pre-Run-Validation (OE-6)

Seit Plugin v5.3.8 prüft das Test Center jeden Testfall **vor dem ersten Step**
statisch auf Schema- und Pattern-Fehler. Tippfehler in Filter-Fields,
fehlende ExecuteRequest-Namen, falsche Lookup-Bind-Syntax und ähnliche
Pack-Bugs werden gefunden, **bevor** ein einziger Service-Call rausgeht.

Ohne diese Validation kostet ein einzelner Filter-Tippfehler typisch
~44 s, weil `WaitForRecord` erst nach Default-Timeout aufgibt.

## Wie es funktioniert

Die `D365TestCenter.Core.Validation.PackValidator`-Klasse läuft pro Testfall
direkt vor `ExecuteSteps`. Sie produziert einen `ValidationReport` mit
Findings je Severity (`Error`, `Warning`, `Info`).

- **Error-Befund:** Test wird mit `Outcome=Error` abgebrochen, kein Step
  läuft, `errorMessage` enthält Code, Position und Fix-Vorschlag.
- **Warning oder Info:** Test läuft normal weiter, Findings landen im
  `fullLog` zur Sichtung.

Die Validation ist **rein statisch** in Phase 1 (kein Dataverse-Call). Sie
braucht weder Auth noch Org-Metadata. Dynamische Checks gegen die Ziel-Env
(Logical-Name-Existenz, OptionSet-Plausibilität) sind als Phase 2 geplant.

## Regel-Katalog Phase 1

| Code | Severity | Beschreibung |
|---|---|---|
| `ACTION_UNKNOWN` | Error | Action-Name ist nicht in der Whitelist. Levenshtein-Vorschlag bei Tippfehlern. |
| `FILTER_FIELD_NOT_LOGICAL` | Error | Filter `field` im OData-Format `_xxx_value` statt Logical-Name. |
| `FILTER_OPERATOR_VALUE_NULL` | Error | Operator `eq`/`ne` mit `value: null` statt `isnull`/`isnotnull`. |
| `EXECUTEREQUEST_MISSING_NAME` | Error | `ExecuteRequest`/`CallCustomApi`/`ExecuteAction` ohne `requestName`, `actionName`, `apiName` oder `entity`. |
| `LOOKUP_BIND_FORMAT` | Warning | Field-Key mit `@odata.bind` hat OData-Wrapping (`_xxx_value@odata.bind`) statt Logical-Name. |
| `STATECODE_STATUSCODE_HINT` | Warning | Create/Update setzt `statuscode` ohne `statecode` (Plattform-Validierung lehnt oft ab). |
| `ASSERT_TARGET_INCOMPLETE` | Error | `Assert target=Query` ohne `entity`+`filter`, oder `target=Record` ohne `recordRef`. |
| `STEP_NUMBER_DUPLICATE` | Warning | Zwei Steps mit derselben `stepNumber > 0` in einem Test. |

## CLI: `validate`-Sub-Command

Pack-File lokal prüfen, ohne Run zu starten:

```bash
dotnet D365TestCenter.Cli.dll validate \
    --pack 08_projects/zastrpay/packs/zp-lead-qualify-mapping.json
```

Output je Finding mit Severity-Tag, Test-ID, Step-Nummer, Code, Message und
Fix-Vorschlag. Exit-Code:

- `0`: keine Errors, höchstens Warnings/Infos
- `1`: mindestens ein Error (oder Warning bei `--strict`)
- `2`: Pack-File nicht lesbar / Parse-Fehler

Mit `--strict` ist auch eine Warning ein Build-Failure. Nützlich für CI.

## Beispiel-Output

```
============================================================
  D365 Test Center - Pack Validation (OE-6)
  Pack:   08_projects/lm/packs/lm-coverage-boost.json
  Strict: False
============================================================

  Tests: 8

  [ERROR  ] PLG-LST-UMSATZ-01              Step 0    FILTER_FIELD_NOT_LOGICAL
             Filter field '_lm_bestellungid_value' uses the OData lookup format. The internal
             QueryExpression builder expects the logical name (Singular, without '_..._value'
             wrapping).
             -> Use 'lm_bestellungid' instead of '_lm_bestellungid_value'.

  Summary: 1 Error, 0 Warning, 0 Info
```

## Engine-Integration

Beim normalen Run via Cli, Custom-API (`jbe_RunIntegrationTests`) oder
CRUD-Trigger-Plugin (`RunTestsOnStatusChange`) läuft derselbe Validator
automatisch — die Logik lebt in `D365TestCenter.Core`, also im
Engine-Pfad, der alle drei Aufrufer nutzen (ADR-0003 Single-Engine).

Bei einem Error-Finding sieht der Tester einen kurzen Test-Run-Eintrag:

```
[Test] PLG-LST-UMSATZ-01 -> FEHLER: Pre-Run-Validation hat 1 Error-Befund(e).
  Test wird nicht ausgefuehrt.
  Pre-run validation failed with 1 error(s):
    Step 0 FILTER_FIELD_NOT_LOGICAL: Filter field '_lm_bestellungid_value' uses
      the OData lookup format. ...
      -> Use 'lm_bestellungid' instead of '_lm_bestellungid_value'.
```

`jbe_outcome=Error`, `jbe_errormessage` enthält den Befund. Kein 44 s
WaitForRecord-Timeout, kein irreführender Step-Trace.

## Was Phase 1 nicht prüft

- **Logical-Name-Existenz** auf der Ziel-Env (z.B. `entity: "contats"` statt
  `contacts`). Phase 2 mit Metadata-Cache.
- **Polymorph-Resolution** für Lookups (z.B. `customerid` vs
  `customerid_account` vs `customerid_contact`). Phase 2.
- **OptionSet-Wert-Plausibilität** (`statecode=99` ist syntaktisch ok aber
  semantisch ungültig). Phase 2.
- **Platzhalter-Konsistenz** (`{alias.id}` mit nicht-existierendem Alias).
  Brauchbar als Phase 2 mit Symbol-Tabelle aus Steps.

## Bezug zu Entscheidungen

- ADR-0003 Single-Engine: Validator als Core-Modul, nicht als Cli-only-Code.
- ADR-0007 ExecuteRequest: Validator akzeptiert alle drei Verb-Aliasse
  (`ExecuteRequest`, `CallCustomApi`, `ExecuteAction`) und alle drei
  Property-Aliasse (`requestName`, `actionName`, `apiName`).
- OE-6 (Workspace): Entscheidungsgrundlage für diesen Validator.
