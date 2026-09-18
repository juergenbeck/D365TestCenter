# Pitfalls und Plattform-Constraints

Sammlung von Stolperfallen die nicht direkt mit dem Test Center zu tun
haben, sondern mit Dataverse/Dynamics-Plattform-Verhalten. Wichtig zu
kennen, weil Tests sonst rot werden für falsche Gründe.

## State-locked Entity-Creation

Manche Standard-Entities erlauben **`CreateRecord` nur in einem
definierten Default-State**. Status-Transitionen zu anderen Werten
müssen per nachfolgendem `UpdateRecord` passieren.

### Betroffene Entities (Auswahl)

| Entity | Erlaubter State bei Create | Übergang zu Endstatus |
|---|---|---|
| `task` | `statecode=0` (Open) | `UpdateRecord` auf statecode=1/2 |
| `email` | `statecode=0` (Draft) | `SendEmailRequest` oder UpdateRecord |
| `quote` | `statecode=0` (Draft) | `UpdateRecord` über die `quotestate`-Übergänge |
| `salesorder` | `statecode=0` (Active) | analog |
| `invoice` | `statecode=0` (Active) | analog |

### Symptom

`CreateRecord` mit Endstatus-`statuscode` (z.B. `6` = Aborted für Task)
liefert:

```
6 is not a valid status code for state code TaskState.Open on task with Id ...
```

Test landet im Outcome `ERROR`. Wenn du darauf mit `expectException`
reagiert hast, schlägt der Match auf deine Plugin-Message fehl, weil
du eine Plattform-Exception statt einer Plugin-Exception bekommen hast
(siehe Pitfall "Plattform-Exception vs Plugin-Exception" weiter unten).

### Pattern

Create im Default-State, dann Update auf den Ziel-Status:

```json
[
  { "stepNumber": 1, "action": "CreateRecord", "entity": "tasks",
    "alias": "task1",
    "fields": {
      "subject": "JBE Test {GENERATED:guid}",
      "statuscode": 2
    }
  },
  { "stepNumber": 2, "action": "UpdateRecord", "alias": "task1",
    "fields": {
      "statecode": 2,
      "statuscode": 6
    }
  }
]
```

**Faustregel:** Wenn `statecode` im Create-Step nicht der Default
(meist 0) ist, hast du wahrscheinlich ein State-Lock.

---

## Plattform-Exception vs Plugin-Exception

`expectException` matched **alles** was an Exception kommt — auch
Plattform-Fehler die nicht von deinem Plugin stammen.

### Symptom

Du hast einen Test mit `expectException: { messageContains: "Status Reason Text" }`
geschrieben, weil du erwartest dass dein Plugin diesen Text in der
Exception hat. Der Test ist aber `FAILED` (nicht `PASSED`), weil eine
**andere** Exception von der Plattform geworfen wurde, bevor dein
Plugin überhaupt zum Zug kam.

### Diagnose

`jbe_assertionresults` enthält bei FAILED-Outcome heute (Plugin v5.3.1)
nur die Match-Fehlschlag-Information, nicht den Text der tatsächlich
geworfenen Exception. Workaround: temporär das `expectException`
entfernen und den Test als reines `Step-Error` laufen lassen — dann
zeigt `jbe_errormessage` die echte Exception.

Geplant für nächste Version: `actualException`-Detail wird auch im
FAILED-Fall mitgeliefert. Siehe Plan
`03_implementation/testcenter-erweiterungen-plan.md` (A13).

### Pattern

Häufig liegt die echte Ursache an einem Plattform-Constraint (z.B.
state-lock wie oben). In dem Fall:

1. Test-Setup so umbauen dass die Plattform-Exception nicht ausgelöst
   wird (z.B. zwei-Schritt-Pattern bei state-locked Entities).
2. **Dann erst** kommt das Plugin zum Zug, und `expectException` matched
   die Plugin-Message wie geplant.

---

## Plugin-Cold-Start

Der erste Test in einer Suite (oder nach längerer Inaktivität) ist
**5-10× langsamer** als nachfolgende Tests:

```
[PASSED ] ZP-LFN-01      7723 ms   <-- Cold-Start
[PASSED ] ZP-LFN-02       922 ms
[PASSED ] ZP-LFN-03       643 ms
```

**Ursache:** Sandbox-Spinup für das Plugin-Package, nicht der
Plugin-Code selbst.

**Konsequenz:**

- `maxDurationMs`-Asserts auf einzelnen Steps nicht zu eng setzen für
  den ersten Test.
- Performance-Benchmarks erst ab Test #2 in einer Suite messen, oder
  einen Warm-up-Test vorschalten.

---

## OData-Annotation vs Logical Name in Filter-Bedingungen

Filter-Bedingungen (`filter: [{ field, operator, value }]`) brauchen
den **Logical Name**, nicht die OData-Annotation:

```jsonc
// FALSCH (OData-Annotation):
"filter": [{ "field": "_originatingleadid_value", "operator": "eq", "value": "..." }]

// RICHTIG (Logical Name):
"filter": [{ "field": "originatingleadid", "operator": "eq", "value": "..." }]
```

**Symptom bei falschem Field-Namen:**
`Could not find a property named '_originatingleadid_value'`.

**Merkregel:** OData-Annotationen mit `_..._value` gibt es nur im
**Lese**-Pfad (z.B. in `$select`-Spalten von `RetrieveRecord`). In
Filtern und Asserts ist der Logical Name die Quelle der Wahrheit.

---

## Single-Target vs Polymorpher Lookup-Bind

Beim Setzen eines Lookups in `CreateRecord`/`UpdateRecord`:

```jsonc
// Polymorpher Lookup (mehrere Ziel-Entities möglich):
"parentcustomerid_account@odata.bind": "/accounts({acc.id})"
"parentcustomerid_contact@odata.bind": "/contacts({con.id})"

// Single-Target-Lookup (nur eine Ziel-Entity):
"originatingleadid@odata.bind": "/leads({lead.id})"
```

Bei Single-Target brauchst du kein `_target`-Suffix. Wenn du es
trotzdem schreibst:

```
'account' entity doesn't contain attribute 'originatingleadid_lead'
```

Mehr in [04-lookup-und-binding.md](04-lookup-und-binding.md).

---

Weiter mit [Coverage-Regeln](06-coverage-regeln.md) oder
[Rezepten](07-rezepte.md).
