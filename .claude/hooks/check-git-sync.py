#!/usr/bin/env python3
"""SessionStart-Hook: meldet einen unvollständig gebliebenen Git-Sync.

AUTO-GENERATED aus ~/.claude/hook-templates/python/check-git-sync.py
(ausgerollt von ~/.claude/scripts/Sync-UmlautTriggers.ps1). Nicht von Hand
editieren, sondern am Template ändern und neu syncen.

Anlass 2026-08-04 (DeutscheBahn): Der SessionStart-Sync führt
`git pull --rebase --autostash` aus. Dieser Befehl liefert Exit 0 auch dann, wenn
der Autostash danach NICHT zurückgespielt werden konnte. Der Rebase selbst gilt als
gelungen, die Zeile "Applying autostash resulted in conflicts" ist nur ein Hinweis
im Ausgabetext, und der Sync-Hook unterdrückt sie zusätzlich mit -q. Zurück bleiben
Dateien im Konfliktzustand, ohne dass eine Merge-Operation läuft, plus ein
liegengebliebener autostash-Eintrag. Auffallen tut das erst beim nächsten
Commit-Versuch ("Committing is not possible because you have unmerged files"), und
der Zustand wird dann leicht für fremde Arbeit einer parallelen Session gehalten.
In Markant und LMApp lagen solche Autostashes wochenlang unbemerkt.

Dieser Hook prüft nach dem Sync auf genau diese drei Spuren und meldet sie. Er
greift bewusst nicht ein: Auflösen ist Handarbeit, weil ein Konflikt inhaltlich
entschieden werden muss (Vereinigung beider Seiten, nicht --ours/--theirs).

ensure_ascii=True gegen die Windows-cp1252-stdout-Falle.
fail-open: jeder Fehler -> Exit 0, ohne Ausgabe.
"""
import json
import os
import subprocess
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

TIMEOUT = 15


def git(repo, *args):
    """git-Aufruf, gibt stdout als Text zurück ('' bei jedem Fehler)."""
    try:
        r = subprocess.run(
            ['git', '-C', repo] + list(args),
            capture_output=True, text=True, encoding='utf-8',
            errors='replace', timeout=TIMEOUT,
        )
        return r.stdout if r.returncode == 0 else ''
    except Exception:
        return ''


def main():
    repo = os.environ.get('CLAUDE_PROJECT_DIR') or '.'
    if not git(repo, 'rev-parse', '--git-dir').strip():
        return 0

    konflikte = [z for z in git(repo, 'diff', '--name-only', '--diff-filter=U').splitlines() if z.strip()]
    autostash = [z for z in git(repo, 'stash', 'list').splitlines() if 'autostash' in z.lower()]

    # Eine abgebrochene Merge-/Rebase-Operation hinterlässt diese Marker im .git-Verzeichnis.
    gitdir = git(repo, 'rev-parse', '--absolute-git-dir').strip()
    reste = []
    if gitdir:
        for marker in ('MERGE_HEAD', 'rebase-merge', 'rebase-apply', 'AUTO_MERGE'):
            if os.path.exists(os.path.join(gitdir, marker)):
                reste.append(marker)

    if not (konflikte or autostash or reste):
        return 0

    teile = ["GIT-SYNC UNVOLLSTÄNDIG (Hook check-git-sync)", ""]
    if konflikte:
        teile.append("Dateien im Konfliktzustand (%d): %s" % (len(konflikte), ', '.join(konflikte[:8])))
        teile.append("  Solange die offen sind, ist in diesem Repo kein Commit möglich.")
    if autostash:
        teile.append("Liegengebliebener Autostash (%d): %s" % (len(autostash), '; '.join(autostash[:4])))
        teile.append("  Das heißt, lokale Änderungen wurden weggestasht und danach NICHT "
                     "zurückgespielt. Sie fehlen im Arbeitsverzeichnis.")
    if reste:
        teile.append("Reste einer abgebrochenen Operation: %s" % ', '.join(reste))

    teile += [
        "",
        "MELDE DAS DEM USER SICHTBAR UND WÖRTLICH, bevor du inhaltlich weiterarbeitest. "
        "Diese Meldung sieht nur Claude, nicht der User. Sie ist kein Grund, den laufenden "
        "Auftrag abzubrechen, aber der User muss entscheiden, ob zuerst aufgeräumt wird.",
        "",
        "Prüfen, was fehlt:  git stash show --stat 'stash@{0}'",
        "Auflösen:           Konflikte als VEREINIGUNG beider Seiten, nicht per "
        "--ours/--theirs, das wirft eine Seite still weg.",
        "Vor stash drop:     SHA sichern (git rev-parse 'stash@{0}'), dann bleibt der Stand "
        "über git stash apply <sha> erreichbar.",
    ]

    print(json.dumps(
        {'hookSpecificOutput': {'hookEventName': 'SessionStart',
                                'additionalContext': '\n'.join(teile)}},
        ensure_ascii=True))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)
