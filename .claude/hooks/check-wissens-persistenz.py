#!/usr/bin/env python3
"""UserPromptSubmit-Hook: erinnert daran, entstandenes Wissen in Dateien wegzuschreiben.
Drei Auslöser:
  1) Handover-Signalwort im Prompt: injiziert die Pre-Handover-Wissens-Inventur (einmal pro Session).
  2) "Alles gut"-Checkpoint: Stand sichern (Inventur, state, handover, Commit/Push).
  3) Recherche-Block: zählt Recherche-Tool-Calls (Read/Grep/Glob/WebFetch/WebSearch) seit dem
     letzten Wissens-Write (Write/Edit auf einen Pfad unter skills/architekturen/memory). Über
     der Schwelle kommt eine aktive Erinnerung; ein Wissens-Write setzt den Zähler zurück.
Der Hook erinnert nur, das Erkennen und Wegschreiben bleibt Modell-Aufgabe.

Plattformneutral (macOS / Windows / Linux). Pendant zur früheren check-wissens-persistenz.ps1.
State-Verzeichnis über tempfile.gettempdir(). Detail:
.claude/skills/claude-code-optimierung/references/wissens-persistenz.md

ASCII-Surrogat-Hinweis: Der Handover-Regex matcht bewusst BEIDE Schreibweisen deutscher
Handover-Phrasen ('fuer heute' neben 'für heute', 'uebergabe' neben 'übergabe', 'abschliessen'
neben 'abschließen'), damit der Hook auch feuert, wenn der User ASCII tippt. Diese ASCII-Formen
sind Match-Funktionsdaten, kein Umlaut-Verstoß; diese Datei steht daher in der exceptions-Sektion
von .githooks/umlaut-allowlist.json.

Fail-open: bei jedem Fehler Exit 0.
"""
import json
import os
import re
import sys
import tempfile
from datetime import datetime, timezone

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

# Schwelle: Recherche-Tool-Calls seit letztem Wissens-Write, ab der erinnert wird.
SCHWELLE = 10

# Handover-Signalwort. Matcht ASCII- UND Umlaut-Schreibweise (Match-Funktionsdaten).
HANDOVER_RE = re.compile(
    r'(handover|kickoff|sessionende|session.?ende|session (abschließen|abschliessen|beenden|abschluss)'
    r'|feierabend|fertig für heute|fertig fuer heute|schluss für heute|schluss fuer heute'
    r'|machen wir (für heute |fuer heute )?schluss|übergabe schreiben|uebergabe schreiben|state schreiben)',
    re.IGNORECASE)
ALLES_GUT_RE = re.compile(r'\balles\s+gut\b', re.IGNORECASE)

RESEARCH_RE = re.compile(r'"name":"(Read|Grep|Glob|WebFetch|WebSearch)"', re.IGNORECASE)
PERSIST_NAME_RE = re.compile(r'"name":\s*"(Write|Edit)"', re.IGNORECASE)
# Wissens-Write-Pfade dieses Repos (repo-spezifisch an die Doku-Struktur angepasst,
# nicht die Markant-Defaults skills|architekturen|memory): der docs/-Baum, Skills und
# der Auto-Memory-Pfad. Ein Treffer setzt den Recherche-Block-Zähler zurück.
PERSIST_PATH_RE = re.compile(r'"file_path":\s*"[^"]*(docs|skills|memory)', re.IGNORECASE)


def emit(msg):
    print(json.dumps({'hookSpecificOutput': {'hookEventName': 'UserPromptSubmit',
                                             'additionalContext': msg}}, ensure_ascii=False))


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
    prompt_text = str(data.get('prompt') or '')

    # State-File pro Session
    state_dir = os.path.join(tempfile.gettempdir(), 'claude-wissens-persistenz')
    try:
        os.makedirs(state_dir, exist_ok=True)
    except Exception:
        pass
    state_file = os.path.join(state_dir, 'session-' + str(session_id) + '.json')

    last_notified = 0
    handover_hint_shown = False
    if os.path.isfile(state_file):
        try:
            with open(state_file, encoding='utf-8-sig') as fh:
                st = json.load(fh)
            last_notified = int(st.get('lastNotifiedROffen') or 0)
            handover_hint_shown = bool(st.get('handoverHintShown') or False)
        except Exception:
            last_notified = 0
            handover_hint_shown = False

    def save_state(r_offen, hh_shown):
        try:
            with open(state_file, 'w', encoding='utf-8') as fh:
                json.dump({'lastNotifiedROffen': r_offen, 'handoverHintShown': hh_shown,
                           'updatedAt': datetime.now(timezone.utc).isoformat()}, fh)
        except Exception:
            pass

    # --- Auslöser 1: Handover-Signalwort (Priorität vor Recherche-Block) ---
    if prompt_text and HANDOVER_RE.search(prompt_text):
        if not handover_hint_shown:
            msg = (
                "PRE-HANDOVER-WISSENS-INVENTUR (Hook check-wissens-persistenz)\n\n"
                "Bevor der Handover oder Kickoff geschrieben wird: Wissens-Inventur als Pflicht-Schritt 0.\n"
                "Der Handover dokumentiert den Stand, er ist NICHT der Ablageort für wiederverwendbares Wissen.\n\n"
                "Vier Fragen, bei Treffer jeweils ZUERST persistieren, dann den Handover schreiben:\n"
                "1. Neue wiederverwendbare Fakten, Patterns oder Pitfalls dieser Session? -> passender Skill\n"
                "   (Kurzfakt in SKILL.md, Detail oder Beleg in references/*.md). Nie ins Memory.\n"
                "2. Architektur- oder Design-Entscheidung getroffen? -> Wissensbasis plus Changelog.\n"
                "3. Korrektur oder Präferenz vom User bekommen, die künftiges Vorgehen ändert? -> Memory (feedback).\n"
                "4. Etwas Wichtiges nur im Chat oder im Entwurf, das dauerhaft gehört? -> umlagern, im Handover nur verlinken.\n\n"
                "Erst wenn die Inventur leer ist, den Handover schreiben; er verweist dann auf die persistierten Stellen.\n\n"
                "Bei einer AUSDRÜCKLICH angeforderten Sessionübergabe (oder Handover/Kickstart) gehören alle drei\n"
                "Rollen dazu, nicht nur der Handover: kickoff, handover und state mit gemeinsamem Zeitstempel. Der\n"
                "Kickoff ist der Eingangs-Prompt der Folge-Session; ohne ihn ist die Übergabe unvollständig.\n"
                "Detail: .claude/skills/claude-code-optimierung/references/handover-prompts.md (Pre-Handover-Wissens-Inventur,\n"
                "Datei-Namens- und Ablage-Konvention) und references/wissens-persistenz.md")
            emit(msg)
            save_state(last_notified, True)
            return 0

    # --- Auslöser 2: "Alles gut"-Checkpoint (Stand sichern) ---
    # Bewusst nicht pro Session gegated: jeder Marker ist ein eigener Checkpoint.
    # Nur bei kurzem, eigenständigem Prompt (getrimmt <= 50 Zeichen).
    prompt_trim = prompt_text.strip() if prompt_text else ''
    if len(prompt_trim) <= 50 and ALLES_GUT_RE.search(prompt_trim):
        msg = (
            "CHECKPOINT \"Alles gut\" (Hook check-wissens-persistenz)\n\n"
            "\"Alles gut\" ist ein Sicherungspunkt, kein Sessionende. Vier Pflicht-Schritte jetzt:\n\n"
            "1. WISSENS-INVENTUR: Ist alles diese Session erlangte wiederverwendbare Wissen persistiert?\n"
            "   Patterns/Pitfalls/verifizierte Verhalten -> passender Skill (SKILL.md bzw. references/*.md);\n"
            "   Architektur- oder Design-Entscheidung -> Wissensbasis plus Changelog;\n"
            "   Korrektur oder Präferenz vom User -> Memory (feedback). Offenes JETZT wegschreiben.\n"
            "2. session-state.md aktualisieren, oder neu anlegen, falls das Thema noch keine hat (aktueller Stand).\n"
            "3. handover-Datei aktualisieren, oder neu anlegen, falls noch keine existiert (aktueller Abschlussbericht-Stand).\n"
            "4. COMMIT plus PUSH: die Änderungen committen und pushen, sonst ist der Stand nicht wirklich gesichert.\n"
            "   Scoped committen (nur die eigenen Session-Änderungen, keine fremde Drift), Commit-Message mit echten\n"
            "   Umlauten, Push gemäß Push-Regel nach Repo-Ort (Markant-Hauptrepo ohne Rückfrage). Ein Sessionabschluss\n"
            "   ohne Commit ist unvollständig.\n\n"
            "Ablage und Namenskonvention: references/handover-prompts.md (Datei-Namens- und Ablage-Konvention).\n"
            "Ist die Ablage nicht eindeutig zuordenbar, kurz fragen statt raten.")
        emit(msg)
        return 0

    # --- Auslöser 3: Recherche-Block-Zähler ---
    if not transcript_path or not os.path.isfile(transcript_path):
        return 0

    # Letzte ~4000 Zeilen als Fenster (Performance).
    try:
        with open(transcript_path, encoding='utf-8', errors='replace') as fh:
            lines = fh.read().split('\n')[-4000:]
    except Exception:
        lines = []
    if not lines:
        return 0

    r_offen = 0
    for line in lines:
        if not line:
            continue
        if PERSIST_NAME_RE.search(line) and PERSIST_PATH_RE.search(line):
            # Wissens-Write erkannt: Block abgeschlossen, Zähler zurücksetzen
            r_offen = 0
            continue
        m = RESEARCH_RE.findall(line)
        if m:
            r_offen += len(m)

    if r_offen < SCHWELLE:
        # Block zu klein oder gerade weggeschrieben: Anker zurücksetzen, nicht feuern
        if last_notified != 0:
            save_state(0, handover_hint_shown)
        return 0

    # r_offen >= Schwelle. Persist seit letztem Feuern (r_offen sank) -> Anker zurücksetzen.
    if r_offen < last_notified:
        last_notified = 0

    if last_notified == 0 or (r_offen - last_notified) >= SCHWELLE:
        msg = (
            "WISSENS-PERSISTENZ (Hook check-wissens-persistenz)\n\n"
            "Seit dem letzten Wissens-Write gab es ~%d Recherche- oder Lese-Operationen ohne Persistierung.\n\n"
            "Falls dabei wiederverwendbares Wissen entstanden ist (ein verifiziertes Verhalten, ein Pattern,\n"
            "ein Pitfall mit Beleg, eine Entscheidung): JETZT wegschreiben, bevor das nächste Thema beginnt.\n"
            "Erst persistieren, dann weiter.\n\n"
            "Wohin:\n"
            "- Fachwissen -> passender Skill (Kurzfakt in SKILL.md, Detail in references/*.md). Nie ins Memory.\n"
            "- Architektur-Entscheidung oder -Stand -> Wissensbasis plus Changelog.\n"
            "- Arbeitsweise, Korrektur, Präferenz -> Memory (feedback).\n\n"
            "War es reine Recherche ohne bleibendes Wissen, ist das ok: kurz prüfen und weiterarbeiten.\n"
            "Detail: .claude/skills/claude-code-optimierung/references/wissens-persistenz.md"
            % r_offen)
        emit(msg)
        save_state(r_offen, handover_hint_shown)

    return 0


if __name__ == '__main__':
    sys.exit(main())
