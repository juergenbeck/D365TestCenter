# Versionierte Git-Hooks

Dieses Verzeichnis enthält Git-Hooks, die ins Repo eingecheckt sind und
für alle Clones gelten, sofern `core.hooksPath` einmalig auf `.githooks`
gesetzt wird.

> **Hinweis:** Die `commit-msg`-Datei und (in kit=python-Repos) die generierte
> Hook-Kette (`umlaut_check_lib.py`, `pre-commit.py`, `pre-commit`,
> `fix-typografie.py`, `.claude/hooks/check-umlaute.py`,
> `.claude/hooks/check-tool-umlaute.py`,
> `.claude/hooks/block-typografie.py`, `.claude/hooks/check-git-sync.py`) werden
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

## Betreff einer Commit-Message nie mit `#` beginnen

Belegt 2026-07-31: Eine Message mit dem Betreff `#40267 abgeschlossen: …` überlebt zwar ein
direktes `git commit -m`, wird aber bei einem späteren `git rebase` durch den Editor gereicht,
und dort gilt `#` am Zeilenanfang als Kommentarzeichen. Der Betreff verschwindet ersatzlos, die
erste Body-Zeile rutscht an seine Stelle, und der Commit trägt danach dauerhaft eine
Roman-Message ohne Titel. Sichtbar wird das erst im Log nach dem Rebase, also zu spät.

Ticketnummern gehören deshalb hinter ein Wort: `Ticket 40267 abgeschlossen: …` oder
`40267 abgeschlossen: …`. Im Body sind `#`-Verweise unkritisch, solange sie nicht am Zeilenanfang
stehen.

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

Meldet die Prüfung einen Treffer, gibt es drei Wege: den Stamm länger fassen
(`haupts` wurde zu `hauptsaech`), das korrekte Wort in `whitelist_tokens`
aufnehmen (verlustfrei, neutralisiert nur das ganze Token und erhält die Erkennung
im Kompositum), oder den Stamm mit Begründung in `accepted_false_positives`
eintragen. `-SkipVerify` umgeht die Sperre, sollte aber die Ausnahme bleiben.