# Steps-Tab verstehen

Der Steps-Tab eines Testergebnisses ist dein wichtigstes Debug-Werkzeug.
Jede Action deines Tests steht hier als Zeile mit Status, Dauer und
Detail-Feldern.

## Das typische Erscheinungsbild

```
+-- Testschritte von RR-001724 ---------------------------------------+
|                                                                     |
| [Filter: alle v]  [Sortierung: Schrittnummer v]  [Spalten anpassen] |
|                                                                     |
| +----+----------------+---------+-------+-----------+--------------+ |
| | #  | Action         | Alias   | Ergb. | Dauer ms  | Details      | |
| +----+----------------+---------+-------+-----------+--------------+ |
| | 1  | CreateRecord   | acc     | OK    | 412       |              | |
| | 2  | CreateRecord   | con     | OK    | 385       |              | |
| | 3  | UpdateRecord   | con     | OK    | 189       |              | |
| | 4  | Assert         |         | OK    | 15        | field: ...   | |
| | 5  | Assert         |         | FAIL  | 23        | expected: .. | |
| |    |                |         |       |           | actual: ...  | |
| | 9000| Cleanup       |         | OK    | 450       |              | |
| +----+----------------+---------+-------+-----------+--------------+ |
+---------------------------------------------------------------------+
```

**Sortierung ist immer nach `stepNumber`.** Cleanup steht mit `9000`
deswegen am Ende.

## Die Spalten im Detail

| Spalte | Technischer Feldname | Bedeutung |
|---|---|---|
| **#** | `jbe_stepnumber` | Aus dem JSON übernommen |
| **Action** | `jbe_action` | `CreateRecord`, `Assert`, ... |
| **Alias** | `jbe_alias` | Aus dem JSON, wenn gesetzt |
| **Entity** | `jbe_entity` | Ziel-Entity der Action |
| **Ergebnis** | `jbe_stepstatus` | `Erfolg`, `Fehler`, `Uebersprungen` |
| **Dauer** | `jbe_durationms` | Millisekunden |
| **Fehlermeldung** | `jbe_errormessage` | Bei Fehler: was war los |
| **Details** | mehrere Felder | Siehe unten |

## Ein Step im Detail

Klick eine Zeile an um das Step-Detail zu öffnen:

```
+-- Testschritt: Assert #5 -------------------------------------+
|                                                               |
|  Schrittnummer  5                                             |
|  Action         Assert                                        |
|  Alias          (leer)                                        |
|  Entity         accounts                                      |
|  Ergebnis       [ Fehler                   v ]                |
|  Dauer          23 ms                                         |
|                                                               |
|  Assertion-Details                                            |
|  +--------------------------------------------------------+   |
|  |  Feld         websiteurl                               |   |
|  |  Operator     Equals                                   |   |
|  |  Erwartet     https://example.com                      |   |
|  |  Tatsächlich https://example.de                       |   |
|  +--------------------------------------------------------+   |
|                                                               |
|  Fehlermeldung                                                |
|  +--------------------------------------------------------+   |
|  | Website wurde korrekt gesetzt                          |   |
|  |                                                        |   |
|  | Assert fehlgeschlagen: erwartet 'https://example.com', |   |
|  | bekommen 'https://example.de'                          |   |
|  +--------------------------------------------------------+   |
|                                                               |
|  Record-Referenz                                              |
|  > 3f2a1b4e-9c27-40d1-b9a2-0e5fa2c4a1d3  [Record öffnen]     |
|                                                               |
|  Input-Daten (JSON)                                           |
|  +--------------------------------------------------------+   |
|  | { "target": "Record", ... }                            |   |
|  +--------------------------------------------------------+   |
|                                                               |
+---------------------------------------------------------------+
```

## Die Assertion-Details

Nur bei `Assert`-Steps gefüllt:

- **Feld** (`jbe_assertionfield`) — was wurde geprüft
- **Operator** (`jbe_assertionoperator`) — welche Vergleichslogik
- **Erwartet** (`jbe_expectedvalue`) — was sollte drinstehen
- **Tatsächlich** (`jbe_actualvalue`) — was war's wirklich

Die Differenz zwischen **Erwartet** und **Tatsächlich** ist deine
wichtigste Debug-Information.

## Die Record-Referenz (`jbe_recordid`)

Bei CreateRecord / UpdateRecord-Steps oft gefüllt: die GUID des
erzeugten/bearbeiteten Records, plus ein klickbarer Link.

**Super nützlich mit `jbe_keeprecords: true`:** du kannst direkt in
den tatsächlichen Record springen, den der Test erzeugt hat, und ihn
dir anschauen.

## Input- und Output-Daten

- **Input-Daten** (`jbe_inputdata`) — das Roh-JSON das diese Action
  ausgelöst hat (mit aufgelösten Platzhaltern).
- **Output-Daten** (`jbe_outputdata`) — die Antwort vom Server (bei
  Bedarf gekürzt).

Die meisten Tests brauchen diese Felder nie. Bei tief verschachtelten
Problemen (Custom-Action-Aufrufe mit ungewöhnlichen Parametern) helfen
sie beim Debuggen.

## Das Stepstatus-Symbol

Die Engine schreibt:

| Status | Was es heißt |
|---|---|
| **Erfolg** | Action erfolgreich durchgelaufen. Bei Assert: Prüfung bestanden |
| **Fehler** | Action hat einen Fehler geworfen. Bei Assert: Prüfung nicht bestanden |
| **Übersprungen** | Der Step wurde nicht ausgeführt (z.B. nach vorherigem Abbruch mit `onError=stop`) |

## Den Test über den Step-Tab "lesen"

Ein erfahrener Leser überfliegt den Step-Tab so:

1. **Alle Steps auf Erfolg?** Super, Test ist sauber.
2. **Welcher Step hat gekippt?** — oft gibt es nur einen Assert-Fehler,
   alle anderen sind OK.
3. **Was war Erwartet vs Tatsächlich?** — hier kommt die Diagnose her.
4. **Dauerte der Step ungewöhnlich lange?** — Hinweis auf Timing-
   Probleme.

## Dauern pro Action-Typ (Erfahrungswerte)

| Action | Typische Dauer |
|---|---|
| `CreateRecord` (einfach) | 100-500 ms |
| `CreateRecord` (mit Plugin-Kette) | 500-3000 ms |
| `UpdateRecord` | 80-200 ms |
| `DeleteRecord` | 80-200 ms |
| `ExecuteRequest QualifyLead` | 2000-5000 ms (wegen Plugin-Chain) |
| `ExecuteRequest Merge` | 30000-60000 ms (wegen Async-Reparent) |
| `Assert target=Record` | 10-30 ms |
| `Assert target=Query` | 30-100 ms |
| `Wait` | exakt der eingestellte Wert |
| `WaitForFieldValue` | variable; so lange das Polling dauert |

**Ausreißer**: ein `CreateRecord` von 5000ms deutet auf ein langsames
Plugin hin. Ein `Assert` mit 500ms auf Netzwerk-Probleme.

## Eigene Sicht / eigene View

Die Steps-Liste ist eine Standard-Sub-Grid. Du kannst:

- Eine **eigene View** auf `jbe_teststep` anlegen, z.B. "Nur meine Fails
  heute" mit entsprechenden Filtern.
- Die Spalten in der Ergebnis-Detail-Sub-Grid anpassen (Projekt-Owner-
  Setting).

Das ist Dynamics-Standard.
