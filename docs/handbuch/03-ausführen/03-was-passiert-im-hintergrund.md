# Was passiert im Hintergrund

Du musst das nicht im Detail verstehen, um Tests zu schreiben. Aber wenn
du weißt was nach dem "Speichern" des Testruns passiert, kannst du
Fehlerbilder besser einordnen.

## Der Ablauf Schritt für Schritt

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
- Filter-Attribut `jbe_teststatus` — feuert nur bei Änderung dieses
  Felds
- Läuft Async (asynchronous plugin execution)
- Ist die eigentliche Engine: orchestriert alles

**Die Core-Engine (C# intern):**

- `TestCenterOrchestrator` — liest Testcases, loopt durch Batches
- `TestRunner` — führt einen einzelnen Testcase aus
- `AssertionEngine` — wertet Assert-Actions aus
- `PlaceholderEngine` — löst Platzhalter auf (`{TIMESTAMP}`, `{alias.id}`)
- `RecordTracker` — merkt sich Create-Ergebnisse für Cleanup

**Die Entities im Schreib-Betrieb:**

- `jbe_testrun` — wird geupdated (Status, Counter, Summary)
- `jbe_testrunresult` — je einer pro Testcase im Lauf
- `jbe_teststep` — je einer pro Action pro Testcase

**Die Custom API (`jbe_RunIntegrationTests`):**

Alternativer Einstiegspunkt für synchrone Runs vom Browser aus. Als
Entwickler-Kollege nutzt du sie nicht direkt — wird nur erwähnt damit
du weißt: wenn du im Plugin-Trace-Log mal `RunIntegrationTestsApi`
siehst, ist das ein synchron-Start.

## Batching

Bei einem Testrun mit vielen Testcases:

- Die Engine arbeitet in Batches zu ca. **12 Testcases**.
- Jeder Batch läuft in einem eigenen Plugin-Aufruf (2-Min-Sandbox-Limit).
- Nach jedem Batch wird das `jbe_batchoffset`-Feld am Testrun
  inkrementiert, und ein neuer Batch startet automatisch.
- Max-Lauf-Größe: ca. 8 Batches = **96 Testcases**.

Für noch größere Läufe gibt es die CLI (nutzt der Projekt-Owner,
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

Am Ende des Tests durchläuft die Engine diese Liste **rückwärts**
und löscht jeden Record. Rückwärts, weil die später angelegten
Records oft Lookups auf die früheren haben — in Rückwärts-Reihenfolge
funktioniert das Löschen ohne Constraint-Verletzung.

**Wenn `jbe_keeprecords: true`:** die Liste wird nicht gelöscht, du
siehst alle Records nach dem Lauf im Dataverse.

**Wenn ein Cleanup-Delete fehlschlägt:** wird im Steps-Tab als Cleanup-
Step mit Ergebnis "Fehler" angezeigt — das Testergebnis (Passed/Failed)
ist davon nicht betroffen. Cleanup-Fehler sind meistens Plugin-Seiten-
effekte die etwas auf dem Record verändert haben.

## Was die UI aktualisiert

Dynamics hat keinen WebSocket-Live-Feed für Records. Die UI hat aber
einen **Auto-Refresh** auf Detail-Seiten, der alle ca. 2-3 Sekunden den
Record neu laedt. Darum siehst du den Zähler `1/5 bestanden` langsam
wachsen.

Wenn du manuell **F5** druckst, fällt das Auto-Refresh kurz aus. Nicht
schlimm, aber dein Wohlfuehl-Moment "jetzt bin ich bei 3/5" kann damit
kurz verschwinden.

## Wo logge ich mit?

Der volle Text-Log eines Laufs steht im `jbe_testrun.jbe_fulllog`-Feld
(Mehrzeilentext). Das ist der Plaintext-Output, den das Plugin während
der Ausführung gesammelt hat — wertvoll für die Fehlersuche bei
komplexen Läufen. Siehe
[../04-auswerten/05-logs-im-detail.md](../04-auswerten/05-logs-im-detail.md).

## Wenn du mehr wissen willst

Das hier ist ein **Anwender-Handbuch**. Für die Produkt-Architektur gibt
es separate Dokumente unter `docs/` — aber die sind nicht Teil deines
Workflows als Test-Autor.
