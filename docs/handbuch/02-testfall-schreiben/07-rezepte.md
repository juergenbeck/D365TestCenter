# Rezepte: Vorlagen für typische Szenarien

Vollstaendige JSON-Templates für häufige Test-Muster. Kopiere, passe an,
fertig. Jedes Rezept ist ausführbar wie es steht (ersetze nur die
Test-ID wenn du mehrere Varianten brauchst).

## Rezept A: Standard-CRUD (Account anlegen und prüfen)

**Wofür:** einfacher Smoke-Test, als erstes Beispiel in einer neuen
Entity-Suite.

```json
{
  "testId": "REZ-A-01",
  "title":  "Account: Create, Update, Verify",
  "enabled": true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc",
      "fields": { "name": "JBE Test {TIMESTAMP}" } },

    { "stepNumber": 2, "action": "UpdateRecord", "alias": "acc",
      "fields": { "websiteurl": "https://www.example.com",
                  "numberofemployees": 50 } },

    { "stepNumber": 3, "action": "Assert", "target": "Record", "recordRef": "{RECORD:acc}",
      "field": "websiteurl", "operator": "Equals", "value": "https://www.example.com",
      "description": "Website korrekt gesetzt", "onError": "continue" },

    { "stepNumber": 4, "action": "Assert", "target": "Record", "recordRef": "{RECORD:acc}",
      "field": "numberofemployees", "operator": "Equals", "value": "50",
      "description": "Mitarbeiterzahl korrekt", "onError": "continue" },

    { "stepNumber": 5, "action": "Assert", "target": "Record", "recordRef": "{RECORD:acc}",
      "field": "statecode", "operator": "Equals", "value": "0",
      "description": "Account ist aktiv", "onError": "continue" }
  ]
}
```

## Rezept B: Parent-Child-Beziehung (Account + Contact)

**Wofür:** Tests die einen Lookup zwischen zwei Entities setzen müssen.
Zeigt das @odata.bind-Pattern.

```json
{
  "testId": "REZ-B-01",
  "title":  "Contact an Account hängen und auslesen",
  "enabled": true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc",
      "fields": { "name": "JBE Test Firma {TIMESTAMP}" } },

    { "stepNumber": 2, "action": "CreateRecord", "entity": "contacts", "alias": "con",
      "fields": {
        "firstname":     "{GENERATED:firstname}",
        "lastname":      "{GENERATED:lastname}",
        "emailaddress1": "{GENERATED:email}",
        "parentcustomerid_account@odata.bind": "/accounts({acc.id})"
      }
    },

    { "stepNumber": 3, "action": "Assert", "target": "Query", "entity": "contacts",
      "filter": [ { "field": "contactid", "operator": "eq", "value": "{con.id}" } ],
      "field": "_parentcustomerid_value", "operator": "Equals", "value": "{acc.id}",
      "description": "Contact zeigt auf den richtigen Parent-Account",
      "onError": "continue" },

    { "stepNumber": 4, "action": "Assert", "target": "Query", "entity": "contacts",
      "filter": [ { "field": "parentcustomerid", "operator": "eq", "value": "{acc.id}" } ],
      "operator": "RecordCount", "value": "1",
      "description": "Der Account hat genau einen Contact",
      "onError": "continue" }
  ]
}
```

## Rezept C: Custom Action aufrufen (QualifyLead)

**Wofür:** Tests die eine Microsoft-SDK-Message auslösen und Folge-Records
prüfen.

```json
{
  "testId": "REZ-C-01",
  "title":  "QualifyLead: Contact und Opportunity entstehen",
  "enabled": true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "leads", "alias": "lead1",
      "fields": {
        "firstname":     "{GENERATED:firstname}",
        "lastname":      "{GENERATED:lastname}",
        "subject":       "Leadqualifizierung Test {TIMESTAMP}",
        "emailaddress1": "{GENERATED:email}"
      }
    },

    { "stepNumber": 2, "action": "ExecuteRequest", "requestName": "QualifyLead",
      "fields": {
        "LeadId":            { "$type": "EntityReference", "entity": "lead", "ref": "lead1" },
        "CreateAccount":     false,
        "CreateContact":     true,
        "CreateOpportunity": true,
        "Status":            { "$type": "OptionSetValue", "value": 3 }
      },
      "waitSeconds": 3 },

    { "stepNumber": 3, "action": "Assert", "target": "Record", "recordRef": "{RECORD:lead1}",
      "field": "statecode", "operator": "Equals", "value": "1",
      "description": "Lead ist qualifiziert (deaktiviert)", "onError": "continue" },

    { "stepNumber": 4, "action": "Assert", "target": "Query", "entity": "contacts",
      "filter": [ { "field": "emailaddress1", "operator": "eq",
                    "value": "{lead1.fields.emailaddress1}" } ],
      "operator": "Exists",
      "description": "Contact wurde erzeugt", "onError": "continue" },

    { "stepNumber": 5, "action": "Assert", "target": "Query", "entity": "opportunities",
      "filter": [ { "field": "name", "operator": "like",
                    "value": "%Leadqualifizierung Test%" } ],
      "operator": "Exists",
      "description": "Opportunity wurde erzeugt", "onError": "continue" }
  ]
}
```

## Rezept D: Async Plugin-Kette (Opportunity schließen)

**Wofür:** Tests die auf ein Plugin warten müssen, das asynchron
Seiten-Effekte erzeugt.

```json
{
  "testId": "REZ-D-01",
  "title":  "Opportunity gewinnen: Plugin setzt Status korrekt",
  "enabled": true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc",
      "fields": { "name": "JBE Test Kunde {TIMESTAMP}" } },

    { "stepNumber": 2, "action": "CreateRecord", "entity": "opportunities", "alias": "opp",
      "fields": {
        "name": "JBE Test Deal {TIMESTAMP}",
        "estimatedvalue": 75000,
        "customerid_account@odata.bind": "/accounts({acc.id})"
      }
    },

    { "stepNumber": 3, "action": "ExecuteRequest", "requestName": "WinOpportunity",
      "fields": {
        "OpportunityClose": {
          "$type":  "Entity", "entity": "opportunityclose",
          "fields": {
            "subject": "Gewonnen",
            "opportunityid@odata.bind": "/opportunities({opp.id})",
            "actualrevenue": { "$type": "Money", "value": 75000 }
          }
        },
        "Status": { "$type": "OptionSetValue", "value": 3 }
      }
    },

    { "stepNumber": 4, "action": "WaitForFieldValue", "alias": "opp",
      "fields": { "statecode": 1 },
      "timeoutSeconds": 30,
      "description": "Warten bis Opportunity geschlossen ist" },

    { "stepNumber": 5, "action": "Assert", "target": "Record", "recordRef": "{RECORD:opp}",
      "field": "statecode", "operator": "Equals", "value": "1",
      "description": "Opportunity geschlossen", "onError": "continue" },

    { "stepNumber": 6, "action": "Assert", "target": "Record", "recordRef": "{RECORD:opp}",
      "field": "statuscode", "operator": "Equals", "value": "3",
      "description": "Status = Won", "onError": "continue" }
  ]
}
```

## Rezept E: Delete mit Negativ-Assert

**Wofür:** Tests die Löschen prüfen.

```json
{
  "testId": "REZ-E-01",
  "title":  "Task löschen und Abwesenheit bestätigen",
  "enabled": true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "tasks", "alias": "tsk",
      "fields": {
        "subject":     "JBE Test Task {TIMESTAMP}",
        "description": "Wird gleich gelöscht"
      }
    },

    { "stepNumber": 2, "action": "Assert", "target": "Record", "recordRef": "{RECORD:tsk}",
      "field": "subject", "operator": "StartsWith", "value": "JBE Test",
      "description": "Task existiert und ist korrekt benannt",
      "onError": "continue" },

    { "stepNumber": 3, "action": "DeleteRecord", "alias": "tsk" },

    { "stepNumber": 4, "action": "Assert", "target": "Query", "entity": "tasks",
      "filter": [ { "field": "activityid", "operator": "eq", "value": "{tsk.id}" } ],
      "operator": "NotExists",
      "description": "Task ist nach Delete weg", "onError": "continue" }
  ]
}
```

## Rezept F: Merge mit Seiten-Effekt

**Wofür:** Zwei Records zusammenführen und sowohl Survivor als auch
Subordinate prüfen.

```json
{
  "testId": "REZ-F-01",
  "title":  "Contact-Merge: Survivor bleibt aktiv, Subordinate deaktiviert",
  "enabled": true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc",
      "fields": { "name": "JBE Test Firma {TIMESTAMP}" } },

    { "stepNumber": 2, "action": "CreateRecord", "entity": "contacts", "alias": "survivor",
      "fields": {
        "firstname":  "Anna", "lastname": "Meier-{TIMESTAMP}",
        "emailaddress1": "anna.meier_{TIMESTAMP}@example.com",
        "parentcustomerid_account@odata.bind": "/accounts({acc.id})"
      }
    },

    { "stepNumber": 3, "action": "CreateRecord", "entity": "contacts", "alias": "duplicate",
      "fields": {
        "firstname":  "Anna", "lastname": "Meier-{TIMESTAMP}",
        "emailaddress1": "anna.meier_{TIMESTAMP}@example.com",
        "parentcustomerid_account@odata.bind": "/accounts({acc.id})"
      }
    },

    { "stepNumber": 4, "action": "ExecuteRequest", "requestName": "Merge",
      "fields": {
        "Target":        { "$type": "EntityReference", "entity": "contact", "ref": "survivor" },
        "SubordinateId": { "$type": "Guid", "ref": "duplicate" },
        "UpdateContent": { "$type": "Entity", "entity": "contact", "fields": {} },
        "PerformParentingChecks": false
      },
      "waitSeconds": 5 },

    { "stepNumber": 5, "action": "Assert", "target": "Record", "recordRef": "{RECORD:survivor}",
      "field": "statecode", "operator": "Equals", "value": "0",
      "description": "Survivor bleibt aktiv", "onError": "continue" },

    { "stepNumber": 6, "action": "Assert", "target": "Record", "recordRef": "{RECORD:duplicate}",
      "field": "statecode", "operator": "Equals", "value": "1",
      "description": "Subordinate wurde deaktiviert", "onError": "continue" },

    { "stepNumber": 7, "action": "Assert", "target": "Record", "recordRef": "{RECORD:survivor}",
      "field": "emailaddress1", "operator": "Contains", "value": "anna.meier",
      "description": "Survivor behaelt seine E-Mail", "onError": "continue" }
  ]
}
```

## Rezept G: WaitForRecord — auf von Plugin erzeugten Record warten

**Wofür:** Ein Plugin erzeugt einen Folgerecord, du weißt nicht die GUID,
musst aber darauf zugreifen.

```json
{
  "testId": "REZ-G-01",
  "title":  "Opportunity wird angelegt, Plugin erzeugt Follow-up-Task",
  "enabled": true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc",
      "fields": { "name": "JBE Test Kunde {TIMESTAMP}" } },

    { "stepNumber": 2, "action": "CreateRecord", "entity": "opportunities", "alias": "opp",
      "fields": {
        "name": "JBE Test Deal {TIMESTAMP}",
        "estimatedvalue": 15000,
        "customerid_account@odata.bind": "/accounts({acc.id})"
      }
    },

    { "stepNumber": 3, "action": "WaitForRecord", "entity": "tasks",
      "alias":   "task1",
      "filter":  [ { "field": "regardingobjectid", "operator": "eq", "value": "{opp.id}" } ],
      "columns": ["subject", "statecode"],
      "timeoutSeconds": 30 },

    { "stepNumber": 4, "action": "Assert", "target": "Record", "recordRef": "{RECORD:task1}",
      "field": "subject", "operator": "Contains", "value": "Follow",
      "description": "Plugin hat Follow-up-Task mit richtigem Subject erzeugt",
      "onError": "continue" },

    { "stepNumber": 5, "action": "Assert", "target": "Record", "recordRef": "{RECORD:task1}",
      "field": "statecode", "operator": "Equals", "value": "0",
      "description": "Task ist offen (aktiv)", "onError": "continue" }
  ]
}
```

## Rezept H: Test mit AutoNumber-Read via columns

**Wofür:** Dynamics generiert eine AutoNumber beim Create (z.B. Lead-
oder Invoice-Nummer). Du willst sie prüfen oder in weiteren Steps nutzen.

```json
{
  "testId": "REZ-H-01",
  "title":  "Lead mit AutoNumber-Subject",
  "enabled": true,
  "steps": [
    { "stepNumber": 1, "action": "CreateRecord", "entity": "leads", "alias": "lead",
      "fields": {
        "firstname": "Anna", "lastname": "Meier-{TIMESTAMP}",
        "subject":   "Anfrage {TIMESTAMP}"
      },
      "columns": ["leadqualitycode", "createdon"]
    },

    { "stepNumber": 2, "action": "Assert", "target": "Record", "recordRef": "{RECORD:lead}",
      "field":    "createdon", "operator": "DateSetRecently", "value": "30",
      "description": "createdon wurde in den letzten 30s gesetzt",
      "onError": "continue" },

    { "stepNumber": 3, "action": "Assert", "target": "Record", "recordRef": "{RECORD:lead}",
      "field":    "leadqualitycode", "operator": "IsNotNull",
      "description": "Default-QualityCode wurde gesetzt",
      "onError": "continue" },

    { "stepNumber": 4, "action": "UpdateRecord", "alias": "lead",
      "fields": { "description": "Anfrage mit AutoNumber {lead.fields.createdon}" } },

    { "stepNumber": 5, "action": "Assert", "target": "Record", "recordRef": "{RECORD:lead}",
      "field": "description", "operator": "Contains", "value": "Anfrage mit AutoNumber",
      "description": "Platzhalter aus columns aufgeloest",
      "onError": "continue" }
  ]
}
```

---

Diese Rezepte decken ca. 80% aller Tests. Für spezielle Dinge schau in
der [Actions-Referenz](02-actions-referenz.md) oder
[Assertions-Dokumentation](05-assertions.md).
