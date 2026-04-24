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
| **#** | `jbe_stepnumber` | Aus dem JSON uebernommen |
| **Action** | `jbe_action` | `CreateRecord`, `Assert`, ... |
| **Alias** | `jbe_alias` | Aus dem JSON, wenn gesetzt |
| **Entity** | `jbe_entity` | Ziel-Entity der Action |
| **Ergebnis** | `jbe_stepstatus` | `Erfolg`, `Fehler`, `Uebersprungen` |
| **Dauer** | `jbe_durationms` | Millisekunden |
| **Fehlermeldung** | `jbe_errormessage` | Bei Fehler: was war los |
| **Details** | mehrere Felder | Siehe unten |

## Ein Step im Detail

Klick eine Zeile an um das Step-Detail zu oeffnen:

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
|  |  Tatsaechlich https://example.de                       |   |
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
|  > 3f2a1b4e-9c27-40d1-b9a2-0e5fa2c4a1d3  [Record oeffnen]     |
|                                                               |
|  Input-Daten (JSON)                                           |
|  +--------------------------------------------------------+   |
|  | { "target": "Record", ... }                            |   |
|  +--------------------------------------------------------+   |
|                                                               |
+---------------------------------------------------------------+
```

## Die Assertion-Details

Nur bei `Assert`-Steps gefuellt:

- **Feld** (`jbe_assertionfield`) — was wurde geprueft
- **Operator** (`jbe_assertionoperator`) — welche Vergleichslogik
- **Erwartet** (`jbe_expectedvalue`) — was sollte drinstehen
- **Tatsaechlich** (`jbe_actualvalue`) — was war's wirklich

Die Differenz zwischen **Erwartet** und **Tatsaechlich** ist deine
wichtigste Debug-Information.

## Die Record-Referenz (`jbe_recordid`)

Bei CreateRecord / UpdateRecord-Steps oft gefuellt: die GUID des
erzeugten/bearbeiteten Records, plus ein klickbarer Link.

**Super nuetzlich mit `jbe_keeprecords: true`:** du kannst direkt in
den tatsaechlichen Record springen, den der Test erzeugt hat, und ihn
dir anschauen.

## Input- und Output-Daten

- **Input-Daten** (`jbe_inputdata`) — das Roh-JSON das diese Action
  ausgeloest hat (mit aufgeloesten Platzhaltern).
- **Output-Daten** (`jbe_outputdata`) — die Antwort vom Server (bei
  Bedarf gekuerzt).

Die meisten Tests brauchen diese Felder nie. Bei tief verschachtelten
Problemen (Custom-Action-Aufrufe mit ungewoehnlichen Parametern) helfen
sie beim Debuggen.

## Das Stepstatus-Symbol

Die Engine schreibt:

| Status | Was es heisst |
|---|---|
| **Erfolg** | Action erfolgreich durchgelaufen. Bei Assert: Pruefung bestanden |
| **Fehler** | Action hat einen Fehler geworfen. Bei Assert: Pruefung nicht bestanden |
| **Uebersprungen** | Der Step wurde nicht ausgefuehrt (z.B. nach vorherigem Abbruch mit `onError=stop`) |

## Den Test ueber den Step-Tab "lesen"

Ein erfahrener Leser ueberfliegt den Step-Tab so:

1. **Alle Steps auf Erfolg?** Super, Test ist sauber.
2. **Welcher Step hat gekippt?** — oft gibt es nur einen Assert-Fehler,
   alle anderen sind OK.
3. **Was war Erwartet vs Tatsaechlich?** — hier kommt die Diagnose her.
4. **Dauerte der Step ungewoehnlich lange?** — Hinweis auf Timing-
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

**Ausreisser**: ein `CreateRecord` von 5000ms deutet auf ein langsames
Plugin hin. Ein `Assert` mit 500ms auf Netzwerk-Probleme.

## Eigene Sicht / eigene View

Die Steps-Liste ist eine Standard-Sub-Grid. Du kannst:

- Eine **eigene View** auf `jbe_teststep` anlegen, z.B. "Nur meine Fails
  heute" mit entsprechenden Filtern.
- Die Spalten in der Ergebnis-Detail-Sub-Grid anpassen (Projekt-Owner-
  Setting).

Das ist Dynamics-Standard.
