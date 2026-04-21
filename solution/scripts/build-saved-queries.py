#!/usr/bin/env python3
"""
Baut SavedQueries (Views) der 4 jbe_*-Tabellen neu auf:
- Umbau der bestehenden Main Active / Associated / QuickFind Views (savedqueryid bleibt)
- Neue Views: "Aktive L\u00e4ufe" und "Fehlgeschlagene Ergebnisse"
- Labels 1031 + 1033
Idempotent.
"""
import re
import uuid
from pathlib import Path

ROOT = Path(r"C:\Users\Juerg\Source\repo\D365TestCenter\solution\src\Entities")
NAMESPACE = uuid.UUID("d365dc00-0000-0000-0000-000000000002")
def sid(key: str) -> str:
    return "{" + str(uuid.uuid5(NAMESPACE, key)) + "}"

# Bestehende View-IDs, die wir umbauen (savedqueryid beibehalten)
EXISTING = {
    "jbe_testcase": {
        "active":   "{0428dd93-00c4-4d07-92cb-84bef887b2fe}",
        "inactive": "{35f7e108-9146-490c-8236-1d412c1dc9bf}",
        "quickfind":"{fd0278cf-d3cf-40b0-8bd9-a876ae5d7ea0}",
        "assoc":    "{6e5299e7-ec2a-4c3e-a15b-7b2907d6ccf6}",
        "lookup":   "{86a0cacb-cb65-4b6b-813d-2b08d653ec85}",
        "advfind":  "{76e78110-4fde-4b3a-8e5e-6f9850161e41}",
    },
    "jbe_testrun": {
        "active":   "{4dc755d4-7d9c-4e25-a28d-de56738f7047}",
        "inactive": "{f387c262-c4f7-46a2-9329-1c16a05b38f5}",
        "quickfind":"{538c8b03-ad36-41a2-98ff-21c24123e40f}",
        "assoc":    "{d3236b63-9e8c-41fe-b02f-b5c785e3729a}",
        "lookup":   "{17298d33-abc8-4f47-8995-fa817155062c}",
        "advfind":  "{b1f74499-3f73-49a2-9341-e9c58054c197}",
    },
    "jbe_testrunresult": {
        "active":   "{958afdaf-4a02-486b-9dff-88c9c34026e0}",
        "inactive": "{7d4d1594-e082-4deb-9999-713a08349052}",
        "quickfind":"{c419bdb9-6a10-4eee-89ba-6bf7cd7e873a}",
        "assoc":    "{d45254f7-cd44-42c2-b032-53ac117f35fc}",
        "lookup":   "{a5a29b63-604e-4e6d-aaf2-805090ec285a}",
        "advfind":  "{58feaf85-cf2a-4a03-ae40-f159960cbf7b}",
    },
    "jbe_teststep": {
        "active":   "{ac43f2ee-e5dc-49a5-b6c0-355b85b0d563}",
        "inactive": "{a8c827d1-4a57-4b12-bf51-e3516a602fa8}",
        "quickfind":"{f277312d-30d0-44dc-b474-ac9ab00faecc}",
        "assoc":    "{bc6dc6ce-e8fd-46e5-bbf1-d9d20cd2c0f1}",
        "lookup":   "{73ac77f1-fcfe-49ee-a2c3-3d3abf11afaa}",
        "advfind":  "{be9c1459-a97b-4219-a10c-5ceb811dbb4b}",
    },
}

# QueryTypes
QT_MAIN = 0
QT_ADVFIND = 1
QT_ASSOC = 2
QT_LOOKUP = 64
QT_QUICKFIND = 4

# ViewSpec: (name_de, name_en, querytype, isdefault, layout_cells, fetch_filter_xml, order_attr, order_desc, jump_attr)
SPECS = {
    "jbe_testcase": {
        "active": (
            "Aktive Testfälle", "Active Test Cases", QT_MAIN, True,
            [("jbe_testid",110), ("jbe_title",250), ("jbe_category",140),
             ("jbe_tags",180), ("jbe_userstories",180), ("jbe_enabled",80),
             ("modifiedon",120)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "jbe_testid", False, "jbe_testid"),
        "inactive": (
            "Inaktive Testfälle", "Inactive Test Cases", QT_MAIN, False,
            [("jbe_testid",110), ("jbe_title",250), ("jbe_category",140),
             ("jbe_tags",180), ("jbe_enabled",80), ("modifiedon",120)],
            '<condition attribute="statecode" operator="eq" value="1" />',
            "jbe_testid", False, "jbe_testid"),
        "quickfind": (
            "Schnellsuche Aktive Testfälle", "Quick Find Active Test Cases", QT_QUICKFIND, True,
            [("jbe_testid",110), ("jbe_title",250), ("jbe_category",140),
             ("jbe_tags",180), ("jbe_userstories",180)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "jbe_testid", False, "jbe_testid",
            # QuickFind hat spezielle FindColumns
            ["jbe_testid", "jbe_title", "jbe_name", "jbe_tags", "jbe_userstories"]),
        "assoc": (
            "Zugeordnete Testfälle", "Associated Test Cases", QT_ASSOC, True,
            [("jbe_testid",110), ("jbe_title",250), ("jbe_category",140),
             ("jbe_enabled",80)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "jbe_testid", False, "jbe_testid"),
    },
    "jbe_testrun": {
        "active": (
            "Aktive Testläufe", "Active Test Runs", QT_MAIN, True,
            [("jbe_name",220), ("jbe_teststatus",110), ("jbe_total",70),
             ("jbe_passed",70), ("jbe_failed",70), ("jbe_testcasefilter",160),
             ("jbe_startedon",140), ("jbe_completedon",140)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "createdon", True, "jbe_name"),
        "inactive": (
            "Inaktive Testläufe", "Inactive Test Runs", QT_MAIN, False,
            [("jbe_name",220), ("jbe_teststatus",110), ("jbe_total",70),
             ("jbe_passed",70), ("jbe_failed",70), ("modifiedon",140)],
            '<condition attribute="statecode" operator="eq" value="1" />',
            "modifiedon", True, "jbe_name"),
        "quickfind": (
            "Schnellsuche Aktive Testläufe", "Quick Find Active Test Runs", QT_QUICKFIND, True,
            [("jbe_name",220), ("jbe_teststatus",110), ("jbe_testcasefilter",160),
             ("jbe_total",70), ("jbe_passed",70), ("jbe_failed",70)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "createdon", True, "jbe_name",
            ["jbe_name", "jbe_testcasefilter"]),
        "assoc": (
            "Zugeordnete Testläufe", "Associated Test Runs", QT_ASSOC, True,
            [("jbe_name",220), ("jbe_teststatus",110), ("jbe_total",70),
             ("jbe_passed",70), ("jbe_failed",70), ("createdon",140)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "createdon", True, "jbe_name"),
    },
    "jbe_testrunresult": {
        "active": (
            "Aktive Testergebnisse", "Active Test Run Results", QT_MAIN, True,
            [("jbe_testid",100), ("jbe_outcome",100), ("jbe_durationms",90),
             ("jbe_errormessage",300), ("jbe_testrunid",200), ("createdon",140)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "createdon", True, "jbe_name"),
        "inactive": (
            "Inaktive Testergebnisse", "Inactive Test Run Results", QT_MAIN, False,
            [("jbe_testid",100), ("jbe_outcome",100), ("jbe_testrunid",200),
             ("modifiedon",140)],
            '<condition attribute="statecode" operator="eq" value="1" />',
            "modifiedon", True, "jbe_name"),
        "quickfind": (
            "Schnellsuche Aktive Testergebnisse", "Quick Find Active Test Run Results", QT_QUICKFIND, True,
            [("jbe_testid",100), ("jbe_outcome",100), ("jbe_durationms",90),
             ("jbe_errormessage",300), ("jbe_testrunid",200)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "createdon", True, "jbe_name",
            ["jbe_testid", "jbe_name", "jbe_errormessage"]),
        "assoc": (
            "Zugeordnete Testergebnisse", "Associated Test Run Results", QT_ASSOC, True,
            [("jbe_testid",100), ("jbe_outcome",100), ("jbe_durationms",90),
             ("jbe_errormessage",300)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "jbe_testid", False, "jbe_name"),
    },
    "jbe_teststep": {
        "active": (
            "Aktive Testschritte", "Active Test Steps", QT_MAIN, True,
            [("jbe_stepnumber",70), ("jbe_phase",100), ("jbe_name",200),
             ("jbe_action",120), ("jbe_entity",130), ("jbe_stepstatus",100),
             ("jbe_durationms",90), ("jbe_testrunresultid",200)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "jbe_stepnumber", False, "jbe_name"),
        "inactive": (
            "Inaktive Testschritte", "Inactive Test Steps", QT_MAIN, False,
            [("jbe_stepnumber",70), ("jbe_phase",100), ("jbe_name",200),
             ("jbe_action",120), ("jbe_stepstatus",100), ("modifiedon",140)],
            '<condition attribute="statecode" operator="eq" value="1" />',
            "modifiedon", True, "jbe_name"),
        "quickfind": (
            "Schnellsuche Aktive Testschritte", "Quick Find Active Test Steps", QT_QUICKFIND, True,
            [("jbe_stepnumber",70), ("jbe_phase",100), ("jbe_name",200),
             ("jbe_action",120), ("jbe_entity",130), ("jbe_stepstatus",100)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "jbe_stepnumber", False, "jbe_name",
            ["jbe_name", "jbe_action", "jbe_entity", "jbe_alias", "jbe_errormessage"]),
        "assoc": (
            "Zugeordnete Testschritte", "Associated Test Steps", QT_ASSOC, True,
            [("jbe_stepnumber",60), ("jbe_phase",100), ("jbe_name",220),
             ("jbe_action",120), ("jbe_stepstatus",100), ("jbe_durationms",80)],
            '<condition attribute="statecode" operator="eq" value="0" />',
            "jbe_stepnumber", False, "jbe_name"),
    },
}

# Neue Views (neue GUIDs, neue Datei)
NEW_VIEWS = {
    "jbe_testrun": [
        {
            "id": sid("jbe_testrun:active-running"),
            "spec": (
                "Laufende Testläufe", "Running Test Runs", QT_MAIN, False,
                [("jbe_name",220), ("jbe_teststatus",110), ("jbe_total",70),
                 ("jbe_passed",70), ("jbe_failed",70), ("jbe_testcasefilter",160),
                 ("jbe_startedon",140)],
                # Status != Completed
                '<condition attribute="statecode" operator="eq" value="0" />'
                '<condition attribute="jbe_teststatus" operator="ne" value="105710002" />',
                "jbe_startedon", True, "jbe_name"),
        },
    ],
    "jbe_testrunresult": [
        {
            "id": sid("jbe_testrunresult:failed"),
            "spec": (
                "Fehlgeschlagene Ergebnisse", "Failed Test Run Results", QT_MAIN, False,
                [("jbe_testid",100), ("jbe_outcome",100), ("jbe_errormessage",400),
                 ("jbe_durationms",90), ("jbe_testrunid",200), ("createdon",140)],
                '<condition attribute="statecode" operator="eq" value="0" />'
                '<condition attribute="jbe_outcome" operator="in">'
                '<value>105710001</value><value>105710003</value>'
                '</condition>',
                "createdon", True, "jbe_name"),
        },
    ],
}

# ------------------ Builder ------------------

def build_savedquery(sqid, entity, pk_attr, spec):
    """spec ist Tuple (de, en, qt, isdefault, cells, filter_xml, order, desc, jump, [findcolumns])"""
    de, en, qt, isdefault = spec[0], spec[1], spec[2], spec[3]
    cells, filter_xml, order_attr, order_desc, jump_attr = spec[4], spec[5], spec[6], spec[7], spec[8]
    find_columns = spec[9] if len(spec) >= 10 else None

    cells_xml = "\n".join(f'          <cell name="{n}" width="{w}" />' for n,w in cells)
    layoutxml = (f'      <grid name="resultset" object="0" jump="{jump_attr}" select="1" icon="1" preview="1">\n'
                 f'        <row name="result" id="{pk_attr}">\n{cells_xml}\n'
                 f'        </row>\n'
                 f'      </grid>')

    attrs_fetch = "\n".join(f'          <attribute name="{n}" />' for n,_ in cells if n != pk_attr)
    order_xml = f'<order attribute="{order_attr}" descending="{"true" if order_desc else "false"}" />'
    fetchxml = (f'      <fetch version="1.0" mapping="logical">\n'
                f'        <entity name="{entity}">\n'
                f'          <attribute name="{pk_attr}" />\n'
                f'{attrs_fetch}\n'
                f'          <filter type="and">\n'
                f'            {filter_xml}\n'
                f'          </filter>\n'
                f'          {order_xml}\n'
                f'        </entity>\n'
                f'      </fetch>')

    isdefault_s = "1" if isdefault else "0"
    isquickfind = "1" if qt == QT_QUICKFIND else "0"

    # Fuer QuickFind brauchen wir isquickfindquery=1 + FindColumns
    # (FindColumns sind in FetchXml via Filter mit dynamischen Parametern normalerweise, aber
    # hier reicht unser Static-Filter; QuickFind-Matcher nutzen die attribute-Liste fetchxml-seitig)
    quickfind_addon = ""
    if qt == QT_QUICKFIND and find_columns:
        # QuickFind-Syntax: filter attribute die "LIKE" suchen (Dataverse baut das on-the-fly)
        # Nur Namen sind noetig, Plattform macht den Rest
        fc_xml = "\n".join(f'          <condition attribute="{c}" operator="like" value="{{0}}" />' for c in find_columns)
        # wir ergaenzen eine zweite filter-Sektion type="or" mit like-Conditions
        fetchxml = fetchxml.replace(
            f'            {filter_xml}\n          </filter>',
            f'            {filter_xml}\n            <filter type="or">\n{fc_xml}\n            </filter>\n          </filter>')

    xml = f"""<?xml version="1.0" encoding="utf-8"?>
<savedqueries xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <savedquery>
    <IsCustomizable>1</IsCustomizable>
    <CanBeDeleted>1</CanBeDeleted>
    <isquickfindquery>{isquickfind}</isquickfindquery>
    <isprivate>0</isprivate>
    <isdefault>{isdefault_s}</isdefault>
    <savedqueryid>{sqid}</savedqueryid>
    <layoutxml>
{layoutxml}
    </layoutxml>
    <querytype>{qt}</querytype>
    <fetchxml>
{fetchxml}
    </fetchxml>
    <IntroducedVersion>1.0.0.0</IntroducedVersion>
    <LocalizedNames>
      <LocalizedName description="{de}" languagecode="1031" />
      <LocalizedName description="{en}" languagecode="1033" />
    </LocalizedNames>
  </savedquery>
</savedqueries>
"""
    return xml

PK_ATTR = {
    "jbe_testcase": "jbe_testcaseid",
    "jbe_testrun": "jbe_testrunid",
    "jbe_testrunresult": "jbe_testrunresultid",
    "jbe_teststep": "jbe_teststepid",
}

for entity, views in SPECS.items():
    sq_dir = ROOT / entity / "SavedQueries"
    if not sq_dir.exists():
        print(f"  [{entity}] SavedQueries-Ordner fehlt")
        continue
    for role, spec in views.items():
        sqid = EXISTING[entity][role]
        xml = build_savedquery(sqid, entity, PK_ATTR[entity], spec)
        target = sq_dir / f"{sqid}.xml"
        target.write_text(xml, encoding="utf-8")
        print(f"  [{entity}] {role}: {spec[1]}")

for entity, items in NEW_VIEWS.items():
    sq_dir = ROOT / entity / "SavedQueries"
    for it in items:
        xml = build_savedquery(it["id"], entity, PK_ATTR[entity], it["spec"])
        target = sq_dir / f"{it['id']}.xml"
        target.write_text(xml, encoding="utf-8")
        print(f"  [{entity}] NEW: {it['spec'][1]}  ({it['id']})")

print("SavedQueries built.")
