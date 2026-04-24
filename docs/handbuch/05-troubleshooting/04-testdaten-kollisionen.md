# Testdaten-Kollisionen

Zwei Tests laufen gleichzeitig (oder derselbe Test zweimal), und pl&ouml;tzlich
scheitern beide. Die Ursache ist meist: **die Testdaten sind nicht
eindeutig**.

## Warum Kollisionen auftreten

Dataverse hat Constraints die Eindeutigkeit erzwingen:

- **Alternate Keys** (z.B. `emailaddress1` auf `contact` kann alternate
  key sein) — zwei Records mit demselben Wert gehen nicht.
- **Business-Plugins** die aktiv Duplicate-Detection machen (DQS,
  Merge-Plugins, etc.) — blockieren einen Create wenn ein Aehnlicher
  existiert.
- **Eindeutige AutoNumber-Felder** die nicht explizit gesetzt werden —
  kein Kollisionsproblem, aber nicht-deterministisch.

## Der Standard-Fix: `{TIMESTAMP}`

In jeder `CreateRecord`-Action mindestens ein Feld mit `{TIMESTAMP}`
einbauen — am besten das Primary-Name-Feld:

```json
"fields": {
  "firstname": "{GENERATED:firstname}",
  "lastname":  "Meier_{TIMESTAMP}",
  "emailaddress1": "anna_{TIMESTAMP}@example.com"
}
```

`{TIMESTAMP}` wird pro `CreateRecord`-Aufruf einzigartig (Genauigkeit
Sekunden, in der Praxis reicht das fuer alle parallelen Szenarien).

## Symptome wenn Testdaten nicht eindeutig sind

### Symptom 1: "Duplicate found"

```
HTTP 400: A record with the same alternate key 'emailaddress1'='anna.meier@example.com'
already exists.
```

-> E-Mail-Feld eindeutiger machen:

```json
"emailaddress1": "{GENERATED:email}"
```

### Symptom 2: "Merge blocked"

```
Plugin 'XYZDuplicateCheck' on Create of contact:
Contact already exists with same name and email.
```

-> Ein Business-Plugin blockt Duplikate. Generierte Namen mit Prefix
nutzen:

```json
"firstname": "{GENERATED:firstname}"
"lastname":  "{GENERATED:lastname}"
```

Beide Platzhalter geben "JBE Test" + Zufallsteil.

### Symptom 3: Test findet mehr Records als erwartet

```
Assert target=Query, operator=RecordCount, value=1
Tatsaechlich: 3
```

-> Deine Query trifft auch Records die **andere Testlaeufe** gerade
haben. Filter enger machen:

```json
// Frueher (findet zu viel):
"filter": [ { "field": "emailaddress1", "operator": "contains", "value": "test" } ]

// Besser (eindeutig pro Lauf):
"filter": [ { "field": "emailaddress1", "operator": "eq",
             "value": "{con.fields.emailaddress1}" } ]
```

### Symptom 4: Test blockt sich selbst beim Cleanup

Wenn `{TIMESTAMP}` nicht eindeutig genug ist und mehrere parallele Runs
den gleichen Wert treffen, versucht der Cleanup den gleichen Record
zweimal zu loeschen — was manchmal in 404 endet.

-> Aktuelle `{TIMESTAMP}` ist nicht das Problem; sie enthaelt die
Sekunde. Solange Tests nicht exakt im selben Sekundenbereich starten,
ist alles gut.

## Parallele Testlaeufe

Mehrere Runs gleichzeitig starten:

- Jeder Run bekommt eine eigene Engine-Instanz
- `{TIMESTAMP}` wird pro Step berechnet — nicht pro Run
- Tests kollidieren **nicht** solange sie `{TIMESTAMP}` in ihren Daten
  haben

**Anti-Pattern:**

```json
"fields": { "name": "JBE Test Firma A" }         // parallele Runs kollidieren
"fields": { "name": "JBE Test Firma {TIMESTAMP}" } // parallele Runs sind isoliert
```

## Derselbe Test zweimal hintereinander

Fall 1: **Der erste Run hat `keeprecords: true`.** Dann sind die
Testdaten noch da. Der zweite Run legt neue an mit anderer
`{TIMESTAMP}` — kein Problem.

Fall 2: **Erster Run Cleanup fehlgeschlagen.** Records hinterlassen.
Naechster Run laeuft mit neuer `{TIMESTAMP}` -> keine Kollision. Der
Muell bleibt aber, muesst manuell aufraeumen.

Fall 3: **Beide Runs exakt parallel gestartet.** `{TIMESTAMP}` kann
bei extrem schnellen Abfolgen auf die selbe Sekunde fallen. Unwahr-
scheinlich, aber moeglich.

Workaround: zusaetzlich noch `{GENERATED:guid}` kombinieren:

```json
"fields": {
  "name":          "JBE Test Firma {TIMESTAMP}_{GENERATED:guid}",
  "description":  "Lauf-ID {GENERATED:guid}"
}
```

Das garantiert Eindeutigkeit, auch bei exakt parallelem Start.

## Tests mit hart-codierten Werten

Manchmal braucht man bestimmte Festwerte (z.B. ein OptionSet):

```json
"fields": {
  "statuscode": 1,       // Festwert ist OK
  "some_field_fixed": "KONSTANTE"
}
```

Wichtig: **Primary-Name und Lookups immer eindeutig machen**. Festwerte
fuer reine Datentyp-Felder (OptionSet, Bool, Integer) sind kein Problem.

## Sanity-Check deines Tests

Nach dem Schreiben eines Tests:

```
[ ] Primary-Name-Feld enthaelt {TIMESTAMP}
[ ] E-Mail-Adressen enthalten {TIMESTAMP} oder {GENERATED:email}
[ ] Filter-Queries nutzen eindeutige Werte (alias.id, Pattern mit TIMESTAMP)
[ ] Kein hart-codierter Name (wie "Test Anna") fuer Records die Alternate
    Keys oder Duplicate-Detection triggern
```

Wenn das OK ist, kann der Test parallel mit anderen laufen.

## Diagnose: "Ist meine Testdaten-Kollision das Problem?"

Wenn dein Test mal laeuft, mal nicht:

1. `jbe_keeprecords: true` setzen.
2. Run einmal starten.
3. Im Browser die erzeugten Records anschauen.
4. Dieselbe Run-Konfiguration nochmal starten.
5. Schauen welche Records beim zweiten Run noch existieren aus Run 1.

Wenn Run 2 auf den Records von Run 1 basiert (z.B. E-Mail-Duplikat),
kommt dein Kollisions-Problem daher.
