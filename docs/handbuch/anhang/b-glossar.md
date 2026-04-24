# Glossar

Begriffe und Konzepte in alphabetischer Reihenfolge.

## Action

Ein Schritt in einem Testfall. Typen: `CreateRecord`, `UpdateRecord`,
`DeleteRecord`, `Assert`, `Wait`, `WaitForFieldValue`, `WaitForRecord`,
`RetrieveRecord`, `ExecuteRequest`, `ExecuteAction`. Jede Action ist ein
Objekt im `steps`-Array.

## Alias

Ein frei gewählter Name, der einen per `CreateRecord` (oder
`WaitForRecord`) angelegten Record identifiziert. Wird per `{alias.id}`,
`{RECORD:alias}` oder direkt in `alias`-Feldern referenziert.

## @odata.bind

Die OData-Syntax zum Setzen eines Lookup-Felds beim Create/Update.
Format: `"<lookupname>_<zielentity>@odata.bind": "/<zielentityplural>(<guid>)"`.

## Assert

Eine Action die prüft. Zwei Varianten: `target: "Record"` (prüft ein
Feld eines bekannten Records) und `target: "Query"` (prüft per Abfrage
ob/wie viele Records mit bestimmten Kriterien existieren).

## Async Plugin

Ein Plugin das nach dem Trigger-Event verzögert läuft (nicht sofort
synchron). Braucht nicht in der 2-Min-Sandbox-Grenze zu bleiben.
Seine Seiten-Effekte (erstellte Records, gesetzte Felder) sind erst
nach einer kleinen Zeitspanne sichtbar. Deshalb `WaitForFieldValue` /
`WaitForRecord`.

## Batch

Das Test Center verarbeitet einen Testrun in Batches zu ~12 Testcases.
Jeder Batch läuft in einem eigenen Plugin-Aufruf (2-Min-Grenze). Nach
dem Batch startet automatisch der nächste.

## Cleanup

Automatische Löschung der während des Tests per `CreateRecord`
angelegten Records. Läuft am Testcase-Ende, in umgekehrter
Reihenfolge. Ausgeschaltet per `jbe_keeprecords: true` am Testrun.

## CRUD-Trigger

Das Plugin `RunTestsOnStatusChange`, das auf Create/Update von
`jbe_testrun` registriert ist. Wenn das Feld `jbe_teststatus` auf
`Geplant` steht, feuert das Plugin und startet die Testausführung.

## Custom Action

Eine Action die in der Solution selbst definiert ist (im Gegensatz zu
Microsoft-Standard-Messages). Aufruf per `ExecuteAction`.

## Entity

Eine Tabelle im Dataverse. Hat einen LogicalName (Singular, z.B.
`contact`) und einen EntitySetName (Plural, z.B. `contacts`). Das Test
Center verwendet den **Plural** im `entity`-Feld.

## Failed

Outcome eines Testcases: alle Actions liefen durch, aber mindestens
eine Assert ergab einen unerwarteten Wert. Unterschied zu Error: Error
heißt "Action hat geworfen", Failed heißt "Logik war anders als
erwartet".

## Filter

Der `jbe_testcasefilter`-Wert am `jbe_testrun`: bestimmt welche
`jbe_testcase`-Records ausgeführt werden. Varianten: `*`, `QS-01`,
`QS-*`, `tag:smoke`, `category:Integration`, komma-getrennte Liste.

## Full-Log

Der Text im Feld `jbe_fulllog` des `jbe_testrun`: vollständiges Plugin-
Protokoll des Laufs. Für tiefere Analysen nützlich.

## LogicalName

Der technische Name einer Entity (Singular) oder eines Attributs in
Dataverse. Beispiele: `account`, `contact`, `emailaddress1`,
`parentcustomerid`.

## Lookup

Ein Feld das auf einen anderen Record zeigt. Beim Setzen: `@odata.bind`-
Syntax. Beim Lesen im Filter: der LogicalName (z.B.
`parentcustomerid`). Beim Lesen per `alias.fields.xxx`: der
`_xxx_value`-Name.

## onError

Step-Property die bestimmt was passiert wenn die Action einen Fehler
wirft. Werte: `"stop"` (Test bricht ab, Outcome Error) oder
`"continue"` (Test läuft weiter). Default für `Assert`: `continue`.
Default für alle anderen: `stop`.

## OptionSet

Ein fester Wertebereich (Typ Picklist in Dataverse). Werte sind
Integer-Codes, Labels sind lokalisiert. Beispiel: `statecode` auf
`lead` hat Werte `0=Open`, `1=Qualified`, `2=Disqualified`.

## Platzhalter

Dynamische Referenzen in Feldwerten, werden zur Laufzeit aufgelöst.
Typen: `{TIMESTAMP}`, `{GENERATED:...}`, `{alias.id}`, `{alias.fields.xxx}`,
`{RECORD:alias}`.

## Record

Ein einzelner Datensatz in einer Dataverse-Entity. Auch "Zeile" in der
Tabelle.

## Record-Tracker

Interne Liste der Engine mit allen per `CreateRecord` angelegten
Records. Wird für den Cleanup verwendet.

## Skipped

Outcome eines Testcases: der Test konnte nicht starten, meist wegen
Parse-Fehler in `jbe_definitionjson`.

## stepNumber

Eindeutige Nummer jeder Action im `steps`-Array. Die Engine sortiert
danach (nicht nach JSON-Reihenfolge). Konvention: 1..N, Cleanup-Step
bekommt 9000.

## Step-Tab

Die Sub-Grid auf einem `jbe_testrunresult` mit allen ausgeführten
`jbe_teststep`-Records. Dein wichtigstes Debug-Werkzeug.

## Sub-Grid

Eine Tabelle auf einem Formular, die zugeordnete Records anzeigt. In
der Model-Driven-App klicken auf einen Record darin öffnet den Detail.

## Target (für Assert)

Unterscheidet wie ein Assert Daten holt: `"Record"` (aus einem bekannten
Alias), `"Query"` (per Abfrage mit Filter).

## Testcase

Die Testfall-Definition. Als `jbe_testcase`-Record in Dataverse
gespeichert. JSON-Definition im Feld `jbe_definitionjson`.

## Testrun

Ein konkreter Testlauf. `jbe_testrun`-Record. Enthält Filter, Status,
Zusammenfassung. Wird durch Status-Setzen auf "Geplant" gestartet.

## Testrunresult

Ergebnis pro Testcase innerhalb eines Testruns. Ein Record pro Testcase
mit Outcome und ggf. Fehlermeldung.

## Teststep

Detail-Record pro Action. Enthält Schrittnummer, Dauer, Ergebnis,
ggf. Assertion-Details.

## Throttling

Rate-Limiting von Dataverse gegen Überlast (HTTP 429). Das Test Center
hat automatische Retries. Bei sehr vielen parallelen Runs kann's
trotzdem spürbar werden.

## TIMESTAMP

Platzhalter `{TIMESTAMP}`, liefert den aktuellen ISO-UTC-Timestamp.
Varianten: `{TIMESTAMP_MINUS_1H}`, `{TIMESTAMP_PLUS_1H}`.

## Trigger (in Dataverse)

Ein Ereignis (Create/Update/Delete) das ein Plugin startet. Der CRUD-
Trigger-Plugin des Test Centers reagiert auf Änderungen am
`jbe_teststatus`-Feld.
