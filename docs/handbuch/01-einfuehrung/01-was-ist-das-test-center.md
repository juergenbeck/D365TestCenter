# Was ist das D365 Test Center?

Ein Framework fuer **automatisierte Integrationstests** in Dynamics 365.
Du beschreibst einen Testfall als JSON (Daten anlegen, Aktionen ausfuehren,
Ergebnisse pruefen), speicherst ihn als Record und startest einen Testlauf
durch ein einfaches Status-Setzen. Der Testlauf laeuft als Async-Plugin im
Server und erzeugt am Ende einen detaillierten Ergebnis-Bericht.

## Wofuer ist es gut?

**Integrations-Tests** — also Tests, die echte Records in Dataverse
anlegen, Plugin-Ketten aufrufen, asynchron ausgefuehrte Workflows abwarten
und am Ende pruefen, ob das Gesamtsystem das erwartete Ergebnis liefert.

**Nicht** fuer:

- **Unit-Tests** (dafuer FakeXrmEasy / xUnit im C#-Code)
- **UI-Tests** (dafuer EasyRepro)
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
                                           | Async-Plugin laeuft:    |
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

Drei Aktionen fuer dich: **anlegen**, **starten**, **auswerten**.
Alles dazwischen macht der Server.

## Die Entities (Kurz-Ueberblick)

| Entity | Zweck |
|---|---|
| `jbe_testcase` | Die Testfall-Definition. JSON im Feld `jbe_definitionjson`. |
| `jbe_testrun` | Ein konkreter Testlauf: "Fuehre die Tests mit Filter X aus". |
| `jbe_testrunresult` | Ergebnis pro Testfall innerhalb eines Laufs (passed/failed/error). |
| `jbe_teststep` | Detail pro Schritt: welche Action, wie lang, was war das Ergebnis. |

Details: [02-entity-modell.md](02-entity-modell.md).

## Was macht das Test Center besonders?

- **Isolation pro Run:** Jeder Testlauf legt seine eigenen Records an,
  raeumt am Ende auf (optional auch behalten). Paralleles Laufen
  kollisionsfrei durch eingebaute `{TIMESTAMP}`-Platzhalter.
- **Protokolliert alles:** Jeder Step hat seinen Record mit Dauer, Erfolg/
  Fehler, erwartetem und tatsaechlichem Wert. Nach einem Fehlschlag siehst
  du sofort wo's haengt.
- **Async-ready:** Actions wie `WaitForFieldValue` warten geduldig bis
  asynchrone Plugins ihre Arbeit getan haben.
- **Eine Sprache fuer alles:** Create, Update, Delete, Custom Action,
  Assertion — alles ist eine "Action" in einer Liste. Reihenfolge im JSON
  ist Ausfuehrungsreihenfolge.

## Typische Anwendungsfaelle

- "Wenn ich einen Lead qualifiziere, werden Contact und Opportunity
  angelegt?" (siehe Quickstart QS-03)
- "Mein Plugin setzt bei Opportunity-Statuswechsel ein Feld. Funktioniert
  das noch nach dem letzten Refactoring?"
- "Eine Invoice ueber 1000 EUR soll eine Approval-Task ausloesen. Passt
  das Timing?"
- "Nach einem Contact-Merge: landen alle abhaengigen Records am Master?"

## Was du hier NICHT findest

- **Wie das Test Center installiert wird** — das macht der Projekt-Owner.
- **Wie die C#-Engine erweitert wird** — das ist Produkt-Entwicklung.
- **CLI-Nutzung** — dieses Handbuch bleibt bei der App.

Ab ins [Entity-Modell](02-entity-modell.md) oder direkt in den
[Quickstart](03-quickstart.md).
