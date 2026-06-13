#!/usr/bin/env python3
"""UserPromptSubmit-Hook: schätzt die Kontextfüllung aus dem Transcript und
injiziert einen Übergabe-Hinweis bei den Schwellen 40% / 60% / 75% des Modell-Caps.
Schwellen-State pro Session, damit dieselbe Schwelle nicht mehrfach feuert.

Plattformneutral (macOS / Windows / Linux). Pendant zur früheren check-kontextlast.ps1.
State-Verzeichnis über tempfile.gettempdir() (= $env:TEMP auf Windows, /tmp bzw.
$TMPDIR auf macOS/Linux). Detail:
.claude/skills/claude-code-optimierung/references/kontextfenster-degradation.md

Fail-open: bei jedem Fehler Exit 0.
"""
import json
import os
import sys
import tempfile
from datetime import datetime, timezone

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass


def fmt_anteil(value):
    """Wie die PowerShell-Interpolation eines double: 43.0 -> '43', 42.9 -> '42.9'."""
    return ('%g' % round(value, 1))


def main():
    try:
        raw = sys.stdin.read()
        if not raw:
            return 0
        data = json.loads(raw)
    except Exception:
        return 0  # Fail-open

    transcript_path = data.get('transcript_path')
    session_id = data.get('session_id') or 'unknown'

    if not transcript_path or not os.path.isfile(transcript_path):
        return 0

    # Cap aus Env-Var oder Default 1M. 1000000 = Opus 1M, 200000 = Sonnet/Haiku.
    cap = 1000000
    cap_raw = os.environ.get('CLAUDE_MODEL_CONTEXT_CAP')
    if cap_raw and cap_raw.isdigit():
        cap = int(cap_raw)

    # Char-Count des Transcripts. Token-Schätzung Char/3.5 (konservativ JSON+Deutsch).
    try:
        size_bytes = os.path.getsize(transcript_path)
    except Exception:
        return 0
    if size_bytes < 1:
        return 0
    tokens = int(round(size_bytes / 3.5))  # wie PowerShell [int] (Banker's Rounding), nicht truncate
    anteil = round((tokens / cap) * 100, 1)

    # Tool-Call-Counter als sekundärer Marker: JSONL-Zeilen mit "type":"tool_use".
    tool_calls = 0
    try:
        with open(transcript_path, encoding='utf-8', errors='replace') as fh:
            for line in fh:
                if '"type":"tool_use"' in line:
                    tool_calls += 1
    except Exception:
        tool_calls = 0

    # Aktuelle Schwelle bestimmen
    if anteil >= 75:
        schwelle = 75
    elif anteil >= 60:
        schwelle = 60
    elif anteil >= 40:
        schwelle = 40
    else:
        return 0

    # State-File pro Session: zuletzt gefeuerte Schwelle merken
    state_dir = os.path.join(tempfile.gettempdir(), 'claude-kontextlast')
    try:
        os.makedirs(state_dir, exist_ok=True)
    except Exception:
        pass
    state_file = os.path.join(state_dir, 'session-' + str(session_id) + '.json')

    letzte = 0
    if os.path.isfile(state_file):
        try:
            with open(state_file, encoding='utf-8-sig') as fh:
                st = json.load(fh)
            letzte = int(st.get('lastTriggered') or 0)
        except Exception:
            letzte = 0

    if schwelle <= letzte:
        return 0

    # State updaten
    try:
        with open(state_file, 'w', encoding='utf-8') as fh:
            json.dump({'lastTriggered': schwelle,
                       'updatedAt': datetime.now(timezone.utc).isoformat()}, fh)
    except Exception:
        pass

    reaktion = {
        40: "Leise Schwelle: Stand-Meldung im nächsten Turn vorbereiten, Übergabe-Vorschlag nach Abschluss des laufenden Mikro-Auftrags.",
        60: "Deutliche Schwelle: Übergabe-Vorschlag jetzt formulieren, User-Entscheidung abwarten bevor neue Arbeitsblöcke begonnen werden.",
        75: "Kritische Schwelle: KEINE neuen Nachlade-Reads mehr. Entweder Übergabe sofort oder Briefing-Spec im Repo (siehe Entscheidungsbaum).",
    }[schwelle]

    msg = (
        "KONTEXT-LAST-WARNUNG (Hook check-kontextlast)\n\n"
        "Geschätzte Auslastung: %s%% (~%d Tokens von %d Cap, %d Tool-Aufrufe bisher).\n"
        "Schwelle erreicht: %d%%.\n\n"
        "%s\n\n"
        "Pflicht-Verhalten laut Skill claude-code-optimierung:\n"
        "1. Kein Auto-Start-Imperativ. User entscheidet ob übergeben wird.\n"
        "2. Bei Übergabe: Stand-Meldung mit fünf Komponenten (Quittung, Roadmap, frische Bewertung, Empfehlung, offene Frage).\n"
        "3. Working-Tree prüfen: git status --short, fremde Drift dokumentieren oder selektiv stashen.\n"
        "4. Verify-Skript-Drift prüfen wenn ADR-Updates seit letztem Verify-Lauf liefen.\n"
        "5. Bei großem Code-Bau-Auftrag (2000+ Zeilen): Briefing-Spec im Repo, NICHT Direkt-Durchzug.\n"
        "6. Pre-Handover-Wissens-Inventur: entstandenes Wissen (Patterns, Pitfalls, Entscheidungen) zuerst in Skills/Wissensbasis/Memory wegschreiben, nicht im Handover lassen.\n\n"
        "Detail-Quellen:\n"
        "- .claude/skills/claude-code-optimierung/references/kontextfenster-degradation.md\n"
        "- .claude/skills/claude-code-optimierung/references/handover-prompts.md\n"
        "- .claude/skills/claude-code-optimierung/references/wissens-persistenz.md"
        % (fmt_anteil(anteil), tokens, cap, tool_calls, schwelle, reaktion))

    print(json.dumps({'hookSpecificOutput': {'hookEventName': 'UserPromptSubmit',
                                             'additionalContext': msg}}, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    sys.exit(main())
