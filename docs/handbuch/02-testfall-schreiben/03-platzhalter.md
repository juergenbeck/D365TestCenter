# Platzhalter

Platzhalter sind die dynamischen Elemente in deinen Testdaten. Sie werden
zur Laufzeit durch echte Werte ersetzt — Record-IDs, Zeitstempel, generierte
Namen. Ohne Platzhalter koenntest du nur statische Tests schreiben.

## Uebersicht

| Platzhalter | Loest auf zu |
|---|---|
| `{TIMESTAMP}` | Aktueller ISO-Timestamp |
| `{TIMESTAMP_MINUS_1H}` | Timestamp vor 1 Stunde |
| `{TIMESTAMP_PLUS_1H}` | Timestamp in 1 Stunde |
| `{alias.id}` | GUID eines per Alias registrierten Records |
| `{alias.fields.xxx}` | Feldwert eines Alias-Records |
| `{RECORD:alias}` | Ebenfalls GUID, aber fuer `recordRef` und Filter-Werte |
| `{GENERATED:firstname}` | Zufallsname mit "JBE Test"-Prefix |
| `{GENERATED:lastname}` | Zufallsname |
| `{GENERATED:email}` | Zufallsmail @example.com |
| `{GENERATED:phone}` | Testnummer `555-xxxx` |
| `{GENERATED:mobile}` | Testnummer `555-xxxx` |
| `{GENERATED:company}` | Zufaelliger Firmenname |
| `{GENERATED:text}` | Zufallstext |
| `{GENERATED:guid}` | Neue GUID |

## TIMESTAMP

Der Klassiker fuer eindeutige Testdaten.

```json
"name": "JBE Test Account {TIMESTAMP}"
```

Loest sich z.B. zu `JBE Test Account 2026-04-24T11:45:32Z` auf.

**Warum ueberhaupt?** Wenn mehrere Testlaeufe parallel laufen (oder du
denselben Test mehrfach ausfuehrst), kollidieren sonst z.B.
AutoNumber-freie Felder oder E-Mail-Uniqueness-Constraints. `{TIMESTAMP}`
macht jeden Testlauf-Datensatz eindeutig.

**Auch in E-Mail-Adressen:**

```json
"emailaddress1": "jbe_test_{TIMESTAMP}@example.com"
```

Loest sich z.B. zu `jbe_test_2026-04-24T11:45:32Z@example.com`.

**Varianten:**

- `{TIMESTAMP}` — jetzt
- `{TIMESTAMP_MINUS_1H}` — vor 1 Stunde, nuetzlich fuer LUW-Tests wenn man
  "aeltere" Quellen simuliert
- `{TIMESTAMP_PLUS_1H}` — in 1 Stunde

Alle liefern ISO-8601-UTC-Timestamps (`2026-04-24T11:45:32Z`).

## Alias-Platzhalter

Nach einem `CreateRecord` kannst du auf den angelegten Record zugreifen:

```json
{ "stepNumber": 1, "action": "CreateRecord", "entity": "accounts",
  "alias": "acc", "fields": { "name": "JBE Test {TIMESTAMP}" } },

{ "stepNumber": 2, "action": "CreateRecord", "entity": "contacts",
  "alias": "con",
  "fields": {
    "firstname": "JBE Test",
    "lastname":  "Kontakt",
    "parentcustomerid_account@odata.bind": "/accounts({acc.id})"
  }
}
```

`{acc.id}` wird zur GUID des vorher angelegten Accounts.

### Zwei Schreibweisen fuer die GUID

```json
"value": "{acc.id}"           // klassisch, funktioniert ueberall
"value": "{RECORD:acc}"       // Alternative, gleicher Effekt
```

Beide liefern die gleiche GUID. `{RECORD:acc}` ist etwas expliziter und
wird bevorzugt in `Assert.recordRef`-Kontexten verwendet. In @odata.bind-
Pfaden funktioniert nur `{acc.id}`.

### Feldwerte per Alias

Wenn du ein Feld eines vorher angelegten Records brauchst:

```json
"value": "{con.fields.emailaddress1}"
```

Das liefert den Wert des `emailaddress1`-Felds aus dem Alias `con`.

**Voraussetzung:** das Feld muss beim `CreateRecord` entweder mitgegeben
oder per `columns` zurueckgelesen worden sein:

```json
{ "action": "CreateRecord", "entity": "contacts", "alias": "con",
  "fields": { "firstname": "Anna", "lastname": "Meier" },
  "columns": ["contactnumber"]
}
```

Danach ist `{con.fields.contactnumber}` die Server-generierte AutoNumber.

## GENERATED-Platzhalter

Fuer realistisch wirkende, aber als Test erkennbare Testdaten.

| Platzhalter | Beispielwert |
|---|---|
| `{GENERATED:firstname}` | `JBE Test Anna` |
| `{GENERATED:lastname}` | `Mustermann5821` |
| `{GENERATED:email}` | `anna.mustermann5821@example.com` |
| `{GENERATED:phone}` | `555-4728` |
| `{GENERATED:mobile}` | `555-9183` |
| `{GENERATED:company}` | `JBE Test Muster GmbH 8273` |
| `{GENERATED:text}` | `Lorem ipsum dolor sit amet, ...` |
| `{GENERATED:guid}` | `3f2a1b4e-9c27-40d1-b9a2-0e5fa2c4a1d3` |

**Wichtig:** `{GENERATED:firstname}` haengt automatisch "JBE Test" vor.
Das ist Absicht — die Konvention macht Testdaten im Dataverse sofort als
Test erkennbar.

## Kombinationen

Du kannst Platzhalter frei kombinieren:

```json
"fields": {
  "emailaddress1":  "{GENERATED:firstname}_{TIMESTAMP}@example.com",
  "mobilephone":    "{GENERATED:mobile}",
  "description":    "Erzeugt von Test {alias_if_exists} am {TIMESTAMP}"
}
```

Das liefert z.B.:

```
emailaddress1:  "JBE Test Anna_2026-04-24T11:45:32Z@example.com"
mobilephone:    "555-9183"
description:    "Erzeugt von Test {alias_if_exists} am 2026-04-24T11:45:32Z"
```

Wenn ein Platzhalter nicht aufgeloest werden kann (z.B. `{alias_if_exists}`
ist ein Tippfehler), bleibt er **woertlich** im String stehen.

## Platzhalter in Filtern

Auch in `Assert.filter` und `WaitForRecord.filter` funktionieren alle
Platzhalter:

```json
"filter": [
  { "field": "accountid",     "operator": "eq", "value": "{acc.id}" },
  { "field": "emailaddress1", "operator": "eq", "value": "{con.fields.emailaddress1}" }
]
```

## Was NICHT geht

- **Arithmetik**: `{acc.id + 1}` — nicht unterstuetzt.
- **Faellt zurueck auf alten Wert**: wenn du den Record zwischendurch
  aenderst, zeigt `{alias.fields.x}` den Wert zum Zeitpunkt des letzten
  `CreateRecord` / `RetrieveRecord`, nicht den aktuellen DB-Stand.
  Fuer aktuelle Werte: `RetrieveRecord` einschieben.
- **Verschachtelte Platzhalter**: `{alias.{name_var}.id}` — nicht
  unterstuetzt.
- **In Zahlen-Feldern**: `"numberofemployees": "{TIMESTAMP_SECONDS}"` geht
  nicht, weil das Feld eine echte Zahl erwartet, und Platzhalter produzieren
  Strings. Workaround: hardcode die Zahl oder nutze `columns` fuer
  dynamische Werte.

## Cheatsheet

```
+--- Platzhalter-Kurzuebersicht ----------------------------+
|                                                           |
|  {TIMESTAMP}             -> 2026-04-24T11:45:32Z          |
|  {TIMESTAMP_MINUS_1H}    -> 2026-04-24T10:45:32Z          |
|                                                           |
|  {acc.id}                -> GUID von Alias 'acc'          |
|  {acc.fields.name}       -> Feldwert 'name' von 'acc'     |
|  {RECORD:acc}            -> GUID von Alias 'acc'          |
|                                                           |
|  {GENERATED:firstname}   -> JBE Test Anna                 |
|  {GENERATED:email}       -> anna@example.com              |
|  {GENERATED:company}     -> JBE Test Muster GmbH          |
|  {GENERATED:guid}        -> neue GUID                     |
|                                                           |
+-----------------------------------------------------------+
```
