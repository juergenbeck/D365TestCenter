#!/usr/bin/env python3
"""Pre-Commit-Hook (Python, plattformneutral).

AUTO-GENERATED aus ~/.claude/hook-templates/python/pre-commit.py
(ausgerollt von ~/.claude/scripts/Sync-UmlautTriggers.ps1). Nicht von Hand editieren.

Prüft ALLE staged .md auf Umlaut-Verstöße (ASCII-Ersatz ae/oe/ue/ss statt
ä/ö/ü/ß) im Datei-Inhalt, via gemeinsamer Lib .githooks/umlaut_check_lib.py.

Bei Verstoß: Report auf stderr, Exit 1. Sauber: Exit 0.
Bypass im Notfall: git commit --no-verify (dokumentieren, warum).
"""
import os
import re
import subprocess
import sys
from itertools import groupby

try:
    sys.stderr.reconfigure(encoding='utf-8')
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from umlaut_check_lib import get_umlaut_violations


def git(*args):
    return subprocess.run(['git', *args], capture_output=True, text=True, encoding='utf-8').stdout


# Anhang-Begleittexte (<name>.<ext>.md) sind 1:1 aus Originaldokumenten extrahierte
# Fremdtexte (Volltextsuche-Hilfe), kein selbst verfasster deutscher Text. Sie werden
# vom Umlaut-Check ausgenommen, weil ASCII-Schreibweisen darin (z.B. Adressen wie
# "Osnabrueck" im Original) nicht verändert werden dürfen (Originaltreue).
COMPANION_RE = re.compile(
    r'\.(pdf|docx?|xlsx?|pptx?|vcf|txt|csv|ics|jpe?g|png|gif|odt|ods)\.md$', re.I)

# Strukturell ausgenommene Pfade (analog zum Pfad-Filter in Markants pre-commit.ps1):
# gespiegelte Skill-Bibliothek (gehört an den zentralen Skill-Master, wird nicht pro
# Repo korrigiert), eingefrorene Historie und Traceability (handover/changelog/reviews/
# research/...), Planungs-/Backlog-/Jira-Dumps, Archive und generierte Outputs. Diese
# enthalten bewusst Fremd-, Alt- oder Code-Surrogate und sind kein selbst verfasster,
# laufender Doku-Bestand. Laufende Doku (Konzepte, READMEs, Datenmodelle, ADRs) bleibt
# geprüft.
EXCLUDE_RE = re.compile(
    r'(^|/)(\.github|\.claude)/skills/'
    r'|(^|/)(handover|changelog|reviews|research|recherche|poc|backlog|planung|jira|output|scans|99_archiv|99_confluence-export)/'
    r'|(^|/)_archive/'
    r'|(^|/)Wissen/temp/'
    r'|(^|/)memory-snapshot[^/]*/'
    r'|(feedback|bug-report|alte-notizen|lessons-learned|skeptiker-review)', re.I)


def main():
    staged = [f for f in git('diff', '--cached', '--name-only', '--diff-filter=ACM').splitlines()
              if f.endswith('.md') and not COMPANION_RE.search(f) and not EXCLUDE_RE.search(f)]
    if not staged:
        return 0
    repo_root = git('rev-parse', '--show-toplevel').strip()

    violations = []
    for rel in staged:
        full = os.path.join(repo_root, rel)
        if not os.path.isfile(full):
            continue
        with open(full, encoding='utf-8') as fh:
            lines = fh.read().split('\n')
        for h in get_umlaut_violations(lines):
            violations.append((rel, h))

    if not violations:
        return 0

    w = sys.stderr.write
    w('\n=================================================================\n')
    w(' Pre-Commit-Hook: Umlaut-Verstöße erkannt\n')
    w('=================================================================\n\n')
    for rel, group in groupby(violations, key=lambda x: x[0]):
        w('  %s\n' % rel)
        for _, h in group:
            note = ', alleinstehend' if h['block'] == 2 else ''
            w("    Zeile %4d [Umlaut]: '%s'%s -> ASCII-Ersatz statt echtem ä/ö/ü/ß. Siehe Skill umlaute.\n"
              % (h['line'], h['match'], note))
            text = h['text']
            snippet = text[:117] + '...' if len(text) > 120 else text
            w('      > %s\n' % snippet)
    w('\n Bypass im Notfall: git commit --no-verify (DOKUMENTIEREN, warum)\n\n')
    return 1


if __name__ == '__main__':
    sys.exit(main())
