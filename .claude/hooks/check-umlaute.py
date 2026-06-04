#!/usr/bin/env python3
"""PostToolUse-Hook: warnt bei ASCII-Surrogaten statt Umlauten (nicht blockierend).

AUTO-GENERATED aus ~/.claude/hook-templates/python/check-umlaute.py
(ausgerollt von ~/.claude/scripts/Sync-UmlautTriggers.ps1). Nicht von Hand editieren.

Plattformneutral (macOS / Windows / Linux). Teilt die Prüf-Logik mit dem
Commit-Hook .githooks/pre-commit.py über .githooks/umlaut_check_lib.py, damit
Schreib-Warnung und Commit-Block dieselben Verstöße erkennen.

Scope:
  - .md : ganze Datei prüfen.
  - .py : nur den NEU geschriebenen Inhalt aus dem Tool-Input prüfen.

Kein Dateinamen-Check: Dateinamen dürfen laut Konvention ASCII-Surrogate
verwenden, nur Datei-INHALTE sind umlautpflichtig.
"""
import json
import os
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass


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
    is_md = ext == '.md'
    is_code = ext == '.py'
    if not is_md and not is_code:
        return 0

    # Umlaut-Trigger-Tooling ausnehmen (definiert die Stamm-Liste selbst)
    norm = file_path.replace('\\', '/')
    if any(t in norm for t in ('umlaut_check_lib', 'umlaut-check-lib', 'Sync-UmlautTriggers', 'umlaute-triggers')):
        return 0

    project_dir = os.environ.get('CLAUDE_PROJECT_DIR') or os.getcwd()
    sys.path.insert(0, os.path.join(project_dir, '.githooks'))
    try:
        from umlaut_check_lib import get_umlaut_violations
    except Exception:
        return 0

    if is_md:
        if not os.path.isfile(file_path):
            return 0
        with open(file_path, encoding='utf-8') as fh:
            lines = fh.read().split('\n')
        scope = ''
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
        scope = ' (in der Änderung)'

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
           % (fname, scope, len(hits), '\n'.join(details)))
    print(json.dumps({'hookSpecificOutput': {'additionalContext': msg}}, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    sys.exit(main())
