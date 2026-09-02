#!/usr/bin/env python3
"""Pre-Commit-Hook (Python, plattformneutral, config-getrieben).

AUTO-GENERATED aus ~/.claude/hook-templates/python/pre-commit.py
(ausgerollt von ~/.claude/scripts/Sync-UmlautTriggers.ps1). Nicht von Hand editieren.

Prüft staged Dateien auf Umlaut-Verstöße (ASCII-Ersatz ae/oe/ue/ss statt ä/ö/ü/ß)
im Datei-Inhalt, via gemeinsamer Lib .githooks/umlaut_check_lib.py.

PRÜFEBENE (ADR-028, seit 2026-07-27, NICHT ohne neues ADR umdrehen): geprüft wird,
was der Commit HINZUFÜGT, nicht der Dateibestand. Der Scan läuft weiterhin über den
ganzen Dateiinhalt (nur so stimmen Code-Fences und mehrzeilige Inline-Spans),
gemeldet wird aber nur, was in einer hinzugefügten Zeile steht (parse_added_lines).
Grund: sonst verlangt der Hook Änderungen an eingefrorener Historie, sobald jemand
eine alte Datei aus anderem Grund anfasst, und die Pfad-Ausnahmeliste muss jede
Ordner-Umbenennung nachziehen (bei handover/ -> sessions/ ist genau das misslungen).
Neue Dateien bestehen nur aus hinzugefügten Zeilen und werden weiterhin vollständig
geprüft; Altbestand bereinigt man bewusst mit .githooks/fix-typografie.py.

Zusätzlich (seit 2026-07-25): staged .md-Dateien werden auf verbotene Typografie
geprüft (Halbgeviertstrich U+2013, Geviertstrich U+2014 sowie die fünf Pfeile
U+2192, U+2190, U+2194, U+21D2, U+21D4; die vier zusätzlichen Pfeile seit
2026-07-27, vorher kannte die zentrale Kette nur U+2192). Ersatz:
Komma, Punkt, Doppelpunkt bzw. ASCII-Pfeil ->. Gilt in beiden file_scope-Profilen
für die Prosa-Endungen in TYPO_EXT (.md, .html, .htm, .txt), NICHT für Code (der
kann legitime Unicode-Zeichen aus Fremdquellen enthalten), nutzt dieselben
Pfad-Ausschlüsse wie der Umlaut-Check; Code-Fences und Inline-Code sind
ausgenommen (wörtliche Fremd-Zitate), in HTML zusätzlich <code>/<pre>. Block/Warn
folgt derselben per-Datei-Entscheidung (is_blocking_file) wie der Umlaut-Check;
mit der üblichen Repo-Config (enforcement warn, block_extensions [".md"]) warnen
.html/.htm/.txt also, während .md blockt. Wer sie ebenfalls blockend führen will,
trägt die Endung in block_extensions ein.

.html/.htm/.txt seit 2026-08-08 (Anlass in LMApp): Ein DevOps-Ticket-Kommentar lag
als .html im Repo, enthielt sechs Geviertstriche und passierte die gesamte Kette
ungehindert, weil sie nur .md kannte. Ausgerechnet die Texte, die nach außen gehen,
liegen selten als .md vor.

Das Verhalten kommt aus der optionalen .githooks/umlaut-allowlist.json:
  file_scope  : "md_only" (Default) prüft nur .md; "all_text" prüft alle Textdateien.
  enforcement : "block" (Default) -> Exit 1 bei Treffer; "warn" -> nur melden, Exit 0.
                Gilt global; pro Endung überstimmbar via block_extensions/warn_extensions.
  block_extensions[] : Endungen (z.B. ".md"), die IMMER blocken, auch bei enforcement=warn.
  warn_extensions[]  : Endungen (z.B. ".cs"), die IMMER nur warnen, auch bei enforcement=block.
                Präzedenz pro Datei: block_extensions > warn_extensions > globales enforcement.
                So lässt sich "Doku blockt, Code warnt" abbilden.
  generated[] : Regex-Liste (repo-relativer Pfad, Vorwärts-Slashes), strukturelle Ausschlüsse.
  exceptions[]: [{path}] exakte oder glob-Einzeldatei-Ausnahmen.
  fence_scope : was INNERHALB eines Code-Fences in .md geprüft wird (ADR-2026-08-08-0117):
                "off" | "comments" | "comments+literals" (Default "comments+literals").
  fence_enforcement : "warn" (Default) -> Fence-Treffer melden, Exit 0; "block" -> Exit 1.
                Bewusst getrennt vom globalen enforcement, damit .md weiter blockend
                geführt werden kann, während die neue Fence-Prüfung erst nur warnt.
Fehlt die Config: md_only + block + eingebaute Default-Ausschlüsse (COMPANION_RE,
DEFAULT_EXCLUDE_RE) - verhält sich exakt wie die bisherige py-Repo-Version.

Projekt-spezifische Zusatz-Prüfungen (z.B. Markants Z4-Drift-/Header-/Scope-Checks)
liegen NICHT hier, sondern im projekt-lokalen .githooks/pre-commit-local.py, das der
Wrapper nach diesem Hook aufruft (nur, wenn dieser Exit 0 lieferte).

Nicht-UTF-8-Bytes (seit 2026-09-03, ADR-2026-09-03-001352): git-Ausgaben werden mit
errors='replace' dekodiert, sonst stirbt der Hook an einem Konsolenprotokoll in der
Codepage der Konsole (stdout bleibt None, AttributeError, Exit 1 ohne einen einzigen
Fund im Report). Weil die Ersetzung die Prüfung dieser Datei unzuverlässig macht, wird
jede betroffene Datei per melde_unvollstaendig() genannt; der Exitcode ändert sich
dadurch nicht.

Bei Verstoß (block): Report auf stderr, Exit 1. Sauber/warn: Exit 0.
Bypass im Notfall: git commit --no-verify (dokumentieren, warum).
"""
import fnmatch
import json
import os
import re
import subprocess
import sys
from itertools import groupby

try:
    sys.stderr.reconfigure(encoding='utf-8')
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from umlaut_check_lib import get_umlaut_violations

# Anhang-Begleittexte (<name>.<ext>.md) sind 1:1 aus Originaldokumenten extrahierte
# Fremdtexte (Volltextsuche-Hilfe). Immer ausgenommen (Originaltreue, ASCII im Original
# darf nicht verändert werden). Gilt in beiden file_scope-Profilen.
COMPANION_RE = re.compile(
    r'\.(pdf|docx?|xlsx?|pptx?|vcf|txt|csv|ics|jpe?g|png|gif|odt|ods)\.md$', re.I)

# Default-Ausschlüsse für das md_only-Profil: gespiegelte Skill-Bibliothek, Dumps
# (Jira, Backlog, Planung), generierte Outputs und Scans, 1:1-Kopien und Fremdtext.
# NUR im md_only-Profil aktiv. Im all_text-Profil definiert das Repo seine Ausschlüsse
# vollständig über generated[] - sonst würde z.B. .claude/skills/ fälschlich ausgenommen,
# obwohl ein Repo (Markant) seine Skills bewusst prüft.
#
# Seit 2026-07-27 (ADR-028) NICHT mehr ausgenommen: eigene Prosa in handover/, changelog/,
# reviews/, research/, recherche/, poc/, _archive/, 99_archiv/ sowie alte-notizen,
# lessons-learned und skeptiker-review. Die Ausnahme gab es, weil der Hook den ganzen
# Dateibestand prüfte und eingefrorene Historie sonst zur Änderung gezwungen hätte; seit
# der diff-basierten Prüfung ist dieser Grund weg. Was bleibt, führt den Verstoß als
# DATEN (Fremdtext, Dump, generierte Kopie) und darf inhaltlich nicht angetastet werden.
DEFAULT_EXCLUDE_RE = re.compile(
    r'(^|/)(\.github|\.claude)/skills/'
    r'|(^|/)(backlog|planung|jira|output|scans|99_confluence-export)/'
    r'|(^|/)Wissen/temp/'
    r'|(^|/)memory-snapshot[^/]*/'
    r'|(feedback|bug-report)', re.I)

# Binär-Endungen (nur all_text-Profil relevant): zeilenweiser Textcheck wäre sinnlos.
BINARY_EXT = {
    '.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.ico', '.svg', '.pdf',
    '.zip', '.7z', '.gz', '.tgz', '.tar', '.rar', '.docx', '.doc', '.xlsx', '.xls',
    '.pptx', '.vsdx', '.msg', '.eml', '.mp3', '.mp4', '.m4a', '.wav', '.mov', '.avi',
    '.mkv', '.woff', '.woff2', '.ttf', '.otf', '.eot', '.exe', '.dll', '.pdb', '.so',
    '.dylib', '.bin', '.dat', '.class', '.jar', '.pyc', '.o', '.a', '.lib', '.nupkg', '.snk',
}


# Verbotene Typografie in Prosa-Dateien. Wörtliche Fremd-Zitate mit diesen Zeichen
# gehören in Code-Fences oder Inline-Code, in HTML in <code>/<pre> (hier ausgenommen).
TYPO_FORBIDDEN = {'–': 'Halbgeviertstrich', '—': 'Geviertstrich',
                  '→': 'Pfeil rechts', '←': 'Pfeil links',
                  '↔': 'Doppelpfeil', '⇒': 'Doppelpfeil rechts',
                  '⇔': 'Doppelpfeil beidseitig'}
_TYPO_INLINE_RE = re.compile(r'`[^`]*`')
_HTML_INLINE_RE = re.compile(r'<(code|pre)\b[^>]*>.*?</\1>', re.DOTALL | re.I)
_HTML_OPEN_RE = re.compile(r'<(code|pre)\b', re.I)
_HTML_CLOSE_RE = re.compile(r'</(code|pre)>', re.I)

# Endungen, die der Typografie-Check prüft. .md und .html/.htm sind Prosa, die nach
# außen geht; .txt seit 2026-08-08 dabei, dort aber bewusst NUR hier und nicht in der
# PreToolUse-Schreibsperre (Fremdinhalt wie Logs und Exporte hat in .txt keine
# Zitat-Ausnahme, ein hartes deny wäre dort ein Fehlalarm ohne Ausweg). Über
# block_extensions/warn_extensions der umlaut-allowlist.json je Repo steuerbar.
TYPO_EXT = ('.md', '.html', '.htm', '.txt')


def get_typo_violations(lines, is_html=False):
    """(zeilennr, labels, zeilentext) für Typografie-Treffer außerhalb von
    Code-Fences und Inline-Code; bei is_html zusätzlich ohne <code>/<pre>.
    Zeilenweise (statt über den ganzen Text), weil die Zeilennummer für die
    added-lines-Filterung nach ADR-028 gebraucht wird."""
    hits = []
    in_fence = False
    in_html_quote = False
    for n, line in enumerate(lines, start=1):
        stripped = line.lstrip()
        if stripped.startswith('```') or stripped.startswith('~~~'):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        clean = _TYPO_INLINE_RE.sub('', line)
        if is_html:
            clean = _HTML_INLINE_RE.sub('', clean)  # <code>x</code> in einer Zeile
            if in_html_quote:
                # Im mehrzeiligen Block: erst ab dem schließenden Tag wieder prüfen.
                if _HTML_CLOSE_RE.search(clean):
                    in_html_quote = False
                    clean = _HTML_CLOSE_RE.split(clean)[-1]
                else:
                    continue
            if _HTML_OPEN_RE.search(clean):
                # Block beginnt hier und endet nicht in dieser Zeile (Inline-Fall ist
                # oben schon entfernt): ab dem öffnenden Tag nicht mehr prüfen.
                in_html_quote = True
                clean = _HTML_OPEN_RE.split(clean)[0]
        labels = sorted({lab for ch, lab in TYPO_FORBIDDEN.items() if ch in clean})
        if labels:
            hits.append((n, labels, line))
    return hits


def git(*args):
    # errors='replace' ist hier Betriebssicherheit, nicht Kosmetik (ADR-2026-09-03-001352):
    # Enthält eine staged Datei Bytes, die kein UTF-8 sind (etwa ein Konsolenprotokoll, das eine
    # PowerShell-Umleitung in der Codepage der Konsole geschrieben hat), scheitert das Dekodieren
    # sonst im Lesethread von subprocess. Die Ausnahme erreicht das Hauptprogramm NICHT, stdout
    # bleibt None, und der Hook stirbt in parse_added_lines mit einem AttributeError - also mit
    # Exit 1, der Signatur eines echten Befunds, obwohl er nichts geprüft hat. Ein Prüfwerkzeug,
    # das an seinem Prüfgegenstand abstürzt, prüft nicht, es blockiert nur.
    return subprocess.run(['git', *args], capture_output=True, text=True,
                          encoding='utf-8', errors='replace').stdout


def melde_unvollstaendig(rel, grund):
    """Nennt eine Datei, deren Prüfung an Nicht-UTF-8-Bytes scheitert (ADR-2026-09-03-001352).

    errors='replace' in git() verhindert den Absturz, macht die Prüfung dieser einen Datei aber
    unzuverlässig: Nicht dekodierbare Bytes werden zu U+FFFD, und ein darin versteckter
    Halbgeviertstrich oder Umlaut-Verstoß ist danach nicht mehr auffindbar. Eine stillschweigend
    verfälscht geprüfte Datei ist schlimmer als eine ungeprüfte, weil man sich auf sie beruft.
    Der Hinweis ändert den Exitcode bewusst NICHT: er meldet die Reichweite der Prüfung, keinen
    Befund am Prüfgegenstand."""
    sys.stderr.write(
        '\n HINWEIS: %s enthält Bytes, die kein UTF-8 sind (%s).\n'
        '  Die Prüfung dieser Datei ist deshalb unvollständig.\n'
        '  Bei einem Konsolenprotokoll beim Erzeugen [Console]::OutputEncoding auf UTF-8\n'
        '  setzen und die Datei neu schreiben.\n\n' % (rel, grund))


# Diff-Hunk-Kopf: @@ -alt,n +neu,m @@ - die Gruppe ist die erste Zeilennummer im NEUEN Stand.
_RE_HUNK = re.compile(r'^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@')


def parse_added_lines(diff_text):
    """Menge der Zeilennummern (1-basiert, neuer Dateizustand), die der Commit
    hinzufügt. Grundlage der diff-basierten Prüfung nach ADR-028. Robust gegen die
    +++/---Header. Leere Menge (etwa reine Umbenennung ohne Inhaltsänderung) heißt:
    dieser Commit fügt nichts hinzu, also gibt es nichts zu verantworten."""
    added = set()
    new_ln = 0
    for line in diff_text.split('\n'):
        if line.startswith('@@'):
            m = _RE_HUNK.match(line)
            if m:
                new_ln = int(m.group(1))
            continue
        if line.startswith('+++') or line.startswith('---'):
            continue
        if line.startswith('+'):
            added.add(new_ln)
            new_ln += 1
        elif line.startswith('-'):
            continue
        else:
            new_ln += 1
    return added


def _norm_ext(ext):
    """Normalisiert eine Endung auf lowercase mit führendem Punkt ('.cs')."""
    ext = str(ext).strip().lower()
    if ext and not ext.startswith('.'):
        ext = '.' + ext
    return ext


def is_blocking_file(rel, cfg):
    """Pro-Datei-Entscheidung block vs. warn. Präzedenz: block_extensions >
    warn_extensions > globales enforcement. Erlaubt 'Doku blockt, Code warnt'."""
    ext = os.path.splitext(rel)[1].lower()
    if ext in cfg['block_extensions']:
        return True
    if ext in cfg['warn_extensions']:
        return False
    return cfg['enforcement'] != 'warn'


def load_config(githooks_dir):
    """Liest .githooks/umlaut-allowlist.json. Defaults (fehlt/kaputt): md_only,
    block, keine Zusatz-Ausschlüsse - verhält sich wie die bisherige py-Repo-Version."""
    cfg = {'file_scope': 'md_only', 'enforcement': 'block',
           'generated': [], 'exceptions': [],
           'block_extensions': set(), 'warn_extensions': set(),
           'fence_scope': 'comments+literals', 'fence_enforcement': 'warn'}
    path = os.path.join(githooks_dir, 'umlaut-allowlist.json')
    if not os.path.isfile(path):
        return cfg
    try:
        with open(path, encoding='utf-8') as fh:
            data = json.load(fh)
    except Exception as e:
        sys.stderr.write('Pre-Commit-Hook: umlaut-allowlist.json nicht lesbar '
                         '(%s) - nutze Defaults.\n' % e)
        return cfg
    if data.get('file_scope'):
        cfg['file_scope'] = str(data['file_scope'])
    if data.get('enforcement'):
        cfg['enforcement'] = str(data['enforcement'])
    if data.get('generated'):
        cfg['generated'] = [re.compile(p) for p in data['generated']]
    if data.get('exceptions'):
        cfg['exceptions'] = [e['path'] for e in data['exceptions']
                             if isinstance(e, dict) and e.get('path')]
    if data.get('block_extensions'):
        cfg['block_extensions'] = {_norm_ext(e) for e in data['block_extensions']}
    if data.get('warn_extensions'):
        cfg['warn_extensions'] = {_norm_ext(e) for e in data['warn_extensions']}
    if data.get('fence_scope'):
        cfg['fence_scope'] = str(data['fence_scope'])
    if data.get('fence_enforcement'):
        cfg['fence_enforcement'] = str(data['fence_enforcement'])
    return cfg


def is_excluded(rel, scope, generated, exceptions):
    """True, wenn die Datei vom Umlaut-Check ausgenommen ist."""
    if COMPANION_RE.search(rel):
        return True
    if scope == 'md_only' and DEFAULT_EXCLUDE_RE.search(rel):
        return True
    for rx in generated:
        if rx.search(rel):
            return True
    for p in exceptions:
        if rel == p or fnmatch.fnmatch(rel, p):
            return True
    return False


def staged_files(scope):
    """Staged Added/Modified/Copied, nach file_scope gefiltert."""
    out = git('diff', '--cached', '--name-only', '--diff-filter=ACM').splitlines()
    if scope == 'md_only':
        # .md für den Umlaut- UND Typografie-Check, die übrigen TYPO_EXT allein für
        # den Typografie-Check (siehe main): sonst liefe die Regel im md_only-Profil
        # an genau den Kommunikationstexten vorbei, für die sie 2026-08-08 erweitert
        # wurde.
        return [f for f in out if f.lower().endswith(TYPO_EXT)]
    # all_text: alle nicht-binären Dateien (NUL-Byte-Absicherung beim Lesen).
    return [f for f in out if os.path.splitext(f)[1].lower() not in BINARY_EXT]


def main():
    cfg = load_config(HERE)
    scope = cfg['file_scope']
    files = [f for f in staged_files(scope)
             if not is_excluded(f, scope, cfg['generated'], cfg['exceptions'])]
    if not files:
        return 0
    repo_root = git('rev-parse', '--show-toplevel').strip()

    violations = []
    typo_violations = []
    for rel in files:
        full = os.path.join(repo_root, rel)
        if not os.path.isfile(full):
            continue
        try:
            # utf-8-sig: ein führendes UTF-8-BOM entfernen, exakt wie PowerShells
            # Get-Content -Encoding UTF8. Ohne Strip begänne Zeile 1 mit dem
            # BOM-Zeichen und H1-/Fence-Erkennung schlüge fehl; ohne BOM wie utf-8.
            with open(full, encoding='utf-8-sig') as fh:
                content = fh.read()
        except UnicodeDecodeError:
            # Bisher stilles continue. Die Datei bleibt ungeprüft, das ist richtig (raten, welche
            # Codepage gemeint war, erzeugt Fundstellen, die im Original nicht stehen) - aber es
            # wird gesagt, statt sie als geprüft durchgehen zu lassen.
            melde_unvollstaendig(rel, 'Dateiinhalt nicht als UTF-8 lesbar')
            continue
        except OSError:
            continue
        if '\x00' in content:  # Binär-Absicherung (all_text)
            continue
        lines = content.split('\n')
        # Fence-Prüfung nur für .md: nur dort sind Code-Fences ein Konstrukt,
        # Quelldateien prüft die Kette ohnehin direkt (ADR-2026-08-08-0117).
        low = rel.lower()
        is_md = low.endswith('.md')
        is_html = low.endswith(('.html', '.htm'))
        # Umlaut-Check unverändert: im md_only-Profil nur .md, sonst alle Textdateien.
        # Die zusätzlich eingesammelten TYPO_EXT-Dateien (.html/.htm/.txt) sind dort
        # NUR für den Typografie-Check da und bleiben vom Umlaut-Check unberührt.
        if scope == 'md_only' and not is_md:
            uml = []
        else:
            uml = get_umlaut_violations(lines, cfg['fence_scope'] if is_md else None)
        # Typografie: Prosa-Endungen (auch im all_text-Profil), gleiche Ausschlüsse.
        typ = get_typo_violations(lines, is_html=is_html) if low.endswith(TYPO_EXT) else []
        if not uml and not typ:
            continue
        # ADR-028: verantwortet wird nur, was dieser Commit hinzufügt. Der Scan oben
        # braucht die GANZE Datei (Code-Fences und mehrzeilige Inline-Spans lassen sich
        # aus einem Diff-Ausschnitt nicht korrekt erkennen), gemeldet wird danach nur,
        # was in einer hinzugefügten Zeile steht. Eine neue Datei besteht ausschließlich
        # aus hinzugefügten Zeilen und wird damit weiterhin vollständig geprüft.
        # Der git-Aufruf steht bewusst hinter dem Treffer-Check: ohne Treffer kostet er nichts.
        diff_text = git('diff', '--cached', '-U0', '--', rel)
        if '\ufffd' in diff_text:  # das Ersatzzeichen aus errors='replace'
            # Der Arbeitsbaum-Stand war als UTF-8 lesbar, der Diff ist es nicht (typisch: die alte
            # Fassung in HEAD oder im Index stammt aus einer Konsolen-Umleitung). Ohne diesen
            # Hinweis tauscht errors='replace' nur einen lauten Absturz gegen ein leises
            # Vorbeisehen: die Zuordnung Fundstelle -> hinzugefügte Zeile kann danebengreifen.
            melde_unvollstaendig(rel, 'Diff nicht vollständig dekodierbar')
        added = parse_added_lines(diff_text)
        for h in uml:
            if h['line'] in added:
                violations.append((rel, h))
        for t in typ:
            if t[0] in added:
                typo_violations.append((rel, t))

    if not violations and not typo_violations:
        return 0

    # Pro Datei block vs. warn entscheiden (block_extensions/warn_extensions/global).
    # Reihenfolge bleibt erhalten, gleiche rel bleiben konsekutiv -> groupby trägt.
    # Fence-Fundstellen laufen in einem eigenen Kanal (fence_enforcement), damit
    # .md weiter blockend geführt werden kann, während die neue Prüfung erst warnt.
    block_viol, warn_viol, fence_viol = [], [], []
    for rel, h in violations:
        if h.get('scope', 'prosa') != 'prosa':
            fence_viol.append((rel, h))
        else:
            (block_viol if is_blocking_file(rel, cfg) else warn_viol).append((rel, h))

    w = sys.stderr.write

    def report(group_viol, title):
        w('\n=================================================================\n')
        w(' %s\n' % title)
        w('=================================================================\n\n')
        for rel, group in groupby(group_viol, key=lambda x: x[0]):
            w('  %s\n' % rel)
            for _, h in group:
                scope = h.get('scope', 'prosa')
                note = ', alleinstehend' if h['block'] == 2 else ''
                if scope == 'fence-kommentar':
                    note += ', Kommentar im Code-Fence'
                elif scope == 'fence-literal':
                    note += ', Text im Code-Fence'
                w("    Zeile %4d [Umlaut]: '%s'%s -> ASCII-Ersatz statt echtem "
                  "ä/ö/ü/ß. Siehe Skill umlaute.\n" % (h['line'], h['match'], note))
                text = h['text']
                snippet = text[:117] + '...' if len(text) > 120 else text
                w('      > %s\n' % snippet)

    # Typografie-Treffer: gleiche block/warn-Entscheidung pro Datei wie Umlaute.
    typo_block, typo_warn = [], []
    for rel, t in typo_violations:
        (typo_block if is_blocking_file(rel, cfg) else typo_warn).append((rel, t))

    def report_typo(group_viol, title):
        w('\n=================================================================\n')
        w(' %s\n' % title)
        w('=================================================================\n\n')
        for rel, group in groupby(group_viol, key=lambda x: x[0]):
            w('  %s\n' % rel)
            for _, (n, labels, text) in group:
                w('    Zeile %4d [Typografie]: %s -> stattdessen Komma, Punkt, '
                  'Doppelpunkt bzw. ASCII-Pfeil ->.\n' % (n, ', '.join(labels)))
                snippet = text.strip()
                snippet = snippet[:117] + '...' if len(snippet) > 120 else snippet
                w('      > %s\n' % snippet)

    fence_blockt = cfg['fence_enforcement'] == 'block'

    if block_viol:
        report(block_viol, 'Pre-Commit-Hook: Umlaut-Verstöße erkannt (Commit blockiert)')
    if warn_viol:
        report(warn_viol, 'Pre-Commit-Hook: Umlaut-Verstöße (WARNUNG, blockt NICHT)')
    if fence_viol:
        report(fence_viol, 'Pre-Commit-Hook: Umlaut-Verstöße in Code-Fences (Commit blockiert)'
               if fence_blockt else
               'Pre-Commit-Hook: Umlaut-Verstöße in Code-Fences (WARNUNG, blockt NICHT)')
        w('    Deutsche Kommentare und Meldungstexte im Beispielcode sind Prosa und\n'
          '    umlautpflichtig; Bezeichner, Pfade und Formatangaben bleiben ungeprüft.\n')
    if typo_block:
        report_typo(typo_block, 'Pre-Commit-Hook: verbotene Typografie in .md (Commit blockiert)')
    if typo_warn:
        report_typo(typo_warn, 'Pre-Commit-Hook: verbotene Typografie in .md (WARNUNG, blockt NICHT)')
    w('\n Bypass im Notfall: git commit --no-verify (DOKUMENTIEREN, warum)\n\n')
    return 1 if (block_viol or typo_block or (fence_viol and fence_blockt)) else 0


if __name__ == '__main__':
    sys.exit(main())
