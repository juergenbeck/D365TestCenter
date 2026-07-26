# Versionierte Git-Hooks

Dieses Verzeichnis enthält Git-Hooks, die ins Repo eingecheckt sind und
für alle Clones gelten, sofern `core.hooksPath` einmalig auf `.githooks`
gesetzt wird.

> **Hinweis:** Die `commit-msg`-Datei und (in kit=python-Repos) die generierte
> Hook-Kette (`umlaut_check_lib.py`, `pre-commit.py`, `pre-commit`,
> `fix-typografie.py`, `.claude/hooks/check-umlaute.py`,
> `.claude/hooks/check-tool-umlaute.py`,
> `.claude/hooks/block-typografie.py`) werden
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

## Pflege

Neuen Verstoß-Stamm entdeckt? In `~/.claude/umlaute-triggers.json` ergänzen
(`fc: true`, wenn der Stamm auch Datei-Inhalte prüfen soll), dann zentral
synchronisieren:

```powershell
pwsh ~/.claude/scripts/Sync-UmlautTriggers.ps1 -Apply
```