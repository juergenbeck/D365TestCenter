# Doku- und Reporting-Lebenszyklus (ADR-0008)

Dieses Dokument beschreibt, wie das Test Center den vollen Dokumentations-Lebenszyklus eines Testfalls
schließt: von der Beschreibung über die Ausführung bis zum Bericht und zurück. Die einzelnen CLI-Commands
sind in der [CLI-Referenz](08_cli-referenz.md) im Detail beschrieben; hier geht es um den Zusammenhang.

## Ausgangslage: drei Welten, eine Wahrheit

Ein Testfall existiert in drei Repräsentationen, die konsistent gehalten werden müssen:

| Welt | Inhalt | Rolle |
|---|---|---|
| **Markdown-Definition** | YAML-Frontmatter (id, titel, status, suite_tags, ticket, `ergebnis_historie`, ...) + fachliche Doku-Sektionen + ein eingebetteter JSON-Block | **Single Source of Truth (SSOT)** |
| **Pack / `jbe_testcase`** | lauffähiger Testfall in Dataverse | aus der Definition generiert |
| **`jbe_testrun` / `-result` / `-step`** | Ergebnisse, KPIs, Timing, Schritte | von der Engine geschrieben |

Ohne Werkzeug driften diese auseinander: Ergebnisse liegen in Dataverse, müssen aber von Hand in die
Ergebnis-Historie der Definition zurückgepflegt werden; die fachliche Doku erreicht das Test Center nie. Der
Doku-/Reporting-Lebenszyklus löst das: **Doku rein, Ergebnis zurück, Bericht raus.** Die Markdown-Definition
bleibt dabei immer SSOT.

## Der Kreislauf

```
   Markdown-Definition (SSOT)
        |  build-pack            erzeugt das Suite-Pack (Doku inklusive)
        v
   Suite-Pack (JSON)
        |  import-pack / sync-docs   schreibt jbe_testcase (+ jbe_documentation)
        v
   jbe_testcase  --run-->  jbe_testrun / -result / -step
        |  sync-results          schreibt ergebnis_historie zurueck in die Definition
        |  report                Durchfuehrungsbericht (Markdown / HTML / PDF)
        |  sync-zephyr           Ergebnis-Upload nach Zephyr Scale
        v
   inventory                    Management-Uebersicht ueber alle Definitionen
```

## Die Etappen

### 1. Definition schreiben (SSOT)

Ein Testfall wird als Markdown-Datei gepflegt: Frontmatter mit Metadaten, fachliche Pflicht-Sektionen
(Zweck, Datenkonstellation, Vorbedingungen, Ablauf, Erwartetes Ergebnis) und ein eingebetteter D365TC-JSON-Block
(`steps`). Das JSON-Schema beschreibt [04_testfall-spezifikation.md](04_testfall-spezifikation.md).

### 2. `build-pack` — Pack erzeugen

`build-pack` extrahiert aus den Definitionen ein importierbares Suite-Pack und übernimmt die fachliche Doku aus
den Pflicht-Sektionen (`documentation`). Archivierte und Entwurf-Definitionen werden übersprungen. Ein
integrierter Lint prüft Pflichtfelder, JSON-Parsbarkeit und `testId == id`.

### 3. `import-pack` (oder `sync-docs`) — nach Dataverse

`import-pack` schreibt das Pack idempotent nach `jbe_testcase` (CREATE+UPDATE), inklusive `jbe_documentation`.
Damit landet die fachliche Doku im Test Center und der HTML-Client zeigt sie im Doku-Tab. `sync-docs` ist der
schlanke Alt-Weg, der nur die Doku durchreicht (ohne die Testfälle neu zu schreiben).

### 4. `run` — ausführen

`run` führt die Testfälle aus und schreibt `jbe_testrun` / `-result` / `-step`. Mit `--sync-defs` kann der
Round-Trip (Etappe 5) direkt im Anschluss erfolgen.

### 5. `sync-results` — Ergebnis zurück (Round-Trip)

`sync-results` liest die Lauf-Ergebnisse und schreibt sie in die `ergebnis_historie` im Frontmatter der
passenden Definition (Matching `testId == id`). Das Frontmatter ist SSOT; Body-Tabelle und README-Aggregat
werden daraus zwischen Markern gerendert. Damit endet die manuelle Historien-Pflege.

### 6. `report` — Durchführungsbericht (drei Formate)

`report` verheiratet die fachliche Doku (aus den Definitionen) mit den Ergebnissen (aus Dataverse) zu einem
Suite-Durchführungsbericht. Zwei Detailstufen (`--detail compact|full`) und drei Ausgabeformen:

| Format | Zweck |
|---|---|
| **Markdown** (`--format md`) | versioniert, reviewbar, intern |
| **HTML / PDF** (`--format html|pdf`) | präsentierbarer Stakeholder-Bericht (PDF via Playwright) |

### 7. `sync-zephyr` — nach Zephyr Scale

`sync-zephyr` spielt die Ergebnisse ins Test-Management (Zephyr Scale Data Center, ATM 1.0): pro Lauf ein neuer
Cycle, dann Bulk-Upload. Matching über `zephyr_key` im Frontmatter. Optional per-Step `scriptResults`
(siehe CLI-Referenz, Stolperfalle Index-Matching). Schreibt in ein externes System.

### 8. `inventory` — Management-Sicht

`inventory` erzeugt aus allen Definitionen eine Übersicht (Status-/Domänen-Rollup + Tabelle pro Domäne mit
Lauf-Trend aus der `ergebnis_historie`). Rein lesend, ohne Dataverse - der Management-Blick auf die ganze
Test-Landschaft.

## Leitprinzipien

1. **Die Markdown-Definition bleibt SSOT.** Das Test Center ersetzt sie nicht, es schließt den Kreis.
2. **Logik im Core, CLI als dünner Wrapper** (ADR-0003): Reporting, Sync und Doku-Parsing sind reine, testbare
   Core-Funktionen; die CLI macht IO, HTTP und Dataverse-Zugriff.
3. **Generisch, nicht projektspezifisch** (ADR-0002): Das Definition-Format ist Produkt-Konvention; jeder Nutzer
   bekommt dieselbe doku-getriebene Mechanik.

Detail zu jedem Command: [CLI-Referenz](08_cli-referenz.md).
