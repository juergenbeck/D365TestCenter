#!/usr/bin/env python3
"""PreToolUse-Sperre: blockt Write/Edit auf .md-, .html- und .htm-Dateien, wenn der
zu schreibende Inhalt verbotene Typografie enthält (Halbgeviertstrich U+2013,
Geviertstrich U+2014 sowie die fünf Pfeile U+2192, U+2190, U+2194, U+21D2, U+21D4).
Ersatz laut globaler CLAUDE.md "Sprache und Stil": Komma, Punkt, Doppelpunkt bzw.
ASCII-Pfeil ->.

Code-Fences und Inline-Code sind ausgenommen (dort dürfen wörtlich zitierte
Fremdinhalte stehen), in HTML zusätzlich <code>- und <pre>-Bereiche.

HTML seit 2026-08-08 (LMApp): Ein DevOps-Ticket-Kommentar lag als .html im Repo,
enthielt sechs Geviertstriche, wurde von keiner Stufe der Kette gemeldet und stand
damit bereits am Work Item. Aufgefallen erst beim Zurücklesen des geposteten
Kommentars. Gerade die Kommunikationstexte, die nach außen gehen (Ticket-Antworten,
Mail-Entwürfe, Reports), liegen selten als .md vor - die Sperre lief also an ihrem
wichtigsten Anwendungsfall vorbei.

.txt ist hier BEWUSST NICHT dabei: Textdateien tragen häufig Fremdinhalt (Logs,
Exporte, Dumps), den man wörtlich behalten muss und für den es in .txt keine
Zitat-Ausnahme gibt; eine harte Schreibsperre erzeugte dort Fehlalarme ohne
Ausweg. Für .txt greift stattdessen der Commit-Check in pre-commit.py, der über
die umlaut-allowlist.json je Endung auf warn oder block gestellt werden kann.

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
    "→": "Pfeil rechts (stattdessen ->)",
    "←": "Pfeil links (stattdessen <-)",
    "↔": "Doppelpfeil (stattdessen <->)",
    "⇒": "Doppelpfeil rechts (stattdessen =>)",
    "⇔": "Doppelpfeil beidseitig (stattdessen <=>)",
}
_RE_INLINE = re.compile(r"`[^`]*`")
# HTML-Gegenstück zur Fence-/Inline-Ausnahme: wörtlich zitierter Fremdinhalt steht
# dort in <code> oder <pre>. Über den ganzen Text (nicht zeilenweise), weil diese
# Blöcke mehrzeilig sind. DOTALL, damit . auch Zeilenumbrüche trifft.
_RE_HTML_QUOTED = re.compile(r"<(code|pre)\b.*?</\1>", re.DOTALL | re.IGNORECASE)

BLOCKED_EXT = (".md", ".html", ".htm")


def violations(text, is_html=False):
    """Fundstellen außerhalb von Code-Fences und Inline-Code, als Label-Menge.
    Bei is_html zusätzlich ohne <code>- und <pre>-Bereiche."""
    if is_html:
        text = _RE_HTML_QUOTED.sub("", text)
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
    low = path.lower()
    if not low.endswith(BLOCKED_EXT):
        return 0
    is_html = low.endswith((".html", ".htm"))

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

    hits = violations("\n".join(texts), is_html=is_html)
    if not hits:
        return 0

    quote_hint = ("<code>- oder <pre>-Bereiche" if is_html
                  else "Code-Fences oder Inline-Code")
    kind = ".html" if is_html else ".md"
    reason = (
        "Typografie-Sperre (" + kind + "): verbotene Zeichen im neuen Inhalt: "
        + ", ".join(hits)
        + ". Regel (CLAUDE.md, Sprache/Stil): stattdessen Komma, Punkt, "
          "Doppelpunkt bzw. ASCII-Pfeil ->. Wörtliche Fremd-Zitate mit solchen "
          "Zeichen in " + quote_hint + " setzen. Inhalt korrigieren "
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
