# API-Referenz: Integration Test Center

## DataverseApi (API-Objekt)

Das `API`-Objekt kapselt alle Zugriffe auf die Dataverse Web API v9.2. Es wird als globales Singleton verwendet und kann im Demo-Modus durch die MockAPI ersetzt werden.

**Basis-URL:** `/api/data/v9.2`

**Standard-Header bei jedem Request:**

```
Accept: application/json
OData-MaxVersion: 4.0
OData-Version: 4.0
Content-Type: application/json
Prefer: odata.include-annotations=*
```

---

### fetch(url, options)

Basis-Methode für alle API-Aufrufe. Alle anderen Methoden delegieren an `fetch()`.

**Signatur:**
```javascript
async fetch(url: string, options?: RequestInit): Promise<object | null>
```

**Parameter:**

| Parameter | Typ          | Beschreibung                                                     |
|-----------|--------------|------------------------------------------------------------------|
| `url`     | `string`     | Relativer Pfad ab Basis-URL (z.B. `/itt_testcases?$top=10`)     |
| `options` | `RequestInit`| Optionales Objekt mit `method`, `headers`, `body` usw.          |

**Rückgabewert:** Geparster JSON-Body oder `null` bei HTTP 204 (No Content).

**OData-Header:** Werden automatisch zu jedem Request hinzugefügt (siehe Standard-Header oben). Zusätzliche Header aus `options.headers` werden gemergt.

**Fehlerbehandlung:** Wirft einen `Error` mit Statuscode und Response-Body bei nicht-erfolgreichen Responses (`!resp.ok`).

**Beispiel:**
```javascript
const data = await API.fetch("/itt_testcases?$select=itt_testid,itt_title&$top=5");
console.log(data.value); // Array mit bis zu 5 Testfällen
```

---

### getMany(entity, query)

Lädt eine Collection von Datensätzen mit OData-Query.

**Signatur:**
```javascript
async getMany(entity: string, query?: string): Promise<Array<object>>
```

**Parameter:**

| Parameter | Typ      | Beschreibung                                                          |
|-----------|----------|-----------------------------------------------------------------------|
| `entity`  | `string` | EntitySet-Name (z.B. `itt_testcases`, `itt_testruns`)                |
| `query`   | `string` | OData-Query-String ohne führendes `?` (z.B. `$select=itt_testid&$top=10`) |

**Rückgabewert:** Array von Datensätzen (`data.value`). Leeres Array falls keine Treffer.

**Beispiel:**
```javascript
const cases = await API.getMany("itt_testcases",
    "$select=itt_testid,itt_title,itt_category&$filter=itt_enabled eq true&$orderby=itt_testid asc&$top=500");
```

---

### getOne(entity, id, select)

Lädt einen einzelnen Datensatz anhand seiner GUID.

**Signatur:**
```javascript
async getOne(entity: string, id: string, select?: string): Promise<object>
```

**Parameter:**

| Parameter | Typ      | Beschreibung                                                |
|-----------|----------|-------------------------------------------------------------|
| `entity`  | `string` | EntitySet-Name (z.B. `itt_testruns`)                       |
| `id`      | `string` | GUID des Datensatzes (ohne geschweifte Klammern)            |
| `select`  | `string` | Kommagetrennte Feldnamen für `$select` (optional)          |

**Rückgabewert:** Einzelner Datensatz als Objekt.

**Beispiel:**
```javascript
const run = await API.getOne("itt_testruns", runId,
    "itt_teststatus,itt_passed,itt_failed,itt_total,itt_started_on");
```

---

### create(entity, data)

Erstellt einen neuen Datensatz.

**Signatur:**
```javascript
async create(entity: string, data: object): Promise<object>
```

**Parameter:**

| Parameter | Typ      | Beschreibung                                                     |
|-----------|----------|------------------------------------------------------------------|
| `entity`  | `string` | EntitySet-Name (z.B. `itt_testcases`)                           |
| `data`    | `object` | Objekt mit den Feldwerten für den neuen Datensatz               |

**OData-Header:** Zusätzlich `Prefer: return=representation`, damit der erstellte Datensatz im Response zurückgegeben wird.

**Rückgabewert:** Der erstellte Datensatz inklusive serversertig generierter Felder (ID, AutoNumber usw.).

**Beispiel:**
```javascript
const newRun = await API.create("itt_testruns", {
    itt_teststatus: 100000000,     // Geplant
    itt_testcasefilter: "category:Bridge"
});
console.log(newRun.itt_testrunid); // Generierte GUID
```

---

### update(entity, id, data)

Aktualisiert einen bestehenden Datensatz (PATCH).

**Signatur:**
```javascript
async update(entity: string, id: string, data: object): Promise<void>
```

**Parameter:**

| Parameter | Typ      | Beschreibung                                            |
|-----------|----------|---------------------------------------------------------|
| `entity`  | `string` | EntitySet-Name                                         |
| `id`      | `string` | GUID des Datensatzes                                    |
| `data`    | `object` | Objekt mit den zu ändernden Feldwerten                 |

**Rückgabewert:** Kein Rückgabewert (void). HTTP 204 bei Erfolg.

**Beispiel:**
```javascript
await API.update("itt_testcases", testcaseId, {
    itt_title: "Neuer Titel",
    itt_enabled: false
});
```

---

### del(entity, id)

Löscht einen Datensatz.

**Signatur:**
```javascript
async del(entity: string, id: string): Promise<void>
```

**Parameter:**

| Parameter | Typ      | Beschreibung                                  |
|-----------|----------|-----------------------------------------------|
| `entity`  | `string` | EntitySet-Name                               |
| `id`      | `string` | GUID des zu löschenden Datensatzes            |

**Rückgabewert:** Kein Rückgabewert (void).

**Beispiel:**
```javascript
await API.del("itt_testrunresults", resultId);
```

---

### executeAction(actionName, params)

Führt eine Custom Action aus (ungebundene Action).

**Signatur:**
```javascript
async executeAction(actionName: string, params?: object): Promise<object>
```

**Parameter:**

| Parameter    | Typ      | Beschreibung                                               |
|--------------|----------|------------------------------------------------------------|
| `actionName` | `string` | Name der Custom Action (z.B. `itt_RunIntegrationTests`)   |
| `params`     | `object` | Parameter-Objekt für die Action (optional)                 |

**Rückgabewert:** Response-Objekt der Action.

**Beispiel:**
```javascript
await API.executeAction("itt_RunIntegrationTests", {
    TestRunId: {
        "@odata.type": "Microsoft.Dynamics.CRM.itt_testrun",
        itt_testrunid: runId
    }
});
```

---

### fetchXml(entity, xml)

Führt eine FetchXML-Abfrage aus.

**Signatur:**
```javascript
async fetchXml(entity: string, xml: string): Promise<Array<object>>
```

**Parameter:**

| Parameter | Typ      | Beschreibung                                             |
|-----------|----------|----------------------------------------------------------|
| `entity`  | `string` | EntitySet-Name                                          |
| `xml`     | `string` | FetchXML als String (wird URL-encoded übergeben)        |

**Rückgabewert:** Array von Datensätzen.

**Beispiel:**
```javascript
const results = await API.fetchXml("itt_testcases",
    `<fetch><entity name="itt_testcase"><attribute name="itt_testid"/></entity></fetch>`);
```

---

### count(entity, filter)

Zählt Datensätze mit optionalem OData-Filter.

**Signatur:**
```javascript
async count(entity: string, filter?: string): Promise<number>
```

**Parameter:**

| Parameter | Typ      | Beschreibung                                             |
|-----------|----------|----------------------------------------------------------|
| `entity`  | `string` | EntitySet-Name                                          |
| `filter`  | `string` | OData-Filter-Ausdruck (optional)                        |

**Rückgabewert:** Anzahl der Datensätze als Ganzzahl (`@odata.count`).

**Beispiel:**
```javascript
const activeCount = await API.count("itt_testcases", "itt_enabled eq true");
```

---

## MockAPI

Die MockAPI simuliert die Dataverse Web API vollständig im Browser. Sie wird automatisch aktiviert, wenn die CRM-API nicht erreichbar ist (z.B. beim lokalen Öffnen der HTML-Datei). Alle Daten werden im Arbeitsspeicher gehalten und optional in `localStorage` persistiert.

### Öffentliche Properties und Methoden

#### currentPack

**Typ:** `string`

Aktuell aktives Demo-Datenpaket. Mögliche Werte: `"standard"`, `"field-governance"`, `"membership"`, `"empty"`.

**Default:** `"standard"`

---

#### switchPack(packName)

Wechselt das aktive Demo-Datenpaket und lädt alle Daten neu.

**Signatur:**
```javascript
switchPack(packName: string): void
```

**Parameter:**

| Parameter  | Typ      | Beschreibung                                              |
|------------|----------|-----------------------------------------------------------|
| `packName` | `string` | Name des Packs: `"standard"`, `"field-governance"`, `"membership"`, `"empty"` |

**Verhalten:**
1. Setzt `currentPack` auf den neuen Pack-Namen
2. Reinitialisiert den In-Memory-Store aus den Pack-Daten
3. Leert den MetaCache (Entities, Attributes, OptionSets, Custom APIs)
4. Aktualisiert den Pack-Indikator in der UI
5. Lädt die aktuelle View neu

---

#### activateDemoMode()

Aktiviert den Demo-Modus, indem alle Methoden des `API`-Objekts durch MockAPI-Äquivalente ersetzt werden.

**Signatur:**
```javascript
activateDemoMode(): void
```

**Verhalten:**
1. Ersetzt `API.fetch`, `API.getMany`, `API.getOne`, `API.create`, `API.update`, `API.del`, `API.executeAction`, `API.fetchXml`, `API.count` durch MockAPI-Methoden
2. Zeigt ein "Demo-Modus"-Banner unterhalb der Navigation
3. Zeigt den Pack-Indikator mit Name und Beschreibung des aktiven Packs
4. Registriert Event-Listener für den Pack-Selector

---

### Interne Methoden

#### _init()

Initialisiert den In-Memory-Store, falls noch nicht geschehen. Wird automatisch bei jedem API-Aufruf aufgerufen.

---

#### _reinitStore()

Erstellt den In-Memory-Store neu aus dem aktuellen DemoPack. Klont die Daten per `JSON.parse(JSON.stringify(...))`, sodass Änderungen die Originaldaten nicht beeinflussen.

**Store-Struktur:**
```javascript
{
    testcases: [...],       // Klone von DemoPack.testCases
    testruns: [...],        // Klone von DemoPack.testRuns
    testrunresults: [...]   // Klone von DemoPack.testRunResults
}
```

---

#### _handleGet(entitySet, url)

Verarbeitet GET-Anfragen auf Entity-Collections. Unterstützt:
- `$filter` (einfache OData-Filter: `eq`, `contains()`, Lookup-Filter, `and`-Verknüpfung)
- `$orderby` (aufsteigend und absteigend)
- `$top` (Limit)
- `$count=true` (Gesamtanzahl im Response)

---

#### _handlePost(entitySet, body)

Verarbeitet POST-Anfragen (Create). Generiert eine UUID, setzt `createdon` und fügt den Datensatz in die Collection ein. Bei `itt_testruns` wird zusätzlich `_simulateTestRun()` ausgelöst.

---

#### _handlePatch(entitySet, id, body)

Verarbeitet PATCH-Anfragen (Update). Merged die übergebenen Felder in den bestehenden Datensatz.

---

#### _handleDelete(entitySet, id)

Verarbeitet DELETE-Anfragen. Entfernt den Datensatz aus der In-Memory-Collection.

---

### localStorage-Persistenz

Die MockAPI hält alle Daten im Arbeitsspeicher (`_store`). Es gibt keine automatische localStorage-Persistenz. Die Daten werden bei jedem Seitenaufruf aus dem aktiven DemoPack neu initialisiert.

---

### Auto-Detection-Mechanismus

Beim Start der Anwendung wird versucht, `/api/data/v9.2/WhoAmI` aufzurufen:

1. **Erfolgreiche Antwort:** Die Anwendung nutzt die echte Dataverse Web API (Produktivmodus innerhalb CRM).
2. **Fehler (Netzwerk, 401, 404 usw.):** `activateDemoMode()` wird automatisch aufgerufen und alle API-Methoden werden durch die MockAPI ersetzt.

```javascript
try {
    const resp = await window.fetch("/api/data/v9.2/WhoAmI", {
        headers: { "Accept": "application/json" }
    });
    if (!resp.ok) throw new Error("API " + resp.status);
} catch {
    activateDemoMode();
}
```

---

## CONFIG-Objekt

Zentrale Konfiguration der Anwendung. Alle Entity-Namen, Feldnamen und OptionSet-Werte sind hier definiert und können an einen anderen Publisher angepasst werden.

### Properties

| Property                       | Typ      | Default              | Beschreibung                                              |
|---------------------------------|----------|----------------------|-----------------------------------------------------------|
| `prefix`                       | `string` | `"itt"`              | Publisher-Prefix (ohne Unterstrich)                       |
| `optionSetBase`                | `number` | `100000000`          | Basiswert für OptionSet-Codes                             |
| **entities**                   |          |                      |                                                           |
| `entities.testcase`            | `string` | `"itt_testcase"`     | Schema-Name der Testfall-Entity                           |
| `entities.testrun`             | `string` | `"itt_testrun"`      | Schema-Name der Testlauf-Entity                           |
| `entities.testrunresult`       | `string` | `"itt_testrunresult"`| Schema-Name der Testergebnis-Entity                       |
| **fields**                     |          |                      |                                                           |
| `fields.testid`               | `string` | `"itt_testid"`       | Feld: Fachliche Test-ID                                   |
| `fields.title`                | `string` | `"itt_title"`        | Feld: Titel des Testfalls                                 |
| `fields.category`             | `string` | `"itt_category"`     | Feld: Kategorie (OptionSet)                               |
| `fields.tags`                 | `string` | `"itt_tags"`         | Feld: Tags (kommagetrennt)                                |
| `fields.enabled`              | `string` | `"itt_enabled"`      | Feld: Aktiv-Flag                                          |
| `fields.definition_json`      | `string` | `"itt_definition_json"` | Feld: JSON-Definition                                  |
| `fields.userstories`          | `string` | `"itt_userstories"`  | Feld: User Stories (kommagetrennt)                        |
| `fields.teststatus`           | `string` | `"itt_teststatus"`   | Feld: Testlauf-Status                                     |
| `fields.passed`               | `string` | `"itt_passed"`       | Feld: Anzahl bestanden                                    |
| `fields.failed`               | `string` | `"itt_failed"`       | Feld: Anzahl fehlgeschlagen                               |
| `fields.total`                | `string` | `"itt_total"`        | Feld: Gesamtanzahl                                        |
| `fields.started_on`           | `string` | `"itt_started_on"`   | Feld: Startzeitpunkt                                      |
| `fields.completed_on`         | `string` | `"itt_completed_on"` | Feld: Abschlusszeitpunkt                                  |
| `fields.testsummary`          | `string` | `"itt_testsummary"`  | Feld: Zusammenfassung                                     |
| `fields.fulllog`              | `string` | `"itt_fulllog"`      | Feld: Vollständiges Log                                   |
| `fields.testcasefilter`       | `string` | `"itt_testcasefilter"` | Feld: Filter-Ausdruck                                  |
| `fields.testresult_json`      | `string` | `"itt_testresult_json"` | Feld: Ergebnis-JSON                                    |
| `fields.testrunid`            | `string` | `"itt_testrunid"`    | Feld: Testlauf-Lookup                                     |
| `fields.testcaseid`           | `string` | `"itt_testcaseid"`   | Feld: Testfall-Lookup                                     |
| `fields.outcome`              | `string` | `"itt_outcome"`      | Feld: Ergebnis (OptionSet)                                |
| `fields.duration_ms`          | `string` | `"itt_duration_ms"`  | Feld: Dauer in Millisekunden                              |
| `fields.error_message`        | `string` | `"itt_error_message"` | Feld: Fehlermeldung                                      |
| `fields.assertion_results`    | `string` | `"itt_assertion_results"` | Feld: Assertion-Ergebnisse (JSON)                    |
| **optionSets**                 |          |                      |                                                           |
| `optionSets.statusPlanned`    | `number` | `100000000`          | Teststatus: Geplant                                       |
| `optionSets.statusRunning`    | `number` | `100000001`          | Teststatus: Läuft                                         |
| `optionSets.statusCompleted`  | `number` | `100000002`          | Teststatus: Abgeschlossen                                 |
| `optionSets.statusError`      | `number` | `100000003`          | Teststatus: Fehler                                        |
| `optionSets.outcomePassed`    | `number` | `100000000`          | Testergebnis: Passed                                      |
| `optionSets.outcomeFailed`    | `number` | `100000001`          | Testergebnis: Failed                                      |
| `optionSets.outcomeError`     | `number` | `100000002`          | Testergebnis: Error                                       |
| `optionSets.outcomeSkipped`   | `number` | `100000003`          | Testergebnis: Skipped                                     |
| `optionSets.outcomeNotImpl`   | `number` | `100000004`          | Testergebnis: Not Implemented                             |
| `optionSets.catUpdateSource`  | `number` | `100000000`          | Kategorie: UpdateSource                                   |
| `optionSets.catCreateSource`  | `number` | `100000001`          | Kategorie: CreateSource                                   |
| `optionSets.catPISA`          | `number` | `100000002`          | Kategorie: PISA                                           |
| `optionSets.catMultiSource`   | `number` | `100000003`          | Kategorie: MultiSource                                    |
| `optionSets.catAdditionalFields` | `number` | `100000004`       | Kategorie: AdditionalFields                               |
| `optionSets.catBridge`        | `number` | `100000005`          | Kategorie: Bridge                                         |
| `optionSets.catMerge`         | `number` | `100000006`          | Kategorie: Merge                                          |
| `optionSets.catRecompute`     | `number` | `100000007`          | Kategorie: Recompute                                      |
| `optionSets.catErrorInjection`| `number` | `100000008`          | Kategorie: ErrorInjection                                 |
| **actions**                    |          |                      |                                                           |
| `actions.runTests`            | `string` | `"itt_RunIntegrationTests"` | Name der Custom Action zum Starten eines Testlaufs |

### Anleitung zum Anpassen an eigenen Publisher

Um das Integration Test Center mit einem anderen Publisher-Prefix zu verwenden (z.B. `markant` statt `itt`):

1. **CONFIG.prefix ändern:** `prefix: "markant"` setzen
2. **Alle Entity-Namen anpassen:** `entities.testcase` wird zu `"markant_testcase"` usw.
3. **Alle Feldnamen anpassen:** Jeder Eintrag unter `fields` muss das neue Prefix erhalten
4. **OptionSet-Namen anpassen:** Die OptionSets heißen dann `markant_teststatus`, `markant_testoutcome`, `markant_testcategory`
5. **Custom Action anpassen:** `actions.runTests` wird zu `"markant_RunIntegrationTests"`
6. **OptionSet-Werte beibehalten:** Die numerischen Codes (100000000 ff.) bleiben gleich, sofern die OptionSets mit denselben Werten angelegt werden

---

## Custom Actions

### itt_RunIntegrationTests

Startet die serverseitige Ausführung eines Integrationstestlaufs.

**Typ:** Ungebundene Action (keine Function)

**Binding Type:** 0 (Global, nicht an eine Entity gebunden)

**Allowed Custom Processing Step Type:** 0 (None, wird synchron ausgeführt)

**Request-Parameter:**

| Parameter   | Typ                                          | Beschreibung                               |
|-------------|----------------------------------------------|--------------------------------------------|
| `TestRunId` | `Microsoft.Dynamics.CRM.itt_testrun` (EntityReference) | Referenz auf den zu startenden Testlauf    |

**Rückgabe:** Leeres Response-Objekt `{}`. Der Fortschritt wird asynchron über Updates auf dem `itt_testrun`-Datensatz und Erstellung von `itt_testrunresult`-Datensätzen abgebildet.

**Ablauf:**

1. Client erstellt einen `itt_testrun`-Datensatz mit Status `Geplant` (100000000) und dem gewünschten Filter
2. Client ruft `itt_RunIntegrationTests` mit der Testlauf-GUID auf
3. Serverseitig wird der Status auf `Läuft` (100000001) gesetzt
4. Für jeden Testfall wird anhand der JSON-Definition die Testausführung durchgeführt:
   - Preconditions erstellen (Account, Contact, ContactSources)
   - Steps ausführen (Create, Update, Wait, API-Aufrufe)
   - Assertions prüfen (Feldwerte, Existenz, Vergleiche)
5. Pro Testfall wird ein `itt_testrunresult` mit Outcome, Dauer und Fehlermeldung erstellt
6. Nach Abschluss wird der Testlauf-Status auf `Abgeschlossen` (100000002) oder `Fehler` (100000003) gesetzt
7. `itt_passed`, `itt_failed`, `itt_total`, `itt_testsummary` und `itt_fulllog` werden aktualisiert

**Beispiel-Aufruf:**
```javascript
// 1. Testlauf erstellen
const run = await API.create("itt_testruns", {
    itt_teststatus: 100000000,
    itt_testcasefilter: "*"
});

// 2. Action aufrufen
await API.executeAction("itt_RunIntegrationTests", {
    TestRunId: {
        "@odata.type": "Microsoft.Dynamics.CRM.itt_testrun",
        itt_testrunid: run.itt_testrunid
    }
});

// 3. Polling auf Fortschritt (alle 3 Sekunden)
const poll = setInterval(async () => {
    const status = await API.getOne("itt_testruns", run.itt_testrunid,
        "itt_teststatus,itt_passed,itt_failed,itt_total");
    if (status.itt_teststatus >= 100000002) {
        clearInterval(poll);
        // Testlauf abgeschlossen
    }
}, 3000);
```

### Weitere Custom APIs (registriert, aber nicht vom Test Center aufgerufen)

| Name                          | Typ      | Beschreibung                                                     |
|-------------------------------|----------|------------------------------------------------------------------|
| `itt_GovernanceApiContact`    | Action   | Ruft die Governance-API für einen Kontakt auf                    |
| `itt_GovernanceApiContactSource` | Action | Ruft die Governance-API für eine Quell-Entity auf               |
| `itt_AssertEnvironment`       | Function | Prüft die Umgebungsvoraussetzungen für Integrationstests         |
