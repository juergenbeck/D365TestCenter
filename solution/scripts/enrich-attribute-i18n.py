#!/usr/bin/env python3
"""
Reichert alle jbe_* Custom-Attribute in den Entity.xml-Dateien an:
  - Setzt displaynames (1031 + 1033) exakt nach Mapping
  - Setzt Descriptions (1031 + 1033) exakt nach Mapping
Idempotent: mehrfaches Ausfuehren aendert nichts mehr.
"""
import re
import sys
from pathlib import Path

ROOT = Path(r"C:\Users\Juerg\Source\repo\D365TestCenter\solution\src\Entities")

# (logical_name) -> (DE_Label, EN_Label, DE_Desc, EN_Desc)
MAPPING = {
    # jbe_testcase
    "jbe_category":       ("Kategorie", "Category",
                           "Fachliche Kategorie des Testfalls (z.B. Merge, Custom API, Update Source).",
                           "Functional category of the test case (e.g. Merge, Custom API, Update Source)."),
    "jbe_definitionjson": ("Definition (JSON)", "Definition (JSON)",
                           "JSON-Definition des Testfalls mit Preconditions, Steps und Assertions. Wird von der Test-Engine ausgewertet.",
                           "JSON definition of the test case containing preconditions, steps and assertions. Evaluated by the test engine."),
    "jbe_enabled":        ("Aktiv", "Enabled",
                           "Wenn aktiv, wird der Testfall bei Sammel-Läufen berücksichtigt. Inaktive Testfälle werden übersprungen.",
                           "If enabled, the test case is included in bulk runs. Disabled test cases are skipped."),
    "jbe_name":           ("Name", "Name",
                           "Primärer Anzeigename des Testfalls. Wird i.d.R. identisch mit 'Titel' gesetzt.",
                           "Primary display name of the test case. Usually set identical to 'Title'."),
    "jbe_tags":           ("Tags", "Tags",
                           "Komma-separierte Tags zur Filterung und Gruppierung (z.B. 'smoke,regression,governance').",
                           "Comma-separated tags for filtering and grouping (e.g. 'smoke,regression,governance')."),
    "jbe_testid":         ("Test-ID", "Test ID",
                           "Eindeutige, lesbare Test-ID (z.B. MGR-01, STD-03). Wird in Reports und Links verwendet.",
                           "Unique, human-readable test ID (e.g. MGR-01, STD-03). Used in reports and links."),
    "jbe_title":          ("Titel", "Title",
                           "Kurzer beschreibender Titel des Testfalls.",
                           "Short descriptive title of the test case."),
    "jbe_userstories":    ("User Stories", "User Stories",
                           "Zugeordnete User Stories oder Backlog-Items, komma-separiert.",
                           "Assigned user stories or backlog items, comma-separated."),

    # jbe_testrun
    "jbe_batchoffset":    ("Batch-Offset", "Batch Offset",
                           "Interner Offset für die Cascade-Batch-Verarbeitung des CRUD-Triggers. Wird automatisch gepflegt.",
                           "Internal offset for the CRUD-trigger cascade batch processing. Maintained automatically."),
    "jbe_completedon":    ("Abgeschlossen am", "Completed On",
                           "Zeitstempel, an dem der Testlauf abgeschlossen wurde (letzter Batch fertig).",
                           "Timestamp when the test run was completed (last batch finished)."),
    "jbe_failed":         ("Fehlgeschlagen", "Failed",
                           "Anzahl der Testfälle mit Outcome 'Failed' oder 'Error' in diesem Lauf.",
                           "Number of test cases with outcome 'Failed' or 'Error' in this run."),
    "jbe_fulllog":        ("Vollständiges Log", "Full Log",
                           "Vollständiges Ausführungs-Log des Testlaufs (alle Testfälle, Schritte und Assertions).",
                           "Complete execution log of the test run (all test cases, steps and assertions)."),
    "jbe_keeprecords":    ("Testdaten behalten", "Keep Test Records",
                           "Wenn aktiv, werden die während des Tests erzeugten Dataverse-Records nicht wieder gelöscht (zur Analyse).",
                           "If enabled, Dataverse records created during the test are kept (not deleted) for later inspection."),
    "jbe_passed":         ("Bestanden", "Passed",
                           "Anzahl der Testfälle mit Outcome 'Passed' in diesem Lauf.",
                           "Number of test cases with outcome 'Passed' in this run."),
    "jbe_startedon":      ("Gestartet am", "Started On",
                           "Zeitstempel, an dem der Testlauf gestartet wurde.",
                           "Timestamp when the test run was started."),
    "jbe_testcasefilter": ("Filter", "Filter",
                           "Testfall-Filter (ID, Wildcard, Kategorie-Prefix, Tag, User Story). Leer oder '*' = alle aktiven Testfälle.",
                           "Test case filter (ID, wildcard, category prefix, tag, user story). Empty or '*' = all active test cases."),
    "jbe_teststatus":     ("Teststatus", "Test Status",
                           "Aktueller Status des Testlaufs: Ausstehend, Läuft, Abgeschlossen oder Fehler.",
                           "Current status of the test run: Pending, Running, Completed or Error."),
    "jbe_testsummary":    ("Zusammenfassung", "Summary",
                           "Kurze Zusammenfassung des Testlauf-Ergebnisses (z.B. 'Läuft... Test 5/30', '28/30 Passed').",
                           "Brief summary of the test run result (e.g. 'Running... Test 5/30', '28/30 Passed')."),
    "jbe_total":          ("Gesamt", "Total",
                           "Gesamtzahl der im Lauf enthaltenen Testfälle.",
                           "Total number of test cases included in this run."),

    # jbe_testrunresult
    "jbe_assertionresults": ("Assertion-Ergebnisse (JSON)", "Assertion Results (JSON)",
                           "JSON-Array aller Assertion-Auswertungen des Testfalls (Feld, Operator, Erwartet, Ist, Ergebnis).",
                           "JSON array of all assertion evaluations of the test case (field, operator, expected, actual, result)."),
    "jbe_durationms":     ("Dauer (ms)", "Duration (ms)",
                           "Ausführungsdauer dieses Testfalls in Millisekunden.",
                           "Execution duration of this test case in milliseconds."),
    "jbe_errormessage":   ("Fehlermeldung", "Error Message",
                           "Fehlermeldung bei fehlgeschlagenen Testfällen. Bei 'Error' enthält sie den Exception-Text.",
                           "Error message for failed test cases. For 'Error' outcomes contains the exception text."),
    "jbe_outcome":        ("Ergebnis", "Outcome",
                           "Ergebnis des Testfalls: Bestanden, Fehlgeschlagen, Übersprungen oder Fehler.",
                           "Outcome of the test case: Passed, Failed, Skipped or Error."),
    "jbe_testrunid":      ("Testlauf", "Test Run",
                           "Zugehöriger Testlauf (Parent-Lookup).",
                           "Associated test run (parent lookup)."),
    "jbe_trackedrecords": ("Erzeugte Records (JSON)", "Tracked Records (JSON)",
                           "JSON-Array der während dieses Testfalls erzeugten Dataverse-Records (für Cleanup).",
                           "JSON array of Dataverse records created during this test case (for cleanup)."),

    # jbe_teststep
    "jbe_action":         ("Aktion", "Action",
                           "Art des Schrittes (z.B. CreateRecord, UpdateRecord, CallCustomApi, WaitForRecord, Assert).",
                           "Type of step (e.g. CreateRecord, UpdateRecord, CallCustomApi, WaitForRecord, Assert)."),
    "jbe_actualvalue":    ("Ist-Wert", "Actual Value",
                           "Bei Assertions: tatsächlich gemessener Wert aus Dataverse.",
                           "For assertions: actual value measured from Dataverse."),
    "jbe_alias":          ("Alias", "Alias",
                           "Alias dieses Schrittes zur Referenzierung in späteren Schritten ({alias.id}, {alias.fields.name}).",
                           "Alias of this step, used to reference it from later steps ({alias.id}, {alias.fields.name})."),
    "jbe_assertionfield": ("Assertion-Feld", "Assertion Field",
                           "Bei Assertions: Name des zu prüfenden Dataverse-Feldes.",
                           "For assertions: name of the Dataverse field being checked."),
    "jbe_assertionoperator": ("Operator", "Operator",
                           "Bei Assertions: Vergleichsoperator (Equals, NotEquals, Contains, IsEmpty, DateSetRecently, ...).",
                           "For assertions: comparison operator (Equals, NotEquals, Contains, IsEmpty, DateSetRecently, ...)."),
    "jbe_entity":         ("Entity", "Entity",
                           "Logischer Name der betroffenen Dataverse-Tabelle.",
                           "Logical name of the affected Dataverse table."),
    "jbe_expectedvalue":  ("Soll-Wert", "Expected Value",
                           "Bei Assertions: erwarteter Wert laut Test-Definition.",
                           "For assertions: expected value according to test definition."),
    "jbe_inputdata":      ("Eingabedaten", "Input Data",
                           "JSON-Darstellung der an den Schritt übergebenen Daten (fields, parameters).",
                           "JSON representation of data passed into the step (fields, parameters)."),
    "jbe_outputdata":     ("Ausgabedaten", "Output Data",
                           "JSON-Darstellung der vom Schritt zurückgelieferten Daten (IDs, Response-Body).",
                           "JSON representation of data returned by the step (IDs, response body)."),
    "jbe_phase":          ("Phase", "Phase",
                           "Phase im Testlebenszyklus: Vorbedingung, Schritt, Prüfung oder Aufräumen.",
                           "Phase in the test lifecycle: Precondition, Step, Assertion or Cleanup."),
    "jbe_recordid":       ("Record-ID", "Record ID",
                           "GUID des vom Schritt erstellten/angefassten Dataverse-Records (falls zutreffend).",
                           "GUID of the Dataverse record created/touched by the step (if applicable)."),
    "jbe_recordurl":      ("Record-URL", "Record URL",
                           "Direkter Link zum Dataverse-Record im CRM-Portal.",
                           "Direct link to the Dataverse record in the CRM portal."),
    "jbe_stepnumber":     ("Schritt-Nr.", "Step Number",
                           "Laufende Nummer des Schritts innerhalb des Testfalls.",
                           "Sequential number of the step within the test case."),
    "jbe_stepstatus":     ("Status", "Status",
                           "Ergebnisstatus des Schritts: Erfolgreich, Fehlgeschlagen oder Übersprungen.",
                           "Result status of the step: Success, Failed or Skipped."),
    "jbe_testrunresultid": ("Testfall-Ergebnis", "Test Run Result",
                           "Zugehöriges Testfall-Ergebnis (Parent-Lookup).",
                           "Associated test run result (parent lookup)."),

    # Gemeinsam genutzte Namen
    # (jbe_name wird pro Entity unterschiedlich beschrieben, daher oben je einzeln)
}

# Sonder-Override pro Entity fuer jbe_name und jbe_testid (Description variiert)
OVERRIDE_BY_ENTITY = {
    "jbe_testrun": {
        "jbe_name": ("Name", "Name",
                     "Anzeigename des Testlaufs (z.B. 'Run 2026-04-21 14:23 - MGR*').",
                     "Display name of the test run (e.g. 'Run 2026-04-21 14:23 - MGR*')."),
    },
    "jbe_testrunresult": {
        "jbe_name": ("Name", "Name",
                     "Anzeigename des Testfall-Ergebnisses (enthält TestID und Outcome-Kürzel).",
                     "Display name of the test run result (contains test ID and outcome shorthand)."),
        "jbe_testid": ("Test-ID", "Test ID",
                       "Test-ID des ausgeführten Testfalls (zur einfachen Filterung ohne Lookup-Resolve).",
                       "Test ID of the executed test case (for easy filtering without resolving the lookup)."),
    },
    "jbe_teststep": {
        "jbe_name": ("Name", "Name",
                     "Kurzer, lesbarer Name des Schritts (wird oft aus Action + Entity generiert).",
                     "Short, human-readable name of the step (often generated from action + entity)."),
    },
}

# Reihenfolge: Descriptions nach displaynames, vor dem schliessenden </attribute>
# Wir verwenden Regex auf Block-Ebene. Robust, weil die Pfad-Struktur sehr regelmaessig ist.

DISPLAYNAMES_RE = re.compile(
    r'(<displaynames>)(\s*<displayname[^/]*/>\s*)+(</displaynames>)',
    re.DOTALL)
DESCRIPTIONS_RE = re.compile(
    r'<Descriptions>\s*(?:<Description[^/]*/>\s*)+</Descriptions>',
    re.DOTALL)

def build_displaynames_block(de, en):
    # kein fuehrendes Indent - der Match-Anker sitzt schon am eingerueckten <displaynames>
    return ("<displaynames>\n"
            f"            <displayname description=\"{de}\" languagecode=\"1031\" />\n"
            f"            <displayname description=\"{en}\" languagecode=\"1033\" />\n"
            f"          </displaynames>")

def build_descriptions_block(de, en):
    return ("          <Descriptions>\n"
            f"            <Description description=\"{de}\" languagecode=\"1031\" />\n"
            f"            <Description description=\"{en}\" languagecode=\"1033\" />\n"
            f"          </Descriptions>")

def process_entity(entity_dir):
    entity_name = entity_dir.name
    xml_path = entity_dir / "Entity.xml"
    if not xml_path.exists():
        return
    text = xml_path.read_text(encoding="utf-8")

    # Iteriere ueber alle <attribute PhysicalName="jbe_..."> Bloecke
    attr_pat = re.compile(
        r'(<attribute PhysicalName="(jbe_\w+)">)(.*?)(</attribute>)',
        re.DOTALL)

    def replace_attr(m):
        header, physname, body, footer = m.group(1), m.group(2), m.group(3), m.group(4)
        # LogicalName aus Body (immer lowercase)
        lm = re.search(r'<LogicalName>(jbe_\w+)</LogicalName>', body)
        if not lm:
            return m.group(0)
        logical = lm.group(1)

        # Mapping holen: erst per-entity override, dann global
        entry = None
        if entity_name in OVERRIDE_BY_ENTITY and logical in OVERRIDE_BY_ENTITY[entity_name]:
            entry = OVERRIDE_BY_ENTITY[entity_name][logical]
        elif logical in MAPPING:
            entry = MAPPING[logical]
        else:
            # Kein Mapping -> unveraendert
            return m.group(0)

        de_label, en_label, de_desc, en_desc = entry

        # displaynames ersetzen (Block existiert immer)
        new_dn = build_displaynames_block(de_label, en_label)
        new_body, n_dn = DISPLAYNAMES_RE.subn(new_dn, body)

        # Descriptions: wenn existiert ersetzen, sonst nach displaynames einfuegen
        new_desc = build_descriptions_block(de_desc, en_desc)
        if DESCRIPTIONS_RE.search(new_body):
            new_body = DESCRIPTIONS_RE.sub(new_desc, new_body)
        else:
            # Einfuegen direkt nach displaynames-Block (nach der schliessenden Zeile)
            new_body = re.sub(
                r'(          </displaynames>)',
                r'\1\n' + new_desc,
                new_body, count=1)

        return header + new_body + footer

    new_text, n = attr_pat.subn(replace_attr, text)
    if new_text != text:
        xml_path.write_text(new_text, encoding="utf-8")
        # Zaehle aktualisierte Attribute grob: Differenz in den Descriptions-Bloecken
        print(f"  [{entity_name}] aktualisiert")
    else:
        print(f"  [{entity_name}] unveraendert")

def main():
    if not ROOT.exists():
        print(f"ROOT nicht gefunden: {ROOT}", file=sys.stderr)
        sys.exit(1)
    for d in sorted(ROOT.iterdir()):
        if d.is_dir() and d.name.startswith("jbe_"):
            process_entity(d)

if __name__ == "__main__":
    main()
