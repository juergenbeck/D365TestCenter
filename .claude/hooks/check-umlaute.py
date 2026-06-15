#!/usr/bin/env python3
"""PostToolUse-Hook: warnt bei ASCII-Surrogaten statt Umlauten (nicht blockierend).

AUTO-GENERATED aus ~/.claude/hook-templates/python/check-umlaute.py
(ausgerollt von ~/.claude/scripts/Sync-UmlautTriggers.ps1). Nicht von Hand editieren.

Plattformneutral (macOS / Windows / Linux). Teilt die Prüf-Logik mit dem
Commit-Hook .githooks/pre-commit.py über .githooks/umlaut_check_lib.py, damit
Schreib-Warnung und Commit-Block dieselben Verstöße erkennen.

Das Verhalten kommt aus der optionalen .githooks/umlaut-allowlist.json (dieselbe
Quelle wie der Commit-Hook):
  file_scope  : "md_only" (Default) prüft .md (ganze Datei) und .py (nur die Änderung);
                "all_text" prüft zusätzlich alle weiteren Textdateien (nur die Änderung).
  generated[] : Regex-Liste (repo-relativer Pfad), ausgeschlossene Pfade.
  exceptions[]: [{path}] exakte oder glob-Einzeldatei-Ausnahmen.
Fehlt die Config: md_only, keine Zusatz-Ausschlüsse (verhält sich wie zuvor).

Scope-Begründung: .md wird ganz geprüft (geringer Alt-Drift, Verstöße überall
sichtbar). Alle anderen Textdateien nur in der NEU geschriebenen Passage aus dem
Tool-Input (Edit.new_string / Write.content / MultiEdit.edits), weil der Bestand
Alt-Drift trägt und eine Ganzdatei-Warnung bei jedem Edit Rauschen erzeugen würde.

Kein Dateinamen-Check: Dateinamen dürfen laut Konvention ASCII-Surrogate
verwenden, nur Datei-INHALTE sind umlautpflichtig.
"""
import fnmatch
import json
import os
import re
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

# Binär-Endungen: zeilenweiser Textcheck wäre sinnlos.
BINARY_EXT = {
    '.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.ico', '.svg', '.pdf',
    '.zip', '.7z', '.gz', '.tgz', '.tar', '.rar', '.docx', '.doc', '.xlsx', '.xls',
    '.pptx', '.vsdx', '.msg', '.eml', '.mp3', '.mp4', '.m4a', '.wav', '.mov', '.avi',
    '.mkv', '.woff', '.woff2', '.ttf', '.otf', '.eot', '.exe', '.dll', '.pdb', '.so',
    '.dylib', '.bin', '.dat', '.class', '.jar', '.pyc', '.o', '.a', '.lib', '.nupkg', '.snk',
}


def load_config(githooks_dir):
    """Liest .githooks/umlaut-allowlist.json. Defaults (fehlt/kaputt): md_only,
    keine Zusatz-Ausschlüsse."""
    cfg = {'file_scope': 'md_only', 'generated': [], 'exceptions': []}
    path = os.path.join(githooks_dir, 'umlaut-allowlist.json')
    if not os.path.isfile(path):
        return cfg
    try:
        with open(path, encoding='utf-8') as fh:
            data = json.load(fh)
    except Exception:
        return cfg
    if data.get('file_scope'):
        cfg['file_scope'] = str(data['file_scope'])
    if data.get('generated'):
        compiled = []
        for p in data['generated']:
            try:
                compiled.append(re.compile(p))
            except re.error:
                pass
        cfg['generated'] = compiled
    if data.get('exceptions'):
        cfg['exceptions'] = [e['path'] for e in data['exceptions']
                             if isinstance(e, dict) and e.get('path')]
    return cfg


def is_excluded(rel, norm, generated, exceptions):
    for rx in generated:
        if rx.search(rel) or rx.search(norm):
            return True
    for p in exceptions:
        if rel == p or fnmatch.fnmatch(rel, p):
            return True
    return False


def main():
    raw = sys.stdin.read()
    try:
        data = json.loads(raw)
    except Exception:
        return 0

    ti = data.get('tool_input') or {}
    file_path = ti.get('file_path')
    if not file_path:
        return 0

    ext = os.path.splitext(file_path)[1].lower()
    if ext in BINARY_EXT:
        return 0

    norm = file_path.replace('\\', '/')

    # Umlaut-Trigger-Tooling ausnehmen (definiert die Stamm-Liste selbst)
    if any(t in norm for t in ('umlaut_check_lib', 'umlaut-check-lib',
                               'Sync-UmlautTriggers', 'umlaute-triggers')):
        return 0

    project_dir = os.environ.get('CLAUDE_PROJECT_DIR') or os.getcwd()
    githooks_dir = os.path.join(project_dir, '.githooks')
    cfg = load_config(githooks_dir)
    scope = cfg['file_scope']

    # Repo-relativen Pfad für den Allowlist-Match bestimmen
    proj_norm = project_dir.replace('\\', '/').rstrip('/')
    if norm.lower().startswith(proj_norm.lower() + '/'):
        rel = norm[len(proj_norm) + 1:]
    else:
        rel = norm
    if is_excluded(rel, norm, cfg['generated'], cfg['exceptions']):
        return 0

    is_md = ext == '.md'
    # md_only: nur .md (ganz) und .py (Änderung). all_text: jede Nicht-Binär-Datei.
    if scope != 'all_text':
        if not is_md and ext != '.py':
            return 0

    sys.path.insert(0, githooks_dir)
    try:
        from umlaut_check_lib import get_umlaut_violations
    except Exception:
        return 0

    if is_md:
        if not os.path.isfile(file_path):
            return 0
        # utf-8-sig: führendes UTF-8-BOM entfernen, wie Get-Content -Encoding UTF8
        # (sonst trägt Zeile 1 das BOM-Zeichen). Ohne BOM wie utf-8.
        with open(file_path, encoding='utf-8-sig') as fh:
            lines = fh.read().split('\n')
        scope_note = ''
    else:
        new_text = None
        if 'content' in ti:
            new_text = ti.get('content')
        elif 'new_string' in ti:
            new_text = ti.get('new_string')
        elif 'edits' in ti:
            new_text = '\n'.join(e.get('new_string', '') for e in ti.get('edits', []))
        if not new_text:
            return 0
        lines = new_text.split('\n')
        scope_note = ' (in der Änderung)'

    hits = get_umlaut_violations(lines)
    if not hits:
        return 0

    fname = os.path.basename(file_path)
    details = []
    for h in hits:
        note = ' (alleinstehend)' if h['block'] == 2 else ''
        details.append("  '%s'%s -> %s" % (h['match'], note, h['text']))
    msg = ("UMLAUT-VERSTÖSSE in %s%s: %d gefunden.\n%s\n"
           "Bitte echte Umlaute (ä ö ü ß) statt ae/oe/ue/ss verwenden. "
           "Technische Bezeichner (Variablen, Funktionsnamen) sind ausgenommen. Detail: Skill umlaute."
           % (fname, scope_note, len(hits), '\n'.join(details)))
    print(json.dumps({'hookSpecificOutput': {'hookEventName': 'PostToolUse', 'additionalContext': msg}}, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    sys.exit(main())
