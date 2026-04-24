# Was passiert im Hintergrund

Du musst das nicht im Detail verstehen, um Tests zu schreiben. Aber wenn
du weisst was nach dem "Speichern" des Testruns passiert, kannst du
Fehlerbilder besser einordnen.

## Der Ablauf Schritt fuer Schritt

```
   Du in der Model-Driven-App                        Dataverse Server
   +------------------------+                        +-------------------+
   |                        |                        |                   |
   | 1. jbe_testrun anlegen |                        |                   |
   |    Status = Geplant    |                        |                   |
   |    [Speichern]         |----- Web API Create -->|                   |
   |                        |                        | 2. Record in DB   |
   |                        |                        | 3. CRUD-Trigger-  |
   |                        |                        |    Plugin feuert  |
   |                        |                        |    (Async)        |
   |                        |                        |                   |
   |                        |<---- UI aktualisiert --| 4. Status         |
   |                        |                        |    "Wird aus-     |
   |                        |                        |    gefuehrt"      |
   |                        |                        |                   |
   |                        |                        | 5. Testcases      |
   |                        |                        |    nach Filter    |
   |                        |                        |    lesen          |
   |                        |                        |                   |
   |                        |                        | 6. Pro Testcase:  |
   |                        |                        |    - JSON parsen  |
   |                        |                        |    - Steps        |
   |                        |                        |      ausfuehren   |
   |                        |                        |    - Ergebnis     |
   |                        |                        |      schreiben    |
   |                        |                        |                   |
   |                        |<---- UI aktualisiert --| 7. passed++       |
   |                        |      (jede 2-3s)       |    oder failed++  |
   |                        |                        |                   |
   |                        |                        | 8. Record-Tracker |
   |                        |                        |    - Cleanup oder |
   |                        |                        |      behalten     |
   |                        |                        |                   |
   |                        |<---- UI aktualisiert --| 9. Status         |
   |                        |                        |    "Abgeschlos."  |
   +------------------------+                        +-------------------+
```

## Die beteiligten Komponenten

**Der CRUD-Trigger-Plugin (`RunTestsOnStatusChange`):**

- Registriert auf `Create` und `Update` von `jbe_testrun`
- Filter-Attribut `jbe_teststatus` — feuert nur bei Aenderung dieses
  Felds
- Laeuft Async (asynchronous plugin execution)
- Ist die eigentliche Engine: orchestriert alles

**Die Core-Engine (C# intern):**

- `TestCenterOrchestrator` — liest Testcases, loopt durch Batches
- `TestRunner` — fuehrt einen einzelnen Testcase aus
- `AssertionEngine` — wertet Assert-Actions aus
- `PlaceholderEngine` — loest Platzhalter auf (`{TIMESTAMP}`, `{alias.id}`)
- `RecordTracker` — merkt sich Create-Ergebnisse fuer Cleanup

**Die Entities im Schreib-Betrieb:**

- `jbe_testrun` — wird geupdated (Status, Counter, Summary)
- `jbe_testrunresult` — je einer pro Testcase im Lauf
- `jbe_teststep` — je einer pro Action pro Testcase

**Die Custom API (`jbe_RunIntegrationTests`):**

Alternativer Einstiegspunkt fuer synchrone Runs vom Browser aus. Als
Entwickler-Kollege nutzt du sie nicht direkt — wird nur erwaehnt damit
du weisst: wenn du im Plugin-Trace-Log mal `RunIntegrationTestsApi`
siehst, ist das ein synchron-Start.

## Batching

Bei einem Testrun mit vielen Testcases:

- Die Engine arbeitet in Batches zu ca. **12 Testcases**.
- Jeder Batch laeuft in einem eigenen Plugin-Aufruf (2-Min-Sandbox-Limit).
- Nach jedem Batch wird das `jbe_batchoffset`-Feld am Testrun
  inkrementiert, und ein neuer Batch startet automatisch.
- Max-Lauf-Groesse: ca. 8 Batches = **96 Testcases**.

Fuer noch groessere Laeufe gibt es die CLI (nutzt der Projekt-Owner,
nicht du).

## Record-Tracker und Cleanup

Jedes `CreateRecord` im Testcase wird intern registriert:

```
[
  { entity: "accounts",   id: "3f2a-...", alias: "acc" },
  { entity: "contacts",   id: "a91c-...", alias: "con" },
  { entity: "activities", id: "b84f-...", alias: "tsk" }
]
```

Am Ende des Tests durchlaeuft die Engine diese Liste **rueckwaerts**
und loescht jeden Record. Rueckwaerts, weil die spaeter angelegten
Records oft Lookups auf die frueheren haben — in Rueckwaerts-Reihenfolge
funktioniert das Loeschen ohne Constraint-Verletzung.

**Wenn `jbe_keeprecords: true`:** die Liste wird nicht geloescht, du
siehst alle Records nach dem Lauf im Dataverse.

**Wenn ein Cleanup-Delete fehlschlaegt:** wird im Steps-Tab als Cleanup-
Step mit Ergebnis "Fehler" angezeigt — das Testergebnis (Passed/Failed)
ist davon nicht betroffen. Cleanup-Fehler sind meistens Plugin-Seiten-
effekte die etwas auf dem Record veraendert haben.

## Was die UI aktualisiert

Dynamics hat keinen WebSocket-Live-Feed fuer Records. Die UI hat aber
einen **Auto-Refresh** auf Detail-Seiten, der alle ca. 2-3 Sekunden den
Record neu laedt. Darum siehst du den Zaehler `1/5 bestanden` langsam
wachsen.

Wenn du manuell **F5** druckst, faellt das Auto-Refresh kurz aus. Nicht
schlimm, aber dein Wohlfuehl-Moment "jetzt bin ich bei 3/5" kann damit
kurz verschwinden.

## Wo logge ich mit?

Der volle Text-Log eines Laufs steht im `jbe_testrun.jbe_fulllog`-Feld
(Mehrzeilentext). Das ist der Plaintext-Output, den das Plugin waehrend
der Ausfuehrung gesammelt hat — wertvoll fuer die Fehlersuche bei
komplexen Laeufen. Siehe
[../04-auswerten/05-logs-im-detail.md](../04-auswerten/05-logs-im-detail.md).

## Wenn du mehr wissen willst

Das hier ist ein **Anwender-Handbuch**. Fuer die Produkt-Architektur gibt
es separate Dokumente unter `docs/` — aber die sind nicht Teil deines
Workflows als Test-Autor.
