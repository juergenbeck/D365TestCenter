#!/usr/bin/env python3
"""
Reichert alle jbe_*-OptionSet-XML-Dateien idempotent mit i18n-Labels an
und fixt Umlaut-Verstoesse. Wird nach jedem `pac solution unpack` ausgefuehrt.
"""
from pathlib import Path

OPTIONSETS = Path(r"C:\Users\Juerg\Source\repo\D365TestCenter\solution\src\OptionSets")

# Vollstaendige Ziel-Definitionen (keine Diffs, sondern Full-Rewrite).
# So ist das Ergebnis garantiert exakt gewuenscht und idempotent.

TEMPLATE = """<?xml version="1.0" encoding="utf-8"?>
<optionset Name="{name}" localizedName="{localized}" description="{desc_en}" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <OptionSetType>picklist</OptionSetType>
  <IsGlobal>1</IsGlobal>
  <IntroducedVersion>1.0.0.0</IntroducedVersion>
  <IsCustomizable>1</IsCustomizable>
  <displaynames>
    <displayname description="{dn_de}" languagecode="1031" />
    <displayname description="{dn_en}" languagecode="1033" />
  </displaynames>
  <Descriptions>
    <Description description="{desc_de}" languagecode="1031" />
    <Description description="{desc_en}" languagecode="1033" />
  </Descriptions>
  <options>
{options}
  </options>
</optionset>"""

OPT_TEMPLATE = """    <option value="{value}" ExternalValue="" IsHidden="0">
      <labels>
        <label description="{de}" languagecode="1031" />
        <label description="{en}" languagecode="1033" />
      </labels>
    </option>"""

DEFINITIONS = {
    "jbe_teststatus": {
        "localized": "Test Status",
        "dn_de": "Teststatus", "dn_en": "Test Status",
        "desc_de": "Status eines Testlaufs.", "desc_en": "Status of a test run.",
        "options": [
            (105710000, "Ausstehend", "Pending"),
            (105710001, "Läuft", "Running"),
            (105710002, "Abgeschlossen", "Completed"),
            (105710003, "Fehler", "Error"),
        ],
    },
    "jbe_testoutcome": {
        "localized": "Test Outcome",
        "dn_de": "Testergebnis", "dn_en": "Test Outcome",
        "desc_de": "Ergebnis einer einzelnen Testfall-Ausführung.",
        "desc_en": "Outcome of a single test case execution.",
        "options": [
            (105710000, "Bestanden", "Passed"),
            (105710001, "Fehlgeschlagen", "Failed"),
            (105710002, "Übersprungen", "Skipped"),
            (105710003, "Fehler", "Error"),   # Neu in v5.4
        ],
    },
    "jbe_stepstatus": {
        "localized": "Step Status",
        "dn_de": "Schrittstatus", "dn_en": "Step Status",
        "desc_de": "Status eines Testschritts.", "desc_en": "Status of a test step.",
        "options": [
            (105710000, "Erfolgreich", "Success"),
            (105710001, "Fehlgeschlagen", "Failed"),
            (105710002, "Übersprungen", "Skipped"),
        ],
    },
    "jbe_stepphase": {
        "localized": "Step Phase",
        "dn_de": "Schrittphase", "dn_en": "Step Phase",
        "desc_de": "Phase eines Testschritts.", "desc_en": "Phase of a test step.",
        "options": [
            (105710000, "Vorbedingung", "Precondition"),
            (105710001, "Schritt", "Step"),
            (105710002, "Prüfung", "Assertion"),
            (105710003, "Aufräumen", "Cleanup"),
        ],
    },
    "jbe_testcategory": {
        "localized": "Test Category",
        "dn_de": "Testkategorie", "dn_en": "Test Category",
        "desc_de": "Kategorie eines Testfalls.", "desc_en": "Category of a test case.",
        "options": [
            (105710000, "Quelle aktualisieren", "Update Source"),
            (105710001, "Quelle erstellen", "Create Source"),
            (105710002, "Quelle löschen", "Delete Source"),
            (105710003, "Multi-Quelle", "Multi-Source"),
            (105710004, "Merge", "Merge"),
            (105710005, "Custom API", "Custom API"),
            (105710006, "Konfiguration", "Config"),
            (105710007, "End-to-End", "End-to-End"),
            (105710008, "Fehlerbehandlung", "Error Handling"),
        ],
    },
}

for name, cfg in DEFINITIONS.items():
    opts_xml = "\n".join(
        OPT_TEMPLATE.format(value=v, de=de, en=en) for v, de, en in cfg["options"]
    )
    xml = TEMPLATE.format(
        name=name, localized=cfg["localized"],
        dn_de=cfg["dn_de"], dn_en=cfg["dn_en"],
        desc_de=cfg["desc_de"], desc_en=cfg["desc_en"],
        options=opts_xml,
    )
    target = OPTIONSETS / f"{name}.xml"
    target.write_text(xml, encoding="utf-8")
    print(f"  {name}.xml: {len(cfg['options'])} Optionen")
print("OptionSets enriched.")
