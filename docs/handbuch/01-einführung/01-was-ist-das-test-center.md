# Was ist das D365 Test Center?

Ein Framework für **automatisierte Integrationstests** in Dynamics 365.
Du beschreibst einen Testfall als JSON (Daten anlegen, Aktionen ausführen,
Ergebnisse prüfen), speicherst ihn als Record und startest einen Testlauf
durch ein einfaches Status-Setzen. Der Testlauf läuft als Async-Plugin im
Server und erzeugt am Ende einen detaillierten Ergebnis-Bericht.

## Wofür ist es gut?

**Integrations-Tests** — also Tests, die echte Records in Dataverse
anlegen, Plugin-Ketten aufrufen, asynchron ausgeführte Workflows abwarten
und am Ende prüfen, ob das Gesamtsystem das erwartete Ergebnis liefert.

**Nicht** für:

- **Unit-Tests** (dafür FakeXrmEasy / xUnit im C#-Code)
- **UI-Tests** (dafür EasyRepro)
- **Performance-Tests** (andere Werkzeuge)

## Der Weg eines Testlaufs auf einen Blick

```
   Du in der Model-Driven-App             Dataverse-Server
   +------------------------+              +-------------------------+
   | jbe_testcase anlegen   |              |                         |
   | (JSON in "Definition") |------------->| gespeichert             |
   +------------------------+              |                         |
                                           |                         |
   +------------------------+              |                         |
   | jbe_testrun anlegen    |              |                         |
   | Filter: "QS-01"        |              |                         |
   | Status: "Geplant"      |------------->| Plugin-Trigger feuert   |
   +------------------------+              |   |                     |
                                           |   v                     |
                                           | Async-Plugin läuft:    |
                                           |  - Testcase lesen       |
                                           |  - Steps der Reihe      |
                                           |    nach abarbeiten      |
                                           |  - Ergebnis + Logs      |
                                           |    schreiben            |
                                           |                         |
   +------------------------+              |                         |
   | jbe_testrun oeffnen    |<-------------| Status: "Abgeschlossen" |
   | -> passed/failed/total |              | jbe_testrunresult +     |
   | -> Steps-Tab ansehen   |              | jbe_teststep gefuellt   |
   +------------------------+              +-------------------------+
```

Drei Aktionen für dich: **anlegen**, **starten**, **auswerten**.
Alles dazwischen macht der Server.

## Die Entities (Kurz-Überblick)

| Entity | Zweck |
|---|---|
| `jbe_testcase` | Die Testfall-Definition. JSON im Feld `jbe_definitionjson`. |
| `jbe_testrun` | Ein konkreter Testlauf: "Führe die Tests mit Filter X aus". |
| `jbe_testrunresult` | Ergebnis pro Testfall innerhalb eines Laufs (passed/failed/error). |
| `jbe_teststep` | Detail pro Schritt: welche Action, wie lang, was war das Ergebnis. |

Details: [02-entity-modell.md](02-entity-modell.md).

## Was macht das Test Center besonders?

- **Isolation pro Run:** Jeder Testlauf legt seine eigenen Records an,
  räumt am Ende auf (optional auch behalten). Paralleles Laufen
  kollisionsfrei durch eingebaute `{TIMESTAMP}`-Platzhalter.
- **Protokolliert alles:** Jeder Step hat seinen Record mit Dauer, Erfolg/
  Fehler, erwartetem und tatsächlichem Wert. Nach einem Fehlschlag siehst
  du sofort wo's hängt.
- **Async-ready:** Actions wie `WaitForFieldValue` warten geduldig bis
  asynchrone Plugins ihre Arbeit getan haben.
- **Eine Sprache für alles:** Create, Update, Delete, Custom Action,
  Assertion — alles ist eine "Action" in einer Liste. Reihenfolge im JSON
  ist Ausführungsreihenfolge.

## Typische Anwendungsfaelle

- "Wenn ich einen Lead qualifiziere, werden Contact und Opportunity
  angelegt?" (siehe Quickstart QS-03)
- "Mein Plugin setzt bei Opportunity-Statuswechsel ein Feld. Funktioniert
  das noch nach dem letzten Refactoring?"
- "Eine Invoice über 1000 EUR soll eine Approval-Task auslösen. Passt
  das Timing?"
- "Nach einem Contact-Merge: landen alle abhängigen Records am Master?"

## Was du hier NICHT findest

- **Wie das Test Center installiert wird** — das macht der Projekt-Owner.
- **Wie die C#-Engine erweitert wird** — das ist Produkt-Entwicklung.
- **CLI-Nutzung** — dieses Handbuch bleibt bei der App.

Ab ins [Entity-Modell](02-entity-modell.md) oder direkt in den
[Quickstart](03-quickstart.md).
