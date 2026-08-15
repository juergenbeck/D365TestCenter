#!/usr/bin/env python3
"""UserPromptSubmit-Hook: warnt an Schwellen, wenn der Kontext voll läuft.

Misst die ECHTE Kontextfüllung aus dem jüngsten usage-Eintrag des Transcripts
(input_tokens + cache_read_input_tokens + cache_creation_input_tokens); das ist auch
nach einem Compact korrekt. Familien-Standard seit claudecode-ADR-2026-07-04-0943
(Palas-Vorbild), löst die frühere Byte-Heuristik bytes/3.5 ab, die systematisch
überschätzte: die Transcript-JSONL wächst append-only, verworfene Tool-Outputs und
Vor-Compact-Historie zählten mit.

Das Kontextfenster folgt dem Modell (siehe MODELL_FENSTER), Vorrang hat die
Umgebungsvariable CLAUDE_CONTEXT_WINDOW. Die Meldung nennt immer die absolute
Token-Zahl, die unabhängig vom angenommenen Fenster korrekt ist.

State-File pro Session, damit dieselbe Schwelle nicht mehrfach feuert.
fail-open: jeder Fehler -> Exit 0.

GENERIERT aus ~/.claude/hook-templates/python/check-kontextlast.py durch
Sync-UmlautTriggers.ps1. Änderungen am Rahmen gehören ins Template, nicht hierher.
AUSGENOMMEN ist die mit KONTEXTLAST-TEXTE markierte Region weiter unten: die ist
repo-spezifisch, wird vom Sync NIE überschrieben und gehört genau hierher
(ADR-2026-08-15-1143 im Repo claudecode).
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

# Kontextfenster je Modell. Quelle: Skill `claude-api` (Modelltabelle, Stand 2026-06-24),
# gegengeprüft am 15.08.2026 an den real erreichten Maxima von 1153 Transcripten: kein
# gemessener Wert überschreitet sein dokumentiertes Fenster.
#
# Ohne diese Tabelle würde eine Haiku-Session (200k Fenster) mit dem 1M-Default bei
# echten 85 Prozent als 17 Prozent gemeldet und die Warnung bliebe aus - derselbe
# Fehler wie eine geschätzte Zahl, nur in die andere Richtung und ohne Korrektiv.
MODELL_FENSTER = {
    'claude-opus-5': 1000000,
    'claude-fable-5': 1000000,
    'claude-mythos-5': 1000000,
    'claude-opus-4-8': 1000000,
    'claude-opus-4-7': 1000000,
    'claude-opus-4-6': 1000000,
    'claude-sonnet-5': 1000000,
    'claude-sonnet-4-6': 1000000,
    'claude-haiku-4-5': 200000,
}


def fenster_aus_modell(modell):
    """Fenster zum Modell, auch bei angehängtem Datums-Suffix."""
    if not modell:
        return None
    if modell in MODELL_FENSTER:
        return MODELL_FENSTER[modell]
    for basis, fenster in MODELL_FENSTER.items():
        if modell.startswith(basis):
            return fenster
    return None


def kontext_tokens(transcript_path):
    """Jüngster usage-Eintrag: (Summe der Eingabe-Token, Modell) = aktuelle Kontextfüllung."""
    if not transcript_path or not os.path.isfile(transcript_path):
        return None, None
    try:
        with open(transcript_path, encoding='utf-8', errors='replace') as fh:
            lines = fh.readlines()
    except Exception:
        return None, None
    for line in reversed(lines):
        line = line.strip()
        if not line or '"usage"' not in line:
            continue
        try:
            obj = json.loads(line)
        except Exception:
            continue
        # Subagenten laufen in eigenen Fenstern; ihre Last gehört nicht in diese Zahl.
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
            nachricht = obj.get('message') if isinstance(obj.get('message'), dict) else {}
            return total, (nachricht.get('model') or obj.get('model'))
    return None, None


def fenster_ableiten(modell):
    """(Fenster, Herkunft). Vorrang: Umgebungsvariable, dann Modell, dann Default."""
    if os.environ.get('CLAUDE_CONTEXT_WINDOW'):
        try:
            window = int(os.environ['CLAUDE_CONTEXT_WINDOW'])
            herkunft = 'CLAUDE_CONTEXT_WINDOW'
        except Exception:
            window, herkunft = DEFAULT_WINDOW, 'Default (CLAUDE_CONTEXT_WINDOW unlesbar)'
    else:
        aus_modell = fenster_aus_modell(modell)
        if aus_modell:
            window, herkunft = aus_modell, 'Modell ' + str(modell)
        else:
            window = DEFAULT_WINDOW
            herkunft = 'Default (Modell %s unbekannt)' % (modell or 'nicht ermittelt')
    if window <= 0:
        window, herkunft = DEFAULT_WINDOW, 'Default (ungültiger Wert)'
    return window, herkunft


# === KONTEXTLAST-TEXTE ANFANG (repo-spezifisch, vom Sync nicht angetastet) ===
# Alles zwischen diesen beiden Markern gehört diesem Repo. Der Sync rendert den
# Rahmen darum herum neu und lässt diese Region unverändert stehen. Pflicht ist
# genau eine Funktion:
#
#     meldung(pct, tokens, window, reached, herkunft) -> str
#
# pct = Prozent (int), tokens/window = absolute Token (int), reached = erreichte
# Schwelle (int), herkunft = woher das Fenster stammt (str). Konstanten, die nur
# der Meldungstext braucht, gehören ebenfalls hierher.

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


def meldung(pct, tokens, window, reached, herkunft):
    """Der an Claude gemeldete Text. Repo-spezifisch, aus der Vorgängerfassung übernommen."""
    tk = round(tokens / 1000)
    wk = round(window / 1000)
    return (
            "KONTEXTLAST ~%d%% (Hook check-kontextlast)\n\n"
            "Echte Kontextfüllung: ~%dk von ~%dk Token (jüngster usage-Eintrag des Transcripts; "
            "Schwelle %d%% erreicht; Fenster aus: %s, justierbar via CLAUDE_CONTEXT_WINDOW).\n\n"
            "%s\n\n"
            "%s"
            % (pct, tk, wk, reached, herkunft, REAKTION[reached], UEBERGABE_HINWEIS)
        )


# === KONTEXTLAST-TEXTE ENDE ===


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

    tokens, modell = kontext_tokens(transcript_path)
    if not tokens:
        return 0

    window, herkunft = fenster_ableiten(modell)
    pct = tokens * 100 // window

    # höchste bereits gemeldete Schwelle aus dem State-File
    state_dir = os.path.join(tempfile.gettempdir(), 'claude-kontextlast')
    try:
        os.makedirs(state_dir, exist_ok=True)
    except Exception:
        pass
    state_file = os.path.join(state_dir, 'session-' + str(session_id) + '.json')
    last_level = 0
    try:
        with open(state_file, encoding='utf-8') as fh:
            st = json.load(fh)
            # lastSchwelle: Altbestand aus Innoform, damit ein laufender State nicht neu feuert.
            last_level = int(st.get('lastLevel', st.get('lastSchwelle', 0)) or 0)
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

    try:
        msg = meldung(pct, tokens, window, reached, herkunft)
    except Exception:
        return 0

    print(json.dumps({'hookSpecificOutput': {'hookEventName': 'UserPromptSubmit',
                                             'additionalContext': msg}}, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)
