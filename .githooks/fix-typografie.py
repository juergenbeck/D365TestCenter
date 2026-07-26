#!/usr/bin/env python3
"""Ersetzt verbotene Typografie (U+2013, U+2014, U+2192) in Markdown regelbasiert.

Die Schreibregel (globale CLAUDE.md, Abschnitt Sprache und Stil) verbietet Halbgeviert-,
Geviertstrich und Pfeil-Sonderzeichen. In Code-Fences und Inline-Code sind sie als
Zitat-Ausnahme erlaubt und bleiben unangetastet.

Maßgeblich ist die Sicht des Hooks: die Fundstellen werden exakt so bestimmt wie in
get_typo_violations aus .githooks/pre-commit.py (Fences zeilenweise, Inline-Spans per
Regex entfernt). Eine eigene Backtick-Paritätsprüfung übersieht Fälle, in denen ein
Inline-Span über mehrere Zeilen läuft.

Klassen und Standard-Ersatz:
  ARROW      Pfeil                                    -> ASCII-Pfeil
  HEADING    Zeile ist Überschrift                    -> Doppelpunkt (Komma bei Zusatz
                                                        oder wenn schon ein Doppelpunkt drin ist)
  LABEL      Strich früh in einer Fett-Label-Zeile    -> Doppelpunkt
  LINESTART  Strich am Zeilenanfang (Fortsetzung)     -> Komma ans Ende der Vorzeile
  LINEEND    Strich am Zeilenende                     -> Komma ans Wortende
  PLAIN      Fließtext                                -> Komma

Aufruf:
  python scripts/fix-typografie.py --report <pfad-oder-glob> ...
  python scripts/fix-typografie.py --apply  <pfad-oder-glob> ...

--report listet die Sonderfälle (alles außer PLAIN und ARROW) im Kontext; die zu prüfende
Restmenge nach dem Lauf ist damit klein. Nach --apply immer gegenlesen und dort nachbessern,
wo ein Komma Subjekt und Prädikat trennen würde oder zwei Doppelpunkte in einem Satz stehen.
"""
import argparse
import glob
import os
import re
import sys

try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

INLINE = re.compile(r'`[^`]*`')
DASH = '–—'          # Halbgeviert, Geviert
# Pfeil-Sonderzeichen und ihr ASCII-Ersatz. Die letzten vier stehen nicht in der
# LMApp-Regel, aber in Markants typo_check_lib; sie hier mitzuführen kostet nichts
# und macht das Werkzeug für die ganze Hook-Familie brauchbar.
ARROWS = {'→': '->', '←': '<-', '↔': '<->', '⇒': '=>', '⇔': '<=>'}
FORBIDDEN = DASH + ''.join(ARROWS)

# Nach dem Strich folgt ein Zusatz, keine neue Aussage: Komma statt Doppelpunkt.
QUALIFIER = re.compile(
    r'^\**(verifiziert|empirisch|beobachtet|gemessen|belegt|Befund|Stand|inklusive|'
    r'korrigiert|geprüft|Nachtrag|Quelle|seit|ab|bis)\b', re.IGNORECASE)


def hook_hits(line):
    """Positionen, die der Hook sieht (Treffer in Inline-Code-Spans zählen nicht)."""
    spans = [m.span() for m in INLINE.finditer(line)]
    return [m.start() for m in re.finditer('[%s]' % FORBIDDEN, line)
            if not any(a <= m.start() < b for a, b in spans)]


def classify(line, pos, stripped):
    ch = line[pos]
    if ch in ARROWS:
        return 'ARROW'
    # Platzhalter in einer Tabellenzelle (| — |): meint "nichts", nicht "und zwar".
    if line[:pos].rstrip().endswith('|') and line[pos + 1:].lstrip().startswith('|'):
        return 'CELL'
    # Bereichsstrich ohne Leerzeichen (10–20, 2015–2020): ASCII-Bindestrich.
    if pos > 0 and line[pos - 1] != ' ' and pos + 1 < len(line) and line[pos + 1] != ' ':
        return 'RANGE'
    rest = line[pos + 1:]
    if stripped.startswith('#'):
        return 'HEADING'
    if not rest.strip():
        return 'LINEEND'
    if stripped.startswith(line[pos]):
        return 'LINESTART'
    # Label heißt: die Zeile (bzw. die Tabellenzelle) BEGINNT mit einem Fett-Block.
    # Ein fettes Wort irgendwo mitten im Satz macht daraus noch kein Label.
    head = label_head(line, pos)
    if head.startswith('**') and len(head) <= 60:
        return 'LABEL'
    return 'PLAIN'


def label_head(line, pos):
    """Text vor dem Strich, auf die Tabellenzelle beschnitten und ohne
    Listenmarker, Blockquote-Zeichen und Emoji am Anfang."""
    head = line[:pos]
    if '|' in head:
        head = head[head.rindex('|') + 1:]
    # Listenmarker nur strippen, wenn ein Leerzeichen folgt: sonst fielen die
    # Fett-Sternchen selbst weg und kein Label würde je erkannt.
    return re.sub(r'^(?:\s|>|[-+](?=\s)|\*(?=\s)|\d+\.(?=\s)|[^\w\s*])+', '', head).strip()


def token_for(kind, line, pos):
    # Endet der Text vor dem Strich auf ? oder !, ist der Satz bereits geschlossen:
    # dort gehört gar kein Satzzeichen hin, nur ein Leerzeichen.
    if line[:pos].rstrip().rstrip('*').endswith(('?', '!')):
        return ''
    # Innerhalb einer offenen Klammer wirkt ein Doppelpunkt wie ein Bruch: Komma.
    if kind in ('HEADING', 'LABEL') and line[:pos].count('(') > line[:pos].count(')'):
        return ','
    if kind == 'HEADING':
        rest = line[pos + 1:].lstrip()
        if QUALIFIER.match(rest):
            return ','
        return ',' if ':' in line[:pos] else ':'
    if kind == 'LABEL':
        # Steht im Label schon ein Doppelpunkt, wäre ein zweiter unlesbar.
        return ',' if ':' in label_head(line, pos) else ':'
    return ','


def process(path, apply_changes):
    raw = open(path, 'rb').read().decode('utf-8')
    # Zeilenenden je Zeile bewahren. Ein pauschales split/join würde bei gemischten
    # Enden (kommt vor) die ganze Datei umschreiben und die Zeilenzählung verschieben.
    lines, ends = [], []
    for part in raw.splitlines(keepends=True):
        m = re.search(r'(\r\n|\n|\r)$', part)
        lines.append(part[:m.start()] if m else part)
        ends.append(m.group(1) if m else '')
    fence = False
    findings = []
    for i in range(len(lines)):
        s = lines[i].lstrip()
        if s.startswith('```') or s.startswith('~~~'):
            fence = not fence
            continue
        if fence:
            continue
        for pos in hook_hits(lines[i]):
            findings.append((i, pos, classify(lines[i], pos, s)))

    if apply_changes:
        # Tokens von links nach rechts bestimmen: stehen mehrere Striche in einer Zeile, darf nur
        # der erste einen Doppelpunkt bekommen, sonst entsteht eine Doppelpunkt-Kette.
        tokens = {}
        doppelpunkt_gesetzt = set()
        for i, pos, kind in findings:
            if kind in ('ARROW', 'CELL', 'RANGE', 'LINESTART'):
                continue
            tok = token_for(kind, lines[i], pos)
            if tok == ':' and i in doppelpunkt_gesetzt:
                tok = ','
            if tok == ':':
                doppelpunkt_gesetzt.add(i)
            tokens[(i, pos)] = tok
        # Rückwärts anwenden, damit die früheren Positionen gültig bleiben.
        for i, pos, kind in reversed(findings):
            line = lines[i]
            if kind == 'ARROW':
                lines[i] = line[:pos] + ARROWS[line[pos]] + line[pos + 1:]
                continue
            if kind in ('CELL', 'RANGE'):
                lines[i] = line[:pos] + '-' + line[pos + 1:]
                continue
            if kind == 'LINESTART':
                indent = line[:len(line) - len(line.lstrip())]
                lines[i] = indent + line.lstrip()[1:].lstrip()
                prev = lines[i - 1].rstrip()
                if prev and prev[-1] not in ',:;.!?':
                    lines[i - 1] = prev + ','
                continue
            tok = tokens[(i, pos)]
            a = pos
            while a > 0 and line[a - 1] == ' ':
                a -= 1
            b = pos + 1
            while b < len(line) and line[b] == ' ':
                b += 1
            rest = line[b:]
            lines[i] = line[:a] + tok + (' ' if rest.strip() else '') + rest
        out = ''.join(l + e for l, e in zip(lines, ends))
        open(path, 'wb').write(out.encode('utf-8'))

    return findings, lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--apply', action='store_true')
    ap.add_argument('--report', action='store_true')
    ap.add_argument('--exclude', action='append', default=[],
                    help='Regex auf den Pfad (Vorwärts-Slashes); mehrfach angebbar. '
                         'Für Dateien, die das Zielrepo bewusst ausnimmt (generierte '
                         'Bundles, Lehrmaterial, Spiegel).')
    ap.add_argument('pfade', nargs='+')
    args = ap.parse_args()

    files = []
    for p in args.pfade:
        files.extend(sorted(glob.glob(p, recursive=True)))
    files = [f for f in files if f.lower().endswith('.md')]
    if args.exclude:
        rx = [re.compile(p) for p in args.exclude]
        vorher = len(files)
        files = [f for f in files if not any(r.search(f.replace(os.sep, '/')) for r in rx)]
        print('Ausgeschlossen: %d Dateien' % (vorher - len(files)))

    gesamt = {}
    for f in files:
        findings, lines = process(f, args.apply)
        if not findings:
            continue
        for _, _, kind in findings:
            gesamt[kind] = gesamt.get(kind, 0) + 1
        if args.report:
            sonder = [x for x in findings if x[2] not in ('PLAIN', 'ARROW')]
            if sonder:
                print('== %s' % f.replace(os.sep, '/'))
                for i, pos, kind in sonder:
                    ctx = lines[i]
                    a = max(0, pos - 70)
                    b = min(len(ctx), pos + 70)
                    print('  %-9s Z%-5d %s%s<<%s>>%s%s' % (
                        kind, i + 1, '...' if a else '', ctx[a:pos],
                        ctx[pos], ctx[pos + 1:b], '...' if b < len(ctx) else ''))
                    if kind == 'LINESTART' and i > 0:
                        print('            Vorzeile: %s' % lines[i - 1][-90:])

    print('\nDateien: %d | %s | Summe: %d' % (
        len(files), ', '.join('%s %d' % (k, v) for k, v in sorted(gesamt.items())),
        sum(gesamt.values())))


if __name__ == '__main__':
    main()
