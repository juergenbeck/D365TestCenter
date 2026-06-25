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
    "jbe_domain":         ("Domäne", "Domain",
                           "Fachliche Domäne des Testfalls (z.B. CRM, Sales, Governance).",
                           "Functional domain of the test case (e.g. CRM, Sales, Governance)."),
    "jbe_testlevel":      ("Test-Level", "Test Level",
                           "Teststufe des Testfalls (z.B. Unit, Integration, End-to-End) als numerischer Wert.",
                           "Test level of the test case (e.g. unit, integration, end-to-end) as a numeric value."),
    "jbe_lifecyclestatus":("Lebenszyklus", "Lifecycle Status",
                           "Lebenszyklus-Status des Testfalls (z.B. Entwurf, Aktiv, Veraltet).",
                           "Lifecycle status of the test case (e.g. draft, active, deprecated)."),
    "jbe_envscope":       ("Umgebungs-Scope", "Environment Scope",
                           "Umgebungen, in denen der Testfall laufen darf (z.B. DEV, TEST).",
                           "Environments in which the test case may run (e.g. DEV, TEST)."),
    "jbe_estimatedminutes":("Geschätzte Minuten", "Estimated Minutes",
                           "Geschätzte Ausführungsdauer des Testfalls in Minuten.",
                           "Estimated execution time of the test case in minutes."),
    "jbe_owner":          ("Verantwortlich", "Owner",
                           "Fachlich verantwortliche Person für den Testfall (Freitext).",
                           "Person functionally responsible for the test case (free text)."),
    "jbe_tickets":        ("Tickets", "Tickets",
                           "Zugeordnete Tickets oder Work-Items (z.B. Azure DevOps, Jira), komma-separiert.",
                           "Associated tickets or work items (e.g. Azure DevOps, Jira), comma-separated."),
    "jbe_zephyrkey":      ("Zephyr-Key", "Zephyr Key",
                           "Schlüssel des zugeordneten Zephyr-/Jira-Testfalls.",
                           "Key of the associated Zephyr/Jira test case."),

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
    "jbe_skipped":        ("Übersprungen", "Skipped",
                           "Anzahl der Testfälle mit Outcome 'Übersprungen' in diesem Lauf.",
                           "Number of test cases with outcome 'Skipped' in this run."),
    "jbe_errored":        ("Fehler", "Errored",
                           "Anzahl der Testfälle mit Outcome 'Fehler' (Exception) in diesem Lauf.",
                           "Number of test cases with outcome 'Error' (exception) in this run."),
    "jbe_recordscreated": ("Erzeugte Records", "Records Created",
                           "Gesamtzahl der während des Laufs erzeugten Dataverse-Records.",
                           "Total number of Dataverse records created during the run."),
    "jbe_avgtestms":      ("Ø Dauer/Test (ms)", "Avg Test (ms)",
                           "Durchschnittliche Ausführungsdauer pro Testfall in Millisekunden.",
                           "Average execution duration per test case in milliseconds."),
    "jbe_mediantestms":   ("Median Dauer/Test (ms)", "Median Test (ms)",
                           "Median der Ausführungsdauer pro Testfall in Millisekunden.",
                           "Median execution duration per test case in milliseconds."),
    "jbe_mintestms":      ("Min. Dauer/Test (ms)", "Min Test (ms)",
                           "Kürzeste Testfall-Ausführungsdauer in Millisekunden.",
                           "Shortest test case execution duration in milliseconds."),
    "jbe_maxtestms":      ("Max. Dauer/Test (ms)", "Max Test (ms)",
                           "Längste Testfall-Ausführungsdauer in Millisekunden.",
                           "Longest test case execution duration in milliseconds."),
    "jbe_totaltestms":    ("Summe Dauer (ms)", "Total Test (ms)",
                           "Summe der Testfall-Ausführungsdauern in Millisekunden.",
                           "Sum of test case execution durations in milliseconds."),
    "jbe_slowesttestid":  ("Langsamster Test", "Slowest Test",
                           "Test-ID des Testfalls mit der längsten Ausführungsdauer.",
                           "Test ID of the test case with the longest execution duration."),
    "jbe_chunksize":      ("Chunk-Größe", "Chunk Size",
                           "Anzahl Testfälle pro Worker-Chunk bei paralleler Ausführung.",
                           "Number of test cases per worker chunk during parallel execution."),
    "jbe_maxconcurrent":  ("Max. parallel", "Max Concurrent",
                           "Maximale Anzahl gleichzeitig laufender Worker-Chunks.",
                           "Maximum number of concurrently running worker chunks."),
    "jbe_continuations":  ("Fortsetzungen", "Continuations",
                           "Anzahl der Self-Trigger-Fortsetzungen des Worker-Laufs.",
                           "Number of self-trigger continuations of the worker run."),

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
# Matcht einen kompletten <Descriptions>-Block INKL. führender Newline+Einrückung.
# Non-greedy ohne [^>] -> robust gegen '>' im Description-Text. Mehrere Treffer (FB-51-Dubletten)
# werden so sauber inkl. ihrer Zeile entfernt; danach wird genau ein Block neu gesetzt.
DESCRIPTIONS_RE = re.compile(
    r'\n[ ]*<Descriptions>.*?</Descriptions>',
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

ATTRIBUTE_BLOCK_RE = re.compile(r'<attribute\b[^>]*>.*?</attribute>', re.DOTALL)

def dedup_descriptions(text):
    """FB-51: Reduziert pro <attribute> mehrere <Descriptions>-Container auf den ersten.
    Wirkt generisch auf ALLE Attribute, auch System-Attribute (statecode/statuscode), die
    nicht im MAPPING stehen und vom Mapping-Pfad daher nicht angefasst werden."""
    def fix_attr(am):
        seen = [0]
        def repl(dm):
            seen[0] += 1
            return dm.group(0) if seen[0] == 1 else ''
        return DESCRIPTIONS_RE.sub(repl, am.group(0))
    return ATTRIBUTE_BLOCK_RE.sub(fix_attr, text)

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

        # Descriptions: ALLE existierenden Blöcke entfernen (Dedup gegen FB-51-Mehrfach-Container),
        # dann genau einen direkt nach dem displaynames-Block setzen. Lambda-Replacement, damit
        # Sonderzeichen im Description-Text nicht als Backreference interpretiert werden.
        new_desc = build_descriptions_block(de_desc, en_desc)
        new_body = DESCRIPTIONS_RE.sub('', new_body)
        new_body = re.sub(
            r'(          </displaynames>)',
            lambda m: m.group(1) + '\n' + new_desc,
            new_body, count=1)

        return header + new_body + footer

    new_text, n = attr_pat.subn(replace_attr, text)
    new_text = dedup_descriptions(new_text)  # FB-51: generischer Dedup über alle Attribute
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
