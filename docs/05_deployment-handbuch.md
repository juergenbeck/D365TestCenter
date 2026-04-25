# Deployment-Handbuch: Integration Test Center

Schritt-für-Schritt-Anleitung zur Einrichtung des Integration Test Centers in einer Dynamics 365 / Dataverse-Umgebung.

## Voraussetzungen

- **Dynamics 365 / Dataverse-Umgebung** (DEV, TEST oder DATATEST)
- **Security Role** mit Lese- und Schreibrechten auf die drei ITT-Entities (siehe Schritt 5)
- **Solution-Publisher** mit eigenem Prefix (Standard: `itt`)
- **Zugriff auf das Maker Portal** (make.powerapps.com) oder alternativ PowerShell mit Dataverse Web API
- **Browser** mit aktivem CRM-Login (für die Web Resource)

## Schritt 1: Publisher und Prefix konfigurieren

Im Quellcode der HTML-Datei (`jbe_testcenter.html`) befindet sich ein `CONFIG`-Block, der den Publisher-Prefix und die Entity-Namen definiert.

### CONFIG-Block anpassen

```javascript
const CONFIG = {
    prefix: "itt",                    // Publisher-Prefix (ohne Unterstrich)
    optionSetBase: 100000000,         // Basis für OptionSet-Werte
    entities: {
        testcase: "jbe_testcase",
        testrun: "jbe_testrun",
        testrunresult: "jbe_testrunresult"
    },
    // ... (Felder und OptionSets)
};
```

### Anpassung bei anderem Publisher-Prefix

Falls ein anderer Publisher verwendet wird (z.B. `xyz` statt `itt`):

1. Den Wert `prefix` auf den eigenen Prefix ändern (z.B. `"xyz"`).
2. Alle Entity-Namen unter `entities` anpassen (z.B. `"xyz_testcase"`).
3. Alle Feld-Schema-Namen unter `fields` anpassen (z.B. `"xyz_testid"`).
4. Die OptionSet-Basiswerte unter `optionSets` beibehalten oder an die eigenen OptionSet-Definitionen anpassen.
5. Den Custom-API-Namen unter `actions.runTests` anpassen (z.B. `"xyz_RunIntegrationTests"`).

## Schritt 2: Entities anlegen

Drei Custom Entities müssen in Dataverse erstellt werden. Dies kann manuell im Maker Portal oder per PowerShell-Skript (`Create-TestingEntities.ps1`) erfolgen.

### Entity 1: Testfall (`jbe_testcase`)

| Eigenschaft | Wert |
|-------------|------|
| Anzeigename | Testfall |
| Schema-Name | `jbe_testcase` |
| Pluralname | Testfälle |
| EntitySet-Name | `jbe_testcases` |
| Primäres Namensfeld | `jbe_name` (AutoNumber empfohlen: `TC-{SEQNUM:8}`) |

**Felder:**

| Schema-Name | Anzeigename | Typ | Pflicht | Beschreibung |
|-------------|-------------|-----|---------|--------------|
| `jbe_testid` | Test ID | String (100) | Pflicht (ApplicationRequired) | Eindeutige Test-ID (z.B. TC01, BTC01) |
| `jbe_title` | Titel | String (300) | Pflicht (ApplicationRequired) | Beschreibender Titel |
| `jbe_category` | Kategorie | OptionSet (Picklist) | Empfohlen | Testkategorie (siehe OptionSets) |
| `jbe_tags` | Tags | String (500) | Optional | Kommagetrennte Tags |
| `jbe_userstories` | User Stories | String (500) | Optional | Kommagetrennte Jira-Keys |
| `jbe_enabled` | Aktiv | Boolean | Optional | Testfall aktiv/deaktiviert (Standard: true) |
| `jbe_definitionjson` | Definition (JSON) | Memo (Multiline) | Optional | JSON-Definition des Testfalls |

**Alternate Key:** `jbe_testid` (ermöglicht Upsert bei Import).

### Entity 2: Testlauf (`jbe_testrun`)

| Eigenschaft | Wert |
|-------------|------|
| Anzeigename | Testlauf |
| Schema-Name | `jbe_testrun` |
| Pluralname | Testläufe |
| EntitySet-Name | `jbe_testruns` |
| Primäres Namensfeld | `jbe_name` (AutoNumber empfohlen: `RUN-{SEQNUM:8}`) |

**Felder:**

| Schema-Name | Anzeigename | Typ | Pflicht | Beschreibung |
|-------------|-------------|-----|---------|--------------|
| `jbe_teststatus` | Status | OptionSet (Picklist) | Optional | Teststatus (siehe OptionSets) |
| `jbe_passed` | Bestanden | Integer | Optional | Anzahl bestandener Tests |
| `jbe_failed` | Fehlgeschlagen | Integer | Optional | Anzahl fehlgeschlagener Tests |
| `jbe_total` | Gesamt | Integer | Optional | Gesamtanzahl Tests im Lauf |
| `jbe_startedon` | Gestartet | DateTime | Optional | Startzeitpunkt |
| `jbe_completedon` | Abgeschlossen | DateTime | Optional | Endzeitpunkt |
| `jbe_testcasefilter` | Testfall-Filter | String (500) | Optional | Angewendeter Filter (z.B. `"*"`, `"story:DYN-1234"`) |
| `jbe_testsummary` | Zusammenfassung | Memo (Multiline) | Optional | Textuelle Zusammenfassung |
| `jbe_fulllog` | Vollständiges Log | Memo (Multiline) | Optional | Komplettes Ausführungslog |
| `jbe_testresult_json` | Ergebnis (JSON) | Memo (Multiline) | Optional | Strukturiertes Ergebnis als JSON |

### Entity 3: Testergebnis (`jbe_testrunresult`)

| Eigenschaft | Wert |
|-------------|------|
| Anzeigename | Testergebnis |
| Schema-Name | `jbe_testrunresult` |
| Pluralname | Testergebnisse |
| EntitySet-Name | `jbe_testrunresults` |
| Primäres Namensfeld | `jbe_name` (AutoNumber empfohlen: `RES-{SEQNUM:8}`) |

**Felder:**

| Schema-Name | Anzeigename | Typ | Pflicht | Beschreibung |
|-------------|-------------|-----|---------|--------------|
| `jbe_testrunid` | Testlauf | Lookup auf `jbe_testrun` | Optional | Zugehöriger Testlauf |
| `jbe_testcaseid` | Testfall | Lookup auf `jbe_testcase` | Optional | Zugehöriger Testfall |
| `jbe_testid` | Test ID | String (100) | Optional | Test-ID (redundant für schnelle Abfragen) |
| `jbe_outcome` | Ergebnis | OptionSet (Picklist) | Optional | Testergebnis (siehe OptionSets) |
| `jbe_durationms` | Dauer (ms) | Integer | Optional | Ausführungsdauer in Millisekunden |
| `jbe_errormessage` | Fehlermeldung | Memo (Multiline) | Optional | Fehlerbeschreibung bei Failed/Error |
| `jbe_assertionresults` | Assertion-Ergebnisse | Memo (Multiline) | Optional | JSON-Array der einzelnen Assertion-Ergebnisse |

### OptionSets

Die folgenden globalen OptionSets müssen angelegt werden (Basiswert: `100000000`):

**jbe_teststatus (Teststatus):**

| Wert | Label |
|------|-------|
| 100000000 | Geplant |
| 100000001 | Läuft |
| 100000002 | Abgeschlossen |
| 100000003 | Fehler |

**jbe_testoutcome (Testergebnis):**

| Wert | Label |
|------|-------|
| 100000000 | Passed |
| 100000001 | Failed |
| 100000002 | Error |
| 100000003 | Skipped |
| 100000004 | NotImplemented |

**jbe_testcategory (Testkategorie):**

| Wert | Label |
|------|-------|
| 100000000 | UpdateSource |
| 100000001 | CreateSource |
| 100000002 | PISA |
| 100000003 | MultiSource |
| 100000004 | AdditionalFields |
| 100000005 | Bridge |
| 100000006 | Merge |
| 100000007 | Recompute |
| 100000008 | ErrorInjection |

**Wichtig:** Die OptionSet-Werte müssen exakt mit den im JavaScript definierten Werten übereinstimmen. Bei abweichenden Werten zeigt die UI falsche Labels an.

## Schritt 3: Web Resource hochladen

### 3.1 Web Resource erstellen

1. Im Maker Portal die Solution öffnen (z.B. `jbe_testing`).
2. Neue Web Resource hinzufügen:
   - **Anzeigename:** Integration Test Center
   - **Name (Schema):** `jbe_testcenter` (wird zu `jbe_/jbe_testcenter.html`)
   - **Typ:** Webseite (HTML)
   - **Inhalt:** Die Datei `jbe_testcenter.html` hochladen.
3. Speichern und publizieren.

### 3.2 Sitemap-Eintrag erstellen (optional, aber empfohlen)

Einen Sitemap-Eintrag anlegen, damit das Test Center im CRM-Navigationsmenü erscheint:

- **Bereich:** z.B. "Testing" oder unter einem bestehenden Bereich
- **Gruppe:** z.B. "Integration Tests"
- **SubArea-Typ:** Web Resource
- **Web Resource:** `jbe_testcenter`

Alternativ kann die Web Resource direkt per URL aufgerufen werden:

```
https://<umgebung>.crm4.dynamics.com/WebResources/jbe_/jbe_testcenter.html
```

### 3.3 Demo-Modus

Wenn die Web Resource außerhalb von Dynamics 365 geöffnet wird (oder die CRM-API nicht erreichbar ist), aktiviert sich automatisch der **Demo-Modus**:

- Alle Daten werden lokal simuliert (kein API-Zugriff nötig).
- Ein gelbes Banner "Demo-Modus: Alle Daten sind simuliert" wird angezeigt.
- Der Pack-Selector im Header ermöglicht den Wechsel zwischen Demo-Paketen.
- Testläufe werden mit simulierten Ergebnissen ausgeführt (ca. 90% Pass-Rate, 1.5-2.5s pro Test).

## Schritt 4: Custom API registrieren (optional)

Die Custom API `jbe_RunIntegrationTests` ermöglicht die serverseitige Testausführung über ein Plugin.

### API-Definition

| Eigenschaft | Wert |
|-------------|------|
| Unique Name | `jbe_RunIntegrationTests` |
| Display Name | Run Integration Tests |
| Binding Type | Unbound (0) |
| Is Function | Nein (Action) |
| Beschreibung | Startet einen Integrationstestlauf |

### Eingabeparameter

| Name | Typ | Pflicht | Beschreibung |
|------|-----|---------|--------------|
| `TestRunId` | EntityReference (jbe_testrun) | Ja | Referenz auf den zu startenden Testlauf |

### Plugin-Assembly

Falls die Custom API serverseitig Tests ausführen soll:

1. Plugin-Assembly mit der Testausführungslogik erstellen (IPlugin-Implementierung).
2. Plugin-Step auf `jbe_RunIntegrationTests` (PostOperation, Synchronous) registrieren.
3. Das Plugin liest den Testlauf-Record, führt die zugeordneten Testfälle aus und schreibt Ergebnisse zurück.

**Hinweis:** Die Custom API ist optional. Das Test Center funktioniert auch ohne sie, indem der Testlauf clientseitig über die MockAPI/DemoMode-Simulation ausgeführt wird.

### Weitere Custom APIs

| API | Typ | Beschreibung |
|-----|-----|--------------|
| `jbe_GovernanceApiContact` | Action | Ruft die Governance-API für einen Kontakt auf |
| `jbe_GovernanceApiContactSource` | Action | Ruft die Governance-API für eine Quell-Entität auf |
| `jbe_AssertEnvironment` | Function | Prüft Umgebungsvoraussetzungen für Integrationstests |

## Schritt 5: Security Roles

### Benötigte Rechte

| Entity | Tester | Entwickler | Admin |
|--------|--------|------------|-------|
| `jbe_testcase` | Lesen | Lesen, Schreiben, Erstellen | Vollzugriff |
| `jbe_testrun` | Lesen, Erstellen | Lesen, Schreiben, Erstellen | Vollzugriff |
| `jbe_testrunresult` | Lesen | Lesen, Schreiben, Erstellen | Vollzugriff |
| Getestete Entities (z.B. Contact, Account) | Lesen, Schreiben, Erstellen | Lesen, Schreiben, Erstellen, Löschen | Vollzugriff |

### Rollen-Empfehlung

**JBE Tester:**
- `jbe_testcase`: Lesen (Organization)
- `jbe_testrun`: Lesen, Erstellen, Schreiben (Organization)
- `jbe_testrunresult`: Lesen (Organization)
- Kann Testläufe starten und Ergebnisse einsehen, aber keine Testfälle bearbeiten.

**ITT Entwickler:**
- Alle Rechte des Testers, zusätzlich:
- `jbe_testcase`: Erstellen, Schreiben, Löschen (Organization)
- `jbe_testrunresult`: Erstellen, Schreiben (Organization)
- Kann Testfälle erstellen, bearbeiten und Ergebnisse korrigieren.

**ITT Admin:**
- Vollzugriff auf alle drei Entities.
- Zusätzlich: Zugriff auf Custom API-Registrierungen, Web Resource Management, Solution-Export.

## Schritt 6: Testen

### 6.1 URL aufrufen

```
https://<umgebung>.crm4.dynamics.com/WebResources/jbe_/jbe_testcenter.html
```

### 6.2 Demo-Modus prüfen

- Falls die Entities noch nicht angelegt sind, erscheint ein Verbindungsfehler mit dem Button "Demo-Ansicht laden".
- Im Demo-Modus sind alle Funktionen verfügbar, Daten werden lokal simuliert.

### 6.3 Ersten Testlauf starten

1. Navigiere zu **Testlauf**.
2. Wähle **Alle aktiven Tests**.
3. Klicke **Testlauf starten**.
4. Beobachte den Live-Fortschritt: Progress-Bar, Passed/Failed-Zähler, Log.
5. Nach Abschluss: Pie-Chart, Einzelergebnisse, Flow-Visualisierung pro Test.

### 6.4 Testfall erstellen (Verifizierung)

1. Navigiere zu **Testfälle**.
2. Klicke **+ Neuer Testfall**.
3. Fülle Metadaten aus (ID, Titel, Kategorie).
4. Gib eine JSON-Definition ein (oder nutze den Beispiel-Testfall aus Pack 4).
5. Prüfe die **Flow-Visualisierung** (Tab-Wechsel im Editor).
6. Klicke **Speichern**.

### 6.5 Metadaten-Explorer testen

- Navigiere zu **Metadaten**.
- Tabs: Tabellen, OptionSets, Custom APIs.
- Prüfe, ob die drei ITT-Entities und deren Attribute korrekt angezeigt werden.
- Nutze die "Snippet"-Funktion, um JSON-Vorlagen für Testfälle zu generieren.
