#!/usr/bin/env python3
"""Stop-Hook: hält den Turn fest, solange die Antwort einen grünen Schritt benennt.

Entscheid: ADR-2026-09-03-113147 (Das Anhalten des Turns wird geregelt, nicht die Form
des Schlusssatzes). Befund und Messung:
vorgaenge/2026-09-01-dauerauftrag-autarke-sessions/befund-2026-09-03-ankuendigen-statt-tun.md

Warum es diesen Hook gibt. Drei Textreparaturen an der Schlusssatz-Regel (01.09., 02.09.,
03.09.2026) haben die Zahl der Fragen gesenkt, nicht die Zahl der folgenlosen Stopps: das
Verhalten ist von der Frage in die Ankündigung und von dort ins Schweigen ausgewichen
(Frage 82,7 -> 49,5 Prozent, Ankündigung 1,5 -> 11,0, stumm 15,8 -> 39,4). Der gemeinsame
Nenner ist nicht der Wortlaut, sondern dass der Turn endet. Genau das misst dieser Hook.

Vier Eigenschaften sind Teil des Entscheids, nicht Umsetzungsdetail:
  * Er blockt höchstens einmal je Turn (stop_hook_active wird respektiert).
  * Er ist fail-open: jeder Fehler endet mit 0. Ein defekter Hook darf nie festhalten.
  * Im Zweifel lässt er enden. Rot erkannt, nichts erkannt -> Turn endet.
  * Ventil: KEIN_TURN_RIEGEL=1 in der Umgebung schaltet ihn ab.

Blockierweg. An der installierten claude.exe belegt sind die Konzepte `blockingErrors`
und `preventContinuation`; ein Hook, der mit Code 2 endet, erzeugt einen blocking error,
und `stop_hook_active` plus CLAUDE_CODE_STOP_HOOK_BLOCK_CAP (Vorgabe 8) sind die
eingebauten Schleifensicherungen. Daneben existiert die JSON-Form mit `decision`/`reason`.
Welche der beiden die Oberfläche auswertet, ist NICHT belegt, deshalb gibt der Hook
bewusst beide Signale ab: das JSON auf stdout und dieselbe Begründung auf stderr mit
Code 2. Beide bewirken dasselbe, eine Doppelwirkung ist ausgeschlossen, weil der zweite
Aufruf stop_hook_active trägt.

Selbstprobe: `python check-turn-ende.py --selbstprobe` (0 sauber, 2 Werkzeug defekt).
"""

import json
import os
import re
import sys

# --- Rote Handlungen: hier endet der Turn, die Freigabe ist Jürgens ------------------
ROT = re.compile(
    r"(mail|e-?mail|anschreiben|nachricht an|ticket-?kommentar|jira|teams|devops|"
    r"versenden|verschicken|rausschicken|hinausgehen|absenden|"
    r"deploy|deployment|prod\b|produktiv|cutover|"
    r"text an \w|entwurf an \w|schreiben an \w|antwort an \w|"
    r"push --force|force-?push|reset --hard|rebase|--amend|branch löschen|"
    r"rm -rf|\bdrop\b|unwiederbringlich|"
    r"rechnung|angebot|vertrag|kündig|honorar|zahlung)",
    re.IGNORECASE,
)

# --- Ein benannter nächster Schritt: als Frage, als Ankündigung, gleichgültig --------
FRAGE = re.compile(
    r"(soll ich\b|sollen wir\b|möchtest du,? dass ich\b|willst du,? dass ich\b|"
    r"soll der\b|soll das\b|sag bescheid,? (wenn|ob)\b|gib bescheid,? (wenn|ob)\b)",
    re.IGNORECASE,
)
ANSAGE = re.compile(
    r"(ich mache\b|mache ich\b|als nächstes\b|der nächste schritt\b|"
    r"nächster schritt\b|ich beginne\b|ich fahre fort\b|ich starte\b|ich nehme mir\b|"
    r"nehme ich mir\b|ich prüfe\b|ich messe\b|ich sehe mir\b|ich lege\b|ich baue\b|"
    r"ich schreibe\b|ich ziehe\b|ich werde\b|werde ich\b|"
    r"sofern du nichts anderes\b|wenn nichts anderes kommt\b|ohne gegenmeldung\b|"
    r"falls nichts anderes kommt\b|würde ich\b|ich würde\b|ich räume\b|räume ich\b|"
    r"ich gehe .{0,20}an\b|ich sehe\b|ich hole\b|ich klär|ich nehme\b)",
    re.IGNORECASE,
)

# --- Wartend: der Schritt hängt an etwas, das die Sitzung nicht vorziehen kann -------
# Gefunden vom Inspektor am 03.09.2026 an zwei Sätzen, die diese Lieferung selbst
# vorschreibt: „Gerade läuft die unabhängige Abnahme. Als Nächstes ziehe ich ihre Befunde
# nach." ist die neue Punkt-5-Form „was jetzt läuft" und wurde trotzdem geblockt. Wer auf
# ein Ergebnis wartet, das noch nicht da ist, hält nicht aus Bequemlichkeit an.
WARTEND = re.compile(
    r"(läuft (gerade|noch|bereits|derzeit)|läuft (die|der|das|ein|eine)\b|"
    r"gerade läuft|noch am laufen|sobald\b|sowie .{0,30}(durch|fertig|da) ist|"
    r"wenn .{0,30}(durch|fertig|zurück) ist|"
    # „Warten" auf einen Prozess ist etwas anderes als Warten auf Jürgen. Ohne diese
    # Trennung entschärft ein angehängtes „ich warte auf deine Freigabe" jeden grünen
    # Schritt, also genau das Verhalten, gegen das der Hook gebaut ist (Inspektor,
    # Runde 2, 03.09.2026; gemessen entschärft WARTEND 82 von 2.377 Blocks, 3,4 Prozent).
    r"(?<!deine antwort )steht (noch )?aus\b|"
    # Die ASCII-Schreibung von Jürgens Namen gehört in die Ausnahme. Sie steht als
    # zusammengesetztes Literal da, weil das Muster sonst beim Umbenennen der Bezeichner
    # als Surrogat auffiel und stillschweigend entfernt wurde - genau das hat am
    # 03.09.2026 das Schlupfloch wieder geöffnet, das die Abnahme zuvor geschlossen hatte.
    # Die Schreibweise als Token ist NICHT verboten: der Selbstprobenfall weiter unten
    # führt sie im Klartext, und die Umlaut-Prüfung meldet auf dieser Datei null Verstöße
    # (Inspektor-Restpunkt, Rollout-Abnahme Runde 2).
    # Der Lookahead überspringt einen optionalen Bestimmer (Artikel oder Possessiv) und
    # führt den Namen ohne Genitiv-s, damit er den Genitiv als Präfix mitnimmt. Vorher
    # kannte er nur unmittelbar folgende Wortformen, und „ich warte auf Jürgen" wie
    # „ich warte auf die Rückmeldung" liefen daran vorbei (Restpunkt des Inspektors der
    # Rollout-Runde 2 vom 03.09.2026; behoben mit ADR-2026-09-03-221001).
    r"warte (noch )?auf (?!(?:(?:die|der|das|den|dem|eine|einen|einer|"
    r"deine|deiner|deinen|deinem|dein) +)?"
    r"(?:jürgen|" + "jue" + "rgen|antwort|freigabe|rückmeldung|zustimmung|"
    # Die Possessive stehen ZUSÄTZLICH als freie Alternative da, nicht nur als
    # Bestimmer vor der Wortliste: „warte auf deine Einschätzung" und „warte auf
    # deinen Auftrag" waren vorher gedeckt und wären es sonst nicht mehr. Eine
    # Verschärfung, die dabei anderswo Fläche verliert, ist keine (Inspektor,
    # 03.09.2026, Abnahme zu ADR-2026-09-03-221001).
    r"gegenmeldung|entscheidung)|dich\b|dein\w*\b)|"
    r"im hintergrund|melde mich, sobald)",
    re.IGNORECASE,
)

# --- Grüne Tätigkeiten: das, was ohne Rückfrage getan werden darf --------------------
ERLAUBT = re.compile(
    r"(lesen|liest|ansehen|anschauen|sichten|nachlesen|"
    r"messen|misst|zählen|prüfen|prüfe|nachmessen|nachprüfen|verifizieren|"
    # „(?<!ver)suche" statt „\bsuche": die Wortgrenze bräche „Ursachensuche", ein realer
    # Fall aus den Transkripten. Ausgenommen wird nur „versuche" (Inspektor, Runde 2).
    r"(?<!ver)suche|suchen|durchsuchen|greppen|kartieren|erheben|auswerten|analysieren|"
    r"bauen|umbauen|schreiben|anlegen|ergänzen|nachziehen|korrigieren|beheben|"
    r"aufbereiten|entwerfen|dokumentieren|committen|adr\b|nachweis|test|trockenlauf|"
    r"weiter|fortfahren|fortsetzen|dranbleiben|angehen|vornehmen|in angriff|"
    # Substantivformen: „die Auswertung", „die Prüfung" benennen dieselbe Tätigkeit wie
    # das Verb, und Antworten formulieren so mindestens ebenso oft.
    r"auswertung|prüfung|messung|analyse|sichtung|recherche|bereinigung|umbau|"
    r"kartierung|erhebung|zählung|korrektur|aufbereitung)",
    re.IGNORECASE,
)


def letzter_absatz(text):
    """Der letzte nicht-leere Absatz. Dort steht, wie die Antwort ausgeht."""
    teile = [t.strip() for t in re.split(r"\n\s*\n", text or "") if t.strip()]
    return teile[-1] if teile else ""


def letzte_abschnitte(text, anzahl=2):
    """Die letzten Absätze zusammen. Die Tätigkeit steht oft einen Satz vor der
    Ankündigung: „Der nächste Schritt ist X ... mache ich dort weiter". Wer nur den
    letzten Absatz liest, sieht das „mache ich" und nicht, worum es geht."""
    teile = [t.strip() for t in re.split(r"\n\s*\n", text or "") if t.strip()]
    return "\n\n".join(teile[-anzahl:]) if teile else ""


def satz_um(text, pos, grenze=140):
    """Der Satz, in dem die Fundstelle steht, auf `grenze` Zeichen gekürzt."""
    anfang = max((text.rfind(z, 0, pos) for z in ".!?\n"), default=-1) + 1
    ende = min((p for p in (text.find(z, pos) for z in ".!?\n") if p != -1),
               default=len(text))
    satz = " ".join(text[anfang:ende + 1].split())
    return satz if len(satz) <= grenze else satz[:grenze - 1] + "…"


def beurteile(nachricht):
    """Gibt (blocken, begründung) zurück. Im Zweifel: nicht blocken."""
    absatz = letzter_absatz(nachricht)
    ende = letzte_abschnitte(nachricht)
    if not absatz:
        return False, ""
    # Rot wird über den weiteren Bereich geprüft: im Zweifel enden lassen.
    if ROT.search(ende):
        return False, ""
    if WARTEND.search(ende):
        return False, ""
    treffer_frage = FRAGE.search(absatz)
    treffer_ansage = ANSAGE.search(absatz)
    if not treffer_frage and not treffer_ansage:
        return False, ""
    if not ERLAUBT.search(ende):
        return False, ""
    art = "als Frage" if treffer_frage else "als Ankündigung"
    treffer = treffer_frage or treffer_ansage
    wort = treffer.group(0)
    # Den ganzen Satz mitgeben, nicht nur das Auslöserwort: sonst nennt die Begründung
    # „mache ich" statt der Sache, um die es geht (Inspektor-Befund zu K2, 03.09.2026).
    schritt = satz_um(absatz, treffer.start())
    return True, (
        f"Diese Antwort benennt {art} einen nächsten Schritt, der nach der "
        f"Ampel grün aussieht: \"{schritt}\" (erkannt an \"{wort}\"). Danach beendet sie "
        "den Turn. Genau das ist der Fall aus "
        "ADR-2026-09-03-113147: Ein Turn endet nicht, solange die Antwort selbst einen "
        "grünen nächsten Schritt benennt.\n\n"
        "Führ den Schritt jetzt aus, statt ihn anzukündigen oder zu erfragen. Danach "
        "berichtet der Schlusssatz, was getan wurde und was läuft.\n\n"
        "Ist der Schritt in Wahrheit rot (Text an Dritte, fremdes System, git-History, "
        "Geld- oder Rechtsfolge, unwiederbringliches Löschen) oder brauchst du wirklich "
        "eine fachliche Weichenstellung von Jürgen, dann sag das in einem Satz "
        "ausdrücklich und beende den Turn; dieser Hook meldet sich kein zweites Mal."
    )


def selbstprobe():
    """Positivfall und Gegenfälle. Findet die Probe ihren Positivfall nicht, ist das
    Werkzeug defekt und meldet 2, nicht 0 und nicht 1."""
    proben = [
        # (Text, erwartet_blocken)
        ("Da ich damit gerade das Verfahren in der Hand habe, mache ich dort weiter, "
         "sofern du nichts anderes willst.", True),
        ("Soll ich mir den EK-Forecast der korrigierten Positionen ansehen?", True),
        ("Als Nächstes würde ich den Vorgang prüfen. Das ist Bauarbeit im Repo.", True),
        ("Der nächste sinnvolle Schritt ist Einräum-Zen.\n\nDa ich damit gerade das "
         "Verfahren in der Hand habe, mache ich dort weiter, sofern du nichts anderes "
         "willst.", True),
        ("Der Stand ist damit vollständig, alles committet und gepusht.", False),
        ("Ich habe die Messung aufbereitet.\n\nAls Nächstes geht die Mail an Vanessa "
         "raus.", False),
        # Beide vom Inspektor am 03.09.2026 als Fehltreffer gefunden, an Sätzen, die
        # diese Lieferung selbst vorschreibt. Der zweite ist die neue Punkt-5-Form.
        ("Der Nachweis ist gebaut und läuft gerade.\n\nIch prüfe sein Ergebnis, sobald "
         "der Lauf durch ist.", False),
        ("Fertig: Messung, ADR und Nachweis stehen, alles committet und gepusht.\n\n"
         "Gerade läuft die unabhängige Abnahme. Als Nächstes ziehe ich ihre Befunde "
         "nach.", False),
        # Restpunkte des Inspektors, Runde 2: das Warten auf Jürgen darf den Riegel nicht
        # entschärfen, und „versuche" ist keine grüne Tätigkeit.
        ("Ich warte auf deine Freigabe. Als Nächstes prüfe ich die Messung.", True),
        # Dieselbe Umgehung in ASCII-Schreibung. Ohne diesen Fall ist der Zweig
        # unbemerkt entfernbar, was am 03.09.2026 genau einmal passiert ist.
        ("Ich warte auf Jürgens Freigabe. Als Nächstes prüfe ich die Messung.",
         True),
        # Ein Possessiv vor einem beliebigen Substantiv bleibt gedeckt, sonst
        # verliert die Verschärfung Fläche, die sie vorher hatte.
        ("Ich warte auf deine Einschätzung. Als Nächstes prüfe ich die Messung.",
         True),
        ("Ich warte auf deinen Auftrag. Als Nächstes prüfe ich die Messung.",
         True),
        ("Ich warte auf Juergens Freigabe. Als Nächstes prüfe ich die Messung.",
         True),
        ("Als Nächstes prüfe ich die Messung. Deine Antwort steht noch aus.", True),
        # Vier Formen des Wartens auf Jürgen, je ein eigener Fall (ADR-2026-09-03-221001).
        # Mit Genitiv-s und in ASCII stehen sie schon oben; hier die beiden Formen, die
        # der Lookahead vorher nicht kannte, plus die ASCII-Form ohne Genitiv-s.
        ("Ich warte auf Jürgen. Als Nächstes prüfe ich die Messung.", True),
        ("Ich warte auf die Rückmeldung. Als Nächstes prüfe ich die Messung.", True),
        ("Ich warte auf Juergen. Als Nächstes prüfe ich die Messung.", True),
        ("Ich warte auf deine Entscheidung. Als Nächstes prüfe ich die Messung.", True),
        # Gegenproben: das Warten auf einen Prozess bleibt eine Ausnahme, mit Artikel
        # ebenso wie ohne. Wäre der Bestimmer nicht optional, fielen diese Fälle mit.
        ("Ich warte auf den Lauf. Als Nächstes prüfe ich sein Ergebnis.", False),
        ("Der Lauf läuft noch. Sobald er durch ist, prüfe ich das Ergebnis.", False),
        ("Ich versuche es später noch einmal.", False),
        ("Soll ich die Mail an Saulius jetzt versenden?", False),
        ("Als Nächstes wäre der Deploy nach PROD fällig.", False),
        ("Ich würde jetzt push --force ausführen, sag Bescheid.", False),
        ("", False),
    ]
    fehler = []
    for text, erwartet in proben:
        ist, _ = beurteile(text)
        if ist != erwartet:
            fehler.append(f"  erwartet {erwartet}, ist {ist}: {text[:70]!r}")
    if fehler:
        print("Selbstprobe GESCHEITERT:", file=sys.stderr)
        print("\n".join(fehler), file=sys.stderr)
        return 2
    print(f"Selbstprobe bestanden ({len(proben)} Fälle, davon "
          f"{sum(1 for _, e in proben if e)} Positivfälle).")
    return 0


def main():
    # Die Ausgabe trägt Umlaute; ohne das schlägt print auf Windows in cp1252 fehl und
    # der Hook fiele über den fail-open-Zweig still aus.
    for strom in (sys.stdout, sys.stderr):
        try:
            strom.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
    if "--selbstprobe" in sys.argv:
        return selbstprobe()
    # Ab hier gilt fail-open ausnahmslos.
    try:
        if os.environ.get("KEIN_TURN_RIEGEL"):
            return 0
        roh = sys.stdin.buffer.read()          # binär lesen, sonst cp1252 auf Windows
        daten = json.loads(roh.decode("utf-8", "replace"))
        if daten.get("stop_hook_active"):
            return 0                            # schon einmal geblockt, jetzt durchlassen
        blocken, grund = beurteile(daten.get("last_assistant_message") or "")
        if not blocken:
            return 0
        # Beide belegten Blockierwege bedienen, siehe Modul-Docstring.
        print(json.dumps({"decision": "block", "reason": grund}, ensure_ascii=False))
        print(grund, file=sys.stderr)
        return 2
    except Exception:
        return 0


if __name__ == "__main__":
    sys.exit(main())
