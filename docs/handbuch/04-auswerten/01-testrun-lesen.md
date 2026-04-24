# TestRun lesen

Der `jbe_testrun`-Record ist dein Ueberblick: wurde der Lauf sauber
abgeschlossen, wie viele Tests sind durchgelaufen, wie viele sind
fehlgeschlagen.

## Die Standardansicht

Nach Abschluss eines Runs oeffnest du den Record:

```
+-- Testlauf: Regression 24.04 -----------------------------+
|                                                           |
|  Name              Regression 24.04                       |
|  Testcase-Filter   QS-*                                   |
|  Test-Status       [ Abgeschlossen          v ]           |
|  Records behalten  [ ] nein                               |
|  Gestartet         24.04.2026 11:45:32                    |
|  Geaendert         24.04.2026 11:47:58                    |
|                                                           |
|  Bestanden          4                                     |
|  Fehlgeschlagen     1                                     |
|  Gesamt             5                                     |
|                                                           |
|  Test-Zusammenfassung                                     |
|  +------------------------------------------------------+ |
|  | 4/5 bestanden, 1 fehlgeschlagen                      | |
|  | Batch 1-5 von 5:                                     | |
|  |   Dieser Batch: 4/5 bestanden                        | |
|  |   Gesamt bisher: 4/5                                 | |
|  | [OK]  QS-01: Account: Create, Update, Verify (1.2s)  | |
|  | [OK]  QS-02: Contact an Account (3.4s)               | |
|  | [OK]  QS-03: Lead qualifizieren (5.1s)               | |
|  | [OK]  QS-04: Opportunity gewinnen (8.2s)             | |
|  | [FAIL] QS-05: Task loeschen (1.8s)                   | |
|  +------------------------------------------------------+ |
|                                                           |
|  [> Zugeordnete Testergebnisse (5)]                       |
|                                                           |
+-----------------------------------------------------------+
```

## Das erste was du sehen willst

1. **Test-Status = Abgeschlossen?** — wenn nein, lauf ist noch aktiv oder
   technisch fehlgeschlagen.
2. **Fehlgeschlagen > 0?** — wenn ja, hast du Probleme.
3. **Welche Tests genau?** — siehe Test-Zusammenfassung.

## Test-Status

| Status | Bedeutung | Was jetzt? |
|---|---|---|
| Geplant | Trigger gesetzt, Plugin noch nicht angelaufen | 1 Minute warten, F5 |
| Wird ausgefuehrt | Plugin laeuft gerade | F5 druecken, zuschauen |
| Abgeschlossen | Lauf ist fertig (Passed oder Failed Tests inklusive) | Ergebnisse auswerten |
| Fehlgeschlagen | **Technischer** Abbruch: Plugin-Exception, Sandbox-Timeout, Parser-Fehler | Full-Log lesen, Projekt-Owner fragen |

**Wichtig:** `Abgeschlossen` mit `Fehlgeschlagen=3` bedeutet "Lauf ist
normal durchgelaufen, aber 3 Tests waren inhaltlich nicht OK". Das ist
ein gesunder Zustand — keine technische Panne.

`Test-Status = Fehlgeschlagen` dagegen bedeutet "der Lauf selbst ist
abgebrochen, ich weiss nicht einmal wie viele Tests geschafft haben".
Das sollte nicht passieren und deutet auf Infrastruktur-Probleme.

## Die Kennzahlen

```
Bestanden      4
Fehlgeschlagen 1
Gesamt         5
```

- **Bestanden**: Testcases mit Outcome = Passed. Alle Asserts OK.
- **Fehlgeschlagen**: Testcases mit Outcome = Failed ODER Error ODER
  Skipped.
- **Gesamt**: Summe. Muss = Anzahl Testcases im Filter sein.

Pruefe: Erwartete Anzahl = Gesamt? Wenn dein Filter `QS-*` trifft aber
nur 3 Testcases existieren, steht hier `Gesamt = 3`. Wenn dein Filter
keinen Treffer hat: `Gesamt = 0` (Lauf laeuft trotzdem durch, nur ohne
Tests).

## Test-Zusammenfassung (Feld `jbe_testsummary`)

Kurzer Plaintext-Ueberblick pro Testcase. Beispiel:

```
4/5 bestanden, 1 fehlgeschlagen
Batch 1-5 von 5:
  Dieser Batch: 4/5 bestanden
  Gesamt bisher: 4/5
[OK]  QS-01: Account: Create, Update, Verify (1234ms)
[OK]  QS-02: Contact an Account haengen (3456ms)
[OK]  QS-03: Lead qualifizieren (5123ms)
[OK]  QS-04: Opportunity gewinnen (8234ms)
[FAIL] QS-05: Task loeschen (1789ms)
```

Jede Zeile:

- Prefix `[OK]`, `[FAIL]`, `[ERR]`, `[SKIP]`
- Test-ID, Doppelpunkt, Titel
- Laufzeit in Klammern

Bei groesseren Runs steht hier auch der Batching-Status mit drin — welche
Batches sind abgearbeitet, welcher laeuft gerade.

## Full-Log (Feld `jbe_fulllog`)

Ausfuehrlicher Plaintext-Log, der waehrend des Runs gesammelt wurde. Hier
steht z.B. wenn die Engine versucht hat einen Record zu loeschen und
fehlgeschlagen ist, oder wenn das Parsen eines JSONs gehakt hat. Siehe
[05-logs-im-detail.md](05-logs-im-detail.md).

## Zugeordnete Testergebnisse

Unten auf der Seite findest du die **zugeordneten Testergebnisse**. Das
ist die Liste aller `jbe_testrunresult`-Records — einer pro Testcase im
Lauf:

```
+-- Zugeordnete Testergebnisse (5) ---------------------+
|                                                       |
|  +----------+-----------+-------------------+-------+ |
|  | Test-ID  | Ergebnis  | Name              | Fehler| |
|  +----------+-----------+-------------------+-------+ |
|  | QS-01    | Bestanden | RR-001720         |       | |
|  | QS-02    | Bestanden | RR-001721         |       | |
|  | QS-03    | Bestanden | RR-001722         |       | |
|  | QS-04    | Bestanden | RR-001723         |       | |
|  | QS-05    | Fehlge... | RR-001724         | Asse..| |
|  +----------+-----------+-------------------+-------+ |
|                                                       |
+-------------------------------------------------------+
```

**Klick auf eine fehlgeschlagene Zeile** oeffnet den Detail-Record mit
Step-Tab und Fehlermeldung — das ist dein naechster Anlaufpunkt. Siehe
[02-testrunresult-detail.md](02-testrunresult-detail.md).

## Tipps zur Tabellen-Ansicht

Die Zugeordnete-Liste ist eine ganz normale Dynamics-Sub-Grid. Du kannst:

- Nach **Ergebnis** sortieren (Bestanden / Fehlgeschlagen / Error)
- Per Spalten-Filter nur die **Fehlgeschlagenen** anzeigen
- Per Doppelklick einen Ergebnis-Record oeffnen

Fuer grosse Runs (>20 Ergebnisse) empfehlen sich diese Filter um nur auf
die Probleme zu schauen.
