# Logs im Detail

Für die meisten Fehlersuchen reicht der Steps-Tab. Für tiefere Analysen
gibt es zwei Langtext-Felder mit Vollprotokoll: `jbe_fulllog` und
`jbe_testsummary` am `jbe_testrun`.

## jbe_testsummary: die Kurzform

Schon am `jbe_testrun`-Formular direkt sichtbar. Plaintext, Zeile für
Zeile, Kurzüberblick:

```
5/5 bestanden, 0 fehlgeschlagen
Batch 1-5 von 5:
  Dieser Batch: 5/5 bestanden
  Gesamt bisher: 5/5
[OK] QS-01: Account anlegen und Website setzen (1234ms)
[OK] QS-02: Contact an Account hängen (3456ms)
[OK] QS-03: Lead qualifizieren (5123ms)
[OK] QS-04: Opportunity gewinnen (8234ms)
[OK] QS-05: Task löschen (1789ms)
```

**Format:**

```
<passed>/<total> bestanden, <failed> fehlgeschlagen

Batch <from>-<to> von <total-batches>:
  Dieser Batch: <pass>/<total> bestanden
  Gesamt bisher: <pass>/<total>

<[OK] | [FAIL] | [ERR] | [SKIP]>  <testId>: <title>  (<duration>ms)
...
```

Für einen Überblick reicht das oft.

## jbe_fulllog: das Vollprotokoll

Mühevoll gefülltes Textfeld mit dem gesamten Plugin-Output. Format
(Auszug):

```
[2026-04-24T11:45:32Z] INFO  TestCenterOrchestrator: Run gestartet.
                             Filter='QS-*', KeepRecords=false
[2026-04-24T11:45:32Z] INFO  Filter trifft 5 Testcases: QS-01, QS-02,
                             QS-03, QS-04, QS-05
[2026-04-24T11:45:32Z] INFO  Batch 1/1 (5 Tests) startet

[2026-04-24T11:45:32Z] INFO  QS-01 Start
[2026-04-24T11:45:32Z] DEBUG Step 1: CreateRecord(accounts)
[2026-04-24T11:45:32Z] DEBUG   Body: { "name": "JBE Test Account ..." }
[2026-04-24T11:45:33Z] DEBUG   Created: 3f2a1b4e-...
[2026-04-24T11:45:33Z] DEBUG Step 2: UpdateRecord(acc)
[2026-04-24T11:45:33Z] DEBUG Step 3: Assert (Record, websiteurl, Equals)
[2026-04-24T11:45:33Z] INFO  QS-01 Passed (1234ms)

[2026-04-24T11:45:33Z] INFO  QS-02 Start
...
```

**Levels:**

- `INFO`, wichtige Meilensteine (Testcase-Start, Batch-Ende)
- `DEBUG`, jeder einzelne Step
- `WARN`, ungewöhnliche Lage, Test läuft aber weiter
- `ERROR`, fataler Fehler, Testcase abgebrochen

## Wann lese ich jbe_fulllog?

1. **Ein Test ist `Error`, die Step-Error-Message ist kryptisch.** Im
   Full-Log siehst du den exakten HTTP-Request, Response-Body, interne
   Exception-Stacks.

2. **Der Run hängt.** Im Full-Log steht der letzte Eintrag. Daraus
   siehst du wo die Engine steht.

3. **Reproduktion eines alten Fehlers.** Der Full-Log ist dein Archiv,
   auch Wochen später kannst du nachvollziehen was passiert ist.

4. **Plugin-Reihenfolge verstehen.** Wenn mehrere Plugins kaskadieren,
   stehen ihre Outputs im Full-Log in der Reihenfolge der Ausführung.

## Das Feld lesen

In der App ist `jbe_fulllog` ein "Mehrzeilentext"-Feld. Es kann einige
Kilobyte groß werden. Die Standard-Textbox in Dynamics ist nicht
ideal, einige Optionen:

- **Klick ins Feld, Strg+A, Strg+C**: rüber in VS Code / Notepad.
  Dort ist es lesbar.
- **Bearbeiten-Button oben rechts am Feld** öffnet einen Vollbild-
  Dialog.
- **Per Tipp bei der Lektüre:** `[ERROR]` oder `[WARN]` im Editor
  suchen, damit du schnell zu kritischen Stellen springst.

## Step-Level Logs

Der `jbe_testsummary` und `jbe_fulllog` sind am **Testrun** angesiedelt.
Pro Step gibt es separat:

- `jbe_teststep.jbe_errormessage`, nur gefüllt bei `Fehler`-Steps
- `jbe_teststep.jbe_inputdata`, das JSON mit dem der Step gestartet
  wurde (mit aufgelösten Platzhaltern)
- `jbe_teststep.jbe_outputdata`, die Server-Antwort in Auszügen

## Browser DevTools

Wenn du den Testfall-Editor selbst debuggst (z.B. schreiben, speichern,
laden), ist manchmal die **Browser-Konsole** nützlich:

```
F12 -> Console
```

Dynamics-Client-API-Aufrufe, Network-Requests, Errors, alles da. Aber:
**Das hat mit der Test-Ausführung nichts zu tun** (die läuft am
Server). Nur für App-Bedienungs-Probleme.

## Ein Beispiel-Log bei einem fehlgeschlagenen Test

```
[2026-04-24T11:45:32Z] INFO  QS-05 Start
[2026-04-24T11:45:32Z] DEBUG Step 1: CreateRecord(tasks), alias=tsk
[2026-04-24T11:45:33Z] DEBUG   Created: 8d5f...
[2026-04-24T11:45:33Z] DEBUG Step 2: Assert (Record), subject StartsWith "JBE Test"
[2026-04-24T11:45:33Z] DEBUG   actual='JBE Test Aufgabe 2026-04-24T11:45:32Z' -> OK
[2026-04-24T11:45:33Z] DEBUG Step 3: DeleteRecord(tsk)
[2026-04-24T11:45:33Z] DEBUG   Deleted 8d5f...
[2026-04-24T11:45:33Z] DEBUG Step 4: Assert (Query, tasks where activityid eq ...)
                              NotExists
[2026-04-24T11:45:33Z] DEBUG   Found 1 matching record!
[2026-04-24T11:45:33Z] WARN    Task wurde nicht wirklich gelöscht.
[2026-04-24T11:45:33Z] ERROR   Assertion fehlgeschlagen.
[2026-04-24T11:45:33Z] INFO  QS-05 Failed (1789ms)
```

Aus dem Log allein kannst du schließen: das `DeleteRecord` hat Erfolg
zurückgemeldet, aber der Query direkt danach findet den Record noch.
Das deutet auf einen Plugin-Konflikt hin: etwas macht den Record sofort
wieder. Oder auf einen Bug im System.

## Zusammenfassung: welches Feld wann

| Feld | Wann lesen |
|---|---|
| `jbe_testsummary` | Auf einen Blick: was ist insgesamt passiert |
| `jbe_fulllog` | Für tiefere Analysen von Error-Cases oder Plugin-Konflikten |
| `jbe_teststep.jbe_errormessage` | Pro Step: warum ist dieser einzelne Step gescheitert |
| `jbe_teststep.jbe_inputdata/outputdata` | Detail-Debugging bei komplexen Actions |
| Browser-DevTools | Nur für App-Bedien-Probleme, nicht für Test-Execution |
