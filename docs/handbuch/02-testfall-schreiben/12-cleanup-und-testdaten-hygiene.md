# Cleanup und Testdaten-Hygiene

Tests laufen gegen eine **echte Umgebung** — jeder Lauf muss seine Spuren
restlos entfernen, sonst akkumulieren Testdaten, verfälschen Folgeläufe und
Auswertungen. Dieses Kapitel erklärt, wie das automatische Cleanup arbeitet,
welche Records es von allein kennt, und mit welchen drei Werkzeugen du ihm
Records beibringst, die es nicht von allein kennt:

| Werkzeug | Für | Seit |
|---|---|---|
| [`trackForCleanup`](#werkzeug-1-trackforcleanup-auf-waitforrecord--findrecord) | EINEN serverseitig erzeugten Record, den du per Query findest | 2026-07-17 (FB-54) |
| [`TrackRecord`](#werkzeug-2-die-action-trackrecord) | EINEN serverseitig erzeugten Record, dessen ID du schon hast (API-Output) | 2026-07-17 (FB-54) |
| [`cleanupChildren`](#werkzeug-3-cleanupchildren-für-plugin-erzeugte-kind-mengen) | VIELE plugin-erzeugte Kinder eines Records, Anzahl dynamisch | 2026-07-23 (ADR-2026-07-23-0808) |

## Wie das Cleanup abläuft

Nach dem letzten Step eines Testfalls (auch nach FAILED/ERROR) läuft die
Cleanup-Phase:

1. **EnvironmentVariable-Restore zuerst** — alle per `SetEnvironmentVariable`
   veränderten Variablen werden auf ihren Vorher-Zustand zurückgesetzt, selbst
   wenn danach Record-Deletes scheitern. So kippt eine hängengebliebene
   Test-Konfiguration keine Folgetests.
2. **Record-Löschung in LIFO-Reihenfolge** — die Löschliste wird rückwärts
   abgearbeitet: der zuletzt registrierte Record fällt zuerst. Dadurch stimmen
   Abhängigkeiten automatisch, wenn du Eltern vor Kindern anlegst (Account ->
   Contact: der Contact wird zuerst gelöscht).
3. Für Records mit deklarierten [`cleanupChildren`](#werkzeug-3-cleanupchildren-für-plugin-erzeugte-kind-mengen)
   werden VOR dem Record selbst dessen Kinder abgeräumt (Details unten).

Im Steps-Tab erscheint das Cleanup als **eine** zusammengefasste Zeile
(StepNumber 9000, z.B. `Cleanup: 5 gelöscht, 0 fehlgeschlagen`). Mit
`jbe_keeprecords = true` am Testrun wird nichts gelöscht; die Cleanup-Zeile
dokumentiert dann, wie viele Records bewusst stehen blieben.

**Der Test-Outcome bleibt vom Cleanup unberührt:** Ein fachlich grüner Test
wird nicht rot, weil das Aufräumen scheiterte. Damit ein Datenleck trotzdem
nie still bleibt, weist der Lauf Cleanup-Fehler aggregiert aus — als Banner
in der CLI-Summary (`CLEANUP-WARNUNG: N Aufräum-Operation(en) fehlgeschlagen`),
als `cleanupFailedCount` im Ergebnis und im Audit-Kommentar
(sync-zephyr/sync-devops).

> **Seit 2026-07-23 ist die Warnzahl ein echter Rest-Indikator.** Vorher
> zählten auch harmlose Doppel-Deletes mit (ein getrackter Record, den die
> Plattform beim Löschen seines Eltern-Records bereits per Cascade
> mitgenommen hatte, warf beim eigenen Delete 404). Solche Fälle zählen
> jetzt als „bereits geräumt". Steht dort eine Zahl > 0, liegen wirklich
> Daten in der Umgebung.

## Was automatisch getrackt wird — und was nicht

| Quelle | In der Löschliste? | Warum |
|---|:---:|---|
| `CreateRecord` | **ja, immer** | Der Test hat den Record erzeugt. |
| `FindRecord` / `WaitForRecord` (Default) | **nein** | Ein GEFUNDENER Record ist Bestand, kein erzeugter. Würde er gelöscht, träfe das geteilte Stammdaten (Stammdaten-Schutz, 2026-06-23). |
| `ExecuteRequest`-/Custom-API-Outputs | **nein** | Die Engine weiß nicht, ob ein Output eine erzeugte Record-ID ist. |
| Von Plugins/APIs **serverseitig** erzeugte Records | **nein** | Der Engine unbekannt — genau dafür gibt es die drei Werkzeuge unten. |

Die Lücke ist tückisch: serverseitig erzeugte Records bleiben nicht nur
selbst liegen — hängen sie mit **Restrict-Delete-Verhalten** an einem
getrackten Record, blockieren sie auch noch dessen Löschung
(`The object you tried to delete is associated with another object`,
bei Kaskaden: `Cascade Delete failed due to cascade restrict relation`).
Der Lauf bleibt grün, das Leck wächst mit jedem Durchlauf (FB-54, belegt
2026-07-17 mit 19 Account-Waisen; 2026-07-22 mit ~105 akkumulierten
Test-Leistungen einer Restrict-gehärteten Beziehung).

## Werkzeug 1: `trackForCleanup` auf WaitForRecord / FindRecord

Wenn die getestete API einen Record erzeugt (z.B. eine Beleg-API erstellt
eine Rechnung), findest du ihn typischerweise ohnehin per `WaitForRecord`,
um ihn zu prüfen. Mit `trackForCleanup: true` nimmst du den Fund zusätzlich
in die Löschliste auf:

```json
{ "stepNumber": 6, "action": "WaitForRecord",
  "entity": "invoices",
  "alias": "beleg",
  "filter": [ { "field": "customerid", "operator": "eq", "value": "{acc.id}" } ],
  "trackForCleanup": true,
  "timeoutSeconds": 60,
  "description": "Von der API erzeugte Rechnung finden UND fürs Cleanup vormerken" }
```

- Default ist **false** — setze `true` NUR, wenn der gefundene Record
  während DIESES Laufs von der getesteten API erzeugt wurde. Ein
  `trackForCleanup: true` auf einem Bestands-Record löscht Stammdaten.
- Die LIFO-Reihenfolge passt automatisch: der Beleg wird nach dem Account
  registriert, also vor ihm gelöscht — der Restrict-Blocker löst sich auf.

## Werkzeug 2: die Action `TrackRecord`

Liefert die getestete API die erzeugte ID als Output-Parameter, brauchst du
keine Query — `TrackRecord` registriert die bekannte ID direkt in Registry
und Löschliste:

```json
{ "stepNumber": 5, "action": "ExecuteRequest",
  "requestName": "lm_CancelInvoice",
  "outputAlias": "cancel",
  "fields": { "Target": { "$type": "EntityReference", "LogicalName": "invoice", "Id": "{inv.id}" } } },
{ "stepNumber": 6, "action": "TrackRecord",
  "entity": "invoices",
  "recordId": "{cancel.outputs.GutschriftInvoiceId}",
  "alias": "gutschrift",
  "description": "Serverseitig erzeugte Storno-Gutschrift fürs Cleanup vormerken" }
```

- `recordId` ist platzhalterauflösbar. Ein **unauflösbarer Platzhalter oder
  eine Nicht-GUID ist ein harter Error** — kein stilles Nichts-Tracken, damit
  ein Tippfehler die Lücke nicht unbemerkt wieder öffnet.
- **Dedup:** Zeigt `recordId` auf einen bereits getrackten Record, entsteht
  kein zweiter Löschlisten-Eintrag (und damit kein 404-Doppel-Delete).
- Mehrere erzeugte Records = je ein `TrackRecord` pro Output.

## Werkzeug 3: `cleanupChildren` für plugin-erzeugte Kind-MENGEN

### Das Problem, das die beiden anderen Werkzeuge nicht lösen

`trackForCleanup` und `TrackRecord` deklarieren **einzelne, benennbare**
Records. Es gibt aber Server-Seiteneffekte, bei denen an einem Test-Record
eine ganze **Menge** von Kindern entsteht, deren Anzahl und Zusammensetzung
du nicht statisch deklarieren kannst:

- Ein Verteilungs-Plugin erzeugt je Monat des Leistungszeitraums eine
  Monatszeile — 3, 12 oder 60 Records, je nach Testdaten.
- Ändert der Test den Zeitraum, werden Zeilen asynchron **gelöscht und neu
  erzeugt** — eine beim Fund getrackte Zeile kann beim Cleanup schon nicht
  mehr existieren, dafür gibt es neue, die nie getrackt wurden.
- Hängen diese Kinder per **Restrict-Delete** am Test-Record, scheitert
  dessen Cleanup-Delete dauerhaft; hängen sie per **RemoveLink** daran,
  geht der Delete zwar durch, nullt aber den Lookup — die Kinder bleiben
  als unauffindbare Waisen zurück.

### Die Lösung: Kind-Beziehung am Parent deklarieren

```json
{ "stepNumber": 2, "action": "CreateRecord",
  "entity": "lm_bestellungs",
  "alias": "lsp",
  "fields": {
    "lm_beschreibung": "JBE Test LSP {TIMESTAMP}",
    "lm_beginnzeitpunkt": "2027-01-01",
    "lm_ende": "2027-03-31",
    "lm_verkaufnettobetragvorsteuern": 3000,
    "lm_umsatzverteilung": 105710000,
    "lm_leistungid@odata.bind": "/lm_leistungs({lst.id})"
  },
  "cleanupChildren": [
    { "entity": "lm_umsatzplans", "lookupField": "lm_bestellungid" }
  ],
  "description": "Position anlegen; das Verteilungs-Plugin erzeugt async Monatszeilen,
                  die beim Cleanup vor der Position abgeräumt werden" }
```

`cleanupChildren` ist eine Liste von Kind-Beziehungen, jede mit:

| Feld | Pflicht | Bedeutung |
|---|:---:|---|
| `entity` | ja | Kind-Entität, EntitySetName (Plural) wie überall im Test-JSON. |
| `lookupField` | ja | Lookup-Feld der KIND-Entität, das auf den Record dieses Steps zeigt. |

Gültig auf `CreateRecord` sowie — nur zusammen mit Tracking — auf
`FindRecord`/`WaitForRecord` (`trackForCleanup: true`) und `TrackRecord`.
Ohne Tracking wird die Deklaration ignoriert (ein Record, der nicht gelöscht
wird, braucht keine Kind-Räumung). Fehlt `entity` oder `lookupField`, wirft
der Cleanup einen klaren Fehler statt still nichts zu tun.

### Semantik im Detail

1. **Query zur Cleanup-Zeit, nicht zur Step-Zeit.** Beim Cleanup-Delete des
   Records fragt die Engine je deklarierter Beziehung alle Kinder ab
   (`lookupField` = Record-ID, in 500er-Seiten) und löscht sie VOR dem
   Record selbst. Weil die Query erst im Cleanup läuft, erfasst sie die
   **finale** Menge — auch Kinder, die lange nach dem Step asynchron
   entstanden sind oder gewandert sind.
2. **Genau EINE Ebene, kein Metadaten-Discovery.** Die Engine löscht exakt
   die deklarierte Beziehung — sie ermittelt NICHT selbstständig per
   Metadaten, was sonst noch blockieren könnte, und steigt nicht rekursiv in
   Kinder von Kindern. Das ist eine bewusste Design-Entscheidung
   (ADR-2026-07-23-0808): Der deterministische Cleanup-Grundsatz „gelöscht
   wird nur, was der Test-Autor benennt" (ADR-2026-07-17-1801, dort wurde
   die automatische Abhängigkeits-Auflösung verworfen) gilt weiter. Haben
   die Kinder selbst Restrict-Kinder, deklariere die Beziehung an deren
   eigenem getrackten Parent.
3. **LIFO bleibt unverändert.** Die Kind-Räumung hängt am jeweiligen
   Parent-Delete; die Reihenfolge der Parents untereinander bestimmt weiter
   die Registrier-Reihenfolge.
4. **Warum Kinder eines Test-Records gefahrlos löschbar sind:** Ein
   Bestands-Record kann keinen Lookup auf einen Record tragen, der erst im
   Test entstanden ist — Kinder eines im Lauf erzeugten Parents sind per
   Konstruktion Lauf-Artefakte. (Ausnahme: ein Test, der Bestands-Records
   aktiv auf den Test-Record umhängt — dann die Deklaration schlicht
   weglassen.)

### Denormalisierte Lookups doppelt nutzen

Trägt die Kind-Entität MEHRERE Lookups in deine Test-Hierarchie, kannst du
die Deklaration auf mehreren Ebenen setzen — Redundanz ist harmlos (die
zweite Query findet 0 Kinder oder räumt Reste, 404 ist toleriert). Beispiel
aus der LM-Suite: `lm_umsatzplan`-Zeilen tragen `lm_bestellungid` (Position)
UND denormalisiert `lm_leistungid` (Leistung). Deklariert werden beide:

```json
{ "stepNumber": 1, "action": "CreateRecord",
  "entity": "lm_leistungs",
  "alias": "lst",
  "fields": { "lm_bezeichnung": "JBE Test Leistung {TIMESTAMP}", "lm_gesamtausgaben": 0 },
  "cleanupChildren": [
    { "entity": "lm_umsatzplans", "lookupField": "lm_leistungid" }
  ] }
```

Das räumt beim Leistungs-Delete ALLE Umsatzplan-Zeilen der Leistung — die
Monatszeilen der Positionen (falls deren eigener Delete sie noch nicht
erwischt hat) und die positionslosen Budget-Zeilen, die sonst beim
RemoveLink-Delete der Leistung zu unauffindbaren Waisen würden.

### Race-Toleranzen: wenn ein Plugin parallel dieselben Kinder löscht

Kind-Mengen, die ein Plugin erzeugt, baut oft auch ein Plugin wieder ab —
und der Abbau-Job kann GLEICHZEITIG mit der Cleanup-Kind-Löschung laufen
(Beispiel: der Cleanup löscht zuerst die getrackte Buchung, deren
Delete-Plugin räumt asynchron die Budget-Zeilen ab, während der Cleanup
dieselben Zeilen über die Deklaration der Leistung löscht). Die Engine
behandelt die beiden möglichen Kollisionen:

| Kollision | Verhalten |
|---|---|
| Kind ist beim Delete **schon weg** (404 / ObjectDoesNotExist 0x80040217) | Zählt als geräumt — Ziel erreicht, kein Fehler. |
| Plattform meldet `More than one concurrent Delete requests detected` (beide löschen **gerade jetzt**) | Wird NICHT blind als erledigt gewertet: die Engine wartet 1 s und fragt die Kinder erneut ab. Ist die Query leer, hat der parallele Job gewonnen — fertig. Taucht das Kind wieder auf (der parallele Delete scheiterte), wird es erneut gelöscht. Maximal 10 Runden, danach sichtbarer Fehler. |

Jeder ANDERE Fehler beim Kind-Delete (Berechtigung, eigener Restrict-Blocker
des Kindes, Plugin-Abbruch) bricht die Kind-Räumung sichtbar ab und zählt
als Cleanup-Fehler; der Parent-Delete wird trotzdem versucht (und scheitert
dann typischerweise ebenfalls sichtbar). Nichts wird still geschluckt.

**Amok-Schutz:** Maximal 10.000 Kinder je deklarierter Beziehung. Mehr
deutet auf eine falsch deklarierte Beziehung (z.B. Lookup eines
Stammdaten-Parents) — der Cleanup bricht mit klarer Meldung ab, statt
weiterzulöschen.

### Log-Sichtbarkeit

Jede Räumung erscheint im Step-Log (`jbe_fulllog` bzw. CLI-Ausgabe):

```
Cleanup...
    Cleanup-Kinder: 3x 'lm_umsatzplans' via 'lm_bestellungid' von lm_bestellung <id> gelöscht
    Bereits gelöscht (kaskadiert): lm_bestellung <id>
    Cleanup: 4 gelöscht, 0 fehlgeschlagen
```

## Welches Werkzeug wofür?

| Situation | Werkzeug |
|---|---|
| Record selbst per `CreateRecord` angelegt | nichts nötig (automatisch getrackt) |
| API erzeugt EINEN Record, du prüfst ihn eh per `WaitForRecord` | `trackForCleanup: true` |
| API liefert die erzeugte ID als Output | `TrackRecord` |
| Plugin erzeugt N Kinder an einem getrackten Record (N dynamisch, ggf. Restrict- oder RemoveLink-Beziehung) | `cleanupChildren` am Step des Parents |
| Kind hat selbst wieder Restrict-Kinder | zusätzliche `cleanupChildren`-Deklaration am getrackten Parent der Zwischenebene (keine automatische Rekursion) |
| Bestands-Record wird nur GELESEN | nichts — und `trackForCleanup` ausdrücklich NICHT setzen |

## Troubleshooting

- **`CLEANUP-WARNUNG: N Aufräum-Operation(en) fehlgeschlagen`** — seit
  2026-07-23 ein echter Rest-Indikator (keine Doppel-Delete-Artefakte mehr).
  Details stehen je Testfall in der Cleanup-Zeile des Steps-Tabs und im
  `jbe_fulllog`; die erste Fehlermeldung nennt Entität und ID.
- **`The object you tried to delete is associated with another object`**
  bzw. `Cascade Delete failed due to cascade restrict relation ...` im
  Cleanup — an dem Record hängen nicht deklarierte Restrict-Kinder. Die
  Fehlermeldung der Kaskade nennt die Kind-Entität; deklariere sie per
  `cleanupChildren` am passenden Parent-Step.
- **Waisen trotz grünem Cleanup** (nur bei RemoveLink-Beziehungen möglich,
  weil der Parent-Delete nicht blockiert) — Read-back fahren:
  Kind-Entität nach dem Test-Namensmuster bzw. dem Lauf-Zeitfenster
  abfragen. Fix: `cleanupChildren` am Parent deklarieren.
- **`cleanupChildren: Konkurrierende Deletes ... klingen nicht ab`** — ein
  paralleler Prozess löscht dieselben Kinder dauerhaft erfolglos (z.B.
  blockt ein tieferer Restrict beide). Die Kette der Kind-Beziehungen
  prüfen; ggf. eine weitere Deklaration auf der Zwischenebene.
- **Pre-Run-Validator:** `cleanupChildren` ist ein bekannter Step-Key (kein
  `STEP_KEY_UNKNOWN`); ein Tippfehler wie `cleanupChilds` wird dagegen als
  unbekannter Key gemeldet. Fehlende Pflichtfelder (`entity`/`lookupField`)
  meldet der Cleanup zur Laufzeit als Fehler.

## Verfügbarkeit

Engine-Feature im geteilten Core (Commit `9b1b984` für FB-54, `297f760` für
`cleanupChildren` + Toleranzen): Der **CLI-Pfad** nutzt es sofort mit einer
neu publizierten CLI; der **Plugin-/Worker-Pfad** übernimmt es mit dem
jeweils nächsten Plugin-Deploy. Referenzen:
[ADR 2026-07-17 1801](https://github.com/juergenbeck/D365TestCenter-Workspace/blob/master/02_decisions/adr/ADR-2026-07-17-1801-cleanup-serverseitig-erzeugte-records.md)
(Tracking serverseitig erzeugter Records, Cleanup-Sichtbarkeit) und
[ADR 2026-07-23 0808](https://github.com/juergenbeck/D365TestCenter-Workspace/blob/master/02_decisions/adr/ADR-2026-07-23-0808-cleanup-kind-deklaration.md)
(Kind-Deklaration, 404-/Konflikt-Toleranz).
