# Git-Hooks: Umlaut-Schutz (Python)

Plattformneutral (macOS / Windows / Linux). Erzwingt echte Umlaute (ä ö ü ß)
statt ASCII-Ersatz (`ae`/`oe`/`ue`/`ss`) in deutschen Texten.

AUTO-GENERATED aus `~/.claude/hook-templates/python/` (ausgerollt von
`~/.claude/scripts/Sync-UmlautTriggers.ps1`). Nicht von Hand editieren.

## Aktivierung

Pro Klon einmalig:

```
git config core.hooksPath .githooks
```

**Automatisch:** Der `SessionStart`-Hook in `.claude/settings.json` setzt das bei
jedem Start von Claude Code idempotent selbst. Nach dem ersten `claude`-Start im
Repo ist der Schutz also ohne manuellen Schritt aktiv, auch auf einem frisch
geklonten Rechner.

## Voraussetzung

`python3` (auf macOS/Linux vorinstalliert) oder `python` (Windows). Keine PowerShell.

## Bestandteile

| Datei | Zweck |
|-------|-------|
| `commit-msg` | bash: blockt Surrogate in der **Commit-Message** |
| `pre-commit` | bash-Wrapper, ruft `pre-commit.py`, danach optional `pre-commit-local.py` |
| `pre-commit.py` | prüft alle staged `.md` auf Surrogate im **Datei-Inhalt** |
| `umlaut_check_lib.py` | gemeinsame Prüf-Logik (Stamm-Liste, Filter) |

Der nicht-blockierende Schreib-Warn-Hook `.claude/hooks/check-umlaute.py` teilt
dieselbe Lib (gleiche Treffer wie der Commit-Block).

## Repo-weite Konfiguration: `umlaut-allowlist.json`

Optionale Datei `.githooks/umlaut-allowlist.json` (wird vom Sync **nicht**
überschrieben). Fehlt sie, gilt der Default `md_only` + `block` (nur `.md`,
blockierend) wie bisher. Felder:

| Feld | Bedeutung |
|------|-----------|
| `file_scope` | `md_only` (Default, nur `.md`) oder `all_text` (alle Nicht-Binär-Dateien, inkl. `.cs`, `.ps1`, ...) |
| `enforcement` | globaler Default: `block` (Exit 1) oder `warn` (nur melden, Exit 0) |
| `block_extensions` | Endungen (z.B. `".md"`), die IMMER blocken, auch bei `enforcement: warn` |
| `warn_extensions` | Endungen (z.B. `".cs"`), die IMMER nur warnen, auch bei `enforcement: block` |
| `generated` | Regex-Liste (repo-relativer Pfad), strukturelle Dauerausschlüsse |
| `exceptions` | `[{path, reason, date}]` bewusste Einzeldatei-Ausnahmen |

Pro Datei gilt: `block_extensions` > `warn_extensions` > globales `enforcement`.
Damit lässt sich z.B. "Doku (`.md`) blockt, Code (`.cs`) warnt nur" abbilden:
`file_scope: all_text`, `enforcement: warn`, `block_extensions: [".md"]`. Im
`all_text`-Profil sind die eingebauten `md_only`-Ausschlüsse inaktiv: das Repo
definiert seine Ausschlüsse vollständig über `generated`.

## Projekt-lokaler Erweiterungspunkt: `pre-commit-local.py`

Der `pre-commit`-Wrapper ruft nach dem Umlaut-Check **optional** ein
`.githooks/pre-commit-local.py` auf, falls es existiert. Diese Datei ist **nicht**
auto-generiert und wird vom Sync **nicht** angelegt oder überschrieben: hier klinkt
jedes Repo eigene Checks ein (z.B. Doku-/Konsistenz-Abgleich), ohne die zentrale
Kette zu verändern. Exit ≠ 0 blockt den Commit. Sie endet auf `.py` und wird damit
von der bestehenden `.gitattributes`-Regel `.githooks/*.py text eol=lf` erfasst.

## Pflege der Stamm-Liste

Single source of truth ist `~/.claude/umlaute-triggers.json` (auf dem Pflege-Rechner).
Nach einer Änderung rendert `pwsh ~/.claude/scripts/Sync-UmlautTriggers.ps1 -Apply`
die Datenregion in `commit-msg` und `umlaut_check_lib.py` neu. Die Trigger-Daten
sind in die Hooks **eingebettet**, zur Laufzeit wird die zentrale JSON nicht
gebraucht, das Repo ist autark.

## Umgehen (nur im Notfall, mit Begründung dokumentieren)

```
git commit --no-verify
```
