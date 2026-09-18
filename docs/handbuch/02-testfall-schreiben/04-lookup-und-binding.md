# Lookup-Felder und @odata.bind

Lookup-Felder sind die Verbindungen zwischen Records: Contact zu Account,
Opportunity zu Account, Task zu beliebiger Regarding-Entity. Sie werden
in Dataverse nicht als GUID direkt gesetzt, sondern über eine spezielle
URL-Syntax: **`@odata.bind`**.

## Das Grundmuster

```json
"fields": {
  "firstname": "Anna",
  "lastname":  "Meier",
  "parentcustomerid_account@odata.bind": "/accounts({acc.id})"
}
```

Das verbindet den neuen Contact mit dem Account, der unter dem Alias
`acc` liegt. Zerlegt:

```
parentcustomerid_account@odata.bind   <-- key
   |                 |      |
   |                 |      +-- magischer Suffix: "hier ist ein Lookup"
   |                 +-- auf welche Zielentity zeigt der Lookup
   +-- Name des Lookup-Felds auf dem Contact

"/accounts({acc.id})"                  <-- value
     |          |
     |          +-- GUID des Zielrecords (hier per Platzhalter)
     +-- EntitySetName der Zielentity
```

## Das Schema im Detail

```
<lookupfeldname>_<zielentity>@odata.bind: "/<zielentityplural>(<guid>)"
```

| Teil | Regel |
|---|---|
| `<lookupfeldname>` | Der **LogicalName** des Lookup-Attributs (z.B. `parentcustomerid`, `regardingobjectid`). |
| `<zielentity>` | Der **LogicalName** der Zielentity (Singular!) — `account`, `contact`, `opportunity`. |
| `<zielentityplural>` | Der **EntitySetName** der Zielentity (Plural!) — `accounts`, `contacts`, `opportunities`. |
| `<guid>` | GUID ohne Anführungszeichen, ohne `guid'...'`-Wrapping. |

## Typische Beispiele

### Contact an Account hängen

```json
"parentcustomerid_account@odata.bind": "/accounts({acc.id})"
```

`parentcustomerid` auf `contact` ist ein polymorpher Lookup (kann auf
`account` oder `contact` zeigen). Der Zielentity-Suffix ist deswegen
Pflicht.

### Opportunity an Account

```json
"customerid_account@odata.bind": "/accounts({acc.id})"
```

### Opportunity an Contact

```json
"customerid_contact@odata.bind": "/contacts({con.id})"
```

### Task "regarding" Opportunity

```json
"regardingobjectid_opportunity@odata.bind": "/opportunities({opp.id})"
```

`regardingobjectid` ist noch breiter polymorph — der Zielentity-Suffix
entscheidet die konkrete Zielentity.

### Nicht-polymorphe Lookups (ohne Zielentity-Suffix)

Manche Lookups zeigen nur auf genau eine Entity. Dann kann der
Zielentity-Teil entfallen — ist aber immer explizit erlaubt:

```json
"ownerid@odata.bind": "/systemusers({userId})"
```

(`ownerid` kann auch auf `teams` zeigen, also eigentlich polymorph.)

**Faustregel:** im Zweifel **immer den Zielentity-Suffix angeben**. Das
funktioniert immer und ist eindeutig.

## Was NICHT geht — die häufigen Fehler

### FALSCH: Lookup ohne @odata.bind

```json
"parentcustomerid": "{acc.id}"              <-- FEHLER
"_parentcustomerid_value": "{acc.id}"       <-- FEHLER (das ist der Read-Format-Name)
```

Dataverse akzeptiert beim Create/Update **nur** den `@odata.bind`-Weg.

### FALSCH: Ziel ist Singular statt Plural

```json
"parentcustomerid_account@odata.bind": "/account({acc.id})"    <-- FEHLER
```

Muss `/accounts(...)` heißen — Plural/EntitySetName.

### FALSCH: GUID in Anführungszeichen

```json
"parentcustomerid_account@odata.bind": "/accounts(guid'...'))"   <-- FEHLER
"parentcustomerid_account@odata.bind": "/accounts('3f2a...')"    <-- FEHLER
```

Nur die blanke GUID in Klammern: `/accounts(3f2a...)`.

### FALSCH: Anderes Feldformat in Filtern

Beim **Lesen** (Filter in Assertions, WaitForRecord etc.) ist das anders!

```json
// In Assert- und WaitForRecord-FILTERN:
"filter": [
  { "field": "parentcustomerid",           "operator": "eq", "value": "{acc.id}" }   // RICHTIG
  { "field": "_parentcustomerid_value",    "operator": "eq", "value": "{acc.id}" }   // FALSCH
]
```

**Im Filter verwendest du den LogicalName** (ohne `_` und ohne `_value`).
Die Engine übersetzt intern.

## Übersicht: Lookup in verschiedenen Kontexten

| Kontext | Format |
|---|---|
| CreateRecord/UpdateRecord fields | `parentcustomerid_account@odata.bind: /accounts(guid)` |
| Filter in Assert / WaitForRecord / Query | `field: parentcustomerid, value: guid` |
| Assert mit `field: "parentcustomerid"` auf Record | `field: parentcustomerid, value: guid` |
| Auslesen per `alias.fields.xxx` | `{con.fields._parentcustomerid_value}` (ausgelesen im `_value`-Format) |

Die letzten drei sehen beim Arbeiten verwirrend aus, weil derselbe Lookup
drei Schreibweisen hat. Die Regel: **beim Schreiben den `@odata.bind`,
beim Lesen im Filter den LogicalName, beim Auslesen eines Feldwerts das
`_xxx_value`**.

## Partner-Entity-Suffix: welche brauche ich?

Wenn ein Lookup polymorph ist (kann auf mehrere Entities zeigen), **musst**
du den Suffix geben. Die wichtigsten Polymorphen:

| Lookup | Mögliche Ziele | Suffixe |
|---|---|---|
| `customerid` (auf opportunity, quote, salesorder, invoice) | account, contact | `customerid_account`, `customerid_contact` |
| `parentcustomerid` (auf contact) | account, contact | `parentcustomerid_account`, `parentcustomerid_contact` |
| `regardingobjectid` (auf activities) | viele (account, contact, opportunity, lead, ...) | `regardingobjectid_account` etc. |
| `ownerid` | systemuser, team | `ownerid_systemuser`, `ownerid_team` |

## Owner setzen (Assign)

Für `ownerid` nimmst du meist `ExecuteRequest` mit `AssignRequest`,
statt es direkt im Create zu setzen. Das ist der Microsoft-empfohlene
Weg und funktioniert mit Privilege-Checks:

```json
{ "action": "ExecuteRequest", "requestName": "Assign",
  "fields": {
    "Target":   { "$type": "EntityReference", "entity": "account", "ref": "acc" },
    "Assignee": { "$type": "EntityReference", "entity": "systemuser",
                  "ref":   "some_user_alias" }
  }
}
```

## Lookup auf einen vorher per WaitForRecord gefundenen Record

Wenn du nicht weißt ob der Record schon existiert:

```json
{ "stepNumber": 3, "action": "WaitForRecord",
  "entity": "opportunities", "alias": "deal",
  "filter": [ { "field": "name", "operator": "eq", "value": "Zieldeal" } ]
},
{ "stepNumber": 4, "action": "CreateRecord", "entity": "tasks",
  "alias":  "task1",
  "fields": {
    "subject": "Follow-up",
    "regardingobjectid_opportunity@odata.bind": "/opportunities({deal.id})"
  }
}
```

Erst Record finden und unter Alias `deal` registrieren, dann im nächsten
Step per `{deal.id}` referenzieren.
