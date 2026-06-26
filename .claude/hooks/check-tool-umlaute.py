#!/usr/bin/env python3
"""PreToolUse-Sperre für prosa-tragende Tool-Aufrufe (AskUserQuestion, TaskCreate,
TaskUpdate): BLOCKT per permissionDecision "deny", wenn in einem deutschen
Frei-Text-Feld ein ASCII-Surrogat (ae/oe/ue/ss statt ä/ö/ü/ß) ODER verbotene
Typografie (Halbgeviert-/Geviertstrich, Pfeil) steht. Der Aufruf erreicht den User
bzw. das System dann nicht; Claude bekommt die Begründung zurück und muss mit echten
Umlauten neu absetzen.

Lückenschluss zu block-umlaut-typografie.py: jener greift nur auf .md-Datei-
Schreibvorgänge und prüft den file_path. Die Texte, die per AskUserQuestion an den
User gehen (Frage, Header, Options-Label/-Description/-Preview) und die per
TaskCreate/TaskUpdate gesetzten Frei-Text-Felder (subject/description/activeForm),
sind deutsche Prosa-UI und unterliegen derselben Umlaut-/Typografie-Pflicht, liefen
aber an allen Datei-Hooks vorbei (kein file_path, kein Write-Match). Gerade im
strukturierten Tool-Input ist der ASCII-Reflex besonders stark.

AUTO-GENERATED-Datenregion aus ~/.claude/umlaute-triggers.json (gerendert vom Sync
~/.claude/scripts/Sync-UmlautTriggers.ps1, Funktion Ensure-ToolHookData).
Plattformneutral (macOS / Windows / Linux); kein Laufzeit-Zugriff auf die JSON, weil
die mobil nicht vorhanden ist - das volle Pattern wird eingebettet (kein
.githooks-Pfad-Risiko: ein PreToolUse-Hook, der am Pfad scheitert, blockt sonst in
einen Deadlock).

Geprüft werden nur Frei-Text-Felder:
  AskUserQuestion : question, header, options[].label, options[].description, options[].preview
  TaskCreate/Update: subject, description, activeForm
(Technische Felder wie taskId, status, owner, metadata bleiben unberührt.)

Tool-Texte sind reine deutsche UI-Prosa, daher die VOLLE Stamm-Liste (alle Blöcke),
nicht die Datei-Inhalt-Teilmenge. Matching ist case-sensitiv (wie das volle
commit-msg-Pattern). Backtick-Zitate und Whitelist-Tokens werden vorab neutralisiert.
Jedes Feld wird einzeln geprüft, damit ein Code-Fence in einem Feld das Fence-Tracking
eines anderen nicht verfälscht.

Mechanismus: permissionDecision "deny" via JSON-stdout (PreToolUse), Exit 0.
ensure_ascii=True gegen die Windows-cp1252-stdout-Falle. Fail-open: bei jedem Fehler
(kein stdin, JSON kaputt) Exit 0, damit ein Guard-Bug keine Aufrufe blockiert.
"""
import json
import re
import sys

TYPO = {"–": "Halbgeviertstrich", "—": "Geviertstrich", "→": "Pfeil"}

# >>> AUTO-GENERATED:TOOLUMLAUT-DATA. Quelle: ~/.claude/umlaute-triggers.json. Nicht von Hand editieren.
TOOL_TRIGGERS = r'\b\w*(abhaengig|abloest|aehn|aelter|aendert|aenderung|aerger|aergerli|aerztl|aeusser|allmaehl|Anhaenge|aufgeloest|Aufloesung|aufraeum|ausfueh|ausgefuehrt|Auszueg|begruend|behoerde|beruecks|Bezueg|Domaene|durchfueh|durchgefuehrt|einfueh|einfuehr|Empfaenger|enthaelt|Entitaeten|Ergaen|ergaenz|erschoepft|erwaehnt|Fachdomaene|faerb|fluess|frueher|fuehl|fuehrend|fuer|gefaehr|gefaehrli|gehoer|geloescht|gemaess|gepruft|Geschaeft|groesse|gruen|haengen|haengt|haetten|haeufig|haupts|hinzugefuegt|hoechste|hoehe|hoehere|jaehrli|klaer|Klaerung|koennen|koennte|Konformitaet|kuendig|kuenftig|laenge|laengst|laesst|laeuft|Loesch|loeschen|loest|Loesung|Massnahme|moechte|moegen|moegli|moeglich|muessen|muesste|naechst|naechster|naehe|naemli|noetig|nuetzlich|Plaene|praezis|pruef|pruefen|Pruefung|qualitaet|raeum|Realitaet|regulaer|Ruecksprach|ruestung|saemtli|schaed|schluessel|schoen|spaet|spaeter|Spaet|stoer|Stoerung|Strassen|Stueck|tatsaechli|Tonalitaet|ueberall|Ueberblick|ueberlauf|uebernom|ueberpruef|ueberprueft|ueberschr|uebersetz|uebersich|Uebersicht|ueberspring|uebertra|ueblich|uebrig|uebung|umstaend|ungefaehr|ungueltig|unmoegl|verfueg|verknuepf|veroeffentl|verstaendlich|verstaerken|Verstoss|vollstaendig|Vorgaenger|vorschlaeg|waehl|waere|wuensch|wuerd|zaehl|zoeger|zurueck|zusaetz|zusaetzli|gueltig|hoeh|ueberarb|primaer|ueberwach|Hoeh|Ueberarb|Primaer|Ueberwach|bestaet|Bestaet|tragfaeh|Tragfaeh|bruecke|Bruecke|aender|fuehr|Fuehr|hoer|loes|Loes|gewaehr|Gewaehr|erwaeg|Erwaeg|genueg|fueg|schuetz|stuetz|nuetz|ueben|Uebung|uebt|schaeft|gruend|Gruend|haeng|haelt|faehig|Faehig|staend|faehr|haendl|Haendl|glaeub|Glaeub|itaet|taetig|Taetig|Moegli|Gueltig|gaeng|Gaeng|faell|Faell|traeg|schlaeg|saetz|Saetz|laeuf|Laeuf|kraeft|Kraeft|maerkt|Maerkt|gebuehr|Gebuehr|waehr|Waehr|waerts|fuellen|fuellt|fuellung|Fuellung|buerg|Buerg|praesent|Praesent|Praemi|Praezis|taegli|woechentl|frueh|Frueh|staerk|Staerk|schwaech|Schwaech|kuerz|Kuerz|ruehr|rueck|Rueck|schoepf|schraenk|knuepf|schluess|Schluess|erloes|Erloes|zueg|Zueg|Wuensch|beduerf|Beduerf|luecke|Luecke|verstaend|Verstaend|erlaeuter|Erlaeuter|vorraet|Vorraet|gefaess|Gefaess|gebaeud|Gebaeud|gelaend|Gelaend|zaehler|Zaehl|Schaed|Naehe|Aeusser|Ueberschr|ueber|Ueber)\w*\b|\b\w*(abschliess|anschliess|aussch|ausschliess|ausschliesslich|aussehen|aussen|ausserhalb|aussert|aussreich|einschliess|gemaess|groess|gross|grosse|grosser|grosses|Hauptstrass|laess|maess|massnahm|regelmaess|schliess|verschliess|verstoess|begruess|Begruess|fliess|geniess|reiss|heiss|giess|schiess|beiss|massg)\w*\b|\b(gaebe|haette|moecht|moege|ueber|wuerde|wuerden)\b|\b(ausser|busse|draussen|fasse|Hauptstrasse)\b'
TOOL_WHITELIST = r'\b(22_qualitaets|abhaengig_von|Address|ausfuehrungs_modi|Azure|bfuer|Class|Daemon|Failed|Issue|kongruent|Layer|loesch|over|overall|ParseFailureCount_ZweiMsMitKaputtemJson_Zaehlt_Beide_B_B2|Payload|Pipeline|Plugin|Process|pruef|Queue|Rescue|Sandbox|Schedule|Session|Source|Stage|Status|Trace|Ueber|User|Value|vorausschauend)\b'
# <<< AUTO-GENERATED:TOOLUMLAUT-DATA

# Case-sensitiv wie der commit-msg-grep (grep -nE ohne -i): die Datenregion führt
# Stämme bewusst in Original-Schreibweise (lowercase und Capitalized getrennt).
_RE_TRIGGERS = re.compile(TOOL_TRIGGERS)
_RE_WHITELIST = re.compile(TOOL_WHITELIST)
_RE_INLINE = re.compile(r'`[^`]*`')


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
