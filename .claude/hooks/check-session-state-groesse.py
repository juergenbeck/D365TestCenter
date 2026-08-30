#!/usr/bin/env python3
"""PostToolUse-Hook: erinnert, wenn eine lebende Stand-Datei zu groß wird.

Anlass (2026-08-08): Lebende Stand-Dateien wachsen zu chronologischen Anhäng-Logs,
weil Sessions oben einen Absatz ergänzen statt den Stand zu überschreiben. Zuerst
in Markant aufgefallen (TOOL-0025: pisa-bridge 2.474 Zeilen / 202 KB, Datenfreigabe
1.683 / 260 KB, mcds-bridge 598 / 48 KB), danach über alle 17 Repos der Familie
gemessen: 25 Dateien jenseits der Schwellen, 11 davon außerhalb von Markant.

Das Problem ist nicht die Größe, sondern die Verlässlichkeit. In jeder der
untersuchten Dateien stand ein überholter Absatz neben einem neueren: „Der Bau hat
noch nicht begonnen" neben dem fertigen Bau, ADR-Stand „1 offen" gegen real 34
geschlossene, ein Riverty-Ticket mit vier Stand-Blöcken, von denen einer im eigenen
Titel als „durch den Vollzug überholt" markiert war und trotzdem stehenblieb.

Zwei Schwellen, ODER-verknüpft. Beide sind nötig:
  - Reine Zeilenzahl greift zu kurz: Markants cdh-fg-bridge hatte 80 Zeilen bei
    65 KB, claudecodes cockpit.md hält mit 81 Zeilen seine Zeilenvorgabe ein und
    trägt dabei 45 KB. Der Inhalt steckt in sehr langen Zeilen.
  - Reine Byte-Zahl greift ebenfalls zu kurz, weil eine lange Liste kurzer Zeilen
    unauffällig bliebe.

Drei Dateiarten mit eigener Schwelle und eigener Botschaft, weil der Befund je Art
ein anderer ist: eine überlaufende Stand-Datei ist falsch gegliedert, ein
überlaufendes Journal heißt dagegen, dass die Übergabe überfällig ist.

Nicht blockierend (additionalContext), fail-open (jeder Fehler -> Exit 0). Warnt
höchstens einmal je Session und Datei, sonst feuert der Hook bei jedem Edit während
der Bereinigung selbst.
"""
import json
import os
import re
import sys
import tempfile

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

# Kalibriert am Gesamtbestand vom 08.08.2026 (300 lebende Stand-Dateien über alle
# target_repos): Median 95 Zeilen / 7,0 KB, p75 165 / 13,7 KB, p90 308 / 27,2 KB.
# Die Schwelle liegt bewusst oberhalb des p90, damit ein normal gepflegter Stand nie
# anschlägt und die Meldung nicht zur Gewohnheit wird. Nicht zu verwechseln mit der
# ZIELGRÖSSE guter Pflege (Richtwert 150 Zeilen); das hier ist die ALARM-Schwelle.
STAND_LINES, STAND_BYTES = 400, 40 * 1024
# Das Cockpit trägt je Strom eine Zeile, das Detail steht in der verlinkten Datei.
# Es ist im gemessenen Bestand der am stärksten verrottete Dateityp: vier von zwölf
# über 40 KB, alle vier ausschließlich über die Byte-Schwelle auffällig.
#
# Für Cockpits gilt BEWUSST KEINE Zeilenschwelle (None). Die Zeilenzahl eines Cockpits
# ist durch die Zahl der Ströme bestimmt und damit legitime Streuung, nicht Verfall: das
# Cockpit von D365TestCenter-Workspace hat 350 Zeilen bei 93 Zeichen je Zeile und ist
# kerngesund, Markants Infopool-Cockpit führt 114 Ströme. Die Krankheit des Dateityps
# sind fette Zellen, und die trifft ausschließlich die Byte-Schwelle. Eine zunächst
# gesetzte Schwelle von 150 Zeilen fand am Bestand keinen einzigen echten Fall und
# erzeugte nur Fehlalarme (gemessen 2026-08-08).
COCKPIT_LINES, COCKPIT_BYTES = None, 40 * 1024
JOURNAL_LINES, JOURNAL_BYTES = 400, 40 * 1024
# Zusätzlich zur Dateigröße: die einzelne fette Zelle (2026-08-29). Die Byte-Schwelle
# greift erst bei 40 KB, eine einzelne Zelle bleibt darunter beliebig lange unsichtbar -
# und wenn sie greift, trifft die Bereinigung jemanden, der die Zellen nicht geschrieben
# hat. Anlass: ein vollständiger Cockpit-Rückbau (35 fette Zellen auf null) hielt keine
# drei Stunden, bevor die nächste Session wieder auf 1.135 Zeichen ging.
#
# ENTSCHEIDEND ist, WAS geprüft wird: der neu geschriebene Text, nicht die Datei. Über
# alle 12 Cockpits der Familie gemessen (272 Zellen, Median 353, p90 1.210, längste
# 10.442) träfe eine Schwelle von 400 auf die ganze Datei 90 Zellen in 7 von 12 Cockpits.
# Eine Meldung, die ein Drittel des Bestands betrifft und die niemand verursacht hat,
# wird weggelesen - genau der Fehler, den dieser Hook mit einer Zeilenschwelle von 150
# schon einmal gemacht und zurückgenommen hat.
#
# Weil nur Neugeschriebenes geprüft wird, DARF die Schwelle die Zielgröße der Regel
# abbilden statt oberhalb des p90 zu liegen. Eine kalibrierte Schwelle von rund 1.200
# hätte den Anlassfall mit 1.135 Zeichen nicht gefangen.
# Entscheid: ADR-2026-08-29-191843 im Repo claudecode.
COCKPIT_ZELLE = 400

# Die Zellenschwelle ist je Repo überschreibbar (2026-08-29, ADR-2026-08-29-210151).
# Anlass: Innoform und DeutscheBahn tragen Cockpit-Zellen mit einem Median von 1.654 und
# 517 Zeichen; dort käme die Meldung bei fast jeder Pflege. Sie beim Rollout schlicht
# auszulassen war keine Lösung, sondern eine Wette darauf, dass niemand den Generator
# laufen lässt - der rollt in ALLE target_repos aus.
#
# Getragen wird die Konfiguration von einer optionalen `.claude/hook-config.json` im
# Repo, die der Sync nicht anfasst (sie steht auf keiner seiner Renderlisten):
#     { "session_state_groesse": { "cockpit_zelle": 2000 } }
# 0 schaltet die Zellenprüfung ab. Bei fehlender, unlesbarer, kaputter Datei oder einem
# unbrauchbaren Wert gilt COCKPIT_ZELLE - Verhalten ändert sich nur dort, wo es jemand
# entschieden hat.
CONFIG_REL = ('.claude', 'hook-config.json')
CONFIG_MAX_EBENEN = 12


def _config_datei(start):
    """Sucht die Konfiguration vom bearbeiteten Pfad aufwärts bis zur Repo-Wurzel.

    Bewusst über den file_path statt über CLAUDE_PROJECT_DIR: der Pfad liegt dem Hook
    ohnehin vor, die Suche ist ohne gesetzte Umgebung prüfbar, und sie trifft auch dann
    das richtige Repo, wenn die Session aus einem anderen Verzeichnis heraus arbeitet.
    CLAUDE_PROJECT_DIR bleibt Rückfallebene.
    """
    try:
        ordner = os.path.dirname(os.path.abspath(start))
        for _ in range(CONFIG_MAX_EBENEN):
            kandidat = os.path.join(ordner, *CONFIG_REL)
            if os.path.isfile(kandidat):
                return kandidat
            if os.path.isdir(os.path.join(ordner, '.git')):
                return None  # Repo-Wurzel erreicht, ohne Konfiguration
            eltern = os.path.dirname(ordner)
            if eltern == ordner:
                return None
            ordner = eltern
    except Exception:
        return None
    return None


def zellengrenze(file_path):
    """Die geltende Zellenschwelle. Bei jedem Zweifel der Standardwert (fail-open)."""
    try:
        pfad = _config_datei(file_path)
        if not pfad:
            basis = os.environ.get('CLAUDE_PROJECT_DIR')
            if basis:
                kandidat = os.path.join(basis, *CONFIG_REL)
                if os.path.isfile(kandidat):
                    pfad = kandidat
        if not pfad:
            return COCKPIT_ZELLE
        with open(pfad, encoding='utf-8-sig') as fh:
            daten = json.load(fh)
        wert = daten['session_state_groesse']['cockpit_zelle']
    except Exception:
        return COCKPIT_ZELLE
    # bool ist in Python ein int - true ginge sonst als Schwelle 1 durch.
    if isinstance(wert, bool) or not isinstance(wert, int) or wert < 0:
        return COCKPIT_ZELLE
    return wert

# Beide Namensschemata sind in der Familie belegt und quer verteilt (session-state.md
# u.a. in Markant, Zastrpay, LMApp, MelanieInterieurDesign; sessionstate.md u.a. in
# Riverty, DeutscheBahn, claudecode), zwei Repos mischen beide. Eine Umbenennung von
# rund 300 Dateien samt aller Verweise brächte inhaltlich nichts, deshalb deckt der
# Hook schlicht beide ab.
STAND_NAMES = ('session-state.md', 'sessionstate.md')

# Selbst-erklärte, eingefrorene Archive sind kein Verfall, sondern korrekt verwaltete
# Historie und dürfen beliebig groß sein. Belegt an XrmForge-Workspace
# docs/05_traceability/session-state.md: 1.558 Zeilen, aber in Zeile 3 als EINGEFROREN
# ausgewiesen und per ADR-0006 zweimal abgelöst (erst STATE.md, dann cockpit.md). Solche
# Dateien liegen nicht zwingend in einem sessions/- oder handover/-Ordner, der Pfad-Filter
# oben greift dort also nicht.
#
# Zwei Bedingungen, beide nötig, sonst entstehen Fehlalarme:
#  (1) ganz oben, gemessen in ZEICHEN statt Zeilen. Eine Zeilengrenze ist wertlos, wenn
#      eine einzelne Tabellenzeile 21.861 Zeichen trägt (Riverty cockpit.md) - dann läge
#      der halbe Dateiinhalt im vermeintlichen Kopf.
#  (2) in der Kopf-Blockquote oder einer Überschrift. Ohne das matcht jede beiläufige
#      Prosa-Erwähnung: Markants INT-0039 ist ein lebender Stand und schreibt bei
#      Zeichen 880 "die eingefrorenen Snapshots".
FROZEN_RE = re.compile(
    r"eingefroren|nicht weiter (angeh|gepfleg|fortgeschrieb)|abgel(ö|oe)st durch",
    re.IGNORECASE)
FROZEN_FENSTER = 1000


def ist_eingefroren(pfad):
    """True, wenn die Datei sich selbst im Kopf als eingefrorenes Archiv ausweist."""
    try:
        with open(pfad, 'rb') as fh:
            kopf = fh.read(4096).decode('utf-8', errors='replace')[:FROZEN_FENSTER]
    except Exception:
        return False
    for zeile in kopf.split('\n'):
        s = zeile.lstrip()
        if (s.startswith('>') or s.startswith('#')) and FROZEN_RE.search(s):
            return True
    return False

WOHIN_STAND = (
    "Die lebende Stand-Datei wird ÜBERSCHRIEBEN, nicht angehängt. Sie trägt den\n"
    "AKTUELLEN Zustand, nicht die Chronik, und wird nach Zustand gegliedert statt nach\n"
    "Datum. Wächst sie über diese Schwelle, ist meist ein chronologisches Anhäng-Log\n"
    "daraus geworden.\n\n"
    "Das ist nicht nur teuer zu lesen, sondern wird falsch: gemessen stand in jeder\n"
    "betroffenen Datei ein überholter Absatz neben einem neueren.\n\n"
    "Wohin der Inhalt gehört:\n"
    "  - Ablauf und Begründung -> journal.md des Strangs beziehungsweise changelog/\n"
    "  - eingefrorener Stand   -> der Snapshot der Übergabe (sessions/ bzw. handover/)\n"
    "  - der aktuelle Zustand  -> hier, nach Zustand gegliedert\n\n"
    "Vor dem Kürzen jeden entfallenden Absatz auf Abdeckung prüfen: steht er im Journal,\n"
    "im Changelog oder in einem Snapshot? Was nirgends steht, vorher wegschreiben."
)

WOHIN_COCKPIT = (
    "Das Cockpit ist die Gesamtübersicht, nicht der Detail-Stand: je Strom eine Zeile\n"
    "mit Datum, Einzeiler und Link auf dessen lebende Stand-Datei. Das Detail gehört in\n"
    "die verlinkte Datei, nicht in die Cockpit-Zeile.\n\n"
    "Achtung, die Zeilenzahl täuscht hier besonders leicht: im gemessenen Bestand fielen\n"
    "alle auffälligen Cockpits ausschließlich über die Byte-Schwelle auf, weil ganze\n"
    "Absätze in einzelne Tabellenzeilen gepackt waren (längste gemessene Zeile: 50.558\n"
    "Zeichen bei formal eingehaltener Zeilenvorgabe)."
)

WOHIN_JOURNAL = (
    "Das Journal ist ein PUFFER, kein Archiv: es sammelt kurze Verlaufseinträge seit der\n"
    "letzten Übergabe und wird beim handover ins Rückblick-Dokument verdichtet und dabei\n"
    "GELEERT. Läuft es über, ist nicht die Gliederung falsch, sondern die Übergabe ist\n"
    "überfällig.\n\n"
    "Entweder jetzt die vollständige Übergabe schreiben und das Journal leeren, oder die\n"
    "Einträge kürzen (Richtwert sechs Zeilen je Eintrag: was getan, was verworfen, was\n"
    "offen)."
)


WOHIN_ZELLE = (
    "Je Strom EIN Satz, kein Absatz: Status und was offen ist. Das Detail gehört in die\n"
    "verlinkte lebende Stand-Datei, die ohnehin autoritativ ist.\n\n"
    "Diese Meldung betrifft NUR den Text, den du gerade geschrieben hast, nicht den\n"
    "Altbestand der Datei. Sie ist also jetzt mit einem Satz zu beheben - später kostet\n"
    "dasselbe eine Aufräumaktion, und die trifft dann jemand anderen.\n\n"
    "Was in der Zelle bleibt: der aktuelle Status, ein Satz dazu, das Offene.\n"
    "Was in die Stand-Datei gehört: Begründungen, Messwerte, Verlauf, Einzelbefunde."
)

# Datenzeile einer Markdown-Tabelle, ohne die Trennzeile |---|---|.
TABELLENZEILE_RE = re.compile(r'^\|(?!\s*[-: ]+\|)')


def neuer_text(tool, tool_input):
    """Der Text, den dieser Aufruf schreibt - nicht der Dateiinhalt.

    Damit misst die Zellenprüfung die Änderung statt der Umwelt: wer eine fette Zelle
    schreibt, wird gewarnt; wer eine Datei mit fettem Altbestand an anderer Stelle
    anfasst, nicht. Dasselbe Muster fährt block-typografie.py im selben Verzeichnis.
    """
    teile = []
    if tool == 'Write':
        wert = tool_input.get('content')
        if isinstance(wert, str):
            teile.append(wert)
    elif tool == 'Edit':
        wert = tool_input.get('new_string')
        if isinstance(wert, str):
            teile.append(wert)
    elif tool == 'MultiEdit':
        for eintrag in tool_input.get('edits') or []:
            if isinstance(eintrag, dict):
                wert = eintrag.get('new_string')
                if isinstance(wert, str):
                    teile.append(wert)
    return '\n'.join(teile)


def fette_zellen(text, grenze=COCKPIT_ZELLE):
    """Tabellenzeilen des neuen Textes, die über der Grenze liegen."""
    treffer = []
    for zeile in text.split('\n'):
        z = zeile.rstrip()
        if TABELLENZEILE_RE.match(z) and len(z) > grenze:
            treffer.append(z)
    return treffer


def klassifiziere(basename):
    """Gibt (Art, Zeilen-Schwelle, Byte-Schwelle, Botschaft) zurück oder None."""
    n = basename.lower()
    if n in STAND_NAMES:
        return ('Stand-Datei', STAND_LINES, STAND_BYTES, WOHIN_STAND)
    if n == 'cockpit.md':
        return ('Cockpit', COCKPIT_LINES, COCKPIT_BYTES, WOHIN_COCKPIT)
    if n == 'journal.md':
        return ('Journal', JOURNAL_LINES, JOURNAL_BYTES, WOHIN_JOURNAL)
    return None


def already_warned(state_file, key):
    """True, wenn diese Datei in dieser Session schon gemeldet wurde."""
    if not os.path.isfile(state_file):
        return False
    try:
        with open(state_file, encoding='utf-8-sig') as fh:
            return key in (json.load(fh) or {}).get('warned', [])
    except Exception:
        return False


def mark_warned(state_file, key):
    warned = []
    if os.path.isfile(state_file):
        try:
            with open(state_file, encoding='utf-8-sig') as fh:
                warned = (json.load(fh) or {}).get('warned', [])
        except Exception:
            warned = []
    if key not in warned:
        warned.append(key)
    try:
        with open(state_file, 'w', encoding='utf-8') as fh:
            json.dump({'warned': warned}, fh, ensure_ascii=False)
    except Exception:
        pass


def main():
    try:
        raw = sys.stdin.read()
        if not raw:
            return 0
        data = json.loads(raw)
    except Exception:
        return 0  # Fail-open

    if str(data.get('tool_name') or '') not in ('Write', 'Edit', 'MultiEdit'):
        return 0

    ti = data.get('tool_input') or {}
    file_path = ti.get('file_path')
    if not file_path:
        return 0

    norm = file_path.replace('\\', '/')
    art = klassifiziere(os.path.basename(norm))
    if not art:
        return 0
    label, lim_lines, lim_bytes, wohin = art

    # Eingefrorene Kopien sind per Definition unveränderlich und dürfen beliebig groß
    # sein: Snapshots der Übergabe, Handover-Historie, Archive sowie Arbeitskopien in
    # Worktrees.
    lower = norm.lower()
    if any(seg in lower for seg in ('/sessions/', '/handover/', '/archiv/', '/archive/',
                                    '/.claude/worktrees/')):
        return 0

    if ist_eingefroren(file_path):
        return 0

    session_id_z = str(data.get('session_id') or 'unknown')
    state_dir_z = os.path.join(tempfile.gettempdir(), 'claude-session-state-groesse')

    # Fette Zelle im NEU GESCHRIEBENEN Text. Läuft vor der Größenprüfung und mit eigenem
    # Melde-Schlüssel, damit eine bereits abgesetzte Größenwarnung derselben Datei diese
    # Meldung nicht verschluckt - es sind zwei verschiedene Befunde mit zwei verschiedenen
    # Handlungen.
    grenze = zellengrenze(file_path)
    if label == 'Cockpit' and grenze > 0:
        lang = fette_zellen(neuer_text(str(data.get('tool_name') or ''), ti), grenze)
        if lang:
            try:
                os.makedirs(state_dir_z, exist_ok=True)
                sf = os.path.join(state_dir_z, 'session-' + session_id_z + '.json')
                zellen_key = lower + '#zelle'
                if not already_warned(sf, zellen_key):
                    laengste = max(lang, key=len)
                    msg_z = (
                        "COCKPIT-ZELLE ZU LANG: %s\n"
                        "Gemessen: %d Zelle(n) über %d Zeichen im neu geschriebenen Text, "
                        "längste %d Zeichen.\nAnfang: %s...\n\n%s"
                        % (norm, len(lang), grenze, len(laengste),
                           laengste[:90], WOHIN_ZELLE))
                    print(json.dumps({'hookSpecificOutput': {
                        'hookEventName': 'PostToolUse',
                        'additionalContext': msg_z}}, ensure_ascii=False))
                    mark_warned(sf, zellen_key)
            except Exception:
                pass  # Fail-open: die Größenprüfung unten läuft trotzdem

    try:
        size = os.path.getsize(file_path)
        with open(file_path, 'rb') as fh:
            lines = sum(1 for _ in fh)
    except Exception:
        return 0  # Datei nicht lesbar: nichts melden

    zu_lang = lim_lines is not None and lines > lim_lines
    if not zu_lang and size <= lim_bytes:
        return 0

    session_id = str(data.get('session_id') or 'unknown')
    state_dir = os.path.join(tempfile.gettempdir(), 'claude-session-state-groesse')
    try:
        os.makedirs(state_dir, exist_ok=True)
    except Exception:
        return 0
    state_file = os.path.join(state_dir, 'session-' + session_id + '.json')

    key = lower
    if already_warned(state_file, key):
        return 0

    exceeded = []
    if zu_lang:
        exceeded.append('%d Zeilen (Schwelle %d)' % (lines, lim_lines))
    if size > lim_bytes:
        exceeded.append('%.1f KB (Schwelle %.0f KB)' % (size / 1024.0, lim_bytes / 1024.0))

    msg = ("%s ZU GROSS: %s\nGemessen: %s\n\n%s" %
           (label.upper(), norm, ', '.join(exceeded), wohin))

    print(json.dumps({'hookSpecificOutput': {'hookEventName': 'PostToolUse',
                                             'additionalContext': msg}}, ensure_ascii=False))
    mark_warned(state_file, key)
    return 0


if __name__ == '__main__':
    sys.exit(main())
