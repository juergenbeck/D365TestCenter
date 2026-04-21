#!/usr/bin/env python3
"""
Baut die 4 Main-Forms der jbe_*-Tabellen spec-basiert neu auf.
- Behaelt die formid aus der bestehenden Form (Merge-Stabilitaet)
- Generiert deterministische GUIDs fuer Tabs/Sections/Cells aus stabilen Namen
  -> idempotent bei mehrfacher Ausfuehrung
- Labels immer 1031 (DE) + 1033 (EN)
"""
import re
import uuid
import hashlib
from pathlib import Path

ROOT = Path(r"C:\Users\Juerg\Source\repo\D365TestCenter\solution\src\Entities")

CLASSIDS = {
    "string":   "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}",
    "picklist": "{3EF39988-22BB-4F0B-BBBE-64B5A3748AEE}",
    "lookup":   "{270BD3DB-D9AF-4782-9025-509E298DEC0A}",
    "boolean":  "{67FAC785-CD58-4F9F-ABB3-4B7DDC6ED5ED}",
    "datetime": "{5B773807-9FB2-42DB-97C3-7A91EFF8ADFF}",
    "number":   "{C6D124CA-7EDA-4A60-AEA9-7FB8D318B68F}",
    "memo":     "{E0DECE4B-6FC8-4A8F-A065-082708572369}",
    "subgrid":  "{E7A81278-8635-4D9E-8D4D-59480B391C5B}",
    "notes":    "{06375649-C143-495E-A496-C962E5B4488E}",
}

# Kontinuierlicher stabiler GUID-Generator (aus einem Namespace + Key).
# Gleicher Key -> gleicher GUID bei jedem Run. So bleiben IDs stabil.
NAMESPACE = uuid.UUID("d365dc00-0000-0000-0000-000000000001")
def sid(key: str) -> str:
    return "{" + str(uuid.uuid5(NAMESPACE, key)) + "}"

# Feldtyp-Map pro Entity
FIELD_TYPES = {
    "jbe_testcase": {
        "jbe_category": "picklist", "jbe_definitionjson": "memo",
        "jbe_enabled": "boolean", "jbe_name": "string", "jbe_tags": "string",
        "jbe_testid": "string", "jbe_title": "string", "jbe_userstories": "string",
    },
    "jbe_testrun": {
        "jbe_batchoffset": "number", "jbe_completedon": "datetime",
        "jbe_failed": "number", "jbe_fulllog": "memo", "jbe_keeprecords": "boolean",
        "jbe_name": "string", "jbe_passed": "number", "jbe_startedon": "datetime",
        "jbe_testcasefilter": "string", "jbe_teststatus": "picklist",
        "jbe_testsummary": "memo", "jbe_total": "number",
    },
    "jbe_testrunresult": {
        "jbe_assertionresults": "memo", "jbe_durationms": "number",
        "jbe_errormessage": "memo", "jbe_name": "string", "jbe_outcome": "picklist",
        "jbe_testid": "string", "jbe_testrunid": "lookup", "jbe_trackedrecords": "memo",
    },
    "jbe_teststep": {
        "jbe_action": "string", "jbe_actualvalue": "string", "jbe_alias": "string",
        "jbe_assertionfield": "string", "jbe_assertionoperator": "string",
        "jbe_durationms": "number", "jbe_entity": "string", "jbe_errormessage": "memo",
        "jbe_expectedvalue": "string", "jbe_inputdata": "memo", "jbe_name": "string",
        "jbe_outputdata": "memo", "jbe_phase": "picklist", "jbe_recordid": "string",
        "jbe_recordurl": "string", "jbe_stepnumber": "number",
        "jbe_stepstatus": "picklist", "jbe_testrunresultid": "lookup",
    },
}

# DE/EN-Feldlabels (aus dem Attribute-i18n-Skript dupliziert, kurzform)
LABELS = {
    "jbe_category": ("Kategorie", "Category"),
    "jbe_definitionjson": ("Definition (JSON)", "Definition (JSON)"),
    "jbe_enabled": ("Aktiv", "Enabled"),
    "jbe_name": ("Name", "Name"),
    "jbe_tags": ("Tags", "Tags"),
    "jbe_testid": ("Test-ID", "Test ID"),
    "jbe_title": ("Titel", "Title"),
    "jbe_userstories": ("User Stories", "User Stories"),
    "jbe_batchoffset": ("Batch-Offset", "Batch Offset"),
    "jbe_completedon": ("Abgeschlossen am", "Completed On"),
    "jbe_failed": ("Fehlgeschlagen", "Failed"),
    "jbe_fulllog": ("Vollständiges Log", "Full Log"),
    "jbe_keeprecords": ("Testdaten behalten", "Keep Test Records"),
    "jbe_passed": ("Bestanden", "Passed"),
    "jbe_startedon": ("Gestartet am", "Started On"),
    "jbe_testcasefilter": ("Filter", "Filter"),
    "jbe_teststatus": ("Teststatus", "Test Status"),
    "jbe_testsummary": ("Zusammenfassung", "Summary"),
    "jbe_total": ("Gesamt", "Total"),
    "jbe_assertionresults": ("Assertion-Ergebnisse (JSON)", "Assertion Results (JSON)"),
    "jbe_durationms": ("Dauer (ms)", "Duration (ms)"),
    "jbe_errormessage": ("Fehlermeldung", "Error Message"),
    "jbe_outcome": ("Ergebnis", "Outcome"),
    "jbe_testrunid": ("Testlauf", "Test Run"),
    "jbe_trackedrecords": ("Erzeugte Records (JSON)", "Tracked Records (JSON)"),
    "jbe_action": ("Aktion", "Action"),
    "jbe_actualvalue": ("Ist-Wert", "Actual Value"),
    "jbe_alias": ("Alias", "Alias"),
    "jbe_assertionfield": ("Assertion-Feld", "Assertion Field"),
    "jbe_assertionoperator": ("Operator", "Operator"),
    "jbe_entity": ("Entity", "Entity"),
    "jbe_expectedvalue": ("Soll-Wert", "Expected Value"),
    "jbe_inputdata": ("Eingabedaten", "Input Data"),
    "jbe_outputdata": ("Ausgabedaten", "Output Data"),
    "jbe_phase": ("Phase", "Phase"),
    "jbe_recordid": ("Record-ID", "Record ID"),
    "jbe_recordurl": ("Record-URL", "Record URL"),
    "jbe_stepnumber": ("Schritt-Nr.", "Step Number"),
    "jbe_stepstatus": ("Status", "Status"),
    "jbe_testrunresultid": ("Testfall-Ergebnis", "Test Run Result"),
    "ownerid": ("Besitzer", "Owner"),
    "createdby": ("Erstellt von", "Created By"),
    "modifiedby": ("Geändert von", "Modified By"),
    "createdon": ("Erstellt am", "Created On"),
    "modifiedon": ("Geändert am", "Modified On"),
}

# Form-Spezifikation
# Jedes entity: (tabs=[{label_de,label_en, sections=[{label_de,label_en,fields=[...], columns, rowspan_overrides}]}], header=[...])
SPECS = {
    "jbe_testcase": {
        "header_fields": ["jbe_testid", "jbe_enabled", "jbe_category", "ownerid"],
        "tabs": [
            ("Übersicht", "Overview", [
                ("Identifikation", "Identification", ["jbe_testid", "jbe_title", "jbe_name"], 1),
                ("Klassifizierung", "Classification", ["jbe_category", "jbe_tags", "jbe_userstories", "jbe_enabled"], 1),
                ("Metadaten", "Metadata", ["createdon", "createdby", "modifiedon", "modifiedby"], 2),
            ]),
            ("Definition (JSON)", "Definition (JSON)", [
                ("Definition", "Definition", [("jbe_definitionjson", 24)], 1),
            ]),
        ],
    },
    "jbe_testrun": {
        "header_fields": ["jbe_teststatus", "jbe_total", "jbe_passed", "jbe_failed"],
        "tabs": [
            ("Übersicht", "Overview", [
                ("Filter & Status", "Filter & Status", [
                    "jbe_testcasefilter", "jbe_teststatus", "jbe_keeprecords",
                    "jbe_startedon", "jbe_completedon"], 2),
                ("Zähler", "Counters", ["jbe_total", "jbe_passed", "jbe_failed"], 3),
                ("Zusammenfassung", "Summary", [("jbe_testsummary", 6)], 1),
            ]),
            ("Vollständiges Log", "Full Log", [
                ("Log", "Log", [("jbe_fulllog", 28)], 1),
            ]),
            ("Ergebnisse", "Results", [
                ("results_subgrid", None, "SUBGRID:jbe_testrunresult:jbe_testrunid", 1),
            ]),
        ],
    },
    "jbe_testrunresult": {
        "header_fields": ["jbe_outcome", "jbe_testid", "jbe_durationms", "jbe_testrunid"],
        "tabs": [
            ("Übersicht", "Overview", [
                ("Zuordnung", "Association", [
                    "jbe_testid", "jbe_outcome", "jbe_durationms", "jbe_testrunid"], 2),
                ("Fehler", "Error", [("jbe_errormessage", 8)], 1),
            ]),
            ("Assertion-Ergebnisse", "Assertion Results", [
                ("Assertions", "Assertions", [("jbe_assertionresults", 28)], 1),
            ]),
            ("Erzeugte Records", "Tracked Records", [
                ("Records", "Records", [("jbe_trackedrecords", 28)], 1),
            ]),
            ("Schritte", "Steps", [
                ("steps_subgrid", None, "SUBGRID:jbe_teststep:jbe_testrunresultid", 1),
            ]),
        ],
    },
    "jbe_teststep": {
        "header_fields": ["jbe_stepstatus", "jbe_phase", "jbe_stepnumber", "jbe_durationms"],
        "tabs": [
            ("Übersicht", "Overview", [
                ("Identifikation", "Identification", [
                    "jbe_stepnumber", "jbe_name", "jbe_phase", "jbe_stepstatus"], 2),
                ("Aktion", "Action", [
                    "jbe_action", "jbe_entity", "jbe_alias", "jbe_recordid"], 2),
                ("Link", "Link", ["jbe_recordurl"], 1),
                ("Fehler", "Error", [("jbe_errormessage", 6)], 1),
            ]),
            ("Eingabedaten", "Input Data", [
                ("Input", "Input", [("jbe_inputdata", 28)], 1),
            ]),
            ("Ausgabedaten", "Output Data", [
                ("Output", "Output", [("jbe_outputdata", 28)], 1),
            ]),
            ("Prüfung", "Assertion", [
                ("Prüfung", "Assertion", [
                    "jbe_assertionfield", "jbe_assertionoperator",
                    "jbe_expectedvalue", "jbe_actualvalue"], 2),
            ]),
        ],
    },
}

# ------------------ Helpers ------------------

def xml_escape(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")

def cell_xml(entity: str, spec_item, indent="                      "):
    """spec_item kann sein:
       - "jbe_field"                -> normale Zelle (rowspan=1)
       - ("jbe_field", rowspan)     -> Memo mit rowspan
       - "SUBGRID:jbe_target:jbe_lookup" -> Subgrid-Zelle
    """
    if isinstance(spec_item, tuple):
        fld, rowspan = spec_item
    elif isinstance(spec_item, str) and spec_item.startswith("SUBGRID:"):
        return subgrid_cell_xml(entity, spec_item, indent)
    else:
        fld, rowspan = spec_item, 1

    ftype = FIELD_TYPES.get(entity, {}).get(fld)
    if ftype is None:
        # Systemfelder
        if fld == "ownerid":       ftype = "lookup"
        elif fld in ("createdby", "modifiedby"): ftype = "lookup"
        elif fld in ("createdon", "modifiedon"): ftype = "datetime"
        else: ftype = "string"

    classid = CLASSIDS[ftype]
    de, en = LABELS.get(fld, (fld, fld))
    cell_id = sid(f"{entity}:cell:{fld}")

    return (f'{indent}<cell id="{cell_id}" colspan="1" rowspan="{rowspan}">\n'
            f'{indent}  <labels>\n'
            f'{indent}    <label description="{xml_escape(de)}" languagecode="1031" />\n'
            f'{indent}    <label description="{xml_escape(en)}" languagecode="1033" />\n'
            f'{indent}  </labels>\n'
            f'{indent}  <control id="{fld}" classid="{classid}" datafieldname="{fld}" disabled="false" />\n'
            f'{indent}</cell>')

def subgrid_cell_xml(entity: str, spec: str, indent="                      "):
    # SUBGRID:target_entity:lookup_field
    _, target_entity, lookup_field = spec.split(":")
    cell_id = sid(f"{entity}:subgrid:{target_entity}")
    # Labels: "Ergebnisse"/"Results" bzw. "Schritte"/"Steps"
    titles = {
        "jbe_testrunresult": ("Ergebnisse", "Results"),
        "jbe_teststep": ("Schritte", "Steps"),
    }
    de, en = titles.get(target_entity, (target_entity, target_entity))
    # Wir setzen ViewId={00000000-0000-0000-0000-000000000000} -> nutzt Default-View der Target-Entity
    # RecordsPerPage=8, ChartGridMode=CHART_AND_GRID
    return (f'{indent}<cell id="{cell_id}" colspan="1" rowspan="16">\n'
            f'{indent}  <labels>\n'
            f'{indent}    <label description="{de}" languagecode="1031" />\n'
            f'{indent}    <label description="{en}" languagecode="1033" />\n'
            f'{indent}  </labels>\n'
            f'{indent}  <control id="{target_entity}_subgrid" classid="{CLASSIDS["subgrid"]}">\n'
            f'{indent}    <parameters>\n'
            f'{indent}      <ViewId>{{00000000-0000-0000-0000-000000000000}}</ViewId>\n'
            f'{indent}      <IsUserView>false</IsUserView>\n'
            f'{indent}      <RelationshipName>{lookup_field}_{target_entity}</RelationshipName>\n'
            f'{indent}      <TargetEntityType>{target_entity}</TargetEntityType>\n'
            f'{indent}      <AutoExpand>Fixed</AutoExpand>\n'
            f'{indent}      <EnableQuickFind>false</EnableQuickFind>\n'
            f'{indent}      <EnableViewPicker>true</EnableViewPicker>\n'
            f'{indent}      <ViewIds />\n'
            f'{indent}      <EnableJumpBar>false</EnableJumpBar>\n'
            f'{indent}      <ChartGridMode>Grid</ChartGridMode>\n'
            f'{indent}      <VisualizationId />\n'
            f'{indent}      <IsUserChart>false</IsUserChart>\n'
            f'{indent}      <EnableChartPicker>false</EnableChartPicker>\n'
            f'{indent}      <RecordsPerPage>8</RecordsPerPage>\n'
            f'{indent}    </parameters>\n'
            f'{indent}  </control>\n'
            f'{indent}</cell>')

def section_xml(entity: str, section_spec, indent="              "):
    name_de, name_en, fields, columns = section_spec
    sec_id = sid(f"{entity}:section:{name_en or name_de}")
    showlabel = "false" if name_en is None else "true"
    label_de = name_de or ""
    label_en = name_en or label_de
    # Rows: wir legen pro Feld eine Row mit einer Cell an (columns=1) ODER gruppieren in Rows a "columns" Cells
    rows_out = []
    if columns == 1:
        for fld in fields:
            rows_out.append(f'{indent}    <row>\n{cell_xml(entity, fld, indent + "      ")}\n{indent}    </row>')
    else:
        # fields in Gruppen von `columns` aufteilen
        for i in range(0, len(fields), columns):
            group = fields[i:i+columns]
            cells = "\n".join(cell_xml(entity, f, indent + "      ") for f in group)
            rows_out.append(f'{indent}    <row>\n{cells}\n{indent}    </row>')
    rows_xml = "\n".join(rows_out)

    section_labels = (f'{indent}  <labels>\n'
                      f'{indent}    <label description="{xml_escape(label_de)}" languagecode="1031" />\n'
                      f'{indent}    <label description="{xml_escape(label_en)}" languagecode="1033" />\n'
                      f'{indent}  </labels>')

    sec_key = name_en if name_en else (name_de if name_de else "x")
    return (f'{indent}<section name="sec_{hashlib.md5((entity + sec_key).encode()).hexdigest()[:8]}" '
            f'showlabel="{showlabel}" showbar="true" columns="{columns}" labelwidth="115" id="{sec_id}" IsUserDefined="1">\n'
            f'{section_labels}\n'
            f'{indent}  <rows>\n{rows_xml}\n{indent}  </rows>\n'
            f'{indent}</section>')

def tab_xml(entity: str, tab_spec, indent="        "):
    name_de, name_en, sections = tab_spec
    tab_id = sid(f"{entity}:tab:{name_en}")
    sections_xml = "\n".join(section_xml(entity, s, indent + "      ") for s in sections)
    return (f'{indent}<tab verticallayout="true" id="{tab_id}" IsUserDefined="1" showlabel="true" expanded="true">\n'
            f'{indent}  <labels>\n'
            f'{indent}    <label description="{xml_escape(name_de)}" languagecode="1031" />\n'
            f'{indent}    <label description="{xml_escape(name_en)}" languagecode="1033" />\n'
            f'{indent}  </labels>\n'
            f'{indent}  <columns>\n'
            f'{indent}    <column width="100%">\n'
            f'{indent}      <sections>\n{sections_xml}\n'
            f'{indent}      </sections>\n'
            f'{indent}    </column>\n'
            f'{indent}  </columns>\n'
            f'{indent}</tab>')

def header_xml(entity: str, fields, indent="      "):
    cells = "\n".join(cell_xml(entity, f, indent + "    ") for f in fields)
    hdr_id = sid(f"{entity}:header")
    return (f'{indent}<header id="{hdr_id}" celllabelposition="Top" columns="{len(fields)*37}" labelwidth="115" celllabelalignment="Left">\n'
            f'{indent}  <rows>\n'
            f'{indent}    <row>\n{cells}\n'
            f'{indent}    </row>\n'
            f'{indent}  </rows>\n'
            f'{indent}</header>')

def footer_xml(entity: str, indent="      "):
    ftr_id = sid(f"{entity}:footer")
    return (f'{indent}<footer id="{ftr_id}" celllabelposition="Top" columns="111" labelwidth="115" celllabelalignment="Left">\n'
            f'{indent}  <rows>\n'
            f'{indent}    <row>\n'
            f'{indent}      <cell id="{sid(entity+":footer:c1")}" showlabel="false"><labels><label description="" languagecode="1033" /></labels></cell>\n'
            f'{indent}      <cell id="{sid(entity+":footer:c2")}" showlabel="false"><labels><label description="" languagecode="1033" /></labels></cell>\n'
            f'{indent}      <cell id="{sid(entity+":footer:c3")}" showlabel="false"><labels><label description="" languagecode="1033" /></labels></cell>\n'
            f'{indent}    </row>\n'
            f'{indent}  </rows>\n'
            f'{indent}</footer>')

FORM_ONLOAD = {
    "jbe_testcase":       "JBE.Forms.TestCase.onLoad",
    "jbe_testrun":        "JBE.Forms.TestRun.onLoad",
    "jbe_testrunresult":  "JBE.Forms.TestRunResult.onLoad",
    "jbe_teststep":       "JBE.Forms.TestStep.onLoad",
}

def events_xml(entity: str, indent="      ") -> str:
    handler = FORM_ONLOAD.get(entity)
    if not handler:
        return ""
    return (f'{indent}<formLibraries>\n'
            f'{indent}  <Library name="jbe_/forms.js" libraryUniqueId="{{d365dc00-0000-0000-0001-000000000001}}" />\n'
            f'{indent}</formLibraries>\n'
            f'{indent}<events>\n'
            f'{indent}  <event name="onload" application="false" active="false">\n'
            f'{indent}    <Handlers>\n'
            f'{indent}      <Handler functionName="{handler}" libraryName="jbe_/forms.js" '
            f'handlerUniqueId="{sid(entity+":onload")}" enabled="true" parameters="" passExecutionContext="true" />\n'
            f'{indent}    </Handlers>\n'
            f'{indent}  </event>\n'
            f'{indent}</events>')

def form_xml(formid: str, entity: str) -> str:
    spec = SPECS[entity]
    tabs_xml = "\n".join(tab_xml(entity, t) for t in spec["tabs"])
    hdr = header_xml(entity, spec["header_fields"])
    ftr = footer_xml(entity)
    evts = events_xml(entity)
    return f"""<?xml version="1.0" encoding="utf-8"?>
<forms xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <systemform>
    <formid>{formid}</formid>
    <IntroducedVersion>1.0.0.0</IntroducedVersion>
    <FormPresentation>1</FormPresentation>
    <FormActivationState>1</FormActivationState>
    <form headerdensity="HighWithControls">
      <tabs>
{tabs_xml}
      </tabs>
{hdr}
{ftr}
{evts}
      <DisplayConditions Order="0" FallbackForm="true">
        <Everyone />
      </DisplayConditions>
    </form>
    <IsCustomizable>1</IsCustomizable>
    <CanBeDeleted>1</CanBeDeleted>
    <LocalizedNames>
      <LocalizedName description="Information" languagecode="1031" />
      <LocalizedName description="Information" languagecode="1033" />
    </LocalizedNames>
    <Descriptions>
      <Description description="Hauptformular für diese Entität." languagecode="1031" />
      <Description description="Main form for this entity." languagecode="1033" />
    </Descriptions>
  </systemform>
</forms>
"""

def extract_formid(xml_path: Path) -> str:
    text = xml_path.read_text(encoding="utf-8")
    m = re.search(r'<formid>\{([0-9a-fA-F-]+)\}</formid>', text)
    return "{" + m.group(1) + "}" if m else None

# ------------------ Main ------------------

for entity in SPECS.keys():
    main_dir = ROOT / entity / "FormXml" / "main"
    if not main_dir.exists():
        print(f"  [{entity}] FormXml/main nicht vorhanden, skip")
        continue
    xmls = list(main_dir.glob("*.xml"))
    if not xmls:
        print(f"  [{entity}] keine Form gefunden, skip")
        continue
    xml_path = xmls[0]
    fid = extract_formid(xml_path)
    if not fid:
        print(f"  [{entity}] formid nicht extrahierbar, skip")
        continue
    new_xml = form_xml(fid, entity)
    xml_path.write_text(new_xml, encoding="utf-8")
    print(f"  [{entity}] Main-Form geschrieben (formid={fid})")

print("Forms built.")
