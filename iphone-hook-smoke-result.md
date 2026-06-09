# iPhone Hook Smoke-Test – Ergebnisblock

Empirischer Nachweis, dass die plattformneutrale Python-Hook-Kette in diesem
Repo läuft und ein ASCII-Surrogat in einer Markdown-Datei den git-pre-commit-Hook
blockt. Durchgeführt auf einem frischen Clone.

## 1. Umgebung

- Python: `Python 3.11.15`
- git: `git version 2.43.0`
- core.hooksPath: `.githooks` (war bereits gesetzt – nicht manuell nötig)

## 2. Block-Test (Gold-Standard)

- Commit geblockt? **ja** (im gültigen Anlauf)
- Exit-Code: **1**
- Report-Kernzeilen:

  ```
  Pre-Commit-Hook: Umlaut-Verstöße erkannt
  iphone-hook-smoke.md
    Zeile 1 [Umlaut]: 'fuer'   -> ASCII-Ersatz statt echtem ä/ö/ü/ß
    Zeile 1 [Umlaut]: 'prueft' -> ASCII-Ersatz statt echtem ä/ö/ü/ß
  Bypass im Notfall: git commit --no-verify (DOKUMENTIEREN, warum)
  ```

## 3. PostToolUse-Schreibwarnung beim Anlegen der Datei?

- **nein** – beim Write kam keine Umlaut-Schreibwarnung. Kein aktiver
  PostToolUse-Umlaut-Hook in dieser Session; das Blocken erfolgt allein über
  den git-pre-commit-Hook.

## 4. Sauber-Check

- Exit: **0** (korrekte Umlaut-Form passiert `pre-commit.py`, kein Commit erzeugt)

## 5. Aufräumen

- git status sauber? **ja** (`git status --porcelain` leer)
- Neuer Commit entstanden? **nein** (HEAD = `ec144ff`, Clone-HEAD)

## 6. Sonstiges / Auffälliges

**WICHTIG:** Im frischen Clone waren die Hook-Dateien **nicht** ausführbar
(`pre-commit`, `commit-msg`, `pre-commit.py` = `-rw-r--r--`). Folge: Der **erste**
Commit-Versuch lief durch (exit=0), git meldete nur als hint
„hook was ignored because it's not set as executable" → der Hook blockte nicht.

Fix: ungewollten Commit per `git reset --soft HEAD~1` zurückgenommen, `chmod +x`
auf die drei Hook-Dateien gesetzt, Block-Test wiederholt → erst dann griff der
Hook (exit=1, Report).

**Konsequenz:** Für einen frischen Clone reicht `git config core.hooksPath
.githooks` nicht; zusätzlich nötig:

```
chmod +x .githooks/pre-commit .githooks/commit-msg .githooks/pre-commit.py
```

Sonst werden die Hooks still ignoriert und Surrogate rutschen durch.

## Fazit

Die Python-Hook-Kette blockt ASCII-Surrogate zuverlässig (exit=1, präziser
Report) – vorausgesetzt die Hook-Skripte sind ausführbar. Das Executable-Bit ist
im frischen Clone der einzige Stolperstein und sollte im Clone-/Setup-Schritt
mitgesetzt werden.
