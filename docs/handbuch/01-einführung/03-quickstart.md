# Quickstart — 5 Beispiele in 15 Minuten

Fuenf progressiv aufgebaute Beispiele, die dich durch die wichtigsten
Features führen. Am Ende des Kapitels hast du fuenf laufende Tests und
alle Kern-Patterns gesehen.

**Vorbereitung:** Öffne die Model-Driven-App **D365 Test Center** in
deinem Browser. Du brauchst Schreibrechte auf `jbe_testcase` und
`jbe_testrun`.

## QS-01 — Account anlegen und Website setzen

**Ziel:** Einen Account erzeugen, die Website setzen, prüfen dass sie
korrekt gespeichert wurde. Drei Actions, das minimale Skelett.

**Was du lernst:**

- Grundaufbau eines Tests (`testId`, `title`, `steps`)
- `CreateRecord` mit Alias
- `UpdateRecord` auf einen bekannten Alias
- `Assert` mit `target: "Record"` und `recordRef`
- Platzhalter `{TIMESTAMP}`

### Schritt 1: Testfall anlegen

Navigiere in der App zu **Testfälle** und klick auf **+ Neu**:

```
+-- Neuer Testfall: jbe_testcase -----------------------------------+
|                                                                   |
|  Test-ID      [ QS-01                                         ]   |
|  Titel        [ Account anlegen und Website setzen            ]   |
|  Aktiviert    [x]                                                 |
|  Tags         [ quickstart                                    ]   |
|                                                                   |
|  Definition (JSON):                                               |
|  +-------------------------------------------------------------+  |
|  | {                                                           |  |
|  |   "testId": "QS-01",                                        |  |
|  |   "title":  "Account: Create, Update und Assert",           |  |
|  |   "enabled": true,                                          |  |
|  |   "steps": [                                                |  |
|  |     {                                                       |  |
|  |       "stepNumber": 1,                                      |  |
|  |       "action": "CreateRecord",                             |  |
|  |       "entity": "accounts",                                 |  |
|  |       "alias":  "acc",                                      |  |
|  |       "fields": { "name": "JBE Test Account {TIMESTAMP}" }  |  |
|  |     },                                                      |  |
|  |     {                                                       |  |
|  |       "stepNumber": 2,                                      |  |
|  |       "action": "UpdateRecord",                             |  |
|  |       "alias":  "acc",                                      |  |
|  |       "fields": { "websiteurl": "https://example.com" }     |  |
|  |     },                                                      |  |
|  |     {                                                       |  |
|  |       "stepNumber": 3,                                      |  |
|  |       "action":    "Assert",                                |  |
|  |       "target":    "Record",                                |  |
|  |       "recordRef": "{RECORD:acc}",                          |  |
|  |       "field":     "websiteurl",                            |  |
|  |       "operator":  "Equals",                                |  |
|  |       "value":     "https://example.com",                   |  |
|  |       "description": "Website wurde korrekt gesetzt",       |  |
|  |       "onError":  "continue"                                |  |
|  |     }                                                       |  |
|  |   ]                                                         |  |
|  | }                                                           |  |
|  +-------------------------------------------------------------+  |
|                                                                   |
|  [Speichern]  [Speichern & Schließen]                            |
+-------------------------------------------------------------------+
```

Klick auf **Speichern & Schließen**.

### Schritt 2: Testlauf starten

Navigiere zu **Testläufe** und klick auf **+ Neu**:

```
+-- Neuer Testlauf: jbe_testrun ------------------------------------+
|                                                                   |
|  Name               [ QS-01 erster Versuch                    ]   |
|  Testcase-Filter    [ QS-01                                   ]   |
|  Test-Status        [ Geplant                v ]                  |
|  Records behalten   [ ] nein                                      |
|                                                                   |
|  [Speichern]  [Speichern & Schließen]                            |
+-------------------------------------------------------------------+
```

Sobald du **Speichern** klickst, startet der Run. Warte ein paar Sekunden
und drücke auf **Aktualisieren** (Refresh im Browser oder F5).

### Schritt 3: Ergebnis prüfen

Nach 5-10 Sekunden:

```
+-- Testlauf: QS-01 erster Versuch ---------------------------------+
|                                                                   |
|  Test-Status    Abgeschlossen                                     |
|  Bestanden      1                                                 |
|  Fehlgeschlagen 0                                                 |
|  Gesamt         1                                                 |
|  Summary        1/1 bestanden, 0 fehlgeschlagen                   |
|                 Batch 1-1 von 1:                                  |
|                   Dieser Batch: 1/1 bestanden                     |
|                   Gesamt bisher: 1/1                              |
|                 [OK] QS-01: Account anlegen und Website setzen    |
|                                                                   |
|  Zugeordnete Testergebnisse (1):                                  |
|  +------+-----------+-------------------------+----------------+  |
|  | Test | Ergebnis  | Name                    | Fehlerursache  |  |
|  +------+-----------+-------------------------+----------------+  |
|  | QS-01| Bestanden | Account anlegen und ... |                |  |
|  +------+-----------+-------------------------+----------------+  |
+-------------------------------------------------------------------+
```

Klick den Ergebnis-Datensatz an und öffne den **Steps-Tab**:

```
Testschritte  (3 Eintraege + Cleanup)
+----+--------------+-------+-------------+----------------------+
| #  | Action       | Alias | Ergebnis    | Detail               |
+----+--------------+-------+-------------+----------------------+
| 1  | CreateRecord | acc   | Erfolg      | 412 ms               |
| 2  | UpdateRecord | acc   | Erfolg      | 89 ms                |
| 3  | Assert       |       | Erfolg      | 12 ms                |
|    |              |       |             |   field: websiteurl  |
|    |              |       |             |   expected: https... |
|    |              |       |             |   actual:   https... |
|9000| Cleanup      |       | Erfolg      | 203 ms               |
+----+--------------+-------+-------------+----------------------+
```

Gratulation — dein erster Test läuft.

---

## QS-02 — Contact an Account, Lookup-Binding

**Ziel:** Account mit Child-Contact anlegen, Contact aktualisieren, beide
Records im Dataverse-Graph verbinden.

**Was du lernst:**

- **Lookup-Binding** mit `@odata.bind` (so machst du Parent-Child)
- Mehrere Aliase im selben Test
- `Assert` mit `target: "Query"` statt `Record` (für Query-basierte Prüfung)
- Generierte Testdaten mit `{GENERATED:...}`

### Testfall-JSON

```json
{
  "testId": "QS-02",
  "title": "Contact an Account anlegen und Jobtitle aktualisieren",
  "enabled": true,
  "steps": [
    {
      "stepNumber": 1,
      "action": "CreateRecord",
      "entity": "accounts",
      "alias":  "acc",
      "fields": { "name": "JBE Test Firma {TIMESTAMP}" }
    },
    {
      "stepNumber": 2,
      "action": "CreateRecord",
      "entity": "contacts",
      "alias":  "con",
      "fields": {
        "firstname": "{GENERATED:firstname}",
        "lastname":  "{GENERATED:lastname}",
        "emailaddress1": "{GENERATED:email}",
        "parentcustomerid_account@odata.bind": "/accounts({acc.id})"
      }
    },
    {
      "stepNumber": 3,
      "action": "UpdateRecord",
      "alias":  "con",
      "fields": { "jobtitle": "Leiter Qualitaet" }
    },
    {
      "stepNumber": 4,
      "action":    "Assert",
      "target":    "Record",
      "recordRef": "{RECORD:con}",
      "field":     "jobtitle",
      "operator":  "Equals",
      "value":     "Leiter Qualitaet",
      "description": "Jobtitle korrekt gespeichert",
      "onError":   "continue"
    },
    {
      "stepNumber": 5,
      "action":    "Assert",
      "target":    "Query",
      "entity":    "contacts",
      "filter":    [
        { "field": "contactid", "operator": "eq", "value": "{con.id}" }
      ],
      "field":     "_parentcustomerid_value",
      "operator":  "Equals",
      "value":     "{acc.id}",
      "description": "Contact haengt am richtigen Account",
      "onError":   "continue"
    }
  ]
}
```

**Wichtige Stellen im Detail:**

- `"parentcustomerid_account@odata.bind": "/accounts({acc.id})"`
  Das ist der kanonische Weg, einen Lookup-Wert beim Create zu setzen.
  Schema: `lookupname_zielentity@odata.bind` -> `/zielentityPlural(GUID)`.
  Mehr dazu in [04-lookup-und-binding.md](../02-testfall-schreiben/04-lookup-und-binding.md).

- `{acc.id}` löst sich nach dem ersten Step in die GUID des
  angelegten Accounts auf. `{con.id}` analog für den Contact.

- `{GENERATED:firstname}` wird zu einem Zufallsnamen mit "JBE Test"-Prefix,
  damit die Testdaten erkennbar sind und nicht mit echten Daten kollidieren.

Nach dem Speichern und einem Run-Start erwartest du:

```
+---+--------------+-------+---------+
| # | Action       | Alias | Ergebn. |
+---+--------------+-------+---------+
| 1 | CreateRecord | acc   | Erfolg  |
| 2 | CreateRecord | con   | Erfolg  |
| 3 | UpdateRecord | con   | Erfolg  |
| 4 | Assert       |       | Erfolg  |
| 5 | Assert       |       | Erfolg  |
+---+--------------+-------+---------+
```

---

## QS-03 — Lead qualifizieren (Custom Action)

**Ziel:** Einen Lead anlegen, per `QualifyLead`-Action qualifizieren, und
prüfen dass der Lead deaktiviert wurde und die erwarteten Folge-Records
(Contact, Opportunity) entstanden sind.

**Was du lernst:**

- `ExecuteRequest` — Microsoft-Standard-Messages wie `QualifyLead`, `Merge`,
  `SetState` aufrufen
- Parameter-Typen mit `$type`: `EntityReference`, `OptionSetValue`,
  `Guid`, `Entity`, `Money`
- `waitSeconds` um dem Plugin-Chain Zeit zu geben
- Multi-Assert-Flow nach einer Plugin-Kette

### Testfall-JSON

```json
{
  "testId": "QS-03",
  "title": "Lead qualifizieren: Contact und Opportunity entstehen",
  "enabled": true,
  "steps": [
    {
      "stepNumber": 1,
      "action": "CreateRecord",
      "entity": "leads",
      "alias":  "lead1",
      "fields": {
        "firstname":     "{GENERATED:firstname}",
        "lastname":      "{GENERATED:lastname}",
        "subject":       "Quickstart-Lead {TIMESTAMP}",
        "emailaddress1": "{GENERATED:email}"
      }
    },
    {
      "stepNumber":  2,
      "action":      "ExecuteRequest",
      "requestName": "QualifyLead",
      "fields": {
        "LeadId":            {
          "$type":  "EntityReference",
          "entity": "lead",
          "ref":    "lead1"
        },
        "CreateAccount":     false,
        "CreateContact":     true,
        "CreateOpportunity": true,
        "Status":            {
          "$type": "OptionSetValue",
          "value": 3
        }
      },
      "waitSeconds": 3,
      "description": "QualifyLead mit Status 3 = Qualified"
    },
    {
      "stepNumber": 3,
      "action":    "Assert",
      "target":    "Record",
      "recordRef": "{RECORD:lead1}",
      "field":     "statecode",
      "operator":  "Equals",
      "value":     "1",
      "description": "Lead ist nun qualifiziert (statecode 1 = Qualified)",
      "onError":   "continue"
    },
    {
      "stepNumber": 4,
      "action":    "Assert",
      "target":    "Query",
      "entity":    "contacts",
      "filter":    [
        { "field": "emailaddress1", "operator": "eq",
          "value": "{lead1.fields.emailaddress1}" }
      ],
      "operator":  "Exists",
      "description": "Contact wurde aus dem Lead erzeugt",
      "onError":   "continue"
    },
    {
      "stepNumber": 5,
      "action":    "Assert",
      "target":    "Query",
      "entity":    "opportunities",
      "filter":    [
        { "field": "name", "operator": "like",
          "value": "%Quickstart-Lead%" }
      ],
      "operator":  "Exists",
      "description": "Opportunity wurde aus dem Lead erzeugt",
      "onError":   "continue"
    }
  ]
}
```

**Was hier besonders ist:**

- `ExecuteRequest` ist die generische Brücke zu jeder SDK-Message. Der
  `requestName` ist der exakte Message-Name aus dem Dynamics-SDK
  (`QualifyLead`, `Merge`, `WinOpportunity`, `SetProcess`, ...).

- Die `fields` des `ExecuteRequest` sind die Parameter der Message. Für
  alles was nicht ein simpler String oder Boolean ist, brauchst du das
  `$type`-Objekt:

  ```json
  { "$type": "EntityReference", "entity": "lead", "ref": "lead1" }
  ```

  Das ersetzt die Run-Time die Ref durch die echte GUID.

- **Warum `waitSeconds: 3`?** `QualifyLead` triggert nachgelagerte
  Plugins (Contact/Opportunity werden asynchron angelegt). Das Test
  Center wartet 3 Sekunden, bevor die Asserts loslaufen. Eleganter ist
  `WaitForFieldValue` oder `WaitForRecord` — siehe QS-04.

- **`Status: 3`** ist der OptionSetValue für "Qualified". Die Werte sind
  entity-spezifisch; für `lead` sind `3=Qualified`, `4=Disqualified`.

### Erwartetes Steps-Tab

```
+---+----------------+-------+---------+----------------+
| # | Action         | Alias | Ergebn. | Detail         |
+---+----------------+-------+---------+----------------+
| 1 | CreateRecord   | lead1 | Erfolg  |                |
| 2 | ExecuteRequest |       | Erfolg  | 3041 ms        |
| 3 | Assert         |       | Erfolg  | statecode=1    |
| 4 | Assert         |       | Erfolg  | Query Exists   |
| 5 | Assert         |       | Erfolg  | Query Exists   |
+---+----------------+-------+---------+----------------+
```

---

## QS-04 — Async Plugin: WaitForFieldValue

**Ziel:** Eine Opportunity schließen (`WinOpportunity`), das triggert
ein Folge-Plugin das `actualrevenue` setzt. Wir warten gezielt auf den
Feldwert statt blind Sekunden zu verbrennen.

**Was du lernst:**

- `WaitForFieldValue` — intelligenter Ersatz für `waitSeconds`
- Timeout-Strategie
- Wann `Wait` sinnvoll ist und wann nicht

### Testfall-JSON

```json
{
  "testId": "QS-04",
  "title": "Opportunity gewinnen: Plugin setzt actualrevenue",
  "enabled": true,
  "steps": [
    {
      "stepNumber": 1,
      "action": "CreateRecord",
      "entity": "accounts",
      "alias":  "acc",
      "fields": { "name": "JBE Test Kunde {TIMESTAMP}" }
    },
    {
      "stepNumber": 2,
      "action": "CreateRecord",
      "entity": "opportunities",
      "alias":  "opp",
      "fields": {
        "name": "JBE Test Deal {TIMESTAMP}",
        "estimatedvalue":                          50000,
        "customerid_account@odata.bind":           "/accounts({acc.id})"
      }
    },
    {
      "stepNumber":  3,
      "action":      "ExecuteRequest",
      "requestName": "WinOpportunity",
      "fields": {
        "OpportunityClose": {
          "$type":  "Entity",
          "entity": "opportunityclose",
          "fields": {
            "subject":                              "Gewonnen",
            "opportunityid@odata.bind":             "/opportunities({opp.id})",
            "actualrevenue":                        { "$type": "Money", "value": 50000 }
          }
        },
        "Status": { "$type": "OptionSetValue", "value": 3 }
      },
      "description": "Opportunity mit Status 3 = Won schließen"
    },
    {
      "stepNumber":    4,
      "action":        "WaitForFieldValue",
      "alias":         "opp",
      "fields":        { "statecode": 1 },
      "timeoutSeconds": 30,
      "description":   "Warten bis die Opportunity wirklich geschlossen ist"
    },
    {
      "stepNumber":    5,
      "action":        "Assert",
      "target":        "Record",
      "recordRef":     "{RECORD:opp}",
      "field":         "statecode",
      "operator":      "Equals",
      "value":         "1",
      "description":   "Opportunity geschlossen (statecode 1)",
      "onError":       "continue"
    },
    {
      "stepNumber":    6,
      "action":        "Assert",
      "target":        "Record",
      "recordRef":     "{RECORD:opp}",
      "field":         "statuscode",
      "operator":      "Equals",
      "value":         "3",
      "description":   "Status = Won (3)",
      "onError":       "continue"
    }
  ]
}
```

**Wichtige Stellen:**

- **`WaitForFieldValue`** pollt den Record jede ~500 ms bis das Feld den
  erwarteten Wert hat, oder bis der Timeout abgelaufen ist. Wenn der
  Timeout zuschlägt, bricht der Test mit `Error` ab (es sei denn du
  setzt `"onError": "continue"`).

- **Vergleich `Wait` vs `WaitForFieldValue`:**

  | Szenario | Nimm |
  |---|---|
  | Du weißt dass es genau X Sekunden dauert | `Wait` |
  | Du weißt nicht wie lange, aber kennst ein Feld das sich ändern muss | `WaitForFieldValue` |
  | Das Plugin erstellt einen komplett neuen Record | `WaitForRecord` |

- **`OpportunityClose`** wird innerhalb des `WinOpportunity`-Calls als
  Parameter `$type: Entity` mitgegeben. Das ist das Muster wenn eine
  Microsoft-Message einen Unter-Record braucht.

---

## QS-05 — Record löschen und Negativ-Assert

**Ziel:** Einen Task anlegen, löschen, prüfen dass er wirklich weg ist.
Das zeigt: auch "etwas existiert nicht mehr" ist eine valide Assertion.

**Was du lernst:**

- `DeleteRecord`
- `Assert`-Operator `NotExists` (statt `Exists`)
- Warum Negativ-Erwartungen wichtig sind

### Testfall-JSON

```json
{
  "testId": "QS-05",
  "title": "Task anlegen, löschen, Abwesenheit prüfen",
  "enabled": true,
  "steps": [
    {
      "stepNumber": 1,
      "action": "CreateRecord",
      "entity": "tasks",
      "alias":  "tsk",
      "fields": {
        "subject":     "JBE Test Aufgabe {TIMESTAMP}",
        "description": "Wird gleich wieder gelöscht"
      }
    },
    {
      "stepNumber": 2,
      "action":    "Assert",
      "target":    "Query",
      "entity":    "tasks",
      "filter":    [
        { "field": "activityid", "operator": "eq", "value": "{tsk.id}" }
      ],
      "operator":  "Exists",
      "description": "Zwischenstand: Task existiert nach dem Create",
      "onError":   "continue"
    },
    {
      "stepNumber": 3,
      "action": "DeleteRecord",
      "alias":  "tsk"
    },
    {
      "stepNumber": 4,
      "action":    "Assert",
      "target":    "Query",
      "entity":    "tasks",
      "filter":    [
        { "field": "activityid", "operator": "eq", "value": "{tsk.id}" }
      ],
      "operator":  "NotExists",
      "description": "Task ist nach Delete wirklich weg",
      "onError":   "continue"
    }
  ]
}
```

**Warum nicht einfach `Record`-Assert nach dem Delete?** Weil der Record
weg ist — ein `target: "Record"` kann ihn nicht mehr laden. `target:
"Query"` mit `NotExists` ist der richtige Weg: das Test Center macht eine
Abfrage und prüfte dass sie 0 Treffer liefert.

**Negativ-Erwartungen sind genauso wichtig wie positive.** Wenn du nur
prüfst dass etwas passiert ist, kannst du nicht unterscheiden zwischen
"das Feature funktioniert" und "ein anderes Plugin hat das zufällig auch
gemacht". Mit einer Negativ-Assert im gleichen Test (`NotExists`,
`IsNull`, `!= alter Wert`) schließt du solche Falsch-Positiven aus.

---

## Zusammenfassung: welche Patterns du jetzt kennst

| Pattern | Erstmals gesehen |
|---|---|
| Testfall-Skelett (`testId`, `title`, `steps`) | QS-01 |
| `CreateRecord` / `UpdateRecord` | QS-01 |
| `Assert target=Record` | QS-01 |
| Platzhalter `{TIMESTAMP}` | QS-01 |
| Platzhalter `{alias.id}` in @odata.bind | QS-02 |
| Platzhalter `{GENERATED:*}` | QS-02 |
| Lookup-Binding `parentcustomerid_account@odata.bind` | QS-02 |
| `Assert target=Query` mit Filter | QS-02 |
| `ExecuteRequest` mit Parametern | QS-03 |
| `$type: EntityReference` / `OptionSetValue` | QS-03 |
| `Assert operator=Exists` | QS-03 |
| Platzhalter `{alias.fields.xxx}` | QS-03 |
| `WaitForFieldValue` | QS-04 |
| `$type: Entity` / `$type: Money` | QS-04 |
| `DeleteRecord` | QS-05 |
| `Assert operator=NotExists` | QS-05 |

Mehr Patterns und Aktionen findest du in
[../02-testfall-schreiben/02-actions-referenz.md](../02-testfall-schreiben/02-actions-referenz.md).
