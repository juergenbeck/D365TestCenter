#!/usr/bin/env python3
"""PostToolUse-Hook: meldet ein ADR oder einen Kickoff ohne Abnahmekriterien.

Hintergrund: ADR-2026-08-20-0712. Der Maßstab für „fertig" soll vor der Arbeit stehen und
nicht nachträglich vom Erbauer erfunden werden. Der Hook warnt und blockt nie: ein ADR kann
eine reine Struktur- oder Begriffsentscheidung sein. Dann steht im Abschnitt „entfällt" plus
Begründung, und das gilt als erfüllt.

Bewusst nur Meldung, kein deny: ein hartes Blockieren würde legitime Fälle aushebeln und den
Bypass zur Gewohnheit machen.
"""

import json
import os
import re
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stdin.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# Dateien, für die der Abschnitt Pflicht ist. Templates selbst sind ausgenommen: sie tragen
# die Ausfüllhilfe, nicht die Kriterien.
#
# ADR-2026-08-20-0749: Die Erkennung ist FUNKTIONAL, nicht pfadbasiert. Die Vorfassung verlangte
# /sessions/ im Pfad und fand deshalb in Markant null von 783 Kickoffs, weil die dort unter
# agent-system/zellen/*/handover/ liegen. Maßgeblich ist der Dateiname nach der familienweit
# stabilen Übergabe-Konvention, nicht der Ordner.
ADR_RE = re.compile(
    r'/(decisions|entscheidungen)/(ADR[-_]|OE[-_]|\d{4}-)[^/]+\.md$', re.IGNORECASE)
# <yyyy-mm-dd>-<hhmm>-kickoff-<kebab>.md, unabhängig vom Ordner. Der Zeitstempel trennt echte
# Session-Kickoffs von Fremdtreffern wie einer Terminnotiz "...-ui-kickoff-workshop.md".
KICKOFF_RE = re.compile(
    r'/\d{4}-\d{2}-\d{2}-\d{4}-kickoff-[^/]+\.md$', re.IGNORECASE)
TEMPLATE_RE = re.compile(r'/_templates?/', re.IGNORECASE)

# Der Abschnitt zählt als vorhanden, wenn die Überschrift da ist UND darunter Text steht.
HEADING_RE = re.compile(r'^#{2,3}\s*fertig,?\s*wenn\b', re.IGNORECASE | re.MULTILINE)

# Zustandsworte ohne Messgrösse. Nur als Hinweis, nicht als Ausschluss.
VAGUE_RE = re.compile(
    r'^\s*[-*]\s*.*\b(sauber umgesetzt|funktioniert|dokumentiert|erledigt|fertig)\s*\.?\s*$',
    re.IGNORECASE | re.MULTILINE)

MSG_MISSING = """ABNAHMEKRITERIEN FEHLEN (Hook check-abnahmekriterien)

  {path_}

Es fehlt der Pflichtabschnitt "Fertig, wenn" (ADR-2026-08-20-0712). Er gehört ausgefüllt,
BEVOR gebaut wird, damit der Maßstab nicht nachträglich vom Erbauer stammt.

Prüfbar heißt: ein Dritter kann ohne Rückfrage entscheiden.
  - ein Befehl mit erwartetem Ergebnis
  - ein Vergleich gegen einen benannten Referenzstand
  - eine Messung mit Zielwert
  - eine Datei mit erwartetem Inhalt
Dazu die Gegenprobe: was darf sich dabei NICHT ändern?

Gibt es nichts zu messen (reine Struktur- oder Begriffsentscheidung), den Abschnitt mit
"entfällt" plus Begründung füllen. Das gilt als erfüllt."""

MSG_EMPTY = """ABNAHMEKRITERIEN LEER (Hook check-abnahmekriterien)

  {path_}

Die Überschrift "Fertig, wenn" steht da, darunter aber nichts Zählbares. Entweder Kriterien
eintragen oder "entfällt" plus Begründung."""

MSG_VAGUE = """ABNAHMEKRITERIEN UNSCHARF (Hook check-abnahmekriterien)

  {path_}
{match}

Diese Zeilen beschreiben einen Zustand, aber nennen keine Messung. Ein Dritter kann daran
nicht ohne Rückfrage entscheiden. Bitte in Befehl, Vergleich, Messwert oder Dateiinhalt
übersetzen."""


# --- Opt-in je Repo (ADR-2026-08-20-0749) ---
# Der Hook wird familienweit ausgerollt, meldet aber nur dort, wo die Regel bekannt ist.
# Erkennungsmerkmal ist eine Vorlage, die den Abschnitt selbst trägt. Fehlt sie, schweigt er.
# Grund: eine Meldung in einem Repo, dessen Arbeitsweise die Regel nicht kennt, ist Lärm ohne
# Adressaten, und der erste Reflex darauf wäre, den Hook ganz abzuschalten.
TEMPLATE_CANDIDATES = (
    '_templates/adr.md', '_templates/kickoff.md',
    'decisions/_templates/adr.md', '_template/adr.md',
    '06_traceability/_templates/adr.md', '06_traceability/_templates/kickoff.md',
)


def _repo_knows_rule(start):
    """Sucht ab dem Datei-Verzeichnis aufwärts eine Vorlage mit dem Abschnitt."""
    d = os.path.dirname(os.path.abspath(start))
    for _ in range(12):
        if os.path.isdir(os.path.join(d, '.git')):
            for rel in TEMPLATE_CANDIDATES:
                full = os.path.join(d, rel.replace('/', os.sep))
                if os.path.isfile(full):
                    try:
                        with open(full, encoding='utf-8-sig') as fh:
                            if HEADING_RE.search(fh.read()):
                                return True
                    except Exception:
                        pass
            return False
        parent = os.path.dirname(d)
        if parent == d:
            return False
        d = parent
    return False


def _section_body(text, match):
    """Text zwischen der Überschrift und der nächsten Überschrift gleicher oder höherer Ebene."""
    rest = text[match.end():]
    next_heading = re.search(r'^#{1,3}\s', rest, re.MULTILINE)
    return rest[:next_heading.start()] if next_heading else rest


def _emit(msg):
    print(json.dumps(
        {'hookSpecificOutput': {'hookEventName': 'PostToolUse', 'additionalContext': msg}},
        ensure_ascii=False))


def main():
    try:
        data = json.loads(sys.stdin.read())
    except Exception:
        return 0

    path_ = str((data.get('tool_input') or {}).get('file_path') or '')
    if not path_:
        return 0
    norm = path_.replace('\\', '/')

    if TEMPLATE_RE.search(norm):
        return 0
    if not (ADR_RE.search(norm) or KICKOFF_RE.search(norm)):
        return 0
    if not os.path.isfile(path_):
        return 0
    if not _repo_knows_rule(path_):
        return 0

    try:
        with open(path_, encoding='utf-8-sig') as fh:
            text = fh.read()
    except Exception:
        return 0

    match = HEADING_RE.search(text)
    if not match:
        _emit(MSG_MISSING.format(path_=norm))
        return 0

    section_body = _section_body(text, match)
    lines = [z for z in section_body.splitlines() if z.strip()]
    if not lines:
        _emit(MSG_EMPTY.format(path_=norm))
        return 0

    vague = VAGUE_RE.findall(section_body)
    if vague:
        raw = [z.strip() for z in section_body.splitlines()
               if VAGUE_RE.match(z)]
        _emit(MSG_VAGUE.format(
            path_=norm, match='\n'.join('    ' + z for z in raw)))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)   # fail-open, wie die übrigen Hooks dieses Repos
