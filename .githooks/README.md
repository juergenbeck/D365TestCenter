# Versionierte Git-Hooks

Dieses Verzeichnis enthält Git-Hooks, die ins Repo eingecheckt sind und
für alle Clones gelten, sofern `core.hooksPath` einmalig auf `.githooks`
gesetzt wird.

> **Hinweis:** Die `commit-msg`-Datei und (in kit=python-Repos) die generierte
> Hook-Kette (`umlaut_check_lib.py`, `pre-commit.py`, `pre-commit`,
> `fix-typografie.py`, `.claude/hooks/check-umlaute.py`,
> `.claude/hooks/check-tool-umlaute.py`,
> `.claude/hooks/block-typografie.py`, `.claude/hooks/check-git-sync.py`,
> `.claude/hooks/check-session-state-groesse.py`) werden
> automatisch aus der zentralen
> Trigger-Liste `~/.claude/umlaute-triggers.json` plus den Templates unter
> `~/.claude/hook-templates/python/` generiert. Manuelle Änderungen werden
> beim nächsten Sync überschrieben. Pflege ausschließlich über
> `pwsh ~/.claude/scripts/Sync-UmlautTriggers.ps1 -Apply`.

## Aktivierung pro Clone (einmalig)

```bash
git config core.hooksPath .githooks
```

## Altbestand bereinigen

`fix-typografie.py` ersetzt verbotene Typografie (Geviert-/Halbgeviertstrich,
Pfeil-Sonderzeichen) regelbasiert in Markdown. Es bestimmt die Fundstellen mit
derselben Logik wie der Hook und ersetzt nach Klasse: Doppelpunkt in
Überschriften und Label-Zeilen, Komma im Fließtext, ASCII-Pfeile, Bindestrich
bei Bereichen und Platzhalter-Zellen.

```bash
python .githooks/fix-typografie.py --report "docs/**/*.md"
python .githooks/fix-typografie.py --apply  "docs/**/*.md"
```

`--report` listet die Sonderfälle im Kontext; die nach dem Lauf zu lesende
Restmenge ist damit klein. Ausnahmen des Repos per `--exclude <regex>`
ausschließen (Muster nicht mit `/` beginnen lassen: Git-Bash wandelt das in
einen Windows-Pfad um, und der Ausschluss läuft ins Leere).

## Code-Fences in Markdown: Kommentare werden geprüft

Seit dem 08.08.2026 überspringt der Datei-Inhalt-Check einen Code-Fence nicht mehr
vollständig. Geprüft wird, was darin deutscher Menschentext ist: Kommentare
(`//`, `///`, `#`, `--`, `/* */`, `<!-- -->`) und Zeichenketten, die nach Prosa
aussehen. Der übrige Code bleibt ungeprüft, Bezeichner, Pfade und Formatangaben
dürfen weiter ASCII tragen.

Anlass war eine committete Anleitung, deren deutsche C#-Kommentare (`Rueckgabe`,
`Identitaetsdatensatz`, `verfuegbar`) alle Prüfungen passierten, weil der Fence
bewusst übersprungen wurde. Das war die gefährlichere Variante des bekannten
Fence-Problems: die Datei wirkt geprüft, und Lehrmaterial wird nachgeahmt.

Die Prüfung **warnt zunächst nur** und blockiert keinen Commit, auch dort nicht, wo
`.md` sonst blockend geführt wird. Zwei Felder in `umlaut-allowlist.json` steuern sie:

```json
"fence_scope": "comments+literals",
"fence_enforcement": "warn"
```

`fence_scope` kennt `off`, `comments` und `comments+literals`, `fence_enforcement`
kennt `warn` und `block`. Ohne Eintrag gelten die oben gezeigten Werte. Wirksam ist
vor allem die Schreib-Warnung: sie meldet den Verstoß in der Session, in der die
Datei entsteht. Entscheid und Messung: ADR-2026-08-08-0117 im Repo `claudecode`.

## Typografie: auch HTML und Text, nicht nur Markdown

Seit dem 08.08.2026 prüft die Kette die verbotene Typografie nicht mehr nur in
`.md`:

| Stufe | Endungen | Wirkung |
|---|---|---|
| Schreibsperre `block-typografie.py` (PreToolUse) | `.md`, `.html`, `.htm` | blockt den Schreibvorgang |
| Commit-Check `pre-commit.py` | `.md`, `.html`, `.htm`, `.txt` | block oder warn je Endung |
| `commit-msg` | Commit-Message | blockt den Commit |

Anlass: Ein Ticket-Kommentar lag als `.html` im Repo, trug sechs Geviertstriche
und passierte jede Stufe, weil sie nur Markdown kannte. Aufgefallen ist es erst
beim Zurücklesen des bereits geposteten Kommentars. Genau die Texte, die nach
außen gehen (Ticket-Antworten, Mail-Entwürfe, Reports), liegen selten als `.md`
vor.

**Zitat-Ausnahme in HTML:** `<code>` und `<pre>` entsprechen den Code-Fences in
Markdown, einzeilig wie mehrzeilig. Wörtlich zitierter Fremdtext gehört dorthin.

**`.txt` bewusst nicht in der Schreibsperre:** Textdateien tragen oft Fremdinhalt
(Logs, Exporte, Dumps), den man unverändert behalten muss, und dort gibt es keine
Zitat-Ausnahme. Ein hartes `deny` wäre ein Fehlalarm ohne Ausweg. Der Commit-Check
erfasst `.txt` trotzdem, dort ist die Meldung folgenlos korrigierbar.

Mit der üblichen Repo-Konfiguration (`enforcement: warn`, `block_extensions:
[".md"]`) warnen die neuen Endungen beim Commit, während `.md` blockt. Wer `.html`
ebenfalls blockend führen will, trägt die Endung in `block_extensions` ein.

## Betreff einer Commit-Message nie mit `#` beginnen

Belegt 2026-07-31: Eine Message mit dem Betreff `#40267 abgeschlossen: …` überlebt zwar ein
direktes `git commit -m`, wird aber bei einem späteren `git rebase` durch den Editor gereicht,
und dort gilt `#` am Zeilenanfang als Kommentarzeichen. Der Betreff verschwindet ersatzlos, die
erste Body-Zeile rutscht an seine Stelle, und der Commit trägt danach dauerhaft eine
Roman-Message ohne Titel. Sichtbar wird das erst im Log nach dem Rebase, also zu spät.

Ticketnummern gehören deshalb hinter ein Wort: `Ticket 40267 abgeschlossen: …` oder
`40267 abgeschlossen: …`. Im Body sind `#`-Verweise unkritisch, solange sie nicht am Zeilenanfang
stehen.

## Technische Bezeichner in einer Commit-Message gehören in Backticks

Der `commit-msg`-Hook sieht Zeichenketten, keine Bedeutung. Ein Dateiname, ein Pfad, ein
Ordner- oder Vorgangsname mit einem Surrogat-Stamm blockt deshalb den Commit, obwohl die
Schreibweise im Bezeichner völlig korrekt ist. Backtick-Zitate ignoriert der Hook, das ist
der Ausweg.

Zwei Belege derselben Ursache: Am 27.08.2026 blockte `alho/pruefe_belegkette.py` über den
Stamm `pruef`. Am 30.08.2026 blockte eine Nachricht, die den Vorgangsnamen
`kontoabruf-doku-und-abloesungsregel` im Fließtext nannte, über den Stamm `abloesung`. Der
zweite Fall ist der tückischere: Ein Ordnername ohne Dateiendung sieht wie Prosa aus, und man
nimmt ihn beim Schreiben gar nicht als Bezeichner wahr. Betroffen sind Vorgangsordner, Branch-
und Strangnamen, Skript-Schalter und Konfigurationsschlüssel.

**Achtung, die Backticks kollidieren mit der Shell.** In `git commit -m "..."` führt Bash
alles zwischen Backticks als Befehl aus und setzt dessen Ausgabe ein; die Nachricht landet
dann verstümmelt in der Historie, und der Commit läuft trotzdem durch. Die Nachricht deshalb
in einfache Anführungszeichen setzen oder die Backticks escapen. Unter PowerShell tritt das
nicht auf.

Eine Nachricht lässt sich ohne Commit-Versuch prüfen:

```bash
bash .githooks/commit-msg <datei>
```

Exit 0 heißt sauber. Das ist billiger als ein Fehlversuch, dessen Grund auf den ersten Blick
unverständlich ist, weil die Nachricht selbst korrekte Umlaute trägt.

## Pflege

Neuen Verstoß-Stamm entdeckt? In `~/.claude/umlaute-triggers.json` ergänzen
(`fc: true`, wenn der Stamm auch Datei-Inhalte prüfen soll), dann zentral
synchronisieren:

```powershell
pwsh ~/.claude/scripts/Sync-UmlautTriggers.ps1 -Apply
```

### Der Stamm muss lang genug sein

Der Sync misst vorher automatisch, ob ein Stamm korrekte deutsche Wörter blockt,
und bricht ab, wenn ja. Grundlage ist eine echte Wortliste (die mit LibreOffice
gelieferte Hunspell-Datei `de_DE_frami.dic`); jeder Treffer darin ist beweisbar
ein Fehlalarm. Nur die Prüfung, ohne Sync:

```powershell
pwsh ~/.claude/scripts/Sync-UmlautTriggers.ps1 -Verify
```

Warum das nötig ist: Der zu kurze Stamm `haupts` blockte Hauptstrang, Hauptsache,
Hauptstadt und sogar das korrekt geschriebene Wort hauptsächlich. Der Hook
vergleicht in der C-Locale, dort zählt ein Umlaut nicht als Wortzeichen und bildet
eine Wortgrenze direkt hinter dem Stamm. Ein zu kurzer Stamm trifft deshalb auch
Wörter, die den Umlaut richtig schreiben.

Meldet die Prüfung einen Treffer, gibt es vier Wege: den Stamm länger fassen
(`haupts` wurde zu `hauptsaech`), das korrekte Wort in `whitelist_tokens`
aufnehmen (verlustfrei, neutralisiert nur das ganze Token und erhält die Erkennung
im Kompositum), das Vorderglied in `wortfugen` aufnehmen (siehe nächster
Abschnitt), oder den Stamm mit Begründung in `accepted_false_positives`
eintragen. `-SkipVerify` umgeht die Sperre, sollte aber die Ausnahme bleiben.

### Einzelnes Wort oder produktives Vorderglied?

Die Wahl zwischen `whitelist_tokens` und `wortfugen` entscheidet, ob die Pflege
irgendwann fertig wird.

`whitelist_tokens` ist richtig für **ein einzelnes Wort**: einen Eigennamen, einen
englischen Fachbegriff, einen Code-Bezeichner. Der Eintrag wirkt auf genau dieses
Token, jede weitere Form braucht einen eigenen.

`wortfugen` ist richtig für ein **produktives Vorderglied**, das beliebig viele
Komposita bildet. Endet es auf `s` und beginnt das Hinterglied ebenfalls mit `s`,
entsteht eine Buchstabenfolge, die einen Stamm vortäuscht: `Preis` plus `Stufe`
ergibt `preisstufe` und enthält damit `reiss`. Ein Eintrag deckt alle Komposita
und alle Flexionsformen zugleich ab.

Der Mechanismus **trennt die Fuge auf**, statt das Wort zu verwerfen: aus
`preisstufe` wird `preis stufe`. Das ist bewusst so, denn ein Verwerfen nähme
einen echten Verstoß im hinteren Wortteil mit. `Preisschaetzung` trägt neben der
harmlosen Fuge das echte Surrogat `schaetz`, und dieser Treffer bleibt erhalten.

Vorgeschichte: Bis zum 09.08.2026 gab es nur den Token-Weg. Er hat den Fall
`preissperre` gelöst und `preisstufe`, `preisstufen`, `preisschildern` und
`preissperrung` offen gelassen; im Wörterbuch stehen rund dreißig weitere. Eine
davon hat eine Rückfrage an den Nutzer hart geblockt. Detail:
`decisions/ADR-2026-08-09-1507` im Repo `claudecode`.

### Ein Stamm auf einem Eigennamen wird nicht aufgehalten

Die Prüfung oben misst gegen das Wörterbuch. Nachnamen wie Müller, Schäfer,
Schröder oder Krüger stehen dort als gültige Wörter, ein Stamm darauf gilt ihr
also als bekannter Fehlalarm und nicht als Verbot. Genau solche Stämme wären aber
schädlich: die ASCII-Form eines Namens ist in E-Mail-Adressen, Benutzernamen und
Dateinamen die richtige Schreibweise, nicht der Verstoß.

Belegt 2026-08-08 an einer Gegenmessung über alle Repos: von 593 Wörtern, die
heute ungeblockt durchkommen, ist der Großteil Eigenname oder Adressbestandteil,
angeführt von `mueller` mit 2086 Vorkommen. Vor jeder Stamm-Ergänzung deshalb
prüfen, ob das Wort auch als Name vorkommt, und im Zweifel den Stamm so lang
fassen, dass er nur die Prosaform trifft.

Die Messung selbst liegt im Repo `claudecode` unter
`vorgaenge/2026-08-08-umlaut-check-code-fence-kommentare/werkzeug/finde-stamm-luecken.py`.
Wichtig für die Laufzeit: die Tokenisierung macht ripgrep, nicht Python. Die
Python-Fassung lief über Stunden ohne Ergebnis, mit ripgrep sind es 26 Sekunden
für 24500 Dateien.

### Entscheidend ist das Trefferbild, nicht die Häufigkeit

Bevor ein Kandidat in die Liste wandert, wird gemessen, welche echten Tokens er
fangen würde. Das Werkzeug dafür liegt neben der Messung oben und heißt
`bewerte-kandidaten.py`; es zeigt je Kandidat die Wörterbuch-Fehlalarme und die
Tokens des Bestands mit Häufigkeit. Erst dieses zweite Bild trägt die
Entscheidung, denn die Wörterbuch-Prüfung sieht den Namensfall nicht.

Drei Muster, an denen ein Kandidat 2026-08-09 durchgefallen ist, jeweils belegt:
Eigennamen und Adressen (`strasse`, `muehle`, `kuehn`); Firmennamen aus
Datendumps, wo die ASCII-Form die Form der Daten selbst ist (`getraenke`,
`kaese`, `gemuese`); und Stämme, deren Masse auf technische Bezeichner fällt
(`flaech` traf die WFS-Attribute `flaecheninanspruchnahme` und `flaecheqm`,
`eigentuem` traf `eigentuemernutzer`). Ist ein Wort zugleich Prosa und Name, ist
der Eintrag als exaktes Wort (`words` / `ss_words`) der saubere Weg: `weiss`
fängt so das Verb, lässt aber `Weissenburger` und `Edelweiss` in Ruhe.

Und: `fc: true` wirkt in **beiden** Wort-Blöcken. Bis 2026-08-09 rendete der Sync
nur `words` in die Datei-Inhalt-Lib, ein `fc: true` auf einem `ss_words`-Eintrag
blieb still wirkungslos. Der Defekt war latent, weil vorher jeder Eintrag dieses
Blocks `fc: false` trug.