# Testfall in der App anlegen

Einen neuen Testfall (`jbe_testcase`-Record) erstellen, JSON einfuegen,
speichern. Der komplette Vorgang dauert 2 Minuten.

## Vorbereitung: JSON fertig haben

Schreibe dein JSON zuerst **außerhalb** der App, z.B. in VS Code. Gründe:

- Syntax-Highlighting erkennt Fehler (fehlende Kommas, Tippfehler)
- Du kannst es zwischenspeichern und später nochmal einfuegen
- Die Textbox in D365 hat kein Auto-Format

Als Vorlage: [Rezepte](../02-testfall-schreiben/07-rezepte.md).

## Schritt 1: In die App navigieren

Öffne in Dynamics die App **D365 Test Center**. Im linken Menue findest
du:

```
+-- Navigation -----------------+
|                               |
|   Dashboard                   |
|                               |
|   D365 Test Center            |
|     > Testfälle              |
|     > Testläufe              |
|     > Testergebnisse          |
|     > Testschritte            |
|                               |
+-------------------------------+
```

Klicke auf **Testfälle**.

## Schritt 2: Neuen Testfall anlegen

Oben rechts findest du **+ Neu**.

```
+-- Testfälle -----------------------------------------+
|                                                       |
|   [+ Neu]  [Aktualisieren]  [Löschen]  Suche: [   ]  |
|                                                       |
|   +--------+--------------------+---------+--------+  |
|   | TestID | Titel              | Enabled | ...    |  |
|   +--------+--------------------+---------+--------+  |
|   | QS-01  | Account anlegen... | Ja      |        |  |
|   | QS-02  | Contact an Acco... | Ja      |        |  |
|   | QS-03  | Lead qualifizi...  | Ja      |        |  |
|   +--------+--------------------+---------+--------+  |
|                                                       |
+-------------------------------------------------------+
```

## Schritt 3: Formular ausfüllen

```
+-- Neuer Testfall ----------------------------------------------+
|                                                                |
|  Test-ID     * [ REZ-A-01                                  ]   |
|  Titel       * [ Account: Create, Update, Verify           ]   |
|  Aktiviert     [x] ja                                          |
|  Tags          [ rezept, standard                          ]   |
|  User Stories  [ DYN-1234                                  ]   |
|                                                                |
|  Definition *                                                  |
|  +----------------------------------------------------------+  |
|  | {                                                        |  |
|  |   "testId": "REZ-A-01",                                  |  |
|  |   "title":  "Account: Create, Update, Verify",           |  |
|  |   "enabled": true,                                       |  |
|  |   "steps": [                                             |  |
|  |     ...                                                  |  |
|  |   ]                                                      |  |
|  | }                                                        |  |
|  +----------------------------------------------------------+  |
|                                                                |
|  [Speichern]  [Speichern und Schließen]  [Abbrechen]          |
+----------------------------------------------------------------+
```

**Pflichtfelder:**

- **Test-ID:** derselbe Wert wie `testId` im JSON. Bei Abweichung nimmt
  die Engine den Wert aus dem JSON.
- **Titel:** Anzeigename, wird auch als Primary Name verwendet.
- **Definition:** das komplette JSON. **Paste mit Strg+V** — die Textbox
  akzeptiert beliebige Mehrzeilentexte.

**Optional aber empfohlen:**

- **Aktiviert** anhaken: sonst läuft der Test beim Run nicht mit.
- **Tags:** komma-getrennt, hilft später beim Filtern (`tag:rezept`).
- **User Stories:** Ticket-Referenzen.

## Schritt 4: Speichern

Klick **Speichern und Schließen**. D365 validiert:

- Pflichtfelder ausgefüllt? -> OK
- JSON-Syntax? -> **wird NICHT geprüft** beim Speichern. Syntaxfehler
  merkst du erst beim Run-Versuch (der Test wird als "Skipped" markiert
  mit Parse-Fehler).

Wenn beim Speichern ein Fehler auftaucht:

```
+-- Fehler -----------------------------------------+
|                                                   |
|  Ein Datensatz mit TestID 'REZ-A-01' existiert    |
|  bereits.                                         |
|                                                   |
|  [OK]                                             |
+---------------------------------------------------+
```

Dann hat diese ID schon jemand angelegt. Aendere die TestID (z.B.
`REZ-A-01-v2`).

## Schritt 5: Verifizieren

Nach dem Speichern bist du zurück auf der Testfälle-Liste. Suche nach
deiner TestID:

```
+-- Testfälle -----------------------------------------+
|   Suche: [ REZ-A-01                          ]        |
|                                                       |
|   +----------+----------------------+---------+----+  |
|   | TestID   | Titel                | Enabled |    |  |
|   +----------+----------------------+---------+----+  |
|   | REZ-A-01 | Account: Create, ... | Ja      |    |  |
|   +----------+----------------------+---------+----+  |
+-------------------------------------------------------+
```

Fertig.

## JSON später ändern

Du kannst einen bestehenden Testfall jederzeit editieren: Zeile anklicken,
Definition-Feld ändern, speichern. Änderungen gelten ab dem **nächsten**
Testlauf.

Laufende Testläufe lesen den Testfall genau **einmal** am Start; eine
Änderung während des Runs wirkt sich nicht mehr aus.

## Bulk-Import / Export

Für viele Tests gleichzeitig steht keine UI-Funktion bereit. Das macht
der Projekt-Owner über Werkzeuge (CLI/Skripte). Du arbeitest pro Test
einzeln.

Weiter mit [Testlauf starten](02-testlauf-starten.md).
