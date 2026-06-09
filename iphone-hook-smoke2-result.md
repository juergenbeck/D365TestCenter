# iPhone Hook Smoke-Test 2 – Ergebnisblock (Fix-Verifikation)

Zweiter, schärferer Lauf auf einem **frischen Clone** in einem **neuen
Verzeichnis** – diesmal mit dem ausdrücklichen Ziel zu beweisen, dass die
git-Hooks **ohne jedes `chmod`** ausführbar ankommen und ein ASCII-Surrogat
sofort blocken. Beim ersten Lauf (`iphone-hook-smoke-result.md`) war noch ein
manuelles `chmod +x` nötig; genau das soll der Fix-Commit `b14bd51`
("hooks: git-Hooks als executable tracken") überflüssig machen.

**Bedingung dieses Tests:** an keiner Stelle wurde `chmod` ausgeführt.

## 1. Umgebung

- Python: `Python 3.11.15`
- git: `git version 2.43.0`
- Clone: frisch nach `D365TestCenter-fresh`, Clone-HEAD = `b14bd51`

## 2. Fix-Commit im frischen Clone vorhanden?

- **ja** – `git log --oneline -5` zeigt als HEAD:

  ```
  b14bd51 hooks: git-Hooks als executable tracken (Mac- und Linux-Clone)
  ```

## 3. Ausführbar ohne chmod?

- core.hooksPath: im frischen Clone zunächst **leer**, danach einmalig auf
  `.githooks` gesetzt (kein Datei-Permission-Eingriff, nur der Verweis auf das
  Hook-Verzeichnis). Hintergrund: Der SessionStart-Hook setzt den Pfad nur im
  Primär-Verzeichnis; ein manuell danebengelegter Zweitclone braucht den
  einmaligen `git config core.hooksPath .githooks`.
- pre-commit x-Bit: **executable** (`-rwxr-xr-x`)
- commit-msg x-Bit: **executable** (`-rwxr-xr-x`)
- **chmod gemacht? nein** – das x-Bit kam ausschließlich aus dem Clone,
  getrackt via `b14bd51`.

  ```
  -rwxr-xr-x  .githooks/commit-msg
  -rwxr-xr-x  .githooks/pre-commit
  pre-commit: executable
  commit-msg: executable
  ```

## 4. Block-Test ohne chmod

Testprobe (per Write-Tool angelegt, Surrogate `fuer`/`prueft` als Probe):

> Smoke-Test `fuer` den Umlaut-Hook: dieser Satz `prueft` das Blocken.

`git add` + `git commit` → Ergebnis:

- Commit geblockt? **ja**
- Exit-Code: **1**
- Report-Kernzeilen:

  ```
  =================================================================
   Pre-Commit-Hook: Umlaut-Verstöße erkannt
  =================================================================

    iphone-hook-smoke2.md
      Zeile 1 [Umlaut]: 'fuer'   -> ASCII-Ersatz statt echtem ä/ö/ü/ß. Siehe Skill umlaute.
      Zeile 1 [Umlaut]: 'prueft' -> ASCII-Ersatz statt echtem ä/ö/ü/ß. Siehe Skill umlaute.

   Bypass im Notfall: git commit --no-verify (DOKUMENTIEREN, warum)
  ```

## 5. Aufräumen

- `git restore --staged` + `rm` der Testprobe
- git status sauber? **ja** (`git status --porcelain` leer)
- Neuer Commit entstanden? **nein** (HEAD weiterhin `b14bd51`)

## 6. Vergleich zum ersten Lauf

| Aspekt                         | 1. Lauf (vorher)              | 2. Lauf (jetzt, mit `b14bd51`) |
|--------------------------------|-------------------------------|--------------------------------|
| Hook-Dateien im Clone          | `-rw-r--r--` (nicht ausführbar) | `-rwxr-xr-x` (ausführbar)     |
| `chmod +x` nötig?              | **ja** (sonst still ignoriert) | **nein**                       |
| Erster Commit-Versuch          | rutschte durch (Hook ignoriert) | sofort geblockt (exit 1)      |

## Fazit

Der Fix `b14bd51` wirkt: Im frischen Clone tragen `pre-commit` und `commit-msg`
das Executable-Bit direkt aus dem Repository – **ohne `chmod`**. Das Surrogat
wird beim allerersten Commit-Versuch sofort geblockt (exit 1, präziser Report
mit `fuer`/`prueft`). Der einzige verbleibende Handgriff bei einem manuell
danebengelegten Zweitclone ist das einmalige Setzen von `core.hooksPath`; im
regulär per SessionStart-Hook initialisierten Klon entfällt auch das.
