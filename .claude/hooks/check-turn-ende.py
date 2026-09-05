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
    r"(mail|e-?mail|anschreiben|nachricht an|ticket-?kommentar|jira|devops|"
    # Teams nur als WEG nach außen, nicht als Produktname. Die blanke Alternative „teams"
    # bremste den Riegel an jedem technischen Bericht über den Dienst - gemessen 108
    # Antworten, in denen allein sie den Block verhinderte, darunter Sätze über den
    # CDP-Port, den Teams-Prozess und die Teams-UI; sie traf sogar „TeamViewer". Ein
    # Beitrag in einen Kanal bleibt rot (ADR-2026-09-05-004350).
    r"teams-nachricht|teams-kanal|in teams\b|über teams|via teams|"
    r"versenden|verschicken|rausschicken|hinausgehen|absenden|"
    r"deploy|deployment|prod\b|produktiv|cutover|"
    # „beitrag an <Person>" kam mit derselben Messung dazu: dieser Grenzfall war bis dahin
    # allein über das zu grobe „teams" gedeckt und wäre nach dessen Präzisierung offen
    # gewesen. Wer eine Alternative entschärft, prüft, was nur über sie gedeckt war.
    r"text an \w|entwurf an \w|schreiben an \w|antwort an \w|beitrag an \w|"
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
    r"ich gehe .{0,20}an\b|ich sehe\b|ich hole\b|ich klär|ich nehme\b|"
    # Die Ich-Form mit „jetzt" ist die Ankündigung als solche, unabhängig vom Verb. Sie
    # kam dazu, nachdem der Riegel am 04.09.2026 in LMApp „Ich erfasse jetzt den Kanal ab
    # dem 28.08. vollständig ... Melde mich, wenn das steht." durchgelassen hat: keines
    # der Muster kannte eines der vier angekündigten Verben. Einzelne Verben zu sammeln
    # wäre die Bewegung, gegen die ADR-2026-09-03-113147 gebaut ist; diese Form fängt die
    # Ankündigung strukturell. Die Gegenrichtung „jetzt \w+e ich" steht bewusst NICHT hier:
    # sie blockte am Bestand „Ab jetzt schreibe ich nicht mehr in state.md", also eine
    # Verneinung am Übergabeschluss (ADR-2026-09-04-170824).
    r"ich \w+e jetzt\b|"
    # Die Ich-Form im Präsens, GENERISCH. Drei Einzelfälle in drei Tagen (erfassen,
    # schreiben, sortieren) haben gezeigt, dass eine Verbliste die Ankündigung nicht
    # fängt: sie ist endlich, die Sprache nicht. Gemessen tragen 2.772 Antworten eine
    # Ich-Form mit einem Verb, das die Wortliste nicht kennt, verteilt auf 286 Verben.
    # Das ist dieselbe Ausweichbewegung, gegen die ADR-2026-09-03-113147 gebaut wurde,
    # nur eine Ebene höher: dort wich die Satzform aus, hier das Vokabular.
    #
    # Der Ausschluss der Hilfs- und Zustandsverben ist Teil des Entscheids, nicht
    # Umsetzungsdetail: sie enden ebenfalls auf -e, und ohne ihn würde „Ich habe die
    # Messung abgeschlossen" als Ankündigung gelten. Was die Form NICHT leistet, ist die
    # Unterscheidung von „ich tue etwas Konkretes" und „ich frage, was du willst" - dafür
    # bleibt ERLAUBT der Filter (ADR-2026-09-05-011212).
    r"\bich\s+(?!(?:habe|hatte|bin|war|kann|konnte|muss|musste|will|wollte|soll|sollte|"
    r"darf|durfte|werde|wurde|wäre|hätte|würde|möchte|denke|glaube|finde|meine|sehe|"
    r"verstehe|danke|freue|hoffe|vermute|melde|sage|antworte|frage|warte|bleibe|stehe|"
    r"lasse|bitte)\b)[a-zäöüß]{3,}e\b)",
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
    # Zwischen „warte" und „auf" darf ein Adverb stehen. Ohne das kippte die am
    # 04.09.2026 eingeführte ANSAGE-Form „ich \w+e jetzt" genau die Fälle in einen Block,
    # die diese Ausnahme schützen soll: „Ich warte jetzt auf das Urteil des Inspektors und
    # ziehe danach Stand und Journal nach." griff über „ich warte jetzt", während
    # „warte (noch )?auf" nur das eine Wort „noch" duldete. Fünf reale Fälle am Bestand,
    # alle Sitzungen, die auf ein Inspektor-Urteil oder eine andere Sitzung warten; vom
    # Inspektor gefunden, nicht vom Erbauer (ADR-2026-09-04-170824, Nachtrag). Das Warten
    # auf Jürgen bleibt unberührt: „Ich warte jetzt auf deine Freigabe" blockt weiterhin,
    # weil der Lookahead dahinter greift.
    r"warte (noch |jetzt |gerade |nun |zunächst |erst )*auf "
    r"(?!(?:(?:die|der|das|den|dem|eine|einen|einer|"
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
    # WORTSTÄMME, nicht Vollformen. Die Liste stand bis zum 05.09.2026 auf Infinitiven
    # („schreiben", „messen", „committen") und traf damit keine einzige finite Verbform -
    # also ausgerechnet die erste Person Singular, in der eine Ankündigung fast immer
    # formuliert ist. „Ich schreibe das jetzt in den Stand und die Runlist und committe."
    # fand hier nichts. Gemessen waren 3.658 Antworten, in denen ANSAGE oder FRAGE traf
    # und ERLAUBT nichts fand; die Umstellung blockt 1.916 zusätzlich bei null Verlust
    # (ADR-2026-09-05-004350).
    r"(les|liest|ansehen|anschau|sicht|nachles|"
    r"mess|misst|zähl|prüf|nachmess|nachprüf|verifizier|"
    # „(?<!ver)suche" statt „\bsuche": die Wortgrenze bräche „Ursachensuche", ein realer
    # Fall aus den Transkripten. Ausgenommen wird nur „versuche" (Inspektor, Runde 2).
    r"(?<!ver)suche|suchen|durchsuch|grepp|kartier|erheb|auswert|analysier|"
    # „bau" braucht seine Endungen ausgeschrieben, sonst trifft der blanke Stamm jedes
    # „Baustein", „Baum" und „Bauart".
    r"bau(e|en|st|t)?\b|umbau|schreib|anleg|ergänz|nachzieh|korrigier|beheb|"
    r"aufbereit|entwerf|dokumentier|committ|adr\b|nachweis|test|trockenlauf|"
    r"weiter|fortfahr|fortsetz|dranbleib|angeh|vornehm|in angriff|"
    # Substantivformen: „die Auswertung", „die Prüfung" benennen dieselbe Tätigkeit wie
    # das Verb, und Antworten formulieren so mindestens ebenso oft.
    r"auswertung|prüfung|messung|analyse|sichtung|recherche|bereinigung|umbau|"
    r"kartierung|erhebung|zählung|korrektur|aufbereitung|"
    # Die Tätigkeiten des LMApp-Falls vom 04.09.2026 plus die zwei aus demselben
    # Bedeutungsfeld, die in der Leseliste von `messung/erkennungsluecke.py` vorkamen.
    # Bewusst NICHT aufgenommen sind „festhalt" und „durchgehen": beide erzeugten am
    # Bestand Fehltreffer auf echten Rückfragen an Jürgen („Soll ich sonst noch etwas
    # festhalten?", eine A/B-Frage mit „durchgehen"). Gemessen an den 1.084 Turn-Enden,
    # also der letzten Antwort je Sitzung: die verworfene Fassung blockte dort vier
    # zusätzlich, davon drei falsch, diese hier genau einen. Über die Menge, die der Hook
    # wirklich sieht - jede Antwort ohne Werkzeugaufruf, rund 36.900 - sind es 149
    # zusätzliche Blocks bei null Verlust; die Turn-Enden allein sind zu eng, um die
    # Fehltrefferseite zu zeigen (ADR-2026-09-04-170824, Nachtrag zu Kriterium 3).
    r"erfass|zuordn|ordne .{0,20}zu\b|einarbeit|nacharbeit|"
    # Die Spitze der Kandidatenliste, systematisch über den Bestand erhoben statt am
    # Einzelfall geraten: 2.772 Antworten, 286 Verben, häufigste `zieh` (282), `schau`
    # (176), `start` (103), `setz` (94). Wer hier ergänzt, erhebt zuerst neu
    # (`messung/erkennungsluecke.py`), statt das nächste Einzelwort nachzutragen.
    #
    # ERLAUBT ist seit ADR-2026-09-05-011212 die EINZIGE Stelle, die eine angekündigte
    # Tätigkeit von einer Frage nach dem Thema trennt - ANSAGE erkennt die Ich-Form
    # generisch. Eine Bauart ohne diesen Filter wurde gemessen und verworfen: sie blockte
    # 43 Prozent aller Antworten, darunter jeden Sessionstart („Bereit. Was soll ich
    # tun?").
    r"zieh|schau|start|setz|nutz|trag|fahr|häng|arbeit|reparier|beginn|klär|schließ|"
    r"ersetz|sortier|trenn|räum|pfleg|stell .{0,15}(fest|her|um)|"
    r"nehme .{0,12}(zurück|vor|mit)|führe .{0,12}(aus|zusammen|durch))",
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
        # Der Fall, den Jürgen am 04.09.2026 aus LMApp gezeigt hat. Der Riegel lag dort
        # byte-gleich und hat ihn durchgelassen: keines der fünf Muster kannte eines der
        # vier angekündigten Verben (ADR-2026-09-04-170824).
        ("Ich erfasse jetzt den Kanal ab dem 28.08. vollständig, mit den Antwort-Threads "
         "und den Bildern, ordne jede Meldung ihrem Stand zu und halte den neuen "
         "Zugangsweg im Skill fest. Melde mich, wenn das steht.", True),
        # Die drei am Bestand gemessenen Fehltreffer der ersten, weiteren Fassung. Sie
        # stehen hier als Gegenfälle, damit die verworfenen Kandidaten nicht später
        # unbemerkt zurückkommen: „jetzt \w+e ich" (eine Verneinung am Übergabeschluss),
        # „festhalt" und „durchgehen" (beide echte Rückfragen an Jürgen).
        ("Ab jetzt schreibe ich nicht mehr in `state.md` oder das Handover dieses "
         "Vorgangs. Die Folge-Session startest du über den Chip.", False),
        ("Soll ich für die Folge-Session sonst noch etwas festhalten?", False),
        ("Soll ich dir den Granular-Token-Weg Schritt für Schritt durchgehen, oder "
         "willst du lieber den klassischen Weg?", False),
        # Das Minimalpaar zum Nachtrag vom 04.09.2026: ein eingeschobenes Adverb darf die
        # Warte-Ausnahme nicht kippen, und es darf zugleich das Warten auf Jürgen nicht
        # entschärfen. Beide Richtungen als eigener Fall, sonst bindet keiner von beiden.
        ("Ich warte jetzt auf den Lauf. Als Nächstes prüfe ich sein Ergebnis.", False),
        ("Ich warte jetzt auf das Urteil des Inspektors und ziehe danach Stand und "
         "Journal des Vorgangs nach.", False),
        ("Ich warte jetzt auf deine Freigabe. Als Nächstes prüfe ich die Messung.", True),
        # Der Riverty-Fall vom 04.09.2026, den Jürgen mit „schon wieder" gezeigt hat. Er
        # lief aus zwei unabhängigen Gründen durch: ERLAUBT kannte nur Infinitive, und ROT
        # griff auf „Teams" im Absatz davor. Der Positivfall führt beide Absätze, damit er
        # beide Ursachen bindet (ADR-2026-09-05-004350).
        ("Übrig bleibt allein der FAQ-Connector. Office 365, Excel Online und Teams haben "
         "null Flow-Verbraucher und sind nicht anzulegen.\n\n"
         "Ich schreibe das jetzt in den Stand und die Runlist und committe.", True),
        # Teams als Weg nach außen bleibt rot, in beiden gebräuchlichen Formen.
        ("Ich stelle den Befund als Beitrag in den Teams-Kanal.", False),
        ("Ich schicke ihm die Zusammenfassung über Teams.", False),
        # Der Grenzfall, der vorher allein über das zu grobe „teams" gedeckt war.
        ("Soll ich den kurzen Beitrag an Marc entwerfen?", False),
        # Der dritte Anlassfall, 05.09.2026 aus Riverty. Weder ANSAGE noch ERLAUBT trafen:
        # „ich \w+e jetzt" scheitert an dem „das" zwischen Verb und Adverb, und `sortier`
        # stand nicht in der Liste (ADR-2026-09-05-011212).
        ("Ich sortiere das jetzt sauber: erledigt-aber-nicht-markiert von echt offen "
         "trennen, und je Schritt sagen, ob es an uns, an einem Dritten oder an einer "
         "Entscheidung hängt.", True),
        # Die Gegenfälle zum Hilfsverb-Ausschluss. Ohne ihn würde die generische Ich-Form
        # jeden Abschlussbericht blocken; diese beiden binden das.
        ("Ich habe die Messung abgeschlossen und den Befund abgelegt.", False),
        ("Ich melde mich, sobald der Lauf durch ist.", False),
        # Sessionstart: die Sitzung hat keine Arbeit, sondern fragt nach dem Thema. Hier
        # trennt allein ERLAUBT - eine Bauart ohne diesen Filter blockte genau das.
        ("Bereit. Was soll ich tun?", False),
        ("Hallo Jürgen. Womit soll ich anfangen?", False),
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
