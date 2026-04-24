# Testlauf starten

Einen Testlauf starten heißt: einen `jbe_testrun`-Record anlegen mit
Status "Geplant". Das Speichern triggert das Plugin, der Lauf beginnt
automatisch.

## Schritt 1: Zu den Testläufen navigieren

In der App **D365 Test Center** im linken Menü: **Testläufe**.

```
+-- Testläufe -----------------------------------------+
|   [+ Neu]  [Aktualisieren]                            |
|                                                       |
|   +----------------+--------+---------+--------+---+  |
|   | Name           | Status | Filter  | P/F/T  |   |  |
|   +----------------+--------+---------+--------+---+  |
|   | Regression ... | Abgesc.| QS-*    | 5/0/5  |   |  |
|   | Test MTC01 ... | Abgesc.| MTC01   | 1/0/1  |   |  |
|   +----------------+--------+---------+--------+---+  |
|                                                       |
+-------------------------------------------------------+
```

## Schritt 2: Neuen Testlauf anlegen

Klick auf **+ Neu**.

```
+-- Neuer Testlauf ----------------------------------------+
|                                                          |
|  Name              * [ Regression 24.04              ]   |
|  Testcase-Filter   * [ QS-*                          ]   |
|  Test-Status         [ Geplant                     v ]   |
|  Records behalten    [ ] nein                            |
|                                                          |
|  Bestanden           (wird automatisch gesetzt)          |
|  Fehlgeschlagen      (wird automatisch gesetzt)          |
|  Gesamt              (wird automatisch gesetzt)          |
|                                                          |
|  [Speichern]  [Speichern & Schließen]                   |
+----------------------------------------------------------+
```

### Die Felder im Detail

**Name** — frei wählbar. Gute Beispiele:
- `Regression vor Release`
- `Sprint 63 Testlauf`
- `Smoke Test 24.04 nachmittags`
- `MTC01 nach Plugin-Fix`

**Testcase-Filter** — steuert WELCHE Tests laufen:

| Filter | Wirkung |
|---|---|
| (leer) oder `*` | Alle aktivierten Testfälle |
| `QS-01` | Nur dieser eine |
| `QS-01,QS-03,QS-05` | Genau diese drei |
| `QS-*` | Alle die mit `QS-` anfangen |
| `tag:smoke` | Alle mit Tag `smoke` |
| `category:Integration` | Alle mit Category `Integration` |

**Test-Status** — muss auf **Geplant** stehen. Das ist der Trigger.

**Records behalten** (`jbe_keeprecords`):

- **Nicht angehakt** (Default): Testdaten werden nach dem Lauf gelöscht.
- **Angehakt**: Testdaten bleiben — nützlich wenn du nach dem Lauf
  manuell im Browser verifizieren willst was passiert ist.

## Schritt 3: Speichern = Start

Klick **Speichern**. Die App bleibt auf der Detailseite. Der Lauf wurde
angestoßen, das CRUD-Trigger-Plugin `RunTestsOnStatusChange` ist aktiv
geworden und arbeitet den Lauf im Hintergrund ab.

**Keine explizite "Start"-Schaltfläche.** Das Speichern ist der Start.

## Schritt 4: Zuschauen

Auf derselben Seite siehst du live die Aktualisierungen (durch den Browser-
Cache kann es 2-3 Sekunden brauchen — notfalls **F5** drücken):

```
+-- Testlauf: Regression 24.04 ---------------------------+
|                                                         |
|  Name           Regression 24.04                        |
|  Testcase-Filter QS-*                                   |
|  Test-Status    [ Wird ausgeführt           v ]        |
|  Records behalten [ ] nein                              |
|                                                         |
|  Bestanden      2                                       |
|  Fehlgeschlagen 0                                       |
|  Gesamt         2  (von 5)                              |
|                                                         |
|  Test-Zusammenfassung:                                  |
|  +-----------------------------------------------+      |
|  | 2/5 bestanden (läuft)                        |      |
|  | [OK] QS-01: Account anlegen...                |      |
|  | [OK] QS-02: Contact an Account...             |      |
|  | Batch 3/5 läuft: QS-03...                    |      |
|  +-----------------------------------------------+      |
+---------------------------------------------------------+
```

Der Status durchläuft:

1. **Geplant** — Trigger wurde aufgenommen, aber das Plugin hat noch
   nicht gestartet (wenige Sekunden).
2. **Wird ausgeführt** — Tests laufen aktiv ab.
3. **Abgeschlossen** — alle Tests haben durchlaufen (egal ob passed oder
   failed).
4. **Fehlgeschlagen** — ein **technischer** Fehler hat den ganzen Run
   abgebrochen (z.B. Serverfehler, Sandbox-Timeout). Nicht "einige Tests
   sind failed" — das ist normal Abgeschlossen mit `failed > 0`.

## Schritt 5: Wie lange dauert ein Lauf?

Richtlinien:

- **Ein einzelner Test** typisch 5-60 Sekunden, je nach Steps und
  `waitSeconds`.
- **Merge-Tests** mit 45s-Waits dauern entsprechend länger.
- **Batch mit N Tests** läuft **sequenziell** (nicht parallel).

**Sandbox-Timeout:** D365 Plugins haben eine 2-Minuten-Grenze pro Sync-
Aufruf. Das Test-Center arbeitet asynchron in Batches zu je ~12 Tests;
jeder Batch hat 2 Minuten. Bei >96 Tests kann es mehrere Runde brauchen
— die Engine macht das automatisch.

## Run abbrechen?

Gibt es **nicht**. Einmal gestartet läuft der Lauf durch. Du kannst aber
den Testrun-Record löschen, während er läuft — das beendet nicht das
Plugin im Hintergrund, aber der Record ist weg und du siehst kein Ergebnis
mehr. Nur für Notfälle.

## Mehrere Testläufe gleichzeitig

Möglich. Jeder Lauf ist isoliert durch `{TIMESTAMP}`. Die Plugin-Sandbox
kann ca. 5 gleichzeitige Ausführungen pro Organisation verkraften. Mehr
stellen sich in die Warteschlange.

## Nach dem Abschluss

Status wechselt auf **Abgeschlossen**. Du kannst jetzt:

1. Die **Test-Zusammenfassung** lesen (Feld `jbe_testsummary`)
2. Auf die **verbundenen Testergebnisse** klicken (`jbe_testrunresult`)
3. Pro Ergebnis die **Testschritte** im Detail ansehen

Weiter mit [Was passiert im Hintergrund](03-was-passiert-im-hintergrund.md)
oder direkt zur [Ergebnis-Auswertung](../04-auswerten/01-testrun-lesen.md).
