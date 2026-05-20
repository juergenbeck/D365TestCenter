# Versionierte Git-Hooks

Dieses Verzeichnis enthält Git-Hooks, die ins Repo eingecheckt sind und
für alle Clones gelten — sofern `core.hooksPath` einmalig auf `.githooks`
gesetzt wird.

> **Hinweis:** Die `commit-msg`-Datei wird automatisch aus der zentralen
> Trigger-Liste `~/.claude/umlaute-triggers.json` generiert. Manuelle
> Änderungen werden beim nächsten Sync überschrieben. Pflege erfolgt
> ausschließlich über `pwsh ~/.claude/scripts/Sync-UmlautTriggers.ps1 -Apply`.

## Aktivierung pro Clone (einmalig)

```bash
git config core.hooksPath .githooks
```

Verifizieren:

```bash
git config --local --get core.hooksPath
# erwartet: .githooks
```

## Aktiver Hook

### `commit-msg`

Blockt Commits, deren Message ASCII-Ersatz für Umlaute enthält
(`ae`/`oe`/`ue`/`ss` statt `ä ö ü ß` in deutschen Texten).

**Erlaubt** sind Surrogate **innerhalb von Inline-Code-Backticks**
(`` `ausserhalb` ``), weil dort Zitate des Verstoß-Patterns stehen.

**Whitelist** für englische Fachbegriffe (User, Queue, Status, Plugin, ...)
und projektspezifische Code-Bezeichner wird vor dem Match-Check angewandt.

**Umgehen** einer einzelnen Verletzung (nur in echten Ausnahmen):

```bash
git commit --no-verify
```

## Pflege

Neuen Verstoß-Stamm entdeckt? In `~/.claude/umlaute-triggers.json` ergänzen,
dann zentral synchronisieren:

```powershell
pwsh ~/.claude/scripts/Sync-UmlautTriggers.ps1 -Apply
```

patcht alle registrierten Repos in einem Lauf.