# Datenmodell: Integration Test Center

## Entity-Relationship-Diagramm (ASCII)

```
  jbe_testcase                 jbe_testrun
  +------------------+        +------------------+
  | jbe_testcaseid PK|        | jbe_testrunid  PK|
  | jbe_testid   AK  |        | jbe_teststatus   |
  | jbe_title        |        | jbe_passed       |
  | jbe_category     |        | jbe_failed       |
  | jbe_tags         |        | jbe_total        |
  | jbe_userstories  |        | jbe_startedon   |
  | jbe_enabled      |        | jbe_completedon |
  | jbe_definition_  |        | jbe_testcasefilter|
  |   json           |        | jbe_testsummary  |
  +--------+---------+        | jbe_fulllog      |
           |                  +--------+---------+
           |                           |
           |    jbe_testrunresult      |
           |    +------------------+   |
           |    | jbe_testrun-     |   |
           +----+   resultid   PK |---+
                | jbe_testid       |
                | jbe_outcome      |
                | jbe_durationms  |
                | jbe_errormessage|
                | jbe_assertion_   |
                |   results        |
                | _jbe_testrunid_  |
                |   value      FK |-----> jbe_testrun
                | _jbe_testcaseid_ |
                |   value      FK |-----> jbe_testcase
                +------------------+
```

Beziehungen:
- `jbe_testrunresult` N:1 `jbe_testrun` (über `_jbe_testrunid_value`)
- `jbe_testrunresult` N:1 `jbe_testcase` (über `_jbe_testcaseid_value`, logische Zuordnung über `jbe_testid`)

---

## Entity: jbe_testcase

Tabelle für die Definition einzelner Testfälle.

**EntitySet-Name:** `jbe_testcases`

**Primary Name:** `jbe_name` (AutoNumber, wird serverseitig generiert)

**Alternate Key:** `jbe_testid` (eindeutige fachliche Test-ID, z.B. `TC01`, `BTC12`, `ERR03`)

| Schema-Name          | Display Name        | Typ                    | Länge / Details            | Required            | Beschreibung                                                        |
|-----------------------|---------------------|------------------------|---------------------------|---------------------|---------------------------------------------------------------------|
| `jbe_testcaseid`     | Testfall ID         | Uniqueidentifier       | GUID                      | SystemRequired      | Primärschlüssel (GUID)                                              |
| `jbe_testid`         | Test ID             | String                 | Zeichenkette              | ApplicationRequired | Fachliche eindeutige ID (Alternate Key), z.B. `TC01`, `ERR03`      |
| `jbe_title`          | Titel               | String                 | Zeichenkette              | ApplicationRequired | Beschreibender Titel des Testfalls                                  |
| `jbe_category`       | Kategorie           | OptionSet (Picklist)   | `jbe_testcategory`        | Recommended         | Testkategorie (UpdateSource, Bridge, ErrorInjection usw.)           |
| `jbe_tags`           | Tags                | String                 | Zeichenkette              | None                | Kommagetrennte Tags, z.B. `LUW,SingleSource,Contact`               |
| `jbe_userstories`    | User Stories        | String                 | Zeichenkette              | None                | Kommagetrennte Jira-Keys, z.B. `DYN-1234,DYN-5678`                 |
| `jbe_definitionjson`| Definition (JSON)   | Multiline (Memo)       | Unbegrenzt                | None                | JSON-Definition des Testfalls (Preconditions, Steps, Assertions)    |
| `jbe_enabled`        | Aktiv               | Boolean                | true/false                | None                | Ob der Testfall bei Gesamtläufen berücksichtigt wird                |

---

## Entity: jbe_testrun

Tabelle für einzelne Testlauf-Ausführungen.

**EntitySet-Name:** `jbe_testruns`

| Schema-Name            | Display Name       | Typ                    | Länge / Details            | Required        | Beschreibung                                                         |
|-------------------------|---------------------|------------------------|---------------------------|-----------------|----------------------------------------------------------------------|
| `jbe_testrunid`        | Testlauf ID         | Uniqueidentifier       | GUID                      | SystemRequired  | Primärschlüssel (GUID)                                               |
| `jbe_teststatus`       | Status              | OptionSet (Picklist)   | `jbe_teststatus`          | None            | Aktueller Status des Testlaufs (Geplant, Läuft, Abgeschlossen, Fehler) |
| `jbe_passed`           | Bestanden           | Integer                | Ganzzahl                  | None            | Anzahl bestandener Tests                                             |
| `jbe_failed`           | Fehlgeschlagen      | Integer                | Ganzzahl                  | None            | Anzahl fehlgeschlagener Tests                                        |
| `jbe_total`            | Gesamt              | Integer                | Ganzzahl                  | None            | Gesamtanzahl der Tests im Lauf                                       |
| `jbe_startedon`       | Gestartet           | DateTime               | ISO 8601                  | None            | Zeitpunkt des Starts                                                 |
| `jbe_completedon`     | Abgeschlossen       | DateTime               | ISO 8601                  | None            | Zeitpunkt des Abschlusses                                            |
| `jbe_testcasefilter`   | Testfall-Filter     | String                 | Zeichenkette              | None            | Filter-Ausdruck (`*`, `category:Bridge`, `tag:LUW`, `story:DYN-1234`, oder kommagetrennte IDs) |
| `jbe_testsummary`      | Zusammenfassung     | Multiline (Memo)       | Unbegrenzt                | None            | Zusammenfassung des Testlaufs (z.B. "12 Tests, 11 bestanden, 1 fehlgeschlagen") |
| `jbe_fulllog`          | Vollständiges Log   | Multiline (Memo)       | Unbegrenzt                | None            | Detailliertes Ausführungslog mit Zeitstempeln                        |

---

## Entity: jbe_testrunresult

Tabelle für Einzelergebnisse pro Testfall innerhalb eines Testlaufs.

**EntitySet-Name:** `jbe_testrunresults`

| Schema-Name              | Display Name          | Typ                    | Länge / Details            | Required        | Beschreibung                                                           |
|---------------------------|-----------------------|------------------------|---------------------------|-----------------|------------------------------------------------------------------------|
| `jbe_testrunresultid`    | Ergebnis ID           | Uniqueidentifier       | GUID                      | SystemRequired  | Primärschlüssel (GUID)                                                 |
| `jbe_testid`             | Test ID               | String                 | Zeichenkette              | None            | Fachliche Test-ID (Referenz auf `jbe_testcase.jbe_testid`)             |
| `jbe_outcome`            | Ergebnis              | OptionSet (Picklist)   | `jbe_testoutcome`         | None            | Testergebnis (Passed, Failed, Error, Skipped)                          |
| `jbe_durationms`        | Dauer (ms)            | Integer                | Ganzzahl                  | None            | Ausführungsdauer in Millisekunden                                      |
| `jbe_errormessage`      | Fehlermeldung         | Multiline (Memo)       | Unbegrenzt                | None            | Fehlermeldung bei Failed/Error                                         |
| `jbe_assertionresults`  | Assertion-Ergebnisse  | Multiline (Memo)       | Unbegrenzt (JSON)         | None            | JSON-Array mit Einzelergebnissen pro Assertion (passed, actual usw.)   |
| `_jbe_testrunid_value`   | Testlauf              | Lookup                 | auf `jbe_testrun`         | None            | Fremdschlüssel auf den zugehörigen Testlauf                           |
| `_jbe_testcaseid_value`  | Testfall              | Lookup                 | auf `jbe_testcase`        | None            | Fremdschlüssel auf den zugehörigen Testfall (optional, logisch über `jbe_testid`) |

---

## OptionSets

### jbe_teststatus (Teststatus)

Status eines Testlaufs.

| Code       | Label           | CONFIG-Konstante       |
|------------|-----------------|------------------------|
| 100000000  | Geplant         | `statusPlanned`        |
| 100000001  | Läuft           | `statusRunning`        |
| 100000002  | Abgeschlossen   | `statusCompleted`      |
| 100000003  | Fehler          | `statusError`          |

### jbe_testoutcome (Testergebnis)

Ergebnis eines einzelnen Testfalls innerhalb eines Testlaufs.

| Code       | Label           | CONFIG-Konstante       |
|------------|-----------------|------------------------|
| 100000000  | Passed          | `outcomePassed`        |
| 100000001  | Failed          | `outcomeFailed`        |
| 100000002  | Error           | `outcomeError`         |
| 100000003  | Skipped         | `outcomeSkipped`       |
| 100000004  | Not Implemented | `outcomeNotImpl`       |

### jbe_testcategory (Testkategorie)

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

- **Child:** `jbe_testrunresult`
- **Parent:** `jbe_testrun`
- **Lookup-Feld:** `_jbe_testrunid_value` (bzw. `jbe_testrunid` als Lookup auf der Entity)
- **Semantik:** Jedes Testergebnis gehört zu genau einem Testlauf. Ein Testlauf kann beliebig viele Ergebnisse enthalten.
- **Kaskaden-Verhalten:** Beim Löschen eines Testlaufs werden die zugehörigen Ergebnisse kaskadiert gelöscht (Restrict oder Cascade je nach Konfiguration).

### N:1 testrunresult zu testcase

- **Child:** `jbe_testrunresult`
- **Parent:** `jbe_testcase`
- **Lookup-Feld:** `_jbe_testcaseid_value` (optional, logische Zuordnung primär über das String-Feld `jbe_testid`)
- **Semantik:** Jedes Testergebnis bezieht sich auf genau einen Testfall. Die Zuordnung erfolgt über die fachliche `jbe_testid` oder optional über den GUID-Lookup.
- **Kaskaden-Verhalten:** RemoveLink oder Restrict (Testfälle sollen nicht versehentlich Ergebnisse löschen).

### Hinweise zur Datenintegrität

- Die Zuordnung `testrunresult` zu `testcase` erfolgt im Code primär über das String-Feld `jbe_testid` (OData-Filter: `$filter=jbe_testid eq 'TC01'`), nicht über den GUID-Lookup. Das ermöglicht flexiblen Import und Upsert.
- Die Zuordnung `testrunresult` zu `testrun` erfolgt über den Lookup-Wert `_jbe_testrunid_value` (OData-Filter: `$filter=_jbe_testrunid_value eq '{runId}'`).
