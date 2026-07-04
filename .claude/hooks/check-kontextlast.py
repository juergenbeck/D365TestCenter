#!/usr/bin/env python3
"""UserPromptSubmit-Hook: warnt an Schwellen (50/70/85 Prozent), wenn der Kontext voll läuft.

HANDGESCHRIEBEN (nicht auto-generiert). Wird NICHT von Sync-UmlautTriggers.ps1 verwaltet.
D365TestCenter-Variante der Familien-Vorlage; Messkern nach Palas-Vorbild, Familien-Standard per
claudecode-ADR-2026-07-04-0943 (löst die frühere Byte-Heuristik ab, die systematisch
überschätzte: die Transcript-JSONL wächst append-only, verworfene Tool-Outputs und
Vor-Compact-Historie zählten mit).

Misst die ECHTE Kontextfüllung aus dem jüngsten usage-Eintrag des Transcripts
(input_tokens + cache_read_input_tokens + cache_creation_input_tokens); das ist auch nach
einem Compact korrekt. Das Kontextfenster ist modell-/session-abhängig und nicht zuverlässig
aus dem Transcript ablesbar; es kommt aus der Umgebungsvariablen CLAUDE_CONTEXT_WINDOW
(Default 1000000, fehlalarm-frei bei großem Fenster). Die Meldung nennt immer die absolute
Token-Zahl, die unabhängig vom angenommenen Fenster korrekt ist.

State-File pro Session, damit dieselbe Schwelle nicht mehrfach feuert.
fail-open: jeder Fehler -> Exit 0.
"""
import json
import os
import sys
import tempfile

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

SCHWELLEN = [50, 70, 85]
DEFAULT_WINDOW = 1000000

REAKTION = {
    50: "Leise Schwelle: Übergabe nach dem laufenden Mikro-Auftrag einplanen, Stand-Meldung "
        "vorbereiten. Der User entscheidet, ob übergeben wird, kein Auto-Start-Imperativ.",
    70: "Deutliche Schwelle: nächsten sinnvollen Schnitt suchen, Übergabe-Vorschlag formulieren "
        "und User-Entscheidung abwarten, bevor neue Arbeitsblöcke beginnen.",
    85: "Kritische Schwelle: nichts Neues mehr anfangen, keine neuen Nachlade-Reads. Jetzt "
        "übergeben oder den Stand als Spec/Briefing im Repo sichern, bevor Auto-Compact die "
        "scharfen Details (exakte Namen, Zeilennummern, Belege) verdichtet.",
}

UEBERGABE_HINWEIS = (
    "Bei Übergabe: ordentliche Übergabe nach der Konvention des D365TestCenter-Workspace-Repos "
    "(dort liegen Session-State und Arbeitsregeln); dieses Produkt-Repo hat kein eigenes "
    "Session-System."
)


def kontext_tokens(transcript_path):
    """Summe der Eingabe-Token des jüngsten usage-Eintrags = aktuelle Kontextfüllung."""
    if not transcript_path or not os.path.isfile(transcript_path):
        return None
    try:
        with open(transcript_path, encoding='utf-8', errors='replace') as fh:
            lines = fh.readlines()
    except Exception:
        return None
    for line in reversed(lines):
        line = line.strip()
        if not line or '"usage"' not in line:
            continue
        try:
            obj = json.loads(line)
        except Exception:
            continue
        if obj.get('isSidechain') is True:
            continue
        usage = None
        if isinstance(obj.get('usage'), dict):
            usage = obj['usage']
        elif isinstance(obj.get('message'), dict) and isinstance(obj['message'].get('usage'), dict):
            usage = obj['message']['usage']
        if not usage:
            continue
        total = 0
        for key in ('input_tokens', 'cache_read_input_tokens', 'cache_creation_input_tokens'):
            val = usage.get(key)
            if isinstance(val, (int, float)):
                total += int(val)
        if total > 0:
            return total
    return None


def main():
    raw = ''
    try:
        raw = sys.stdin.read()
    except Exception:
        pass
    try:
        data = json.loads(raw) if raw else {}
    except Exception:
        return 0

    transcript_path = data.get('transcript_path', '')
    session_id = data.get('session_id', 'unknown')

    tokens = kontext_tokens(transcript_path)
    if not tokens:
        return 0

    try:
        window = int(os.environ.get('CLAUDE_CONTEXT_WINDOW', DEFAULT_WINDOW))
    except Exception:
        window = DEFAULT_WINDOW
    if window <= 0:
        window = DEFAULT_WINDOW

    pct = tokens * 100 // window

    # höchste noch nicht gemeldete Schwelle aus dem State-File
    state_dir = os.path.join(tempfile.gettempdir(), 'claude-kontextlast')
    try:
        os.makedirs(state_dir, exist_ok=True)
    except Exception:
        pass
    state_file = os.path.join(state_dir, 'session-' + str(session_id) + '.json')
    last_level = 0
    try:
        with open(state_file, encoding='utf-8') as fh:
            last_level = int(json.load(fh).get('lastLevel', 0))
    except Exception:
        last_level = 0

    due = [s for s in SCHWELLEN if pct >= s and s > last_level]
    if not due:
        return 0
    reached = max(due)

    try:
        with open(state_file, 'w', encoding='utf-8') as fh:
            json.dump({'lastLevel': reached}, fh)
    except Exception:
        pass

    tk = round(tokens / 1000)
    wk = round(window / 1000)
    msg = (
        "KONTEXTLAST ~%d%% (Hook check-kontextlast)\n\n"
        "Echte Kontextfüllung: ~%dk von ~%dk Token (jüngster usage-Eintrag des Transcripts; "
        "Schwelle %d%% erreicht; Fenster justierbar via CLAUDE_CONTEXT_WINDOW).\n\n"
        "%s\n\n"
        "%s"
        % (pct, tk, wk, reached, REAKTION[reached], UEBERGABE_HINWEIS)
    )
    print(json.dumps({'hookSpecificOutput': {'hookEventName': 'UserPromptSubmit',
                                             'additionalContext': msg}}, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)
