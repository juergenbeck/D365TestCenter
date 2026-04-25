# D365 Test Center — Entwickler-Handbuch

Dieses Handbuch richtet sich an Entwickler, die mit dem D365 Test Center
Integrationstests schreiben, ausführen und auswerten. Alles was du hier
brauchst, kannst du in der Model-Driven-App in Dynamics 365 machen —
kein CLI, kein PowerShell, kein Deployment-Tool.

## Voraussetzungen

- Dynamics 365 / Dataverse-Grundkenntnisse: du weißt was Entity,
  Attribute, Lookup und OptionSet bedeuten
- Zugang zur Model-Driven-App **D365 Test Center** mit Rechten zum Anlegen
  von `jbe_testcase`- und `jbe_testrun`-Records
- Ein JSON-Editor ist hilfreich (VS Code, Notepad++) — man kann aber auch
  direkt im Mehrzeilentext-Feld der App arbeiten

## Was das Test Center ist

Ein Integrations-Testframework für Dynamics 365. Tests werden als JSON
definiert, als Record in die Entity `jbe_testcase` gespeichert, und durch
einen `jbe_testrun`-Record gestartet. Die Ausführung läuft automatisch
als Async-Plugin im Server und protokolliert jeden Schritt als
`jbe_teststep`-Record. Mehr dazu in
[01-einführung/01-was-ist-das-test-center.md](01-einführung/01-was-ist-das-test-center.md).

## Lese-Pfade

### Neu hier? Starte hier.

1. [Was ist das Test Center?](01-einführung/01-was-ist-das-test-center.md) — 5 Min Überblick
2. [Entity-Modell](01-einführung/02-entity-modell.md) — welche Records spielen zusammen
3. [Quickstart mit 5 Beispielen](01-einführung/03-quickstart.md) — erste 10 Minuten

### Du willst einen neuen Test schreiben

1. [JSON-Schema](02-testfall-schreiben/01-json-schema.md) — Grundaufbau
2. [Actions-Referenz](02-testfall-schreiben/02-actions-referenz.md) — was man machen kann
3. [Platzhalter](02-testfall-schreiben/03-platzhalter.md) — `{TIMESTAMP}`, `{alias.id}` und Co.
4. [Rezepte](02-testfall-schreiben/07-rezepte.md) — Vorlagen für Standard-Szenarien
5. [Negative-Path-Tests](02-testfall-schreiben/09-negative-path.md) — `expectFailure` für erwartete Fehler (v5.3+)
6. [Pitfalls und Plattform-Constraints](02-testfall-schreiben/10-pitfalls.md) — state-locked Creation, Plattform-Exception, Cold-Start, Lookup-Binds

### Du willst einen Test ausführen

1. [Testfall in der App anlegen](03-ausführen/01-testfall-in-app-anlegen.md)
2. [Testlauf starten](03-ausführen/02-testlauf-starten.md)

### Dein Test scheitert

1. [Entscheidungsbaum](05-troubleshooting/01-entscheidungsbaum.md) — systematisch zum Fehler
2. [Häufige Fehler](05-troubleshooting/02-häufige-fehler.md) — Top 12 mit Fix
3. [Fehleranalyse](04-auswerten/04-fehleranalyse.md) — FAILED vs ERROR verstehen

### Du suchst schnell ein JSON-Snippet

[Anhang A — Cheat Sheet](anhang/a-cheat-sheet.md) — 20 häufige Patterns auf einer Seite

## Verzeichnisstruktur

```
docs/handbuch/
  README.md                        <-- du bist hier
  01-einführung/                  Architektur, Entity-Modell, Quickstart
  02-testfall-schreiben/           Die Sprache: Schema, Actions, Platzhalter, Assertions
  03-ausführen/                   Test in der App anlegen und starten
  04-auswerten/                    Ergebnisse lesen, Steps-Tab, Fehleranalyse
  05-troubleshooting/              Entscheidungsbaum, häufige Fehler, Timing
  anhang/                          Cheat Sheet, Glossar, Links
```

## Konventionen in diesem Handbuch

- **JSON-Beispiele** sind immer vollständig und lauffähig, es sei denn
  der Kontext sagt anders ("Snippet").
- **ASCII-Grafiken** zeigen typische UI-Ansichten in Dynamics. Die Box-
  Zeichen `+ - |` dienen zur Orientierung, ersetzen keinen Screenshot.
- **Entity-Namen** sind immer EntitySetName (Plural): `accounts`, `contacts`,
  `leads`, `opportunities`. Im Test-JSON ist `entity` **immer Plural**.
- **Datumswerte** im Handbuch als ISO-Format `2026-04-24T10:30:00Z`.

## Feedback und Fragen

Anmerkungen an den Projekt-Owner. Fehlende Szenarien oder unklare Stellen
sind sehr willkommen — dieses Handbuch wird iterativ ausgebaut.
