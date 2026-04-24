# Testdaten-Konventionen

Testdaten sollen sofort als Testdaten erkennbar sein, nicht mit echten
Daten kollidieren und beim Cleanup eindeutig gefunden werden koennen.
Diese Konventionen machen das moeglich — und sie sind nicht verhandelbar,
sondern durch `{GENERATED:*}`-Platzhalter bereits vorgegeben.

## Die 4 Grundregeln

1. **Prefix "JBE Test"** in allen generierten Namen und Titeln
2. **`example.com` / `pruef.invalid`** in allen E-Mail-Adressen und URLs
3. **`555-xxxx`** in allen Telefonnummern
4. **`{TIMESTAMP}`** in mindestens einem Feld pro Record (fuer Eindeutigkeit)

## Prefix "JBE Test"

Jeder vom Test erzeugte Record traegt das Prefix **"JBE Test"** im
Primary-Name-Feld:

```json
"fields": { "name": "JBE Test Firma {TIMESTAMP}" }
```

Das bedeutet fuer jede Entity:

| Entity | Feld | Beispiel |
|---|---|---|
| accounts | `name` | `JBE Test Firma 2026-04-24T...` |
| contacts | `firstname` + `lastname` | `JBE Test Anna`, `JBE Test Meier-2026...` |
| leads | `subject` | `JBE Test Anfrage 2026-...` |
| opportunities | `name` | `JBE Test Deal 2026-...` |
| tasks | `subject` | `JBE Test Follow-up 2026-...` |

**`{GENERATED:firstname}` und `{GENERATED:lastname}`** haengen das Prefix
automatisch vor. `{GENERATED:company}` ebenfalls. Wenn du diese Platzhalter
verwendest, ist das Prefix automatisch gesetzt.

**Warum?** 

- Dein DevOps-Team kann jederzeit Testdaten finden: `name contains "JBE Test"`.
- Falls ein Test schiefgeht und Records uebrig bleiben, sind sie
  identifizierbar.
- Nutzer in DEV sehen sofort, dass das Testdaten sind und nicht echte.

## E-Mails mit @example.com

```json
"emailaddress1": "{GENERATED:email}"
// -> anna.meier8273@example.com
```

**Warum `example.com`?** Die Domain ist per RFC 2606 fuer Dokumentations-
zwecke reserviert. Es gibt dort keine echten Postfaecher. Ein Testdaten-
Konflikt mit einem echten Kunden ist ausgeschlossen.

**Alternative: `.invalid`-Domain** (z.B. `pruef.invalid`). Auch RFC-sicher
und oft fuer Test-Daten verwendet. Beide funktionieren.

**Niemals verwenden:**

- `.com`, `.de`, `.org` ohne `example` / `test`: koennte echte Domain sein.
- `gmail.com`, `gmx.de`, `outlook.com`: Adressen koennten echt sein.
- Firmen-interne Domains wie `juerg@eigenefirma.de`: darf nicht in
  Testdaten auftauchen.

## Telefonnummern mit 555

```json
"telephone1": "{GENERATED:phone}"
// -> 555-4728
```

Die Vorwahl `555` ist in US-Film- und TV-Konventionen als "nicht echt"
etabliert. In Deutschland funktioniert auch `+49 555 xxx` oder
`555-XXXX` (Duplizitaet ausgeschlossen).

## Eindeutigkeit mit {TIMESTAMP}

Fast jeder Test sollte in mindestens einem Feld `{TIMESTAMP}` enthalten,
damit mehrere parallele Runs oder Wiederholungen desselben Tests nicht
auf Uniqueness-Constraints stolpern.

**Empfehlung: im Primary-Name-Feld:**

```json
"name":    "JBE Test Firma {TIMESTAMP}"
"subject": "JBE Test Anfrage {TIMESTAMP}"
```

Das macht jeden Record eindeutig UND du erkennst im Datenbestand, wann
er erzeugt wurde.

## Was passiert wenn Konventionen verletzt werden

- **Kein Prefix:** schwer zu unterscheiden von echten Records. Cleanup
  manuell problematisch. Risiko in PROD.
- **Echte E-Mail-Domain:** bei Plugin-Tests die E-Mails versenden kann
  tatsaechlich eine Mail an den Empfaenger gehen. Ungut.
- **Echte Telefonnummer:** einige D365-Customizations wirken auf
  Telefonfeld (z.B. Dial-Out-Plugins).
- **Fehlender TIMESTAMP:** Tests kollidieren bei parallelen Runs.

## Fuer projektspezifische Test-IDs: eigene Konventionen

Wenn dein Projekt mehrere Testsuiten hat, konventionell sind kurze
Praefixe:

| Praefix | Bedeutung |
|---|---|
| `QS-*` | Quickstart (Einstiegstests) |
| `STD-*` | Standard-CRUD-Tests |
| `E2E-*` | End-to-End-Integration |
| `REZ-*` | Rezepte aus diesem Handbuch |
| `MTC-*` | Merge-Test-Cases (projektspezifisch Markant) |

Die Praefixe helfen beim Filtern (`category:STD*` oder `STD*` als
Filter-Value im Testrun).

## Record-Cleanup — verlass dich auf's Framework

Das Test Center raeumt nach jedem Lauf automatisch auf (Record-Tracker
sammelt alle `CreateRecord`-Ergebnisse, loescht sie am Ende in umgekehrter
Reihenfolge). Du brauchst keinen eigenen Cleanup-Step.

Ausnahmen:

- `jbe_keeprecords: true` am Testrun: Daten bleiben zum Debuggen.
- Plugin-erzeugte Records die nicht im Tracker landen: werden nicht
  geloescht. Das ist aus Sicht des Tests OK, weil sie meist
  `keeprecords=true` auf dem Folge-Owner haben oder eigene Cleanup-Logik.

## Anti-Patterns

**Real wirkende Testdaten:**

```json
"name":          "Siemens AG"                 // nein! klingt echt
"emailaddress1": "sven.m@siemens.com"         // nein! klingt echt
"telephone1":    "089-12345678"               // nein! klingt echt
```

**Hart-codierte Namen ohne TIMESTAMP:**

```json
"name": "JBE Test Firma A"                    // Kollisionen bei parallelem Run
"subject": "Test-Lead"                        // Kollisionen
```

**Umlaute oder Sonderzeichen absichtlich einbauen:**

Umlaute sind erlaubt und sogar gut (Encoding-Tests!). Aber:

```json
"name": "Test \"mit\" Anfuehrungszeichen"     // JSON-Escape noetig
"name": "Test mit\nUmbruch"                   // geht, aber selten sinnvoll
```

Generell: Schreib die Testdaten so, dass ein Kollege dich nicht hassen
wird wenn er in drei Monaten den Test debuggt.

## Konventions-Cheatsheet

```
+--- Testdaten-Konventionen ---------------------+
|                                                |
|  Prefix:        JBE Test                       |
|  E-Mail:        {GENERATED:email}              |
|                 -> ...@example.com             |
|  Telefon:       {GENERATED:phone}              |
|                 -> 555-xxxx                    |
|  Eindeutigkeit: {TIMESTAMP} in name oder       |
|                 subject                        |
|                                                |
|  Praefixe:      QS- STD- E2E- REZ-             |
|                                                |
|  Cleanup:       automatisch (Record-Tracker)   |
|                                                |
+------------------------------------------------+
```
