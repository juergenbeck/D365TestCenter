#!/usr/bin/env python3
"""Gemeinsame Umlaut-Pruef-Logik fuer die Datei-Inhalt-Hooks (Python-Port).

AUTO-GENERATED-Geruest aus ~/.claude/hook-templates/python/umlaut_check_lib.py
(ausgerollt von ~/.claude/scripts/Sync-UmlautTriggers.ps1). Die Datenregion
zwischen den AUTO-GENERATED-Markern wird zusaetzlich aus
~/.claude/umlaute-triggers.json gerendert. Nicht von Hand editieren.

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
UMLAUT_BLOCK1 = r'\b\w*(abhaengig|aehn|aelter|aendert|aenderung|aerger|aergerli|aerztl|aeusser|ausfueh|ausgefuehrt|begruend|behoerde|durchfueh|einfueh|enthaelt|faerb|fluess|frueher|fuehl|fuer|gefaehr|gehoer|geloescht|gemaess|groesse|gruen|haengt|haetten|haeufig|hoechste|hoehe|hoehere|jaehrli|klaer|koennen|kuendig|laenge|laeuft|loesch|loeschen|loest|loesung|moegli|muessen|naechst|naehe|naemli|noetig|praezis|pruef|qualitaet|regulaer|ruestung|saemtli|schaed|schluessel|schoen|spaet|spaeter|spaet|stoer|tatsaechli|ueberall|uebernom|uebersetz|uebersich|uebertra|ueblich|uebrig|uebung|umstaend|ungefaehr|verfueg|verknuepf|wuensch|wuerd|zaehl|zoeger|gueltig|hoeh|ueberarb|primaer|ueberwach|hoeh|ueberarb|primaer|ueberwach|bestaet|bestaet|tragfaeh|tragfaeh|bruecke|bruecke|aender|fuehr|fuehr|hoer|loes|loes|gewaehr|gewaehr|erwaeg|erwaeg|genueg|fueg|schuetz|stuetz|nuetz|ueben|uebung|uebt|schaeft|gruend|gruend|haeng|haelt|faehig|faehig|staend|faehr|haendl|haendl|glaeub|glaeub|itaet|taetig|taetig|moegli|gueltig|gaeng|gaeng|faell|faell|traeg|schlaeg|saetz|saetz|laeuf|laeuf|kraeft|kraeft|maerkt|maerkt|gebuehr|gebuehr|waehr|waehr|waerts|fuellen|fuellt|fuellung|fuellung|buerg|buerg|praesent|praesent|praemi|praezis|taegli|woechentl|frueh|frueh|staerk|staerk|schwaech|schwaech|kuerz|kuerz|ruehr|rueck|rueck|schoepf|schraenk|knuepf|schluess|schluess|erloes|erloes|zueg|zueg|wuensch|beduerf|beduerf|luecke|luecke|verstaend|verstaend|erlaeuter|erlaeuter|vorraet|vorraet|gefaess|gefaess|gebaeud|gebaeud|gelaend|gelaend|zaehler|zaehl|schaed|naehe|aeusser|ueber|ueber)\w*\b'

# Block 2: alleinstehende Woerter
UMLAUT_BLOCK2 = r'\b(gaebe|haette|moecht|moege|ueber|wuerde|wuerden)\b'

UMLAUT_WHITELIST = [
    '22_qualitaets', 'abhaengig_von', 'Address', 'ausfuehrungs_modi', 'Azure', 'bfuer', 'Class', 'Daemon',
    'Failed', 'Issue', 'kongruent', 'Layer', 'loesch', 'over', 'overall', 'ParseFailureCount_ZweiMsMitKaputtemJson_Zaehlt_Beide_B_B2',
    'Payload', 'Pipeline', 'Plugin', 'Process', 'pruef', 'Queue', 'Rescue', 'Sandbox',
    'Schedule', 'Session', 'Source', 'Stage', 'Status', 'Trace', 'Ueber', 'User',
    'Value',
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
    """Umgebender Token ist Slug/Bezeichner/Ordnername: enthaelt Underscore oder
    Ziffer (in deutscher Prosa nicht vorhanden). Rand-Markup vorher abgeschnitten."""
    core = _strip_edges(token)
    if not core:
        return False
    return ('_' in core) or bool(re.search(r'[0-9]', core))


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
