#!/usr/bin/env python3
"""PreToolUse-Sperre für prosa-tragende Tool-Aufrufe (AskUserQuestion, TaskCreate,
TaskUpdate): BLOCKT per permissionDecision "deny", wenn in einem deutschen
Frei-Text-Feld ein ASCII-Surrogat (ae/oe/ue/ss statt ä/ö/ü/ß) ODER verbotene
Typografie (Halbgeviert-/Geviertstrich, die fünf Pfeile) steht. Der Aufruf erreicht den User
bzw. das System dann nicht; Claude bekommt die Begründung zurück und muss mit echten
Umlauten neu absetzen.

Lückenschluss zu block-umlaut-typografie.py: jener greift nur auf .md-Datei-
Schreibvorgänge und prüft den file_path. Die Texte, die per AskUserQuestion an den
User gehen (Frage, Header, Options-Label/-Description/-Preview) und die per
TaskCreate/TaskUpdate gesetzten Frei-Text-Felder (subject/description/activeForm),
sind deutsche Prosa-UI und unterliegen derselben Umlaut-/Typografie-Pflicht, liefen
aber an allen Datei-Hooks vorbei (kein file_path, kein Write-Match). Gerade im
strukturierten Tool-Input ist der ASCII-Reflex besonders stark.

AUTO-GENERATED aus ~/.claude/hook-templates/python/check-tool-umlaute.py (ausgerollt
von ~/.claude/scripts/Sync-UmlautTriggers.ps1; die Datenregion zwischen den
AUTO-GENERATED-Markern wird aus ~/.claude/umlaute-triggers.json gerendert). Nicht von
Hand editieren, sondern am Template ändern und neu syncen.
Plattformneutral (macOS / Windows / Linux); kein Laufzeit-Zugriff auf die JSON, weil
die mobil nicht vorhanden ist - das volle Pattern wird eingebettet (kein
.githooks-Pfad-Risiko: ein PreToolUse-Hook, der am Pfad scheitert, blockt sonst in
einen Deadlock).

Geprüft werden nur Frei-Text-Felder:
  AskUserQuestion : question, header, options[].label, options[].description, options[].preview
  TaskCreate/Update: subject, description, activeForm
(Technische Felder wie taskId, status, owner, metadata bleiben unberührt.)

Tool-Texte sind reine deutsche UI-Prosa, daher die VOLLE Stamm-Liste (alle Blöcke),
nicht die Datei-Inhalt-Teilmenge. Matching ist case-insensitiv (wie das volle
commit-msg-Pattern), fängt also auch Satzanfang-Großschreibung (Fuer, Waere, ...)
und Vollcaps. Backtick-Zitate und Whitelist-Tokens werden vorab neutralisiert.
Jedes Feld wird einzeln geprüft, damit ein Code-Fence in einem Feld das Fence-Tracking
eines anderen nicht verfälscht.

Mechanismus: permissionDecision "deny" via JSON-stdout (PreToolUse), Exit 0.
ensure_ascii=True gegen die Windows-cp1252-stdout-Falle. Fail-open: bei jedem Fehler
(kein stdin, JSON kaputt) Exit 0, damit ein Guard-Bug keine Aufrufe blockiert.
"""
import json
import re
import sys

TYPO = {"–": "Halbgeviertstrich", "—": "Geviertstrich", "→": "Pfeil rechts",
        "←": "Pfeil links", "↔": "Doppelpfeil", "⇒": "Doppelpfeil rechts",
        "⇔": "Doppelpfeil beidseitig"}

# >>> AUTO-GENERATED:TOOLUMLAUT-DATA. Quelle: ~/.claude/umlaute-triggers.json. Nicht von Hand editieren.
TOOL_TRIGGERS = r'\b\w*(abhaengig|abloest|aehn|aelter|aeltest|aender|aendert|aenderung|aequivalent|aerger|aergerli|aerztl|aeusser|allmaehl|anhaenge|aufgeloest|aufloesung|aufraeum|aufwaend|ausfueh|ausgefuehrt|auszueg|beduerf|begruend|behoerde|beruecks|bestaet|bezueg|braeuch|bruech|bruecke|buendel|buerg|buero|domaene|duerfen|durchfueh|durchgefuehrt|einfueh|einfuehr|empfaenger|enthaelt|entitaeten|entwuerf|ergaen|ergaenz|erlaeuter|erloes|erschoepft|erwaeg|erwaehnt|fachdomaene|faehig|faehr|faell|faelsch|faerb|fluess|frueh|frueher|fueg|fuehl|fuehr|fuehrend|fuellen|fuellt|fuellung|fuenf|fuer|gaeng|gaest|gebaeud|gebuehr|gedaechtnis|gefaehr|gefaehrli|gefaess|gehoer|gelaend|geloescht|gemaess|gemuetlich|genueg|gepruft|geruest|geschaeft|gespraech|gewaehr|glaeub|glueck|groesse|gruen|gruend|gueltig|haelt|haendl|haeng|haengen|haengt|haetten|haeufig|hauptsaech|hinzugefuegt|hoechste|hoeh|hoehe|hoehere|hoer|itaet|jaehrli|juenger|klaer|klaerung|knuepf|koennen|koennte|koerper|konformitaet|kraeft|kuech|kuendig|kuenft|kuenftig|kuerz|laed|laenge|laengst|laesst|laeuf|laeuft|loes|loesch|loeschen|loest|loesung|luecke|maengel|maerkt|maerz|massnahme|moechte|moegen|moegli|moeglich|muessen|muesste|naechst|naechster|naehe|naemli|natuerlich|noetig|nuetz|nuetzlich|oeffn|oekonomie|persoenlich|plaene|praefix|praemi|praesent|praevent|praezedenz|praezis|primaer|pruef|pruefen|pruefung|qualitaet|quarantaene|raeum|realitaet|regulaer|rueck|ruecksprach|ruehr|ruestung|saemtli|saetz|schaed|schaeft|schaerf|schaetz|schlaeg|schluess|schluessel|schoen|schoepf|schraenk|schuetz|schwaech|sekretaer|spaet|spaeter|staedt|staend|staerk|stoer|stoerung|strassen|stueck|stuend|stuetz|taegli|taetig|tatsaechli|temporaer|tonalitaet|traeg|tragfaeh|ueben|ueber|ueberall|ueberarb|ueberblick|ueberlauf|uebernom|ueberpruef|ueberprueft|ueberschr|uebersetz|uebersich|uebersicht|ueberspring|uebertra|ueberwach|ueblich|uebrig|uebt|uebung|umstaend|ungefaehr|ungueltig|unmoegl|urspruengl|verfueg|verknuepf|veroeffentl|verstaend|verstaendlich|verstaerken|verstoss|voellig|vollstaendig|vorgaenger|vorraet|vorschlaeg|waecht|waehl|waehr|waere|waerme|waerts|widerspruech|woechentl|woertlich|wuensch|wuerd|zaehl|zaehler|zoeger|zueg|zurueck|zusaetz|zusaetzli)\w*\b|\b\w*(abschliess|anschliess|ausschliess|ausschliesslich|aussen|äusser|ausserdem|ausserhalb|aussert|aussreich|begruess|beiss|einschliess|fliess|gemaess|geniess|giess|groess|gross|gröss|grosse|grosser|grosses|hauptstrass|heiss|laess|liess|maess|mäss|massg|massnahm|mutmass|regelmaess|reiss|schiess|schliess|schmeiss|stoess|stoss|süss|verschliess|verstoess)\w*\b|\b(gaebe|haette|moecht|moege|muell|ueber|wuerde|wuerden)\b|\b(ausser|bloss|busse|draussen|fasse|folgendermassen|gleichermassen|gruss|hauptstrasse|spass|weiss|weisst)\b'
TOOL_WHITELIST = r'\b(22_qualitaets|abhaengig_von|address|ausfuehrungs_modi|azure|bfuer|budgetpraeventionintegration|class|daemon|failed|issue|kongruent|layer|loesch|over|overall|parsefailurecount_zweimsmitkaputtemjson_zaehlt_beide_b_b2|payload|pipeline|plugin|process|pruef|queue|rescue|sandbox|schaetzle|schedule|schmeissner|session|source|stage|status|trace|user|value|vorausschauend)\b'
TOOL_FUGEN = r'(kreis|preis)s'
# <<< AUTO-GENERATED:TOOLUMLAUT-DATA

# Case-insensitiv (wie der commit-msg-Hook, der auf einer lowercase-Kopie matcht,
# und wie umlaut_check_lib.py mit re.IGNORECASE): die Datenregion führt reine
# lowercase-Stämme, re.IGNORECASE deckt Groß-/Kleinschreibung ab. Die Whitelist
# wird ebenfalls case-insensitiv neutralisiert, damit englische Fachbegriffe
# (User, Status, Azure, ...) in jeder Schreibweise verschont bleiben.
_RE_TRIGGERS = re.compile(TOOL_TRIGGERS, re.IGNORECASE)
_RE_WHITELIST = re.compile(TOOL_WHITELIST, re.IGNORECASE)
_RE_INLINE = re.compile(r'`[^`]*`')

# Wortfugen (Feld wortfugen der Trigger-JSON, seit 2026-08-09): Ein Vorderglied auf -s
# erzeugt vor einem s-Anfang eine Folge, die einen Stamm vortäuscht, "Preisstufe"
# enthält "reiss". Die Fuge wird durch ein Leerzeichen aufgetrennt, statt das Token
# wie bei der Whitelist ganz zu verwerfen: so verschwindet der Fehlalarm, ein echter
# Verstoß im hinteren Wortteil (`Preisschaetzung`) bleibt aber erkennbar. Anlass war
# eine per deny abgewiesene AskUserQuestion zur Preisstufe eines Azure-Dienstes.
# Leerer Wert heißt "keine Fugen konfiguriert": dann NICHT kompilieren, eine leere
# Regex würde sonst vor jedem Zeichen ein Leerzeichen einfügen.
_RE_FUGEN = re.compile(TOOL_FUGEN, re.IGNORECASE) if TOOL_FUGEN else None


def collect_fields(tool, ti):
    """Liefert (where, text)-Paare aller deutschen Frei-Text-Felder des Tools.
    header ist ein kurzes UI-Label, trotzdem deutscher Text; preview ist optional
    und kann Markdown/Code enthalten (die Backtick-/Whitelist-Filter fangen das)."""
    fields = []
    if tool == 'AskUserQuestion':
        for qi, q in enumerate(ti.get('questions') or [], start=1):
            if not isinstance(q, dict):
                continue
            for key in ('question', 'header'):
                val = q.get(key)
                if isinstance(val, str) and val:
                    fields.append(('Frage %d (%s)' % (qi, key), val))
            for oi, opt in enumerate(q.get('options') or [], start=1):
                if not isinstance(opt, dict):
                    continue
                for key in ('label', 'description', 'preview'):
                    val = opt.get(key)
                    if isinstance(val, str) and val:
                        fields.append(('Frage %d Option %d (%s)' % (qi, oi, key), val))
    elif tool in ('TaskCreate', 'TaskUpdate'):
        for fn in ('subject', 'description', 'activeForm'):
            val = ti.get(fn)
            if isinstance(val, str) and val:
                fields.append(('%s (%s)' % (tool, fn), val))
    return fields


def typo_hits(texts):
    hits = []
    for t in texts:
        for ch, label in TYPO.items():
            if ch in t:
                hits.append(label)
    return sorted(set(hits))


def main():
    try:
        raw = sys.stdin.read()
        if not raw:
            return 0
        data = json.loads(raw)
    except Exception:
        return 0  # Fail-open

    tool = str(data.get('tool_name') or '')
    ti = data.get('tool_input')
    if not isinstance(ti, dict) or tool not in ('AskUserQuestion', 'TaskCreate', 'TaskUpdate'):
        return 0

    fields = collect_fields(tool, ti)
    if not fields:
        return 0

    # Surrogat-Treffer: Backtick-Inline-Code raus, Whitelist neutralisieren, dann matchen
    matches = set()
    for _where, text in fields:
        clean = _RE_INLINE.sub('', text)
        if _RE_FUGEN is not None:
            clean = _RE_FUGEN.sub(r'\1 s', clean)
        clean = _RE_WHITELIST.sub('', clean)
        for m in _RE_TRIGGERS.finditer(clean):
            matches.add(m.group(0))
    # Typografie-Treffer auf dem Rohtext (Sonderzeichen, von Backticks unberührt)
    th = typo_hits([t for _, t in fields])

    problems = []
    if matches:
        problems.append("ASCII-Surrogate statt Umlaute (" + ", ".join(sorted(matches)[:5]) + ")")
    if th:
        problems.append("verbotene Typografie: " + ", ".join(th))
    if not problems:
        return 0

    reason = ("%s-Sperre: " % tool + "; ".join(problems)
              + ". Diese Tool-Texte sind deutsche UI: echte Umlaute (ä ö ü ß) verwenden, "
                "keine Geviert-/Halbgeviertstriche oder Pfeile (stattdessen Komma/Doppelpunkt bzw. ->). "
                "Technische Tokens bei Bedarf in Backticks zitieren. Texte korrigieren und erneut absetzen.")
    out = {"hookSpecificOutput": {"hookEventName": "PreToolUse",
                                  "permissionDecision": "deny",
                                  "permissionDecisionReason": reason}}
    sys.stdout.write(json.dumps(out, ensure_ascii=True))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)
