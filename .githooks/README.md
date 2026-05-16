# Versionierte Git-Hooks

Dieses Verzeichnis enthält Git-Hooks, die ins Repo eingecheckt sind und
für alle Clones gelten — sofern `core.hooksPath` einmalig auf `.githooks`
gesetzt wird.

## Aktivierung pro Clone (einmalig)

```bash
git config core.hooksPath .githooks
```

Verifizieren:

```bash
git config --local --get core.hooksPath
# erwartet: .githooks
```

## Aktive Hooks

### `commit-msg`

Blockt Commits, deren Message ASCII-Ersatz für Umlaute enthält
(`ae`/`oe`/`ue`/`ss` statt `ä ö ü ß` in deutschen Texten).

**Erlaubt** sind Surrogate **innerhalb von Inline-Code-Backticks**
(`` `ausserhalb` ``), weil dort Zitate des Verstoß-Patterns stehen
(Audit-Reports, Lessons-Doku, Skill-Updates).

**Trigger-Liste** umfasst die häufigsten Grundformen plus typische
zusammengesetzte Stämme. Pflege bei neu entdecktem Stamm:

- Hier in `.githooks/commit-msg` ergänzen
- Synchron in der zentralen Umlaut-Skill-Definition halten
  (`~/.claude/skills/umlaute/SKILL.md` oder Repo-lokal)

**Umgehen** einer einzelnen Verletzung (nur in echten Ausnahmen):

```bash
git commit --no-verify
```

## Ursprung

Hook-Template stammt aus `juergenbeck/Zastrpay` (Datei
`.githooks/commit-msg`). Bei Updates der Trigger-Liste an einer Stelle
bitte alle Clones synchron pflegen.
