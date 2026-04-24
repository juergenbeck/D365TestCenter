# JSON-Schema eines Testfalls

Alles was du in das Feld `jbe_definitionjson` schreibst, muss diesem
Schema folgen. Dieses Dokument ist der Referenz-Ueberblick. Fuer einzelne
Actions siehe [02-actions-referenz.md](02-actions-referenz.md).

## Grundgeruest

```json
{
  "testId":      "MY-01",
  "title":       "Kurzer aussagekraeftiger Titel",
  "description": "Optionaler Langtext",
  "category":    "Optional",
  "tags":        ["optional", "array"],
  "userStories": "DYN-1234 DYN-5678",
  "enabled":     true,
  "steps": [
    { "stepNumber": 1, "action": "...", "...": "..." },
    { "stepNumber": 2, "action": "...", "...": "..." }
  ]
}
```

## Top-Level-Felder

| Feld | Typ | Pflicht | Bedeutung |
|---|---|:---:|---|
| `testId` | String | **ja** | Eindeutige Test-ID. Muss mit dem `jbe_testid` des Records uebereinstimmen. Empfohlen: Praefix + Nummer, z.B. `QS-01`, `MGR-04`. |
| `title` | String | **ja** | Kurzer aussagekraeftiger Titel. Wird in Reports angezeigt. |
| `description` | String | nein | Langtext mit Hintergrund, Motivation, Edge-Cases. |
| `category` | String | nein | Freie Kategorisierung, z.B. `"CRUD"`, `"Merge"`, `"Integration"`. Per Filter ansprechbar (`category:CRUD`). |
| `tags` | String-Array | nein | Mehrere Tags. Per Filter ansprechbar (`tag:smoke`). |
| `userStories` | String | nein | Ticket-Referenzen, z.B. `"DYN-1234 DYN-5678"`. Nur Dokumentation. |
| `enabled` | Boolean | nein | Default `true`. Wenn `false`: Testcase wird beim Run uebersprungen. |
| `steps` | Array | **ja** | Geordnete Liste von Actions. Mindestens 1. |

## Der steps-Array

Das ist das eigentliche Programm des Tests. Jeder Eintrag ist eine
Action. Die JSON-Reihenfolge ist die Ausfuehrungsreihenfolge.

**Pflichtfelder jeder Step-Struktur:**

| Feld | Typ | Bedeutung |
|---|---|---|
| `stepNumber` | Integer | Fortlaufende Nummer 1..N. Dient zur Sortierung im Steps-Tab. |
| `action` | String | Der Action-Typ (siehe [Actions-Referenz](02-actions-referenz.md)). |

**Optional in jedem Step:**

| Feld | Typ | Bedeutung |
|---|---|---|
| `description` | String | Kommentar fuer den Log, oft hilfreich fuer Debugging. |
| `onError` | String | `"continue"` oder `"stop"`. Steuert Fehlerverhalten je Step. |

**Was `action`-abhaengig dazukommt:** variiert. Siehe
[Actions-Referenz](02-actions-referenz.md).

## Fehlerverhalten (`onError`)

| Action | Default | Bedeutung |
|---|---|---|
| `Assert` | `continue` | Wenn der Assert fehlschlaegt, laeuft der Test weiter. Am Ende ist das `jbe_testrunresult.jbe_outcome = Failed`. |
| Alle anderen | `stop` | Wenn die Action wirft (z.B. 404), bricht der Test ab. `jbe_outcome = Error`. |

Du kannst das per Step ueberschreiben:

```json
{ "action": "UpdateRecord", "alias": "acc",
  "fields": { "websiteurl": "https://example.com" },
  "onError": "continue" }
```

Bedeutet: wenn das Update fehlschlaegt, Test laeuft trotzdem weiter.
Sinnvoll z.B. wenn das Update optional ist.

## Reihenfolge und Nummerierung

- `stepNumber` **muss fortlaufend** sein: 1, 2, 3, ... — keine Luecken,
  keine Dopplungen. Die Engine sortiert aber danach, nicht nach
  JSON-Reihenfolge.
- Konvention: fange mit 1 an, inkrementiere um 1.
- Der Cleanup-Step am Ende des Tests bekommt intern `stepNumber = 9000`
  (siehst du im Steps-Tab).

## Beispiel mit allen Top-Level-Feldern

```json
{
  "testId":      "MGR-04",
  "title":       "Merge Szenario A: Duplikat wird Master",
  "description": "Testet ob bei Szenario A die Golden-Record-ID vom GR auf den Survivor (Duplikat) transferiert wird. Hintergrund: Projekt-Ticket DYN-8113.",
  "category":    "Merge",
  "tags":        ["merge", "golden-record", "dyn-8113"],
  "userStories": "DYN-8113",
  "enabled":     true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc",
      "fields": { "name": "JBE Test Merge {TIMESTAMP}" } },
    { "stepNumber": 2, "action": "CreateRecord", "entity": "contacts", "alias": "gr",
      "fields": { "firstname": "JBE Test GR", "lastname": "Merge_{TIMESTAMP}",
                  "parentcustomerid_account@odata.bind": "/accounts({acc.id})" } }
  ]
}
```

## Was NICHT mehr gueltig ist (Legacy)

Fruehere Versionen verwendeten getrennte Arrays `preconditions`, `steps`,
`assertions`. Seit ADR-0004 gibt es nur noch die einheitliche
`steps`-Liste. Wenn du auf alte Testcases triffst, gilt folgendes Mapping:

| Altes Feld | Neues Schema |
|---|---|
| `preconditions: [...]` | `CreateRecord`-Steps am Anfang der `steps`-Liste |
| `assertions: [...]` | `Assert`-Steps am Ende der `steps`-Liste |
| `dataMode: "template"` | entfallen, nicht mehr noetig |

Alle Records auf den Umgebungen sind schon migriert. Neue Tests schreibst
du direkt im neuen Schema.

## JSON-Validitaet pruefen

Das `jbe_definitionjson`-Feld nimmt jeden Text an — auch kaputtes JSON.
Der Test laeuft dann aber als `Skipped` mit einem Parse-Fehler.

Empfehlung: **JSON vor dem Einfuegen in VS Code oder einem Online-
Validator pruefen.** Typische Fehler:

- Fehlende Kommas zwischen Step-Objekten
- Einfache statt doppelte Anfuehrungszeichen
- Kommentare `//` oder `/* */` (JSON kennt keine Kommentare)
- Umlaute ohne UTF-8 (Umlaute sind erlaubt, aber die Codierung muss UTF-8
  sein — das macht der Browser automatisch)

Als Plausibilitaetscheck reicht oft: kopiere das JSON in VS Code, speichere
als `.json` — VS Code markiert Syntaxfehler sofort rot.
