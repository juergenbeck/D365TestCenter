# Entity-Modell

Das Test Center arbeitet mit vier Entities. Wenn du ihre Rolle und die
wichtigsten Felder kennst, verstehst du jeden Testlauf.

```
            jbe_testcase (N Records)
              |
              | "ausgewaehlt durch Filter"
              v
           jbe_testrun (1 Record pro Lauf)
              |
              | 1 : N
              v
         jbe_testrunresult (1 Record pro ausgewaehlten Testcase)
              |
              | 1 : N
              v
           jbe_teststep (1 Record pro Action im Testcase)
```

## jbe_testcase — die Testfall-Definition

Enthaelt die komplette Beschreibung eines Testfalls im JSON-Format. Du
schreibst den Test einmal, und er liegt von da an dauerhaft in dieser
Entity. Gleichnamige Test-IDs pro Umgebung sind nicht erlaubt.

**Wichtigste Felder:**

| Feld | Typ | Bedeutung |
|---|---|---|
| `jbe_testid` | String | Eindeutige ID, z.B. `QS-01`. Per Filter ansprechbar. |
| `jbe_name` | String (Primary) | Anzeigename. Standardmäßig gleich `title` aus dem JSON. |
| `jbe_title` | String | Aussagekräftiger Titel des Tests. |
| `jbe_tags` | String | Komma-getrennte Tags zur Kategorisierung. |
| `jbe_userstories` | String | Jira/DevOps-Ticket-Referenzen. |
| `jbe_enabled` | Two Options | `Ja`/`Nein`. Deaktivierte Tests werden beim Run übersprungen. |
| `jbe_definitionjson` | Mehrzeilentext | **Hier steht die komplette JSON-Definition.** |

Das Feld `jbe_definitionjson` ist das Herzstück. Alles was der Test tun
soll, steht dort als JSON-Objekt.

## jbe_testrun — ein konkreter Lauf

Du legst pro Testlauf einen neuen Record an. Das Anlegen selbst startet
die Ausführung automatisch.

**Wichtigste Felder:**

| Feld | Typ | Bedeutung |
|---|---|---|
| `jbe_name` | String (Primary) | Beschreibung, z.B. `"Regression 24.04 nachmittags"`. |
| `jbe_testcasefilter` | String | Welche Tests laufen sollen. Siehe unten. |
| `jbe_teststatus` | OptionSet | `Geplant` (105710000), `Wird ausgefuehrt` (105710001), `Abgeschlossen` (105710002), `Fehlgeschlagen` (105710003). |
| `jbe_keeprecords` | Two Options | `Ja` behaelt Testdaten, `Nein` räumt am Ende auf. |
| `jbe_passed` / `jbe_failed` / `jbe_total` | Integer | Werden vom Plugin gesetzt. |
| `jbe_testsummary` | Mehrzeilentext | Kurzer Ergebnis-Text. |
| `jbe_fulllog` | Mehrzeilentext | Vollstaendiger Log des Laufs. |
| `jbe_startedon` / `modifiedon` | DateTime | Zeitstempel. |

**Der Filter `jbe_testcasefilter`** steuert welche `jbe_testcase`-Records
ausgeführt werden:

| Filter | Wirkung |
|---|---|
| `*` oder leer | Alle aktivierten Tests |
| `QS-01` | Nur der eine Test mit exakt dieser `jbe_testid` |
| `QS-01,QS-03` | Komma-getrennte Liste |
| `QS-*` | Wildcard: alle Tests die mit `QS-` beginnen |
| `tag:smoke` | Alle Tests mit Tag `smoke` |
| `category:Integration` | Alle Tests der Kategorie `Integration` |

**Der Start-Trigger:** Sobald du den `jbe_testrun` mit `jbe_teststatus =
Geplant` speicherst, feuert das CRUD-Trigger-Plugin `RunTestsOnStatusChange`
und der Lauf beginnt asynchron. Du musst also einfach den Record speichern.

## jbe_testrunresult — Ergebnis pro Test

Pro Test im Lauf wird ein Ergebnis-Record geschrieben. Wenn dein Filter
drei Tests trifft, hast du drei `jbe_testrunresult`-Records.

**Wichtigste Felder:**

| Feld | Typ | Bedeutung |
|---|---|---|
| `jbe_testid` | String | Welcher Test war das. |
| `jbe_name` | String (Primary) | AutoNumber, Anzeige wie `RR-001671`. |
| `jbe_outcome` | OptionSet | `Passed`, `Failed`, `Error`, `Skipped`. |
| `jbe_errormessage` | Mehrzeilentext | Wenn Error/Failed: Grund. |
| `jbe_testrunid` | Lookup | Rückverweis auf den `jbe_testrun`. |

**Outcomes im Detail:**

- **Passed** — alle Actions haben geklappt, alle Asserts waren erfolgreich.
- **Failed** — alle Actions liefen durch, aber mindestens eine Assert hat
  einen unerwarteten Wert gefunden.
- **Error** — eine Action hat geworfen (Netzwerkfehler, ungültiger Alias,
  Plugin-Exception, ...). Der Test wurde abgebrochen.
- **Skipped** — der Test wurde vor dem ersten Step abgebrochen, meist
  wegen einem Problem beim Parsen der JSON-Definition.

## jbe_teststep — Detail pro Action

Für jede Action im Test wird ein Step-Record geschrieben. Das ist dein
Log zum Mitlesen.

**Wichtigste Felder:**

| Feld | Typ | Bedeutung |
|---|---|---|
| `jbe_stepnumber` | Integer | Reihenfolge (1, 2, 3, ..., meist auch 9000 für Cleanup). |
| `jbe_action` | String | `CreateRecord`, `Assert`, `ExecuteRequest`, ... |
| `jbe_alias` | String | Der Alias aus dem JSON, falls gesetzt. |
| `jbe_entity` | String | Ziel-Entity der Action. |
| `jbe_stepstatus` | OptionSet | `Erfolg`, `Fehler`, `Uebersprungen`. |
| `jbe_durationms` | Integer | Wie lange hat der Step gedauert. |
| `jbe_errormessage` | Mehrzeilentext | Bei Fehler: Grund. |
| `jbe_assertionfield` | String | Bei Assert: geprüftes Feld. |
| `jbe_expectedvalue` | String | Bei Assert: erwarteter Wert. |
| `jbe_actualvalue` | String | Bei Assert: tatsächlicher Wert. |
| `jbe_recordid` | String | ID des erzeugten/bearbeiteten Records (als Klick-Link). |
| `jbe_inputdata` / `jbe_outputdata` | Mehrzeilentext | Rohdaten für Debugging. |
| `jbe_testrunresultid` | Lookup | Rückverweis. |

Im Steps-Tab der `jbe_testrunresult`-Detailansicht siehst du diese Steps
als Raster. Das ist dein wichtigstes Werkzeug bei der Fehleranalyse.

## Wer räumt auf?

Standardmäßig löscht das Test Center am Ende des Laufs alle Records,
die während des Tests angelegt wurden (per interner Record-Tracker-Liste).
Ausnahmen:

- `jbe_keeprecords = Ja` im Testrun: **nichts** wird gelöscht, du siehst
  alle Testdaten in Dataverse. Nützlich zum Debugging.
- Einzelne `CreateRecord`-Steps mit `"track": false`: werden nicht fürs
  Cleanup erfasst (selten gebraucht).

Die Test-Records selbst (`jbe_testrun`, `jbe_testrunresult`, `jbe_teststep`)
werden **nie** automatisch gelöscht — sie sind dein Audit-Log.

---

Weiter mit dem [Quickstart](03-quickstart.md).
