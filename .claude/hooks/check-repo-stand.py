#!/usr/bin/env python3
"""SessionStart-Hook: misst den Stand gegen origin und meldet ihn, ohne einzugreifen.

AUTO-GENERATED aus ~/.claude/hook-templates/python/check-repo-stand.py
(ausgerollt von Sync-UmlautTriggers.ps1). Nicht von Hand editieren, sondern am
Template ändern und neu syncen.

Anlass (TOOL-0034, belegt am 30.08.2026): Der bisherige SessionStart-Sync setzte
`git pull --rebase --autostash origin <branch>` ab und warnte bei jedem Fehlschlag mit
der schärfsten Meldung, die der Sessionstart kennt. Diese Warnung war über Wochen
unbegründet, belegt in 15 Handovers seit dem 19.07.2026 und in 17 Sync-Logs, einmal
mit einer falschen Verlustmeldung als Folge.

Die Ursache ist gemessen: ein Wettlauf um .git/FETCH_HEAD. `git pull` schreibt diese
Datei und liest sie danach wieder, um das Rebase-Ziel zu bestimmen. Schreibt ein
zweiter git-Prozess im selben Arbeitsbaum in dieses Zeitfenster, liest der Rebase-Teil
mehr mergefähige Einträge, als sein eigener Fetch hinterlassen hat, und bricht ab.
Gemessen mit drei gleichzeitigen Prozessen: 157 Fehler in 180 Läufen, während
derselbe Aufruf allein in 60 Läufen kein einziges Mal scheiterte.

Zwei Konsequenzen stecken in diesem Hook:

1. Er rebased NICHT mehr. Er misst und meldet. Damit fällt der Fehlalarm weg, und
   zugleich ist ein Verlustrisiko ausgeschlossen, das bei der naheliegenden Abhilfe
   (fetch und rebase getrennt) gemessen wurde: bei unsauberem Arbeitsbaum und
   Gleichzeitigkeit verschwand dort die lokale Änderung aus dem Arbeitsbaum, ohne
   Eintrag in `git stash list`, auffindbar nur noch über `git fsck --lost-found`.

2. Sein eigener Fetch läuft mit --no-write-fetch-head und fasst die umkämpfte Datei
   deshalb gar nicht erst an. Ohne diesen Schalter würde der Hook zwar selbst nicht
   mehr scheitern, aber weiterhin parallele Läufe zu Fall bringen: gemessen 21 Fehler
   in 60 Läufen eines fremden Prozesses, allein durch einen nebenher laufenden Fetch.

Das Nachziehen ist damit bewusst Handarbeit und wird nur noch empfohlen, nicht
ausgeführt. Der Schwester-Hook check-git-sync.py prüft weiterhin auf Konfliktreste,
liegengebliebenen Autostash und abgebrochene Operationen.

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

TIMEOUT = 20


def git(repo, *args):
    """git-Aufruf, gibt (rc, stdout) zurück."""
    try:
        r = subprocess.run(
            ['git', '-C', repo] + list(args),
            capture_output=True, text=True, encoding='utf-8',
            errors='replace', timeout=TIMEOUT,
        )
        return r.returncode, r.stdout.strip()
    except Exception:
        return 1, ''


def melde(text):
    print(json.dumps(
        {'hookSpecificOutput': {'hookEventName': 'SessionStart',
                                'additionalContext': text}},
        ensure_ascii=True))


def main():
    repo = os.environ.get('CLAUDE_PROJECT_DIR') or '.'
    rc, _ = git(repo, 'rev-parse', '--git-dir')
    if rc != 0:
        return 0

    rc, branch = git(repo, 'rev-parse', '--abbrev-ref', 'HEAD')
    if rc != 0 or not branch or branch == 'HEAD':
        return 0

    # Schonend holen: aktualisiert refs/remotes/origin/<branch>, lässt FETCH_HEAD
    # unberührt und stört damit keinen parallel laufenden Sync.
    rc, _ = git(repo, 'fetch', '--no-write-fetch-head', 'origin', branch, '--quiet')
    if rc != 0:
        melde(
            "REPO-STAND NICHT PRÜFBAR (Hook check-repo-stand)\n\n"
            "Der Abgleich mit origin ist fehlgeschlagen, etwa wegen fehlender "
            "Netzverbindung oder Anmeldung. Der lokale Stand kann veraltet sein.\n\n"
            "Vor Aussagen zum Repo-Stand von Hand prüfen:  "
            "git fetch origin %s && git status -sb" % branch
        )
        return 0

    rc, zahlen = git(repo, 'rev-list', '--left-right', '--count',
                     'HEAD...origin/%s' % branch)
    if rc != 0 or not zahlen:
        return 0
    teile = zahlen.split()
    if len(teile) != 2:
        return 0
    try:
        voraus, zurück = int(teile[0]), int(teile[1])
    except ValueError:
        return 0

    if voraus == 0 and zurück == 0:
        return 0

    kopf = "REPO-STAND WEICHT VON ORIGIN AB (Hook check-repo-stand)"
    zeilen = [kopf, "", "Branch: %s" % branch]

    if voraus and zurück:
        zeilen += [
            "",
            "ECHTE DIVERGENZ: lokal %d Commit(s) voraus, gleichzeitig %d zurück."
            % (voraus, zurück),
            "Beide Seiten haben sich bewegt. Das ist der Fall, der von Hand "
            "entschieden werden muss, BEVOR Stand-Aussagen getroffen werden.",
            "",
            "Vorschlag:  git pull --rebase --autostash origin %s" % branch,
            "Danach prüfen:  git diff --name-only --diff-filter=U  und  git stash list",
        ]
    elif zurück:
        zeilen += [
            "",
            "Lokal %d Commit(s) hinter origin. Kein Konflikt, nur veraltet: parallele "
            "Sessions oder der Infopool-Sync haben inzwischen gepusht." % zurück,
            "",
            "Nachziehen (dieser Hook tut es bewusst NICHT selbst):",
            "   git pull --rebase --autostash origin %s" % branch,
        ]
    else:
        zeilen += [
            "",
            "Lokal %d Commit(s) vor origin, also noch nicht gepusht. Kein Fehler, "
            "aber auf anderen Rechnern fehlt dieser Stand." % voraus,
            "",
            "Pushen:  git push origin %s" % branch,
        ]

    zeilen += [
        "",
        "MELDE DAS DEM USER SICHTBAR, bevor du inhaltlich weiterarbeitest. Diese "
        "Meldung sieht nur Claude, nicht der User.",
    ]
    melde('\n'.join(zeilen))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)
