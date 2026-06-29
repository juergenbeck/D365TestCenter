#!/usr/bin/env python3
"""Gemeinsame Umlaut-Prüf-Logik für die Datei-Inhalt-Hooks (Python-Port).

Diese Datei ist zentral gepflegt: Sync-UmlautTriggers.ps1 rollt den kompletten
Code-Rahmen aus ~/.claude/hook-templates/python/umlaut_check_lib.py in jedes
kit=python-Repo aus und rendert die Datenregion zwischen den AUTO-GENERATED-
Markern aus ~/.claude/umlaute-triggers.json. Nicht von Hand editieren, sondern
am Template ändern und neu syncen.

Plattformneutral (macOS / Windows / Linux). Genutzt von:
  - .githooks/pre-commit.py          (Commit-Block, blockierend)
  - .claude/hooks/check-umlaute.py   (Schreib-Warnung, nicht blockierend)

Verhaltensgleich zur PowerShell-Lib umlaut-check-lib.ps1 in den Windows-Repos
(Markant): gleiche Stamm-Liste (fc=true-Teilmenge der zentralen Trigger-JSON),
gleiche Filter (Code-Fences, Inline-Code, Autolinks, URLs, Link-Targets, technische
Bezeichner, Slug-/Domain-/Dateinamen-Tokens).
"""
import re

# >>> AUTO-GENERATED:UMLAUT-DATA. Quelle: ~/.claude/umlaute-triggers.json. Nicht von Hand editieren.
UMLAUT_BLOCK1 = r'\b\w*(abhaengig|aehn|aelter|aender|aendert|aenderung|aerger|aergerli|aerztl|aeusser|ausfueh|ausgefuehrt|beduerf|begruend|behoerde|bestaet|bruecke|buerg|durchfueh|einfueh|enthaelt|erlaeuter|erloes|erwaeg|faehig|faehr|faell|faerb|fluess|frueh|frueher|fueg|fuehl|fuehr|fuellen|fuellt|fuellung|fuer|gaeng|gebaeud|gebuehr|gefaehr|gefaess|gehoer|gelaend|geloescht|gemaess|genueg|gewaehr|glaeub|groesse|gruen|gruend|gueltig|haelt|haendl|haeng|haengt|haetten|haeufig|hoechste|hoeh|hoehe|hoehere|hoer|itaet|jaehrli|klaer|knuepf|koennen|kraeft|kuendig|kuenft|kuerz|laenge|laeuf|laeuft|loes|loesch|loeschen|loest|loesung|luecke|maerkt|moegli|muessen|naechst|naehe|naemli|noetig|nuetz|praemi|praesent|praezis|primaer|pruef|qualitaet|regulaer|rueck|ruehr|ruestung|saemtli|saetz|schaed|schaeft|schlaeg|schluess|schluessel|schoen|schoepf|schraenk|schuetz|schwaech|spaet|spaeter|staend|staerk|stoer|stuetz|taegli|taetig|tatsaechli|traeg|tragfaeh|ueben|ueber|ueberall|ueberarb|uebernom|uebersetz|uebersich|uebertra|ueberwach|ueblich|uebrig|uebt|uebung|umstaend|ungefaehr|verfueg|verknuepf|verstaend|vorraet|waehr|waerts|woechentl|wuensch|wuerd|zaehl|zaehler|zoeger|zueg)\w*\b|\b\w*(fliess|geniess|schliess)\w*\b'

# Block 2: alleinstehende Woerter
UMLAUT_BLOCK2 = r'\b(gaebe|haette|moecht|moege|ueber|wuerde|wuerden)\b'

UMLAUT_WHITELIST = [
    '22_qualitaets', 'abhaengig_von', 'Address', 'ausfuehrungs_modi', 'Azure', 'bfuer',
    'Class', 'Daemon', 'Failed', 'Issue', 'kongruent', 'Layer',
    'loesch', 'over', 'overall', 'ParseFailureCount_ZweiMsMitKaputtemJson_Zaehlt_Beide_B_B2', 'Payload', 'Pipeline',
    'Plugin', 'Process', 'pruef', 'Queue', 'rescue', 'Sandbox',
    'Schedule', 'Session', 'Source', 'Stage', 'Status', 'Trace',
    'Ueber', 'User', 'Value', 'vorausschauend',
]
# <<< AUTO-GENERATED:UMLAUT-DATA

_WHITELIST_LOWER = {w.lower() for w in UMLAUT_WHITELIST}  # -contains ist in PowerShell case-insensitiv

_RE_BLOCK1 = re.compile(UMLAUT_BLOCK1, re.IGNORECASE)
_RE_BLOCK2 = re.compile(UMLAUT_BLOCK2, re.IGNORECASE)
_RE_FENCE = re.compile(r'^\s*```')
_RE_INLINE = re.compile(r'`+[^`]+`+')
_RE_AUTOLINK = re.compile(r'<[^>\s]+>')
_RE_URL = re.compile(r'(https?://|www\.)\S+', re.IGNORECASE)
_RE_LINKTARGET = re.compile(r'\]\([^)]*\)')

# Test-IsCodeIdentifier: case-sensitiv auf dem MATCH (CamelCase bzw. Underscore/Ziffer)
_RE_LOWER = re.compile(r'[a-zäöüß]')
_RE_INNER_UPPER = re.compile(r'.[A-ZÄÖÜ]')
_RE_UND_DIGIT = re.compile(r'[_0-9]')

# Test-IsTechnicalToken: TLD-/Datei-Endungen (kein Buchstabe/Ziffer danach)
_RE_TLD = re.compile(
    r'\.(de|com|org|net|eu|io|info|gov|co|uk|at|ch|html?|php|aspx?|pdf|md|txt|json|ya?ml|csv|xml|'
    r'ps[md]?1|sh|bat|cmd|jsx?|tsx?|py|css|scss|sql|png|jpe?g|gif|webp|svg|ico|mp[34]|mov|zip|docx?|xlsx?|pptx?)'
    r'(?![0-9A-Za-zäöüßÄÖÜ])',
    re.IGNORECASE,
)
_RE_WWW = re.compile(r'www\.[a-z0-9-]', re.IGNORECASE)
_RE_HANDLE = re.compile(r'^\W*@\w')


def _strip_edges(token):
    # Rand-Zeichen abschneiden, die nicht Buchstabe/Ziffer/Underscore sind
    # (Markdown-Emphase, Klammern, Satzzeichen). Innere _/Ziffern bleiben.
    return re.sub(r'\W+$', '', re.sub(r'^\W+', '', token))


def is_code_identifier(token):
    """CamelCase-Bezeichner (z.B. FehlerEndgueltig): Kleinbuchstabe UND
    (Binnenmajuskel ODER Underscore/Ziffer). Auf den MATCH angewendet."""
    return bool(_RE_LOWER.search(token)) and (
        bool(_RE_INNER_UPPER.search(token)) or bool(_RE_UND_DIGIT.search(token))
    )


def is_slug_token(token):
    """Umgebender Token ist Slug/Bezeichner/Ordnername und laut CLAUDE.md von der
    Umlaut-Pflicht ausgenommen. Erkennungen auf dem Kern-Token (Rand-Markup vorher
    abgeschnitten):
      1. Underscore oder Ziffer (in deutscher Prosa nicht vorhanden), z.B.
         `04_tests-und-qualitaet`, `OE-M6_...`.
      2. Kebab-Slug: mehrere durch Bindestrich verbundene Segmente (>=2), jedes
         nur aus Kleinbuchstaben (inkl. Umlauten), z.B. `story-uebersicht`,
         `analyse-vor-aenderung`. Capitalized Komposita (`Plugin-Loeschung`,
         `Master-Daten`) sind dagegen Prosa und werden weiter geprüft.
    Verhaltensgleich zur PowerShell-Regex `^[\\p{Ll}]+(-[\\p{Ll}]+)+$` (-cmatch,
    case-sensitiv)."""
    core = _strip_edges(token)
    if not core:
        return False
    if ('_' in core) or bool(re.search(r'[0-9]', core)):
        return True
    # Kebab-Slug: >=2 nicht-leere Segmente, jedes nur aus Kleinbuchstaben.
    # str.islower() deckt a-z plus die Umlaute ab und entspricht damit \p{Ll};
    # ein Großbuchstabe in einem Segment disqualifiziert den Slug (Prosa).
    segments = core.split('-')
    if len(segments) < 2:
        return False
    return all(seg and all(ch.islower() for ch in seg) for seg in segments)


def is_technical_token(token):
    """Umgebender Token ist URL, Domain, Dateiname oder Social-Handle -> umlaut-frei."""
    if not token:
        return False
    if '://' in token:
        return True
    if _RE_WWW.search(token):
        return True
    if _RE_TLD.search(token):
        return True
    if _RE_HANDLE.match(token):
        return True
    return False


def _surrounding_token(text, start, end):
    s = start
    while s > 0 and not text[s - 1].isspace():
        s -= 1
    e = end
    while e < len(text) and not text[e].isspace():
        e += 1
    return text[s:e]


def get_umlaut_violations(lines):
    """Nimmt die Zeilen einer Markdown-Datei und liefert pro Verstoss ein dict
    {line, match, text, block}. Wendet Fence-Skip, Inline-Code-, Autolink-, URL-,
    Link-Target-Filter sowie Whitelist-, Bezeichner-, Slug- und Technical-Heuristik an."""
    result = []
    in_fence = False
    for i, line in enumerate(lines):
        if _RE_FENCE.match(line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        clean = _RE_INLINE.sub('', line)
        clean = _RE_AUTOLINK.sub('', clean)
        clean = _RE_URL.sub('', clean)
        clean = _RE_LINKTARGET.sub(']()', clean)

        for m in _RE_BLOCK1.finditer(clean):
            val = m.group(0)
            tok = _surrounding_token(clean, m.start(), m.end())
            if (val.lower() not in _WHITELIST_LOWER
                    and not is_code_identifier(val)
                    and not is_slug_token(tok)
                    and not is_technical_token(tok)):
                result.append({'line': i + 1, 'match': val, 'text': line.strip(), 'block': 1})

        for m in _RE_BLOCK2.finditer(clean):
            val = m.group(0)
            tok = _surrounding_token(clean, m.start(), m.end())
            if (not is_code_identifier(val)
                    and not is_slug_token(tok)
                    and not is_technical_token(tok)):
                result.append({'line': i + 1, 'match': val, 'text': line.strip(), 'block': 2})

    return result
