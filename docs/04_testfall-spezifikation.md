# Testfall-Spezifikation

Dieses Dokument ist der Einstieg in das Testfall-Format. Es zeigt das Grundgerüst und sagt,
wo die Einzelheiten stehen. Die gepflegte Spezifikation ist das Handbuch-Kapitel
[02-testfall-schreiben](handbuch/02-testfall-schreiben/), und bei einem Widerspruch gilt das
Handbuch.

## Ein Testfall ist eine geordnete Liste von Actions

Jeder Testfall wird als JSON im Feld `jbe_definitionjson` gespeichert. Seit ADR-0004 hat er
genau eine Ausführungsliste, `steps`. Die JSON-Reihenfolge ist die Ausführungsreihenfolge.
Vorbedingungen sind gewöhnliche `CreateRecord`-Schritte am Anfang, Prüfungen sind
`Assert`-Schritte, oft am Ende, aber Zwischenprüfungen sind erlaubt und erwünscht.

```json
{
  "testId": "MY-01",
  "title": "Account anlegen und Website setzen",
  "tags": ["smoke"],
  "enabled": true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc1",
      "fields": { "name": "JBE Test Firma {GENERATED:guid}" } },
    { "stepNumber": 2, "action": "UpdateRecord", "alias": "acc1",
      "fields": { "websiteurl": "https://example.com" } },
    { "stepNumber": 3, "action": "Assert", "target": "Record", "recordRef": "{RECORD:acc1}",
      "field": "websiteurl", "operator": "Equals", "value": "https://example.com",
      "onError": "continue", "description": "Website wurde gesetzt" }
  ]
}
```

Alle Top-Level-Felder und alle Pflicht- und Optionsfelder eines Schritts stehen in
[01-json-schema.md](handbuch/02-testfall-schreiben/01-json-schema.md).

## Abgelöst: getrennte preconditions- und assertions-Blöcke

Frühere Fassungen kannten neben `steps` zwei weitere Blöcke, `preconditions` und `assertions`,
teils als Array, teils als Flag-Objekt der Form `{"createAccount": true}`. **Die Engine kennt
beide nicht mehr.** Das Modell führt nur `Steps`, alles andere wird beim Einlesen still
verworfen. Ein Testfall im Altformat parst sauber, läuft grün und prüft dabei nichts.

`validate --pack` meldet das als Fehler, mit `PRECONDITIONS_OBSOLETE` und
`ASSERTIONS_OBSOLETE`. Die Umstellung eines Altfalls:

| Altformat | Heute |
|---|---|
| `preconditions: [...]` | `CreateRecord`-Schritte am Anfang der `steps`-Liste |
| `preconditions: {createAccount: true, ...}` | derselbe Schritt, ausgeschrieben mit `entity`, `alias` und `fields` |
| `assertions: [...]` | `Assert`-Schritte in der `steps`-Liste |

Detail und weitere Prüfregeln in
[11-pre-run-validation.md](handbuch/02-testfall-schreiben/11-pre-run-validation.md).

## Wo die Einzelheiten stehen

| Thema | Kapitel |
|---|---|
| Top-Level-Felder, Aufbau eines Schritts, `onError`, `condition` | [01-json-schema.md](handbuch/02-testfall-schreiben/01-json-schema.md) |
| Alle Actions mit ihren Parametern (`CreateRecord`, `UpdateRecord`, `DeleteRecord`, `ExecuteRequest`, `Wait`, die `WaitFor*`-Familie, `BrowserAction` und die übrigen) | [02-actions-referenz.md](handbuch/02-testfall-schreiben/02-actions-referenz.md) |
| Platzhalter: `{GENERATED:*}`, die `{TIMESTAMP*}`-Familie, `{alias.id}`, `{alias.fields.X}`, `{RECORD:alias}`, `{ROW:*}` | [03-platzhalter.md](handbuch/02-testfall-schreiben/03-platzhalter.md) |
| Lookups setzen, `@odata.bind`, polymorphe Ziele | [04-lookup-und-binding.md](handbuch/02-testfall-schreiben/04-lookup-und-binding.md) |
| `Assert` mit `target: "Record"` und `target: "Query"`, Filter-Syntax, alle Operatoren | [05-assertions.md](handbuch/02-testfall-schreiben/05-assertions.md) |
| Was ein Testfall abdecken muss, damit er etwas aussagt | [06-coverage-regeln.md](handbuch/02-testfall-schreiben/06-coverage-regeln.md) |
| Fertige Muster für wiederkehrende Aufgaben | [07-rezepte.md](handbuch/02-testfall-schreiben/07-rezepte.md) |
| Konventionen für Testdaten | [08-testdaten-konventionen.md](handbuch/02-testfall-schreiben/08-testdaten-konventionen.md) |
| Negativtests, erwartete Fehler und Ausnahmen | [09-negative-path.md](handbuch/02-testfall-schreiben/09-negative-path.md) |
| Fallstricke, die andere schon getroffen haben | [10-pitfalls.md](handbuch/02-testfall-schreiben/10-pitfalls.md) |
| Statische Prüfung vor dem Lauf und alle Befund-Codes | [11-pre-run-validation.md](handbuch/02-testfall-schreiben/11-pre-run-validation.md) |
| Aufräumen der Testdaten, `keepRecords`, `trackForCleanup`, `cleanupChildren` | [12-cleanup-und-testdaten-hygiene.md](handbuch/02-testfall-schreiben/12-cleanup-und-testdaten-hygiene.md) |

## Testfall-Lebenszyklus

**Erstellen.** Über den JSON-Editor im Test Center oder per `import-pack` aus einer Pack-Datei.
Die Metadaten (Test-ID, Titel, Kategorie, Tags, User Stories) liegen als Felder des
`jbe_testcase`-Datensatzes, die Definition im Memo-Feld `jbe_definitionjson`.

**Validieren.** Beim Speichern wird das JSON syntaktisch geprüft. Die inhaltliche Prüfung
macht `validate --pack` vor dem Lauf, und der TestRunner wiederholt sie je Testfall: ein
Befund der Stufe Error bricht den Testfall mit Outcome `Error` ab, statt ihn scheinbar laufen
zu lassen.

**Ausführen.** Der Lauf wird über die UI oder über `run --filter` gestartet. Es entsteht ein
`jbe_testrun`-Datensatz, je Testfall ein `jbe_testrunresult` mit Outcome, Dauer, Fehlermeldung
und den Einzelergebnissen der Prüfungen. Die Schritte laufen der Reihe nach, Platzhalter
werden dabei aufgelöst.

**Ergebnis.** Ein Testfall ist `Passed`, wenn kein Schritt fehlgeschlagen ist, `Failed` bei
mindestens einer fehlgeschlagenen Prüfung und `Error`, wenn ein Schritt mit `onError: "stop"`
eine Ausnahme geworfen hat. Die Details stehen im Lauf-Log und in der History-Ansicht je
Testfall-ID.
