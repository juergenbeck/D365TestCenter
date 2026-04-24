# Fehleranalyse

Systematisches Vorgehen wenn ein Test nicht tut was du erwartest. Dieses
Dokument ist kein Fehlerkatalog sondern **ein Denkwerkzeug**: wie
gehe ich vor, wenn Test X rot ist?

## Die erste Frage: Failed oder Error?

Bevor du irgendetwas anderes machst, guck auf **`jbe_outcome`** des
Testergebnis-Records:

| Outcome | Was es bedeutet | Erster Verdacht |
|---|---|---|
| **Failed** | Alle Actions liefen, aber mindestens ein Assert war nicht OK | Test-Erwartung oder Testdaten-Design |
| **Error** | Eine Action hat geworfen, der Test wurde abgebrochen | JSON-Syntax, Permissions, Plugin-Problem |
| **Skipped** | Test konnte nicht starten | JSON-Parse |

Diese drei sind sehr unterschiedliche Bugs. Verwechsle sie nicht.

## Failed — die Asserts stimmen nicht

### Schritt 1: Welcher Assert?

Öffne den Steps-Tab, sortiere oder filtere auf `jbe_stepstatus = Fehler`.
Pro rote Zeile:

- Lies die `description` — was war die Erwartung fachlich?
- Lies `Erwartet` und `Tatsächlich`.

### Schritt 2: Erwartet vs Tatsächlich — vier typische Muster

**Muster A: Timing**

```
Erwartet:     1
Tatsächlich: null (oder 0, oder der Default-Wert)
```

Der Wert sollte vom Plugin gesetzt worden sein, war aber noch nicht.
Lösung: `WaitForFieldValue` einbauen, Timeout erhöhen.

**Muster B: Test-Erwartung veraltet**

```
Erwartet:     288260001
Tatsächlich: 288260003
```

Das Plugin setzt jetzt einen anderen Wert als der Test annimmt. Im Code
wurde etwas geändert, der Test wurde nicht nachgezogen.

**Muster C: Typ-/Format-Fehler**

```
Erwartet:     5000
Tatsächlich: 5000.0000
```

Money-Felder kommen als Decimal-Strings zurück. Im Test:
`"value": "5000.0000"` oder `"value": "5000.00"` — je nachdem was
Dataverse gerade zurückgibt. Tipp: erst manuell ohne Assert laufen
lassen, `Tatsächlich` auslesen, im Test nachtragen.

**Muster D: Lookup-Format**

```
Erwartet:     {RECORD:acc}  -> GUID
Tatsächlich: null
```

Du vergleichst vermutlich `parentcustomerid` als Record-Assert. Im
`target: Record` funktioniert das aber anders als im Filter. Umstellen
auf `target: Query` und im Filter `parentcustomerid eq <guid>`.

### Schritt 3: Die Daten prüfen

Wenn du unsicher bist was im Dataverse tatsächlich drin steht: lass
den Test mit `jbe_keeprecords: true` nochmal laufen. Danach kannst du
die Records direkt im Browser anschauen und vergleichen.

Shortcut: Im Steps-Tab beim CreateRecord-Step ist die GUID mit einem
Klick-Link verfügbar. Öffnen, nachschauen.

## Error — eine Action ist geworfen

### Schritt 1: Welche Action war's?

Im Steps-Tab: die **erste** rote Zeile mit Status `Fehler`. Alles danach
ist "Übersprungen".

### Schritt 2: Die Fehlermeldung lesen

Die `jbe_errormessage` des Steps ist dein Schlüssel. Häufige Muster:

**HTTP 400 Bad Request:**

```
HTTP 400: attribute 'someunknown' does not exist on type accounts.
```

-> Tippfehler im `fields`-Objekt. Attribute-Name prüfen.

**HTTP 400 bei Lookup:**

```
HTTP 400: expected guid format.
```

-> Lookup-Binding falsch formatiert. Siehe
[../02-testfall-schreiben/04-lookup-und-binding.md](../02-testfall-schreiben/04-lookup-und-binding.md).

**HTTP 403 Forbidden:**

```
HTTP 403: Principal user does not have the required permission.
```

-> Dein Service-User hat nicht die nötigen Privilegien für die Entity.
Projekt-Owner fragen.

**Plugin-Exception:**

```
Plugin 'SomeName' on Create of 'contact' wirft:
  Business rule XYZ violated: Email already exists.
```

-> Anderes Plugin hat deinen Create blockiert. Testdaten anpassen
(Email eindeutiger machen mit `{TIMESTAMP}`).

**Alias nicht gefunden:**

```
Alias 'con' existiert nicht oder wurde nicht erfolgreich registriert.
```

-> Tippfehler beim Alias oder der erste CreateRecord ist gescheitert
(dann gibt es den Alias nicht zum Referenzieren).

**`WaitForFieldValue` Timeout:**

```
WaitForFieldValue: Feld 'statecode' hat Wert 0 nach 30 Sekunden erreicht.
Erwartet war 1.
```

-> Das Plugin hat das Feld in der Wartezeit nicht gesetzt. Entweder:
Timeout erhöhen, oder Plugin ist gerade kaputt.

### Schritt 3: Fix einspielen und erneut laufen lassen

Einen neuen Testrun anlegen mit dem gleichen Filter. Nicht am alten
Testrun herumbiegen — ein neuer Run ist ein neuer Audit-Eintrag.

## Skipped — der Test konnte nicht mal starten

### Ursache 1: JSON-Syntax

```
JSON-Parse-Fehler: line 15, col 3: unexpected token
```

Testfall öffnen, JSON rauskopieren, in VS Code validieren. Typische
Sünder:

- Fehlendes Komma nach einem Step-Objekt
- Einfache statt doppelte Anführungszeichen
- Trailing Comma nach letztem Array-Element
- Kommentare `//` (nicht erlaubt in JSON)

### Ursache 2: enabled = false

Wenn der Testfall-Record `jbe_enabled = nein` hat, wird er vom Run
übersprungen. Bei Filter `*` ohne Hit auf einen Test mit `jbe_enabled`=
`ja` landet `Gesamt = 0` — dann existiert gar kein Result-Record.

### Ursache 3: Pflichtfelder fehlen

Wenn dein JSON z.B. `testId` nicht enthält, parsed der Testcase zwar
als JSON, aber die Engine kann nichts damit anfangen. Ergebnis: Skipped
mit entsprechender Meldung.

## Der Entscheidungsbaum als Pseudo-Flow

Siehe
[../05-troubleshooting/01-entscheidungsbaum.md](../05-troubleshooting/01-entscheidungsbaum.md)
für den strukturierten Ablauf.

## Wann es NICHT am Test liegt

Manchmal ist der Test vollkommen OK und das Problem ist eine Umgebungs-
Instabilität:

- **Ein anderer User hat Records auf der DEV-Umgebung beeinflusst.**
  Typisch: Bug in einem Business-Plugin, der durch parallele Arbeit
  ausgelöst wurde.
- **Die Umgebung hat Hiccups.** Dataverse hatte kurze Outages, das
  CRUD-Trigger-Plugin hängt. Erneut laufen lassen hilft.
- **Sandbox-Resource-Limit.** Plugin-Sandbox-Pool war erschöpft. Warte
  1-2 Minuten, erneut starten.

**Erster Sanity-Check** bei allen mysteriösen Fehlern: den Test **zum
zweiten Mal** laufen lassen, und **einen bekanntermaßen grünen Test
gegenprüfen**. Wenn auch der rot ist, liegt es nicht an deinem Code.

## Kleine Checkliste als Anker

```
+--- Fehleranalyse-Reihenfolge ----------------------+
|                                                    |
|  1. Testrun-Status = Abgeschlossen?                |
|     (sonst: warte / Infra-Problem)                 |
|                                                    |
|  2. outcome des Testrunresults?                    |
|     Failed  -> zu Step 4                           |
|     Error   -> zu Step 5                           |
|     Skipped -> JSON validieren                     |
|                                                    |
|  3. (entfällt, nur Gliederung)                    |
|                                                    |
|  4. Welcher Assert?                                |
|     Erwartet vs Tatsächlich abgleichen            |
|     Timing? Veraltet? Format?                      |
|                                                    |
|  5. Welche Action?                                 |
|     HTTP-Code?                                     |
|     JSON-Syntax im Step-Input?                     |
|     Plugin-Exception?                              |
|                                                    |
|  6. Als letzter Schritt: gegen-prüfen auf         |
|     einem bekannten grünen Test                   |
|                                                    |
+----------------------------------------------------+
```
