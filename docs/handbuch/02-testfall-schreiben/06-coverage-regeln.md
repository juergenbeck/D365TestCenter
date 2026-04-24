# Coverage-Regeln: Wie viele Asserts sind genug?

Ein Test mit zwei Asserts ist nicht automatisch schlechter als einer mit
zehn — aber oft uebersieht man wichtige Erwartungen. Dieses Dokument
beschreibt Regeln und Heuristiken, damit deine Tests das pruefen was sie
pruefen sollen.

## Regel 1: Leitfrage — was wuerde ich manuell pruefen?

Bevor du Asserts schreibst, frag dich: **"Wenn ich diesen Test manuell im
Browser ausfuehren wuerde — was wuerde ich mir danach alles ansehen, bevor
ich ihn als bestanden abzeichne?"**

Jede Sichtpruefung ist eine Assertion.

Beispiel fuer "Lead qualifizieren":

- Der Lead ist deaktiviert (statecode=1). -> Assert
- Im Dashboard ist ein neuer Contact. -> Assert
- Im Dashboard ist eine neue Opportunity. -> Assert
- Der Contact hat die richtige E-Mail. -> Assert
- Der Contact zeigt auf den richtigen Parent-Account. -> Assert
- Die Opportunity hat den Status "Offen". -> Assert

Das sind sechs Asserts. Viele Tests in der Praxis haben nur drei, weil
die letzten drei "wirken auch so OK" wirken. Tu das nicht.

## Regel 2: Pro Operation mindestens 2-3 Asserts

| Operation | Mindest-Asserts |
|---|---|
| `CreateRecord` | 2-3: (a) Record existiert, (b) wichtigste Felder korrekt |
| `UpdateRecord` | 1 pro geaendertem Feld + 1 fuer Felder die NICHT geaendert werden sollen |
| `DeleteRecord` | 1: Record existiert nicht mehr (`NotExists`) |
| Status-Change | 2: statecode + statuscode jeweils mit neuem Wert |
| `ExecuteRequest` (Plugin-Kette) | 1 pro erwartetem Side-Effect + 1 pro nicht erwarteten |

"Wichtigste Felder" heisst nicht alle Felder — nur die, die fachlich
bedeutsam sind. Bei einem Contact-Create fuer einen Invoicing-Test wuerde
ich `firstname`, `lastname`, `emailaddress1`, `parentcustomerid` pruefen,
aber nicht `telephone2` oder `middlename`.

## Regel 3: Positive UND negative Erwartungen

Pruefe nicht nur was **passieren soll**, sondern auch was **nicht** passieren
soll. Das ist der Unterschied zwischen "Test ist nicht explodiert" und
"Feature funktioniert wie erwartet".

**Schlecht (nur positive):**

```json
{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:con1}",
  "field": "markant_goldenrecordid", "operator": "Equals", "value": "GR-42" }
```

**Gut (positive + negative):**

```json
// positive Erwartung
{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:con1}",
  "field": "markant_goldenrecordid", "operator": "Equals", "value": "GR-42",
  "description": "Golden-Record-ID transferiert", "onError": "continue" },

// negative Erwartung (FoundMaster wurde geleert)
{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:con1}",
  "field": "markant_foundmaster", "operator": "IsNull",
  "description": "FoundMaster wurde nach Transfer geleert", "onError": "continue" },

// negative Erwartung (Duplikat deaktiviert)
{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:con2}",
  "field": "statecode", "operator": "Equals", "value": "1",
  "description": "Duplikat wurde deaktiviert", "onError": "continue" }
```

Der zweite Test faengt Bugs, die der erste uebersieht: z.B. wenn das
Plugin die Golden-Record-ID kopiert aber nicht die Originaldaten aufraeumt,
oder wenn das Duplikat nicht wirklich deaktiviert wurde sondern nur "so
wirkt".

## Regel 4: Symmetrie bei umgekehrten Szenarien

Wenn Test B die Umkehrung von Test A ist, **muessen die Asserts strukturell
identisch sein** — nur mit vertauschten Rollen.

Beispiel:

- **MGR-04A**: Duplikat wird Master (7 Asserts am Survivor, 4 am Subordinate)
- **MGR-04B**: Master bleibt Master (muss auch 7 am Survivor, 4 am
  Subordinate pruefen — mit vertauschter Perspektive)

Asymmetrische Coverage zwischen Szenario und Umkehrung ist ein Test-
Design-Bug. Der Grund: vertauschte Rollen koennten unterschiedliche Bugs
haben. Nur wenn beide Seiten gleich stark geprueft werden, weisst du, dass
die Symmetrie wirklich gilt.

**Als Review-Pattern:** Zaehle die Asserts beider Tests. Unterschied > 1
ist verdaechtig.

## Regel 5: Record-Alias konsequent nutzen

Wenn ein Record einen Alias hat:

- Steps referenzieren ihn per `alias` oder `recordRef`
- Assertions nutzen `target: "Record"` mit `recordRef: "{RECORD:alias}"`
- Platzhalter wie `{alias.id}` in Lookup-Bindings und Filtern

**Nicht vermischen:** mal Alias, mal hart-codierte GUID, mal Query-Filter
auf Primary Name. Das macht den Test unleserlich und fehleranfaellig.

## Regel 6: description auf jeder Assertion

Jede Assertion braucht eine `description`. Das ist nicht Kosmetik sondern
Dokumentation:

- Wer den Test liest versteht sofort **warum** die Assertion existiert.
- Im Fehlerfall steht die description im Report.
- Bei Test-Failure weiss man sofort welche fachliche Erwartung verletzt
  wurde.

Siehe [05-assertions.md](05-assertions.md#description--warum-sie-wichtig-ist).

## Regel 7: Precondition-Vollstaendigkeit

Ein Test muss **alle notwendigen Vorbedingungen explizit** anlegen.

- Alle referenzierten Parent-Records (Accounts, Contacts) als eigener Step
- Alle Lookup-Ziele als Step
- Alle Sub-Records die Plugin-Logik erwartet
- Alle Feld-Vorbedingungen die das Szenario voraussetzt

**Nicht verlassen auf:** "ist bestimmt schon in der Umgebung da". Solche
Tests laufen nur auf EINER Umgebung gruen und sind damit wertlos.

**Konsequenz:** Der Test ist idempotent und eigenstaendig. Bei
`keeprecords=false` raeumt er sich selbst auf. Wenn du einen Test jeden
Tag 5x laufen lassen kannst ohne dass er Datenmuell hinterlaesst, ist er
sauber.

## Regel 8: Assertions am Ende, nicht dazwischen

Streng genommen ist das optional — Asserts koennen ueberall stehen. Aber
als Konvention und fuer Lesbarkeit:

```
CreateRecord acc
CreateRecord con
CreateRecord cs
ExecuteRequest Merge
Assert ...
Assert ...
Assert ...
Assert ...
```

ist lesbarer als:

```
CreateRecord acc
Assert acc existiert
CreateRecord con
Assert con existiert
CreateRecord cs
Assert cs existiert
ExecuteRequest Merge
Assert ...
```

**Ausnahme: Zwischen-Asserts** sind legitim wenn du einen komplexen
mehrstufigen Flow testest und zwischendurch pruefen willst, dass ein
Zwischenzustand stimmt bevor du weitermachst:

```
CreateRecord lead
ExecuteRequest QualifyLead
Assert lead.statecode == 1           // Zwischen-Assert: qualified
UpdateRecord created_opp
Assert created_opp.estimatedvalue == 50000
```

Hier ist der Zwischen-Assert nicht nur Doku — er ist die Voraussetzung
dass der naechste Step funktioniert. Wenn der Lead nicht qualifiziert
wurde, gibt es keine Opportunity, und das Update wirft einen Fehler. Der
Zwischen-Assert macht klar wo es wirklich haengt.

## Anti-Pattern: Der "Smoke-Test" der nichts pruefe

```json
{ "testId": "STD-01",
  "title": "Account anlegen",
  "steps": [
    { "action": "CreateRecord", "entity": "accounts", "alias": "acc",
      "fields": { "name": "JBE Test" } }
  ]
}
```

Dieser Test prueft nur "Create schlaegt nicht fehl". Aber was wenn der
Account mit falschem OwnerTeam angelegt wird? Was wenn ein Plugin die
Name aendert? Was wenn der statecode anders ist als erwartet? Der Test
faengt nichts davon ab.

**Besser — selbst der einfachste Smoke-Test hat mindestens 2 Asserts:**

```json
{ "testId": "STD-01", "title": "Account anlegen", "steps": [
  { "action": "CreateRecord", "entity": "accounts", "alias": "acc",
    "fields": { "name": "JBE Test {TIMESTAMP}" } },
  { "action": "Assert", "target": "Record", "recordRef": "{RECORD:acc}",
    "field": "name", "operator": "StartsWith", "value": "JBE Test",
    "description": "Name korrekt gespeichert", "onError": "continue" },
  { "action": "Assert", "target": "Record", "recordRef": "{RECORD:acc}",
    "field": "statecode", "operator": "Equals", "value": "0",
    "description": "Account ist aktiv nach Create", "onError": "continue" }
]}
```

## Zusammenfassung

| Regel | Erinnerung |
|---|---|
| 1 | Was wuerde ich manuell pruefen? |
| 2 | 2-3 Asserts pro Operation als Minimum |
| 3 | Positive UND negative Erwartungen |
| 4 | Symmetrie bei umgekehrten Szenarien |
| 5 | Record-Alias konsequent |
| 6 | `description` auf jeder Assertion |
| 7 | Preconditions vollstaendig und explizit |
| 8 | Zwischen-Asserts erlaubt, aber sparsam einsetzen |
