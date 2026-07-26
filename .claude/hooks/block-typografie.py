#!/usr/bin/env python3
"""PreToolUse-Sperre: blockt Write/Edit auf .md-Dateien, wenn der zu schreibende
Inhalt verbotene Typografie enthält (Halbgeviertstrich U+2013, Geviertstrich U+2014,
Pfeil U+2192). Ersatz laut globaler CLAUDE.md "Sprache und Stil": Komma, Punkt,
Doppelpunkt bzw. ASCII-Pfeil ->.

Code-Fences und Inline-Code sind ausgenommen (dort dürfen wörtlich zitierte
Fremdinhalte stehen).

AUTO-GENERATED aus ~/.claude/hook-templates/python/block-typografie.py
(ausgerollt von ~/.claude/scripts/Sync-UmlautTriggers.ps1). Nicht von Hand
editieren, sondern am Template ändern und neu syncen.

Anlass 2026-07-25 (Zastrpay): Eine Session hat trotz dokumentierter Regel massenhaft
Geviertstriche in Doku und einen Kunden-Mail-Entwurf geschrieben; die bestehende
Hook-Kette prüfte Typografie nur in AskUserQuestion/TaskCreate/TaskUpdate
(check-tool-umlaute.py), nicht in Write/Edit. Dieser Hook schließt die Lücke.

Mechanismus: permissionDecision "deny" via JSON-stdout (PreToolUse), Exit 0.
ensure_ascii=True gegen die Windows-cp1252-stdout-Falle. stdin wird BINÄR gelesen
und explizit als UTF-8 dekodiert: bei Pipes nimmt Python auf Windows sonst die
ANSI-Codepage (cp1252), die Unicode-Zeichen kämen als Byte-Salat an und würden
nie matchen (belegt im Selbsttest 2026-07-25). Fail-open: bei jedem Fehler
(kein stdin, JSON kaputt) Exit 0, damit ein Guard-Bug nichts blockiert.
"""
import json
import re
import sys

TYPO = {
    "–": "Halbgeviertstrich",
    "—": "Geviertstrich",
    "→": "Pfeil (statt dessen ->)",
}
_RE_INLINE = re.compile(r"`[^`]*`")


def violations(text):
    """Fundstellen außerhalb von Code-Fences und Inline-Code, als Label-Menge."""
    found = set()
    in_fence = False
    for line in text.split("\n"):
        stripped = line.lstrip()
        if stripped.startswith("```") or stripped.startswith("~~~"):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        clean = _RE_INLINE.sub("", line)
        for ch, label in TYPO.items():
            if ch in clean:
                found.add(label)
    return sorted(found)


def main():
    try:
        raw = sys.stdin.buffer.read().decode("utf-8", errors="replace")
        if not raw:
            return 0
        data = json.loads(raw)
    except Exception:
        return 0  # Fail-open

    tool = str(data.get("tool_name") or "")
    ti = data.get("tool_input")
    if not isinstance(ti, dict) or tool not in ("Write", "Edit"):
        return 0

    path = str(ti.get("file_path") or "")
    if not path.lower().endswith(".md"):
        return 0

    texts = []
    if tool == "Write":
        val = ti.get("content")
        if isinstance(val, str):
            texts.append(val)
    else:  # Edit
        val = ti.get("new_string")
        if isinstance(val, str):
            texts.append(val)
    if not texts:
        return 0

    hits = violations("\n".join(texts))
    if not hits:
        return 0

    reason = (
        "Typografie-Sperre (.md): verbotene Zeichen im neuen Inhalt: "
        + ", ".join(hits)
        + ". Regel (CLAUDE.md, Sprache/Stil): stattdessen Komma, Punkt, "
          "Doppelpunkt bzw. ASCII-Pfeil ->. Wörtliche Fremd-Zitate mit solchen "
          "Zeichen in Code-Fences oder Inline-Code setzen. Inhalt korrigieren "
          "und erneut schreiben."
    )
    out = {"hookSpecificOutput": {"hookEventName": "PreToolUse",
                                  "permissionDecision": "deny",
                                  "permissionDecisionReason": reason}}
    sys.stdout.write(json.dumps(out, ensure_ascii=True))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)
