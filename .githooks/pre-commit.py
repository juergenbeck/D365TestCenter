#!/usr/bin/env python3
"""Pre-Commit-Hook (Python, plattformneutral, config-getrieben).

AUTO-GENERATED aus ~/.claude/hook-templates/python/pre-commit.py
(ausgerollt von ~/.claude/scripts/Sync-UmlautTriggers.ps1). Nicht von Hand editieren.

Prüft staged Dateien auf Umlaut-Verstöße (ASCII-Ersatz ae/oe/ue/ss statt ä/ö/ü/ß)
im Datei-Inhalt, via gemeinsamer Lib .githooks/umlaut_check_lib.py.

Das Verhalten kommt aus der optionalen .githooks/umlaut-allowlist.json:
  file_scope  : "md_only" (Default) prüft nur .md; "all_text" prüft alle Textdateien.
  enforcement : "block" (Default) -> Exit 1 bei Treffer; "warn" -> nur melden, Exit 0.
                Gilt global; pro Endung überstimmbar via block_extensions/warn_extensions.
  block_extensions[] : Endungen (z.B. ".md"), die IMMER blocken, auch bei enforcement=warn.
  warn_extensions[]  : Endungen (z.B. ".cs"), die IMMER nur warnen, auch bei enforcement=block.
                Präzedenz pro Datei: block_extensions > warn_extensions > globales enforcement.
                So lässt sich "Doku blockt, Code warnt" abbilden.
  generated[] : Regex-Liste (repo-relativer Pfad, Vorwärts-Slashes), strukturelle Ausschlüsse.
  exceptions[]: [{path}] exakte oder glob-Einzeldatei-Ausnahmen.
Fehlt die Config: md_only + block + eingebaute Default-Ausschlüsse (COMPANION_RE,
DEFAULT_EXCLUDE_RE) - verhält sich exakt wie die bisherige py-Repo-Version.

Projekt-spezifische Zusatz-Prüfungen (z.B. Markants Z4-Drift-/Header-/Scope-Checks)
liegen NICHT hier, sondern im projekt-lokalen .githooks/pre-commit-local.py, das der
Wrapper nach diesem Hook aufruft (nur, wenn dieser Exit 0 lieferte).

Bei Verstoß (block): Report auf stderr, Exit 1. Sauber/warn: Exit 0.
Bypass im Notfall: git commit --no-verify (dokumentieren, warum).
"""
import fnmatch
import json
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

# Anhang-Begleittexte (<name>.<ext>.md) sind 1:1 aus Originaldokumenten extrahierte
# Fremdtexte (Volltextsuche-Hilfe). Immer ausgenommen (Originaltreue, ASCII im Original
# darf nicht verändert werden). Gilt in beiden file_scope-Profilen.
COMPANION_RE = re.compile(
    r'\.(pdf|docx?|xlsx?|pptx?|vcf|txt|csv|ics|jpe?g|png|gif|odt|ods)\.md$', re.I)

# Default-Ausschlüsse für das md_only-Profil: gespiegelte Skill-Bibliothek, eingefrorene
# Historie/Traceability, Planungs-/Backlog-/Jira-Dumps, Archive, generierte Outputs.
# NUR im md_only-Profil aktiv. Im all_text-Profil definiert das Repo seine Ausschlüsse
# vollständig über generated[] - sonst würde z.B. .claude/skills/ fälschlich ausgenommen,
# obwohl ein Repo (Markant) seine Skills bewusst prüft.
DEFAULT_EXCLUDE_RE = re.compile(
    r'(^|/)(\.github|\.claude)/skills/'
    r'|(^|/)(handover|changelog|reviews|research|recherche|poc|backlog|planung|jira|output|scans|99_archiv|99_confluence-export)/'
    r'|(^|/)_archive/'
    r'|(^|/)Wissen/temp/'
    r'|(^|/)memory-snapshot[^/]*/'
    r'|(feedback|bug-report|alte-notizen|lessons-learned|skeptiker-review)', re.I)

# Binär-Endungen (nur all_text-Profil relevant): zeilenweiser Textcheck wäre sinnlos.
BINARY_EXT = {
    '.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.ico', '.svg', '.pdf',
    '.zip', '.7z', '.gz', '.tgz', '.tar', '.rar', '.docx', '.doc', '.xlsx', '.xls',
    '.pptx', '.vsdx', '.msg', '.eml', '.mp3', '.mp4', '.m4a', '.wav', '.mov', '.avi',
    '.mkv', '.woff', '.woff2', '.ttf', '.otf', '.eot', '.exe', '.dll', '.pdb', '.so',
    '.dylib', '.bin', '.dat', '.class', '.jar', '.pyc', '.o', '.a', '.lib', '.nupkg', '.snk',
}


def git(*args):
    return subprocess.run(['git', *args], capture_output=True, text=True,
                          encoding='utf-8').stdout


def _norm_ext(ext):
    """Normalisiert eine Endung auf lowercase mit führendem Punkt ('.cs')."""
    ext = str(ext).strip().lower()
    if ext and not ext.startswith('.'):
        ext = '.' + ext
    return ext


def is_blocking_file(rel, cfg):
    """Pro-Datei-Entscheidung block vs. warn. Präzedenz: block_extensions >
    warn_extensions > globales enforcement. Erlaubt 'Doku blockt, Code warnt'."""
    ext = os.path.splitext(rel)[1].lower()
    if ext in cfg['block_extensions']:
        return True
    if ext in cfg['warn_extensions']:
        return False
    return cfg['enforcement'] != 'warn'


def load_config(githooks_dir):
    """Liest .githooks/umlaut-allowlist.json. Defaults (fehlt/kaputt): md_only,
    block, keine Zusatz-Ausschlüsse - verhält sich wie die bisherige py-Repo-Version."""
    cfg = {'file_scope': 'md_only', 'enforcement': 'block',
           'generated': [], 'exceptions': [],
           'block_extensions': set(), 'warn_extensions': set()}
    path = os.path.join(githooks_dir, 'umlaut-allowlist.json')
    if not os.path.isfile(path):
        return cfg
    try:
        with open(path, encoding='utf-8') as fh:
            data = json.load(fh)
    except Exception as e:
        sys.stderr.write('Pre-Commit-Hook: umlaut-allowlist.json nicht lesbar '
                         '(%s) - nutze Defaults.\n' % e)
        return cfg
    if data.get('file_scope'):
        cfg['file_scope'] = str(data['file_scope'])
    if data.get('enforcement'):
        cfg['enforcement'] = str(data['enforcement'])
    if data.get('generated'):
        cfg['generated'] = [re.compile(p) for p in data['generated']]
    if data.get('exceptions'):
        cfg['exceptions'] = [e['path'] for e in data['exceptions']
                             if isinstance(e, dict) and e.get('path')]
    if data.get('block_extensions'):
        cfg['block_extensions'] = {_norm_ext(e) for e in data['block_extensions']}
    if data.get('warn_extensions'):
        cfg['warn_extensions'] = {_norm_ext(e) for e in data['warn_extensions']}
    return cfg


def is_excluded(rel, scope, generated, exceptions):
    """True, wenn die Datei vom Umlaut-Check ausgenommen ist."""
    if COMPANION_RE.search(rel):
        return True
    if scope == 'md_only' and DEFAULT_EXCLUDE_RE.search(rel):
        return True
    for rx in generated:
        if rx.search(rel):
            return True
    for p in exceptions:
        if rel == p or fnmatch.fnmatch(rel, p):
            return True
    return False


def staged_files(scope):
    """Staged Added/Modified/Copied, nach file_scope gefiltert."""
    out = git('diff', '--cached', '--name-only', '--diff-filter=ACM').splitlines()
    if scope == 'md_only':
        return [f for f in out if f.endswith('.md')]
    # all_text: alle nicht-binären Dateien (NUL-Byte-Absicherung beim Lesen).
    return [f for f in out if os.path.splitext(f)[1].lower() not in BINARY_EXT]


def main():
    cfg = load_config(HERE)
    scope = cfg['file_scope']
    files = [f for f in staged_files(scope)
             if not is_excluded(f, scope, cfg['generated'], cfg['exceptions'])]
    if not files:
        return 0
    repo_root = git('rev-parse', '--show-toplevel').strip()

    violations = []
    for rel in files:
        full = os.path.join(repo_root, rel)
        if not os.path.isfile(full):
            continue
        try:
            # utf-8-sig: ein führendes UTF-8-BOM entfernen, exakt wie PowerShells
            # Get-Content -Encoding UTF8. Ohne Strip begänne Zeile 1 mit dem
            # BOM-Zeichen und H1-/Fence-Erkennung schlüge fehl; ohne BOM wie utf-8.
            with open(full, encoding='utf-8-sig') as fh:
                content = fh.read()
        except (UnicodeDecodeError, OSError):
            continue
        if '\x00' in content:  # Binär-Absicherung (all_text)
            continue
        lines = content.split('\n')
        for h in get_umlaut_violations(lines):
            violations.append((rel, h))

    if not violations:
        return 0

    # Pro Datei block vs. warn entscheiden (block_extensions/warn_extensions/global).
    # Reihenfolge bleibt erhalten, gleiche rel bleiben konsekutiv -> groupby trägt.
    block_viol, warn_viol = [], []
    for rel, h in violations:
        (block_viol if is_blocking_file(rel, cfg) else warn_viol).append((rel, h))

    w = sys.stderr.write

    def report(group_viol, title):
        w('\n=================================================================\n')
        w(' %s\n' % title)
        w('=================================================================\n\n')
        for rel, group in groupby(group_viol, key=lambda x: x[0]):
            w('  %s\n' % rel)
            for _, h in group:
                note = ', alleinstehend' if h['block'] == 2 else ''
                w("    Zeile %4d [Umlaut]: '%s'%s -> ASCII-Ersatz statt echtem "
                  "ä/ö/ü/ß. Siehe Skill umlaute.\n" % (h['line'], h['match'], note))
                text = h['text']
                snippet = text[:117] + '...' if len(text) > 120 else text
                w('      > %s\n' % snippet)

    if block_viol:
        report(block_viol, 'Pre-Commit-Hook: Umlaut-Verstöße erkannt (Commit blockiert)')
    if warn_viol:
        report(warn_viol, 'Pre-Commit-Hook: Umlaut-Verstöße (WARNUNG, blockt NICHT)')
    w('\n Bypass im Notfall: git commit --no-verify (DOKUMENTIEREN, warum)\n\n')
    return 1 if block_viol else 0


if __name__ == '__main__':
    sys.exit(main())
