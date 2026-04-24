# Entscheidungsbaum: Mein Test scheitert — was tun?

Systematischer Ablauf um ein Testproblem einzugrenzen. Starte oben,
folge den Verzweigungen.

## Der Baum

```
                  +----------------------------+
                  | Testrun-Status = "Geplant" |
                  | seit > 2 Minuten?          |
                  +------------+---------------+
                               |
                         Ja    |   Nein
                  +------------+----------+
                  v                       v
            Infra-Problem          Status normal erreicht?
            (Plugin-Queue          (Geplant -> Running ->
             haengt, Auth-          Abgeschlossen)
             Fehler, Sandbox       
             erschoepft)                  |
                                          v
                  +-------------------------------------+
                  |    jbe_teststatus nach dem Run      |
                  +-+----------+--------------+---------+
                    |          |              |
                Abgeschl.     Fehlge-      Wird noch ausgefuehrt
                    |        schlagen      (F5, warten)
                    v          |
                                                           
          +--------------------+       
          | Ueberhaupt          |
          | Ergebnisse (result- |
          | Sub-Grid leer)?     |
          +-+----------+--------+
            |          |
       Ja (0)      Nein
            |          |
            v          v
  Filter leer?   Ergebnisse vorhanden
  - Filter prüfen     |
  - enabled=true?      v
                +-----------------------+
                | Alle Ergebnisse       |
                | Bestanden?            |
                +-+----------+----------+
                  |          |
              Ja          Nein
                  |          |
                  v          v
               Test        Mindestens 1 Nicht-
            ist sauber.    Bestanden. Weiter unten.
```

## Bei mindestens einem Nicht-Bestanden

Öffne das betroffene `jbe_testrunresult` und schau auf `jbe_outcome`:

```
                +-----------------------------------+
                | jbe_outcome des Testergebnisses?  |
                +--+----------+-------------+-------+
                   |          |             |
                Failed     Error         Skipped
                   |          |             |
                   v          v             v
            Siehe A       Siehe B       Siehe C
```

### Fall A: Failed

```
  +-----------------------------------------+
  | Welche Assert-Steps im Steps-Tab        |
  | sind rot?                               |
  +---------------+-------------------------+
                  |
                  v
  +-----------------------------------------+
  | Lies Erwartet vs Tatsaechlich           |
  +-----+-----------------------------------+
        |
        v
  +-----------------------------------------+
  | Ursache einordnen:                      |
  | - Timing? (WaitForFieldValue nachlegen) |
  | - Test veraltet? (Erwartungswert neu)   |
  | - Format? (Decimal, String, Null)       |
  | - Lookup-Schreibweise?                  |
  +---------+-------------------------------+
            |
            v
  Testdaten anpassen, erneut laufen lassen.
```

### Fall B: Error

```
  +-------------------------------------------+
  | Welche Action-Step im Steps-Tab ist rot?  |
  +-------+-----------------------------------+
          |
          v
  +-------------------------------------------+
  | jbe_errormessage lesen                    |
  +-------+-----------------------------------+
          |
          v
  +-------------------------------------------+
  | HTTP 400?                                 |
  |  - Tippfehler in Feldname?                |
  |  - Lookup-Binding falsch?                 |
  |  - Pflichtfeld fehlt?                     |
  | HTTP 403?                                 |
  |  - Permission-Problem (Projekt-Owner)     |
  | HTTP 5xx?                                 |
  |  - Server-Problem, erneut versuchen       |
  | Plugin-Exception?                         |
  |  - Business-Plugin blockiert. Testdaten   |
  |    anpassen, ggf. eindeutigere Werte.     |
  | Alias nicht gefunden?                     |
  |  - Tippfehler oder vorheriger Create ist  |
  |    fehlgeschlagen                         |
  | WaitForFieldValue Timeout?                |
  |  - Plugin läuft nicht oder Wert anders.  |
  |    Timeout erhoehen oder Erwartung neu.   |
  +-------+-----------------------------------+
          |
          v
  Fix, erneut laufen lassen.
```

### Fall C: Skipped

```
  +-------------------------------------------+
  | Skipped = Testcase konnte nicht starten.  |
  | Meist JSON-Parse-Fehler.                  |
  +-------+-----------------------------------+
          |
          v
  +-------------------------------------------+
  | jbe_errormessage lesen (hat Line/Column)  |
  +-------+-----------------------------------+
          |
          v
  +-------------------------------------------+
  | JSON aus dem Feld kopieren                |
  | -> VS Code / JSONLint                     |
  | -> Fehler fixen                           |
  | -> Zurückkopieren, speichern             |
  +-------+-----------------------------------+
          |
          v
  Testcase aktualisieren, neuer Run.
```

## Die Kurzform als Checkliste

```
[ ] Testrun-Status = Abgeschlossen  (sonst warten / Infra-Problem)
[ ] Ergebnis-Sub-Grid hat Eintraege (sonst Filter/Enabled)
[ ] outcome des fehlgeschlagenen Ergebnisses ist:
    [ ] Failed  -> siehe Assert-Detail im Steps-Tab
    [ ] Error   -> Error-Message im Step
    [ ] Skipped -> JSON validieren
[ ] Erwartung vs Realitaet abgeglichen
[ ] Fix eingespielt (Testfall oder Code)
[ ] Zweiter Run: OK?
```

## Der letzte Rettungsanker

Wenn nichts hilft, zwei Gegenproben:

1. **Einen bekannten grünen Test auf derselben Umgebung starten.** Wenn
   der auch rot wird: Infra-Problem, kein Test-Problem. Projekt-Owner
   fragen.

2. **Den fehlgeschlagenen Test auf einer anderen Umgebung laufen
   lassen** (falls verfuegbar). Wenn dort grün: umgebungs-spezifisches
   Problem.
