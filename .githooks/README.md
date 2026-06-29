# Versionierte Git-Hooks

Dieses Verzeichnis enthält Git-Hooks, die ins Repo eingecheckt sind und
für alle Clones gelten — sofern `core.hooksPath` einmalig auf `.githooks`
gesetzt wird.

> **Hinweis:** Die `commit-msg`-Datei und (in kit=python-Repos) die
> Datei-Inhalt-Kette (`umlaut_check_lib.py`, `pre-commit.py`, `pre-commit`,
> `.claude/hooks/check-umlaute.py`) werden automatisch aus der zentralen
> Trigger-Liste `~/.claude/umlaute-triggers.json` plus den Templates unter
> `~/.claude/hook-templates/python/` generiert. Manuelle Änderungen werden
> beim nächsten Sync überschrieben. Pflege ausschließlich über
> `pwsh ~/.claude/scripts/Sync-UmlautTriggers.ps1 -Apply`.

## Aktivierung pro Clone (einmalig)

```bash
git config core.hooksPath .githooks
```

## Pflege

Neuen Verstoß-Stamm entdeckt? In `~/.claude/umlaute-triggers.json` ergänzen
(`fc: true`, wenn der Stamm auch Datei-Inhalte prüfen soll), dann zentral
synchronisieren:

```powershell
pwsh ~/.claude/scripts/Sync-UmlautTriggers.ps1 -Apply
```