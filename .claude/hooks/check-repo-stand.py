#!/usr/bin/env python3
"""SessionStart-Hook: gleicht den Stand mit origin ab und zieht ihn selbst nach.

AUTO-GENERATED aus ~/.claude/hook-templates/python/check-repo-stand.py
(ausgerollt von Sync-UmlautTriggers.ps1). Nicht von Hand editieren, sondern am
Template ändern und neu syncen.

Maßstab (Jürgen, 14.09.2026, ADR-2026-09-14-085634): Jedes Repo soll sich anfühlen wie
OneDrive, eine Sitzung startet also auf dem aktuellen Stand. Vom 30.08. bis 14.09.2026
hat dieser Hook nur gemessen und gemeldet; das war ein Irrtum und ist aufgehoben.

Die Bauart folgt zwei Messungen aus TOOL-0034 (30.08.2026), die weiter gelten:

1. `git pull` schreibt und liest `.git/FETCH_HEAD`. Laufen mehrere git-Prozesse im selben
   Arbeitsbaum gleichzeitig, bricht der Rebase-Teil ab (157 Fehler in 180 Läufen mit drei
   parallelen Prozessen). Deshalb holt der Hook mit --no-write-fetch-head und ruft nie
   `git pull` auf.
2. Fetch plus Rebase mit Autostash verlor bei unsauberem Arbeitsbaum und Gleichzeitigkeit
   eine lokale Änderung ohne Eintrag in `git stash list`. Deshalb gibt es hier keinen
   Stash und keinen Rebase.

Nachgezogen wird so:

- nur hinter origin: `git merge --ff-only`. Würde eine lokale Änderung überschrieben,
  verweigert git selbst und fasst nichts an.
- voraus und hinter: `git merge --no-edit`, aber nur bei sauberem getrackten Arbeitsbaum,
  weil ein `merge --abort` uncommittete Änderungen mitreißen kann. Kein Rebase, denn er
  stellt HEAD zwischenzeitlich ab, und ein Commit einer parallelen Sitzung landete dann
  auf einem abgehängten HEAD.
- nur voraus: nichts ändern, Push empfehlen.

Was nicht nachgezogen werden kann, wird mit Grund gemeldet. Der Schwester-Hook
check-git-sync.py prüft weiterhin auf Konfliktreste und abgebrochene Operationen.

ensure_ascii=True gegen die Windows-cp1252-stdout-Falle.
fail-open: jeder unerwartete Fehler -> Exit 0, ohne Ausgabe.
"""
import json
import os
import subprocess
import sys
import time

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

FETCH_TIMEOUT = 10
LOCAL_TIMEOUT = 6
LOCK_VERSUCHE = 3
LOCK_PAUSE = 0.5


def git(repo, *args, timeout=LOCAL_TIMEOUT):
    """git-Aufruf, gibt (rc, stdout, stderr) zurück."""
    env = dict(os.environ)
    env['GIT_TERMINAL_PROMPT'] = '0'
    env['GIT_EDITOR'] = 'true'
    try:
        r = subprocess.run(
            ['git', '-C', repo] + list(args),
            capture_output=True, text=True, encoding='utf-8',
            errors='replace', timeout=timeout, env=env,
        )
        return r.returncode, r.stdout.strip(), r.stderr.strip()
    except Exception as exc:
        return 1, '', str(exc)


def git_mit_sperre(repo, *args):
    """Wiederholt kurz, solange nur .git/index.lock belegt ist."""
    rc, out, err = 1, '', ''
    for versuch in range(LOCK_VERSUCHE):
        rc, out, err = git(repo, *args)
        if rc == 0 or 'index.lock' not in (out + err):
            break
        if versuch < LOCK_VERSUCHE - 1:
            time.sleep(LOCK_PAUSE)
    return rc, out, err


def melde(text):
    print(json.dumps(
        {'hookSpecificOutput': {'hookEventName': 'SessionStart',
                                'additionalContext': text}},
        ensure_ascii=True))


def git_pfad(repo, name):
    rc, out, _ = git(repo, 'rev-parse', '--git-path', name)
    if rc != 0 or not out:
        return None
    return out if os.path.isabs(out) else os.path.join(repo, out)


def operation_laeuft(repo):
    for name in ('MERGE_HEAD', 'rebase-merge', 'rebase-apply', 'CHERRY_PICK_HEAD'):
        pfad = git_pfad(repo, name)
        if pfad and os.path.exists(pfad):
            return True
    return False


def auszug(text, zeilen=8):
    teile = [z for z in text.splitlines() if z.strip()]
    return '\n'.join('   ' + z for z in teile[:zeilen])


def main():
    repo = os.environ.get('CLAUDE_PROJECT_DIR') or '.'
    rc, _, _ = git(repo, 'rev-parse', '--git-dir')
    if rc != 0:
        return 0

    rc, branch, _ = git(repo, 'rev-parse', '--abbrev-ref', 'HEAD')
    if rc != 0 or not branch or branch == 'HEAD':
        return 0
    ziel = 'origin/%s' % branch

    # Schonend holen: aktualisiert refs/remotes/origin/<branch>, lässt FETCH_HEAD
    # unberührt und stört damit keinen parallel laufenden git-Prozess.
    rc, _, _ = git(repo, 'fetch', '--no-write-fetch-head', 'origin', branch, '--quiet',
                   timeout=FETCH_TIMEOUT)
    if rc != 0:
        melde(
            "REPO-STAND NICHT PRÜFBAR (Hook check-repo-stand)\n\n"
            "Der Abgleich mit origin ist fehlgeschlagen, etwa wegen fehlender "
            "Netzverbindung oder Anmeldung. Der lokale Stand kann veraltet sein.\n\n"
            "Von Hand nachziehen:  git fetch origin %s && git merge --ff-only %s\n\n"
            "MELDE DAS DEM USER SICHTBAR." % (branch, ziel)
        )
        return 0

    rc, zahlen, _ = git(repo, 'rev-list', '--left-right', '--count', 'HEAD...%s' % ziel)
    teile = zahlen.split() if rc == 0 else []
    if len(teile) != 2:
        return 0
    try:
        voraus, hinten = int(teile[0]), int(teile[1])
    except ValueError:
        return 0

    if voraus == 0 and hinten == 0:
        return 0

    kopf = "REPO-STAND (Hook check-repo-stand), Branch %s" % branch
    sichtbar = ("MELDE DAS DEM USER SICHTBAR, bevor du inhaltlich weiterarbeitest. "
                "Diese Meldung sieht nur Claude, nicht der User.")

    if voraus == 0:
        if operation_laeuft(repo):
            melde("%s\n\nLokal %d Commit(s) hinter origin, NICHT nachgezogen: eine "
                  "Merge-, Rebase- oder Cherry-Pick-Operation ist noch offen.\n\n%s"
                  % (kopf, hinten, sichtbar))
            return 0
        rc, out, err = git_mit_sperre(repo, 'merge', '--ff-only', '--quiet', ziel)
        if rc == 0:
            melde("%s\n\n%d Commit(s) von origin nachgezogen (Fast-Forward). Der "
                  "Arbeitsbaum ist auf dem aktuellen Stand.\n\nNenne das dem User in "
                  "einem Satz." % (kopf, hinten))
            return 0
        melde("%s\n\nLokal %d Commit(s) hinter origin, NICHT nachgezogen. git hat den "
              "Fast-Forward verweigert und nichts verändert:\n%s\n\nMeist überschreibt "
              "der neue Stand eine uncommittete Datei. Diese Datei committen (falls "
              "eigene Arbeit) und danach:  git merge --ff-only %s\n\n%s"
              % (kopf, hinten, auszug(err or out), ziel, sichtbar))
        return 0

    if hinten == 0:
        melde("%s\n\nLokal %d Commit(s) vor origin, also noch nicht gepusht. Auf anderen "
              "Rechnern fehlt dieser Stand.\n\nPushen:  git push origin %s\n\n%s"
              % (kopf, voraus, branch, sichtbar))
        return 0

    # Voraus und hinter: Merge nur bei sauberem getrackten Arbeitsbaum.
    rc, status, _ = git(repo, 'status', '--porcelain', '--untracked-files=no')
    if rc != 0 or status or operation_laeuft(repo):
        grund = ("uncommittete Änderungen an getrackten Dateien:\n%s" % auszug(status)
                 if status else "eine Operation ist noch offen oder der Status war nicht lesbar")
        melde("%s\n\nDIVERGENZ: lokal %d Commit(s) voraus, %d zurück. NICHT nachgezogen, "
              "weil %s\n\nEin Merge wird nur bei sauberem Arbeitsbaum versucht, damit ein "
              "Abbruch keine Änderung mitreißt. Erst committen, dann:  git merge --no-edit "
              "%s\n\n%s" % (kopf, voraus, hinten, grund, ziel, sichtbar))
        return 0

    rc, out, err = git_mit_sperre(repo, 'merge', '--no-edit', '--quiet', ziel)
    if rc == 0:
        melde("%s\n\nDivergenz aufgelöst: %d Commit(s) von origin per Merge nachgezogen, "
              "%d lokale Commit(s) bleiben erhalten und sind noch nicht gepusht.\n\n"
              "Pushen:  git push origin %s\n\nNenne das dem User in einem Satz."
              % (kopf, hinten, voraus, branch))
        return 0

    merge_head = git_pfad(repo, 'MERGE_HEAD')
    if merge_head and os.path.exists(merge_head):
        git(repo, 'merge', '--abort')
    melde("%s\n\nDIVERGENZ: lokal %d Commit(s) voraus, %d zurück. Der Merge ist "
          "gescheitert und wurde zurückgenommen, der Stand ist unverändert:\n%s\n\n"
          "Das muss inhaltlich entschieden werden:  git merge %s  und die Konflikte "
          "als Vereinigung beider Seiten auflösen.\n\n%s"
          % (kopf, voraus, hinten, auszug(err or out), ziel, sichtbar))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)
