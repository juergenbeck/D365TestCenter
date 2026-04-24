# Testergebnis im Detail

Ein `jbe_testrunresult` ist das Ergebnis eines einzelnen Testcases
innerhalb eines Runs. Hier sind die wichtigen Debug-Infos.

## Die Detail-Seite

```
+-- Testergebnis: RR-001724 -------------------------------+
|                                                          |
|  Name          RR-001724                                 |
|  Test-ID       QS-05                                     |
|  Ergebnis      [ Fehlgeschlagen             v ]          |
|  Testlauf      > Regression 24.04                        |
|                                                          |
|  Fehlermeldung                                           |
|  +----------------------------------------------------+  |
|  | Assert fehlgeschlagen: Task ist nach Delete weg    |  |
|  | Erwartet: NotExists                                |  |
|  | Tatsächlich: 1 Record gefunden                    |  |
|  +----------------------------------------------------+  |
|                                                          |
|  [> Testschritte (4)]                                    |
|                                                          |
+----------------------------------------------------------+
```

## Das Ergebnis-Feld (`jbe_outcome`)

| Wert | Bedeutung |
|---|---|
| **Bestanden** | Alle Asserts OK, alle Actions ohne Fehler |
| **Fehlgeschlagen** | Mindestens ein Assert hat einen unerwarteten Wert gefunden. Der Test ist trotzdem bis zum Ende durchgelaufen |
| **Error** | Eine Non-Assert-Action hat einen Fehler geworfen (z.B. 404, Exception). Der Test wurde abgebrochen |
| **Übersprungen** | Der Test wurde vor dem ersten Step abgebrochen — meist JSON-Parse-Fehler |

## Fehlermeldung (`jbe_errormessage`)

Der Plaintext-Grund warum das Ergebnis NICHT Bestanden ist. Format
variiert je nach Outcome:

### Bei Failed

```
Assert fehlgeschlagen: <deine description>
Erwartet:     <Wert>
Tatsächlich: <Wert>
```

oder für Multi-Failure:

```
3 Assertions fehlgeschlagen:
 - Assert #4 (Sub-CS bleibt aktiv): erwartet '0', war '1'
 - Assert #7 (FoundMaster geleert): erwartet IsNull, war 'GR-42'
 - Assert #9 (Survivor aktiv): erwartet '0', war '1'
```

### Bei Error

```
Action 'CreateRecord' auf 'accounts' wirft:
  HTTP 400 Bad Request.
  Attribute 'unbekannt' does not exist on type 'Microsoft.Dynamics.CRM.account'.
```

Oder bei einem Plugin-Fehler:

```
ExecuteRequest 'Merge' wirft:
  Plugin 'SomeBusinessPlugin' returned:
  'Merge blocked: duplicate email address'.
```

### Bei Skipped

```
JSON-Parse-Fehler in jbe_definitionjson:
  Line 12, Col 5: missing ',' after property
```

## Zugeordnete Testschritte

Unten auf der Seite: **Testschritte** (Sub-Grid). Alle `jbe_teststep`-
Records die zu diesem Ergebnis gehören — ein Schritt pro Action im
Testcase plus ggf. Cleanup-Step.

```
+-- Testschritte (5) --------------------------------------+
|                                                          |
|  +---+--------------+-------+---------------+----------+ |
|  | # | Action       | Alias | Ergebnis      | Dauer    | |
|  +---+--------------+-------+---------------+----------+ |
|  | 1 | CreateRecord | tsk   | Erfolg        | 412 ms   | |
|  | 2 | Assert       |       | Erfolg        | 11 ms    | |
|  | 3 | DeleteRecord | tsk   | Erfolg        | 92 ms    | |
|  | 4 | Assert       |       | **Fehler**    | 23 ms    | |
|  +---+--------------+-------+---------------+----------+ |
|                                                          |
+----------------------------------------------------------+
```

Klick auf den fehlgeschlagenen Step (4) öffnet das Step-Detail — das
ist dein eigentlicher Debug-Punkt. Siehe
[03-steps-tab-verstehen.md](03-steps-tab-verstehen.md).

## Die 4 typischen Analyse-Muster

### Muster 1: Failed mit klarer Fehlermeldung

`Fehlermeldung` sagt direkt was nicht stimmt. Geh zum Step-Detail des
fehlgeschlagenen Asserts, schau dir `Erwartet` und `Tatsächlich` an.

**Typische Ursachen:**

- Timing: Plugin war noch nicht fertig. Lösung: `waitSeconds` erhöhen
  oder auf `WaitForFieldValue` umstellen.
- Falscher erwarteter Wert im Test: manchmal ist der Test falsch, nicht
  der Code.
- Plugin-Regression: der Code hat sich geändert und die Erwartung ist
  jetzt veraltet.

### Muster 2: Error bei einer Action

Action-Fehler sind oft **syntaktisch**: falscher Entity-Name, fehlendes
Pflichtfeld, Lookup-Binding falsch formatiert.

Oder **permission-basiert**: dein Service-User hat nicht die noetigen
Rechte.

Oder **Plugin-Interferenz**: ein anderes Plugin auf der Zielumgebung hat
den Create blockiert.

**Faustregel:** HTTP 4xx = Test-Problem, HTTP 5xx = Server-Problem.

### Muster 3: Skipped

JSON-Syntaxfehler oder fehlende Pflichtfelder im Test-Schema. Beispiel:

```
JSON-Parse-Fehler in jbe_definitionjson:
  Line 15, Col 3: unexpected token 'steps'
```

Öffne den Testfall im Editor, validiere das JSON, speichere neu.

### Muster 4: Passed — aber der Test sollte eigentlich Failed sein

Kommt vor wenn dein Test zu wenig prüft. Jede `Assert` die nicht
fehlschlägt, ist "OK" — auch wenn die Umgebung gar nicht in dem Zustand
ist den du dir vorgestellt hast.

**Lösung:** mehr Asserts hinzufügen. Besonders Negativ-Erwartungen
(`IsNull`, `NotExists`, `!= alter Wert`). Siehe
[../02-testfall-schreiben/06-coverage-regeln.md](../02-testfall-schreiben/06-coverage-regeln.md).

## Das Testlauf-Feld

Oben rechts auf der Seite ist ein Link zurück zum `jbe_testrun` — so
wechselst du zwischen "Detail eines Einzeltests" und "Überblick aller
Tests des Runs".

## Kann ich einen fehlgeschlagenen Test nochmal laufen lassen?

Nicht direkt auf dem alten Run. Lege einfach einen neuen `jbe_testrun`
an, Filter auf die fehlgeschlagene TestID, `keeprecords: true` wenn du
den Zustand hinterher inspizieren willst.
