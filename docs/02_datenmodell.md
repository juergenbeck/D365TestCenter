# Datenmodell: Integration Test Center

## Entity-Relationship-Diagramm (ASCII)

```
  itt_testcase                 itt_testrun
  +------------------+        +------------------+
  | itt_testcaseid PK|        | itt_testrunid  PK|
  | itt_testid   AK  |        | itt_teststatus   |
  | itt_title        |        | itt_passed       |
  | itt_category     |        | itt_failed       |
  | itt_tags         |        | itt_total        |
  | itt_userstories  |        | itt_started_on   |
  | itt_enabled      |        | itt_completed_on |
  | itt_definition_  |        | itt_testcasefilter|
  |   json           |        | itt_testsummary  |
  +--------+---------+        | itt_fulllog      |
           |                  +--------+---------+
           |                           |
           |    itt_testrunresult      |
           |    +------------------+   |
           |    | itt_testrun-     |   |
           +----+   resultid   PK |---+
                | itt_testid       |
                | itt_outcome      |
                | itt_duration_ms  |
                | itt_error_message|
                | itt_assertion_   |
                |   results        |
                | _itt_testrunid_  |
                |   value      FK |-----> itt_testrun
                | _itt_testcaseid_ |
                |   value      FK |-----> itt_testcase
                +------------------+
```

Beziehungen:
- `itt_testrunresult` N:1 `itt_testrun` (über `_itt_testrunid_value`)
- `itt_testrunresult` N:1 `itt_testcase` (über `_itt_testcaseid_value`, logische Zuordnung über `itt_testid`)

---

## Entity: itt_testcase

Tabelle für die Definition einzelner Testfälle.

**EntitySet-Name:** `itt_testcases`

**Primary Name:** `itt_name` (AutoNumber, wird serverseitig generiert)

**Alternate Key:** `itt_testid` (eindeutige fachliche Test-ID, z.B. `TC01`, `BTC12`, `ERR03`)

| Schema-Name          | Display Name        | Typ                    | Länge / Details            | Required            | Beschreibung                                                        |
|-----------------------|---------------------|------------------------|---------------------------|---------------------|---------------------------------------------------------------------|
| `itt_testcaseid`     | Testfall ID         | Uniqueidentifier       | GUID                      | SystemRequired      | Primärschlüssel (GUID)                                              |
| `itt_testid`         | Test ID             | String                 | Zeichenkette              | ApplicationRequired | Fachliche eindeutige ID (Alternate Key), z.B. `TC01`, `ERR03`      |
| `itt_title`          | Titel               | String                 | Zeichenkette              | ApplicationRequired | Beschreibender Titel des Testfalls                                  |
| `itt_category`       | Kategorie           | OptionSet (Picklist)   | `itt_testcategory`        | Recommended         | Testkategorie (UpdateSource, Bridge, ErrorInjection usw.)           |
| `itt_tags`           | Tags                | String                 | Zeichenkette              | None                | Kommagetrennte Tags, z.B. `LUW,SingleSource,Contact`               |
| `itt_userstories`    | User Stories        | String                 | Zeichenkette              | None                | Kommagetrennte Jira-Keys, z.B. `DYN-1234,DYN-5678`                 |
| `itt_definition_json`| Definition (JSON)   | Multiline (Memo)       | Unbegrenzt                | None                | JSON-Definition des Testfalls (Preconditions, Steps, Assertions)    |
| `itt_enabled`        | Aktiv               | Boolean                | true/false                | None                | Ob der Testfall bei Gesamtläufen berücksichtigt wird                |

---

## Entity: itt_testrun

Tabelle für einzelne Testlauf-Ausführungen.

**EntitySet-Name:** `itt_testruns`

| Schema-Name            | Display Name       | Typ                    | Länge / Details            | Required        | Beschreibung                                                         |
|-------------------------|---------------------|------------------------|---------------------------|-----------------|----------------------------------------------------------------------|
| `itt_testrunid`        | Testlauf ID         | Uniqueidentifier       | GUID                      | SystemRequired  | Primärschlüssel (GUID)                                               |
| `itt_teststatus`       | Status              | OptionSet (Picklist)   | `itt_teststatus`          | None            | Aktueller Status des Testlaufs (Geplant, Läuft, Abgeschlossen, Fehler) |
| `itt_passed`           | Bestanden           | Integer                | Ganzzahl                  | None            | Anzahl bestandener Tests                                             |
| `itt_failed`           | Fehlgeschlagen      | Integer                | Ganzzahl                  | None            | Anzahl fehlgeschlagener Tests                                        |
| `itt_total`            | Gesamt              | Integer                | Ganzzahl                  | None            | Gesamtanzahl der Tests im Lauf                                       |
| `itt_started_on`       | Gestartet           | DateTime               | ISO 8601                  | None            | Zeitpunkt des Starts                                                 |
| `itt_completed_on`     | Abgeschlossen       | DateTime               | ISO 8601                  | None            | Zeitpunkt des Abschlusses                                            |
| `itt_testcasefilter`   | Testfall-Filter     | String                 | Zeichenkette              | None            | Filter-Ausdruck (`*`, `category:Bridge`, `tag:LUW`, `story:DYN-1234`, oder kommagetrennte IDs) |
| `itt_testsummary`      | Zusammenfassung     | Multiline (Memo)       | Unbegrenzt                | None            | Zusammenfassung des Testlaufs (z.B. "12 Tests, 11 bestanden, 1 fehlgeschlagen") |
| `itt_fulllog`          | Vollständiges Log   | Multiline (Memo)       | Unbegrenzt                | None            | Detailliertes Ausführungslog mit Zeitstempeln                        |

---

## Entity: itt_testrunresult

Tabelle für Einzelergebnisse pro Testfall innerhalb eines Testlaufs.

**EntitySet-Name:** `itt_testrunresults`

| Schema-Name              | Display Name          | Typ                    | Länge / Details            | Required        | Beschreibung                                                           |
|---------------------------|-----------------------|------------------------|---------------------------|-----------------|------------------------------------------------------------------------|
| `itt_testrunresultid`    | Ergebnis ID           | Uniqueidentifier       | GUID                      | SystemRequired  | Primärschlüssel (GUID)                                                 |
| `itt_testid`             | Test ID               | String                 | Zeichenkette              | None            | Fachliche Test-ID (Referenz auf `itt_testcase.itt_testid`)             |
| `itt_outcome`            | Ergebnis              | OptionSet (Picklist)   | `itt_testoutcome`         | None            | Testergebnis (Passed, Failed, Error, Skipped)                          |
| `itt_duration_ms`        | Dauer (ms)            | Integer                | Ganzzahl                  | None            | Ausführungsdauer in Millisekunden                                      |
| `itt_error_message`      | Fehlermeldung         | Multiline (Memo)       | Unbegrenzt                | None            | Fehlermeldung bei Failed/Error                                         |
| `itt_assertion_results`  | Assertion-Ergebnisse  | Multiline (Memo)       | Unbegrenzt (JSON)         | None            | JSON-Array mit Einzelergebnissen pro Assertion (passed, actual usw.)   |
| `_itt_testrunid_value`   | Testlauf              | Lookup                 | auf `itt_testrun`         | None            | Fremdschlüssel auf den zugehörigen Testlauf                           |
| `_itt_testcaseid_value`  | Testfall              | Lookup                 | auf `itt_testcase`        | None            | Fremdschlüssel auf den zugehörigen Testfall (optional, logisch über `itt_testid`) |

---

## OptionSets

### itt_teststatus (Teststatus)

Status eines Testlaufs.

| Code       | Label           | CONFIG-Konstante       |
|------------|-----------------|------------------------|
| 100000000  | Geplant         | `statusPlanned`        |
| 100000001  | Läuft           | `statusRunning`        |
| 100000002  | Abgeschlossen   | `statusCompleted`      |
| 100000003  | Fehler          | `statusError`          |

### itt_testoutcome (Testergebnis)

Ergebnis eines einzelnen Testfalls innerhalb eines Testlaufs.

| Code       | Label           | CONFIG-Konstante       |
|------------|-----------------|------------------------|
| 100000000  | Passed          | `outcomePassed`        |
| 100000001  | Failed          | `outcomeFailed`        |
| 100000002  | Error           | `outcomeError`         |
| 100000003  | Skipped         | `outcomeSkipped`       |
| 100000004  | Not Implemented | `outcomeNotImpl`       |

### itt_testcategory (Testkategorie)

Fachliche Kategorie eines Testfalls.

| Code       | Label              | CONFIG-Konstante        |
|------------|--------------------|-------------------------|
| 100000000  | UpdateSource       | `catUpdateSource`       |
| 100000001  | CreateSource       | `catCreateSource`       |
| 100000002  | PISA               | `catPISA`               |
| 100000003  | MultiSource        | `catMultiSource`        |
| 100000004  | AdditionalFields   | `catAdditionalFields`   |
| 100000005  | Bridge             | `catBridge`             |
| 100000006  | Merge              | `catMerge`              |
| 100000007  | Recompute          | `catRecompute`          |
| 100000008  | ErrorInjection     | `catErrorInjection`     |

---

## Beziehungen

### N:1 testrunresult zu testrun

- **Child:** `itt_testrunresult`
- **Parent:** `itt_testrun`
- **Lookup-Feld:** `_itt_testrunid_value` (bzw. `itt_testrunid` als Lookup auf der Entity)
- **Semantik:** Jedes Testergebnis gehört zu genau einem Testlauf. Ein Testlauf kann beliebig viele Ergebnisse enthalten.
- **Kaskaden-Verhalten:** Beim Löschen eines Testlaufs werden die zugehörigen Ergebnisse kaskadiert gelöscht (Restrict oder Cascade je nach Konfiguration).

### N:1 testrunresult zu testcase

- **Child:** `itt_testrunresult`
- **Parent:** `itt_testcase`
- **Lookup-Feld:** `_itt_testcaseid_value` (optional, logische Zuordnung primär über das String-Feld `itt_testid`)
- **Semantik:** Jedes Testergebnis bezieht sich auf genau einen Testfall. Die Zuordnung erfolgt über die fachliche `itt_testid` oder optional über den GUID-Lookup.
- **Kaskaden-Verhalten:** RemoveLink oder Restrict (Testfälle sollen nicht versehentlich Ergebnisse löschen).

### Hinweise zur Datenintegrität

- Die Zuordnung `testrunresult` zu `testcase` erfolgt im Code primär über das String-Feld `itt_testid` (OData-Filter: `$filter=itt_testid eq 'TC01'`), nicht über den GUID-Lookup. Das ermöglicht flexiblen Import und Upsert.
- Die Zuordnung `testrunresult` zu `testrun` erfolgt über den Lookup-Wert `_itt_testrunid_value` (OData-Filter: `$filter=_itt_testrunid_value eq '{runId}'`).
