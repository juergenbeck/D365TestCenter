# Testdokumentation mit Belegbildern je Schritt (HTML und DOCX)

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Formular- und Feldnamen) steht in der `PROJEKT-KONTEXT.md`
> des jeweiligen Repos.

Grundlage: ADR 2026-09-16-1957. Nur im CLI-Pfad, weil nur dort `BrowserAction` läuft.

## Wofür

Ein bestandener UI-Lauf soll als Nachweis an ein Ticket gehen. Das Test Center legt von sich aus nur im
Fehlerfall ein Bild ab, und lose Bilder taugen dafür nicht: Man sieht ihnen nicht an, zu welchem Schritt
sie gehören und was sie belegen sollen. Mit `--evidence-dir` entsteht stattdessen je Lauf eine
Dokumentation, gegliedert nach den Schritten des manuellen Testfalls, mit einem Beleg je geprüfter
Erwartung und den gelesenen Istwerten neben dem Bild.

Heraus kommen vier Dinge im angegebenen Ordner: `testdokumentation.html`, `testdokumentation.docx`,
`evidence.json` (die Rohdaten samt Lauf-Kennung) und der Ordner `bilder/`. Ans Ticket gehört das HTML
oder das DOCX, nicht die Einzelbilder.

## Aufbau eines dokumentierenden Testfalls

**Schritte des manuellen Falls als `docSteps`** am Testfall: je Schritt `number`, ein kurzer `title`,
`action` (was der Tester tut) und `expected` (was er erwartet), in Klartext ohne HTML. Stammt der Fall aus
einer Testverwaltung, werden Durchführung und Erwartung von dort übernommen, damit beide Spuren dieselbe
Sprache sprechen.

**Zuordnung der technischen Schritte:** Jeder Schritt verweist per `docStep` auf eine dieser Nummern;
folgende Schritte erben die zuletzt gesetzte. Schritte vor der ersten Nummer gelten als Vorbereitung.
`docStep: 0` erklärt einen Schritt ausdrücklich zur Vorbereitung. Ein Erzeuger des Packs prüft sinnvoll
selbst, dass jeder manuelle Schritt mindestens einen technischen Schritt hat.

**Ein Beleg je geprüfter Erwartung**, nicht je Bildschirmausschnitt:

```json
{ "stepNumber": 17, "action": "BrowserAction", "operation": "screenshot",
  "name": "lead-nach-nachzug", "docStep": 6,
  "caption": "Lead nach dem Nachzug: Kontakt, Vor- und Nachname und E-Mail entsprechen dem Kontakt",
  "highlightFields": ["parentcontactid", "firstname", "lastname", "emailaddress1"] }
```

- `caption` sagt, was das Bild belegt. Sie steht als Bildunterschrift in der Dokumentation.
- `highlightFields` nennt die geprüften Felder mit ihrem logischen Namen. Sie werden umrandet, ihre
  Beschriftung und ihr angezeigter Wert werden über die Formular-API gelesen und als Tabelle neben das Bild
  gestellt. Aufgenommen werden nur die Formularabschnitte dieser Felder, nicht die ganze Seite.
- `clip` steuert den Ausschnitt: `section` (Standard bei `highlightFields`), `viewport` oder `page`.
- Ein Feld, das auf dem Formular nicht sichtbar ist, erzeugt einen Hinweis, keinen Fehler.

## Eine Anzahl belegen: Unterliste statt Zusicherung

Eine Erwartung wie „es gibt genau einen Datensatz" kann ein Formularbild nicht zeigen. Dafür nimmt
`highlightFields` auch den Namen eines Unterlisten-Steuerelements: Die Liste wird umrandet, und als Istwert
steht die Zahl ihrer Datensätze (`getTotalRecordCount()`, ersatzweise die geladenen Zeilen).

Zwei Bedingungen, sonst ist die Liste nicht im Dokument:

1. **Der Reiter muss offen sein.** Eine Unterliste auf einem nicht geöffneten Reiter wird gar nicht
   gerendert. Ein `evaluate`-Schritt davor öffnet ihn:
   `Xrm.Page.getControl('<control>').getParent().getParent().setFocus()`.
2. **Die Liste muss geladen sein.** Nach dem Öffnen so lange warten, bis die Anzahl dem entspricht, was
   eine unabhängige Abfrage liefert (`Xrm.WebApi.retrieveMultipleRecords` mit demselben Filter); erst dann
   das Bild aufnehmen. Der Vergleich ist zugleich die eigentliche Prüfung, das Bild ist der Beleg dazu.

Im DOM heißt eine Unterliste `dataSetRoot_<control>`; die Beschriftung des Istwerts nimmt die Überschrift
des Formularabschnitts, weil die Beschriftung des Steuerelements davon abweichen kann.

## Einblendungen

Vor jeder Aufnahme blendet das Test Center bekannte Einblendungen der Oberfläche per CSS aus und stellt sie
danach wieder her; geklickt wird nichts, der Datensatz bleibt unberührt. Abgedeckt sind die
Copilot-Datensatzzusammenfassung, der Knopf der Formularunterstützung und die Microsoft-Hinweisfenster
(Fluent v9 `.fui-TeachingPopoverSurface`, Fluent v8 `.ms-TeachingBubble`). Eigene Selektoren kommen über
`hideSelectors`; `hideOverlays: false` schaltet die eingebaute Liste ab.

Das Ausblenden läuft auch unmittelbar vor jeder einzelnen Aufnahme, weil eine Einblendung erst während der
Wartezeit auf die Felder erscheinen kann. Was ausgeblendet wurde, steht je Beleg in `evidence.json`.

Kennungen neuer Einblendungen findet man im DOM-Schnappschuss des Playwright-Mitschnitts über ihren
sichtbaren Text; sie tragen in der Regel kein `data-id`.

## Lauf und Neuerzeugung

```
D365TestCenter.Cli run --org <url> --browser-state <state.json> --filter <fälle> --evidence-dir <ordner>
D365TestCenter.Cli evidence-report --evidence-dir <ordner>
```

`evidence-report` erzeugt beide Dokumente aus derselben `evidence.json` erneut, ohne den Lauf zu
wiederholen; die HTML ist dabei byte-gleich zur im Lauf erzeugten. Ein Lauf ohne `--evidence-dir` verhält
sich wie bisher und legt keinen Ordner an.

## Fallen, gemessen an einer Model-Driven-App

- **Gesperrte oder ausgeblendete Felder** setzt ein Fall über die Formular-API
  (`Xrm.Page.getAttribute(...).setValue(...)`, danach `Xrm.Page.data.save()`). Das bedient dieselben
  Formularereignisse wie ein Tester. Die Abweichung vom manuellen Fall gehört in die Beschreibung.
- **`Xrm.Page.data.save()` lehnt mit einem Objekt ab, nicht mit einem Fehlerobjekt.** Ohne eigenes
  `try/catch` steht im Protokoll nur „Object". Das Objekt trägt `errorCode` und `message`; nützlich ist
  zusätzlich die Liste der leeren Pflichtfelder.
- **„Formular geladen" heißt nicht „Felder registriert".** `Xrm.Page.data.entity.getEntityName()` meldet die
  Entität schon, während `getAttribute(<feld>)` noch nichts liefert. Die Bereitschaftsprüfung wartet deshalb
  auf das erste benötigte Feld.
- **Ein Formular kann geladen und trotzdem leer sein.** Gemessen: richtige Entität, aber
  `attributes.get()` über eine Minute lang leer, teils mit einem Dialog „Skriptfehler". Abhilfe im Pack:
  nach dem Öffnen begrenzt auf das erste Feld warten und sonst einmal `location.reload()` auslösen, danach
  kurz warten. Die Bereitschaftsmeldung nennt sinnvollerweise Formular-Kennung, Feldanzahl und offene
  Dialogtexte.
- **`Xrm.Navigation.openForm` taugt nicht zum Seitenwechsel in einem `evaluate`**, sein Promise hängt am
  Formular. Verlässlich ist `setTimeout(() => location.assign(<adresse>), 200); return 'ok';` plus ein
  `delay`. Kennungen zwischen den Seiten hält `localStorage` (gleiche Herkunft).
- **Ein ganzseitiges Bild zeigt im Formular nur den oberen Teil**, weil der Inhalt in einem eigenen
  Scrollbereich liegt. Das ist der Grund für `clip: section` und das Scrollen zum Feld.
- **Ein abgelaufener Anmeldezustand** zeigt sich als sofortiger Fehler aller Fälle mit „Storage-state
  expired". Dann `ui-setup` erneut laufen lassen; die Lebensdauer liegt bei rund einem Tag.

## Wenn ein Lauf zu hängen scheint

Ein `evaluate` endet nach `timeoutSeconds` (Standard 120) mit einer Beschreibung des Seitenzustands
(Adresse, Anzahl Frames, offene Dialoge, ob die Seite noch antwortet). Ein Skript, das selbst länger
wartet, braucht einen höheren Wert.

Ist `value` gesetzt und liefert das Hauptfenster einen anderen Wert, wird der Ausdruck in jedem iFrame
wiederholt (je Frame höchstens 10 Sekunden), damit Bibliotheken in einem eingebetteten Frame gefunden
werden. Für Skripte mit Nebenwirkungen oder langen Wartezeiten gehört `frameFallback: false` an den
Schritt; sonst laufen sie je Frame erneut. Genau das war einmal die Ursache eines scheinbaren Hängers: ein
Bereitschaftsskript meldete nach 60 Sekunden einen Befund und wurde danach in 16 Frames wiederholt, was den
Fall 17 Minuten festhielt.

**Bei der Ursachensuche zuerst den Mitschnitt lesen**, nicht die Oberfläche raten: In `trace.zip` stehen in
`trace.trace` zu jedem Aufruf ein `before` und ein `after` mit Zeitstempeln, daraus ergibt sich die Dauer je
`Frame.evaluateExpression`. Der Mitschnitt wird erst beim Beenden geschrieben, ein abgeschossener Lauf
hinterlässt keinen; native Browserdialoge stehen als `browser-dialog` im Protokoll.
