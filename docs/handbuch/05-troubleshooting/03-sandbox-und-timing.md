# Sandbox-Limits und Timing

Dynamics-Plugins laufen in einer Sandbox mit harten Grenzen. Wer lange
Tests schreibt, rennt früher oder später in diese Grenzen. Hier die
wichtigsten.

## Die 2-Minuten-Grenze

**Jeder synchrone Plugin-Aufruf darf maximal 2 Minuten laufen.** Danach
bricht die Sandbox den Aufruf ab.

Der Test Center CRUD-Trigger läuft **asynchron**, hat damit technisch
erst einmal mehr Luft. Aber intern arbeitet er in Batches:

- Ein Batch = ca. **12 Testcases**
- Jeder Batch läuft wieder **innerhalb eines 2-Min-Limits**
- Nach dem Limit startet der nächste Batch automatisch

**Was das für dich bedeutet:** Einzelne Tests sollten unter **90 Sekunden**
bleiben. Darüber hinaus wirst du unzuverlaessig: mal läuft's, mal
Timeout.

## Typische Zeitfresser

### 1. Lange `Wait`-Schritte

```json
{ "action": "Wait", "waitSeconds": 60 }
```

60 Sekunden nur für "Plugin-Chain abwarten". Das ist Verschwendung,
wenn das Plugin meist schon nach 3 Sekunden fertig ist.

**Besser:** `WaitForFieldValue` mit großzügigem Timeout. Wartet so
lange wie noetig, aber nicht laenger.

### 2. `WaitForRecord` mit großem Timeout ohne Prüfung

```json
{ "action": "WaitForRecord", "entity": "tasks",
  "filter": [],
  "timeoutSeconds": 120 }
```

Wenn das Plugin den Record gar nicht erzeugt, wartet der Test 120
Sekunden umsonst. Bei 8 solchen Tests im Batch -> Timeout garantiert.

**Besser:** Timeout realistisch setzen. Wenn dein Plugin typisch 5s
braucht, reichen 15-20s Timeout — mit Puffer.

### 3. Sehr viele Records in einem Test

Wenn ein Test 50 `CreateRecord`-Steps hat, dauert er allein dafür
schon 15+ Sekunden. Plus jede Action triggert Plugins, die eigene
Laufzeiten haben.

**Besser:** Testfälle thematisch splitten. Ein Test für eine
fachliche Frage, nicht 50 Patterns auf einmal.

## Richtlinien für Testgröße

| Test-Typ | Richtwert |
|---|---|
| Einzel-CRUD | 5-15 Sekunden |
| Plugin-Kette mit 1-2 Plugins | 10-30 Sekunden |
| Merge, QualifyLead, WinOpportunity | 30-60 Sekunden |
| Multi-Step-Szenario mit Waits | bis 90 Sekunden |
| > 90 Sekunden | **aufteilen** |

## Fehler-Pattern erkennen

Wenn ein Test mal `Passed`, mal `Error "Sandbox timeout"` ist, hat er
Timing-Probleme. **Nicht** "ist halt mal so". Nachjustieren.

Typische Ursachen:

- Tests die mehrere `Wait`-Schritte aufaddieren (`Wait 10, Wait 5,
  Wait 15` = 30 Sekunden fix)
- `WaitForFieldValue` mit realistischem Timeout aber unrealistischem
  Plugin-Verhalten
- Tests die in Batches mit anderen langsamen Tests laufen (siehe unten)

## Batches und Nachbartests

Der Run arbeitet in Batches zu ~12 Tests. **Jeder Batch hat 2 Minuten
insgesamt**, nicht pro Test. Wenn ein Test 80 Sekunden braucht, bleiben
40 Sekunden für 11 andere Tests.

**Deshalb:** vermeide sehr lange Tests im gleichen Batch mit vielen
anderen. Wenn du weißt dass ein Test 60 Sekunden braucht:

- Tagge ihn `slow`
- Starte separaten Run für `tag:slow` — dann ist er alleine im Batch
- Oder markiere einen eigenen Filter-Präfix z.B. `SLOW-*`

## Throttling durch Dataverse

Dataverse hat Limits gegen Überlast:

- Max 6000 API-Calls pro 5-Min-Fenster pro Service-User
- Max 52 gleichzeitige Calls pro User
- `HTTP 429 Too Many Requests` bei Verletzung

Das Test Center hat automatische Retries mit Backoff. Du siehst das nur
an laengeren Laufzeiten. Wenn du mehrere riesige Testruns parallel
startest, kann's dich treffen.

## Tipps zum Optimieren

### Nutze `columns` sparsam

Jedes Feld in `columns` wird vom Server frisch gelesen. Je mehr Felder,
desto laenger der Create-Step. Für typische Tests reicht 0-2 Felder.

### `RetrieveRecord` nur wenn noetig

Das ist ein extra HTTP-Call. Wenn die AssertionEngine sowieso frisch
liest, ist das Retrieve redundant.

### Mehrere `Assert` nach einer Action batchen

Wenn alle Asserts denselben Record prüfen, kannst du `target: Record`
nutzen. Das ist billiger als mehrere `target: Query` (jede wäre eine
eigene Dataverse-Abfrage).

### Wait-Step-Zeiten realistisch halten

```json
// Viel zu konservativ (120s für eine Plugin-Chain die 5s braucht):
{ "action": "Wait", "waitSeconds": 120 }

// Realistisch (mit 20% Puffer):
{ "action": "Wait", "waitSeconds": 6 }
```

Wenn du das Timing nicht kennst: Erst mal großzügig schaetzen, den
Test 3x laufen lassen, dann das Maximum + 20% einstellen.

## Kann ich einen Test asynchron starten und später prüfen?

Nein, der Lauf ist pro Testrun ein Block. Alternative:

- Ein Test legt einen Record an, der ein langes Plugin triggert
  (`keeprecords: true`).
- Ein **zweiter Test**, später gestartet, prüft den Zustand.

Zwei getrennte `jbe_testrun`-Records.

## Überprüfung deiner Tests

Vor dem Committen eines neuen Tests:

```
[ ] Test läuft < 90 Sekunden?
[ ] Wait-Schritte notwendig (nicht dekorativ)?
[ ] WaitForFieldValue/WaitForRecord mit sinnvollem Timeout?
[ ] columns-Liste enthaelt nur was du wirklich brauchst?
[ ] Der Test kann 10 Mal hintereinander ohne Kollision laufen?
```
