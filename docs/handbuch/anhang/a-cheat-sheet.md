# Cheat Sheet

Die 20 haeufigsten Patterns auf einer Seite. Copy & Paste in deinen
Testfall, anpassen, fertig.

## Grundgeruest

```json
{
  "testId":  "MY-01",
  "title":   "Beschreibung",
  "enabled": true,
  "steps": [
  ]
}
```

## Record anlegen

```json
{ "stepNumber": 1, "action": "CreateRecord",
  "entity": "accounts", "alias": "acc",
  "fields": { "name": "JBE Test {TIMESTAMP}" } }
```

## Record mit Parent-Lookup

```json
{ "stepNumber": 2, "action": "CreateRecord",
  "entity": "contacts", "alias": "con",
  "fields": {
    "firstname": "{GENERATED:firstname}",
    "lastname":  "{GENERATED:lastname}",
    "parentcustomerid_account@odata.bind": "/accounts({acc.id})"
  } }
```

## Feld aendern

```json
{ "stepNumber": 3, "action": "UpdateRecord", "alias": "con",
  "fields": { "jobtitle": "Leiter" } }
```

## Assert auf Feldwert (Record)

```json
{ "stepNumber": 4, "action": "Assert",
  "target":    "Record",
  "recordRef": "{RECORD:con}",
  "field":     "jobtitle",
  "operator":  "Equals",
  "value":     "Leiter",
  "description": "Jobtitle gesetzt",
  "onError":   "continue" }
```

## Assert Exists (Query)

```json
{ "stepNumber": 5, "action": "Assert",
  "target":  "Query",
  "entity":  "opportunities",
  "filter":  [ { "field": "customerid", "operator": "eq", "value": "{acc.id}" } ],
  "operator": "Exists",
  "description": "Opportunity existiert",
  "onError": "continue" }
```

## Assert NotExists (nach Delete)

```json
{ "stepNumber": 7, "action": "Assert",
  "target":  "Query",
  "entity":  "tasks",
  "filter":  [ { "field": "activityid", "operator": "eq", "value": "{tsk.id}" } ],
  "operator": "NotExists",
  "description": "Record ist weg",
  "onError": "continue" }
```

## Assert RecordCount

```json
{ "stepNumber": 6, "action": "Assert",
  "target":  "Query",
  "entity":  "contacts",
  "filter":  [ { "field": "parentcustomerid", "operator": "eq", "value": "{acc.id}" } ],
  "operator": "RecordCount",
  "value":    "3",
  "description": "Genau 3 Contacts am Account",
  "onError": "continue" }
```

## Assert IsNull

```json
{ "stepNumber": 8, "action": "Assert",
  "target":    "Record", "recordRef": "{RECORD:con}",
  "field":     "emailaddress2",
  "operator":  "IsNull",
  "description": "Zweitmail nicht gesetzt",
  "onError":   "continue" }
```

## Warten auf Feldwert

```json
{ "stepNumber": 4, "action": "WaitForFieldValue",
  "alias":         "opp",
  "fields":        { "statecode": 1 },
  "timeoutSeconds": 30 }
```

## Warten auf Record

```json
{ "stepNumber": 5, "action": "WaitForRecord",
  "entity": "tasks", "alias": "task1",
  "filter": [ { "field": "regardingobjectid", "operator": "eq", "value": "{opp.id}" } ],
  "columns": ["subject"],
  "timeoutSeconds": 30 }
```

## Feste Wartezeit

```json
{ "stepNumber": 3, "action": "Wait", "waitSeconds": 3 }
```

## Record loeschen

```json
{ "stepNumber": 6, "action": "DeleteRecord", "alias": "tsk" }
```

## QualifyLead

```json
{ "stepNumber": 2, "action": "ExecuteRequest",
  "requestName": "QualifyLead",
  "fields": {
    "LeadId":            { "$type": "EntityReference", "entity": "lead", "ref": "lead1" },
    "CreateAccount":     false,
    "CreateContact":     true,
    "CreateOpportunity": true,
    "Status":            { "$type": "OptionSetValue", "value": 3 }
  },
  "waitSeconds": 3 }
```

## WinOpportunity

```json
{ "stepNumber": 3, "action": "ExecuteRequest",
  "requestName": "WinOpportunity",
  "fields": {
    "OpportunityClose": {
      "$type": "Entity", "entity": "opportunityclose",
      "fields": {
        "subject": "Gewonnen",
        "opportunityid@odata.bind": "/opportunities({opp.id})",
        "actualrevenue": { "$type": "Money", "value": 50000 }
      }
    },
    "Status": { "$type": "OptionSetValue", "value": 3 }
  } }
```

## Merge

```json
{ "stepNumber": 4, "action": "ExecuteRequest",
  "requestName": "Merge",
  "fields": {
    "Target":        { "$type": "EntityReference", "entity": "contact", "ref": "master" },
    "SubordinateId": { "$type": "Guid", "ref": "duplicate" },
    "UpdateContent": { "$type": "Entity", "entity": "contact", "fields": {} },
    "PerformParentingChecks": false
  },
  "waitSeconds": 5 }
```

## Assign (Owner aendern)

```json
{ "stepNumber": 3, "action": "ExecuteRequest",
  "requestName": "Assign",
  "fields": {
    "Target":   { "$type": "EntityReference", "entity": "account", "ref": "acc" },
    "Assignee": { "$type": "EntityReference", "entity": "systemuser",
                  "ref": "some_user_alias" }
  } }
```

## Custom Action aufrufen

```json
{ "stepNumber": 3, "action": "ExecuteAction",
  "actionName":  "new_MyCustomAction",
  "parameters":  { "param1": "{acc.id}", "param2": "value" } }
```

## Record neu laden

```json
{ "stepNumber": 4, "action": "RetrieveRecord",
  "alias":   "opp",
  "columns": ["statecode", "statuscode", "actualrevenue"] }
```

## Platzhalter-Referenz

```
{TIMESTAMP}              <- Aktueller Zeitstempel
{TIMESTAMP_MINUS_1H}     <- Zeitstempel vor 1h
{GENERATED:firstname}    <- JBE Test + Zufall
{GENERATED:lastname}     <- Zufall
{GENERATED:email}        <- Zufall @example.com
{GENERATED:phone}        <- 555-xxxx
{GENERATED:company}      <- JBE Test Firma + Zufall
{GENERATED:guid}         <- Neue GUID

{acc.id}                 <- GUID aus Alias 'acc'
{acc.fields.name}        <- Feldwert 'name' aus 'acc'
{RECORD:acc}             <- Alternative GUID-Syntax
```

## Filter-Operatoren

```
eq          gleich
ne          ungleich
gt ge lt le Zahlen/Datum
contains    Teilstring
startswith  beginnt mit
endswith    endet mit
like        SQL-Pattern mit %
```

## Assert-Operatoren

```
Equals NotEquals
GreaterThan LessThan
Contains StartsWith EndsWith
IsNull IsNotNull
Exists NotExists RecordCount
DateSetRecently
```
