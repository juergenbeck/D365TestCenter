"""
Migriert Testfall-JSONs vom Vor-ADR-0004-Format auf das neue Schema:
alte 3 Listen (preconditions / steps / assertions) werden zu einer
einzigen Steps-Liste zusammengefuehrt.

Regeln:
- Jede precondition wird ein CreateRecord-Step am Anfang der Steps-Liste.
- Jede assertion wird ein Assert-Step am Ende der Steps-Liste.
- StepNumbers werden neu vergeben: 1..N (durchlaufend).
- Datei wird in-place ueberschrieben.
- Idempotent: wenn weder preconditions noch assertions vorhanden sind,
  bleibt die Datei unveraendert.

Behandelt beide Root-Formen:
- Einzel-Testfall: {"testId":"...","preconditions":[...],"steps":[...],"assertions":[...]}
- TestSuite: {"suiteId":"...","testCases":[{...},{...}]}
- Pack-Format (Arrays): [{testId:...,...}, {testId:...,...}]

Einsatz:
  python scripts/migrate-testcase-schema-v2.py <root-pfad>
  python scripts/migrate-testcase-schema-v2.py --dry-run <root-pfad>
"""

import argparse
import json
import sys
from pathlib import Path


def migrate_testcase(tc: dict) -> bool:
    """Transformiert einen einzelnen Testfall in-place. Gibt True zurueck wenn
    etwas migriert wurde, False wenn der Testfall bereits im neuen Format war."""

    has_pre = "preconditions" in tc and tc["preconditions"]
    has_asserts = "assertions" in tc and tc["assertions"]

    # leere Arrays / leere Objekte {} aus Altformat einfach entfernen
    if "preconditions" in tc and not has_pre:
        del tc["preconditions"]
    if "assertions" in tc and not has_asserts:
        del tc["assertions"]

    if not has_pre and not has_asserts:
        return False

    old_steps = tc.get("steps", [])
    new_steps: list[dict] = []

    # 1) Preconditions → CreateRecord-Steps
    preconditions = tc.get("preconditions", []) or []
    if isinstance(preconditions, dict):
        # FG-TestTool-Legacy-Objekt-Form {createContact: true, ...}
        # wird nicht automatisch uebersetzt — hier ist manuelle Arbeit noetig.
        # Wir markieren das und ueberspringen.
        print(f"  WARN: Testfall '{tc.get('testId', tc.get('id', '?'))}' hat "
              f"preconditions als OBJEKT (FG-TestTool-Form). Nicht migriert.",
              file=sys.stderr)
        return False

    for pre in preconditions:
        step = {
            "action": "CreateRecord",
        }
        if "entity" in pre:
            step["entity"] = pre["entity"]
        if "alias" in pre:
            step["alias"] = pre["alias"]
        if "fields" in pre:
            step["fields"] = pre["fields"]
        if "columns" in pre and pre["columns"]:
            step["columns"] = pre["columns"]
        # waitForAsync wird explizit durchgereicht (wird vom Runner bisher
        # als Hint auf waitForGovernance-artige Polling-Logik gelesen)
        if pre.get("waitForAsync"):
            step["waitForAsync"] = True
        new_steps.append(step)

    # 2) Original-Steps beibehalten (die Action-Semantik stimmt schon)
    for step in old_steps:
        new_steps.append(step)

    # 3) Assertions → Assert-Steps
    assertions = tc.get("assertions", []) or []
    for ast in assertions:
        step = {
            "action": "Assert",
        }
        # Alle relevanten Felder aus der alten Assertion uebernehmen
        for key in ("target", "field", "entity", "recordRef", "filter",
                    "operator", "value", "description"):
            if key in ast and ast[key] is not None:
                step[key] = ast[key]
        # Explicit onError=continue setzen um das historische Assert-Verhalten
        # (Test laeuft weiter, sammelt Failures) auch nach ADR-0004 beizubehalten.
        step["onError"] = "continue"
        new_steps.append(step)

    # 4) StepNumbers neu vergeben 1..N
    for idx, step in enumerate(new_steps, start=1):
        step["stepNumber"] = idx

    # 5) Aufraeumen auf Testcase-Ebene
    tc["steps"] = new_steps
    tc.pop("preconditions", None)
    tc.pop("assertions", None)
    return True


def migrate_file(path: Path, dry_run: bool) -> tuple[int, int]:
    """Migriert eine einzelne JSON-Datei. Gibt (migrated_count, total_count)
    zurueck."""
    try:
        raw = path.read_text(encoding="utf-8")
    except Exception as e:
        print(f"  SKIP {path} (read: {e})", file=sys.stderr)
        return 0, 0

    try:
        data = json.loads(raw)
    except json.JSONDecodeError as e:
        print(f"  SKIP {path} (invalid JSON: {e})", file=sys.stderr)
        return 0, 0

    migrated = 0
    total = 0

    # Root kann sein:
    #   a) Einzel-Testfall (hat testId oder id auf Top-Level)
    #   b) Suite: {"suiteId": ..., "testCases": [...]}
    #   c) Pack-Array: [{testId:...}, {testId:...}]

    def handle_testcase(tc: dict) -> None:
        """Testfall im aktuellen Schema (testId/id auf Top-Level)."""
        nonlocal migrated, total
        total += 1
        if migrate_testcase(tc):
            migrated += 1

    def handle_dataverse_record(rec: dict) -> None:
        """
        Testfall als Dataverse-Record-Wrapper: jbe_testcaseid, jbe_testid,
        jbe_title, jbe_definitionjson (Objekt oder String). Kommt in
        Pack-Dateien wie empty.json und standard.json vor.
        """
        nonlocal migrated, total
        total += 1
        def_field = rec.get("jbe_definitionjson")
        if def_field is None:
            return
        # Kann String (JSON-enkapsuliert) oder direkt Objekt sein
        if isinstance(def_field, str):
            try:
                def_obj = json.loads(def_field)
            except json.JSONDecodeError:
                return
            if migrate_testcase(def_obj):
                rec["jbe_definitionjson"] = json.dumps(
                    def_obj, ensure_ascii=False, separators=(",", ":"))
                migrated += 1
        elif isinstance(def_field, dict):
            if migrate_testcase(def_field):
                migrated += 1

    if isinstance(data, dict):
        if "testCases" in data and isinstance(data["testCases"], list):
            for tc in data["testCases"]:
                if not isinstance(tc, dict):
                    continue
                if "testId" in tc or "id" in tc:
                    handle_testcase(tc)
                elif "jbe_testid" in tc or "jbe_definitionjson" in tc:
                    handle_dataverse_record(tc)
        elif "testId" in data or "id" in data:
            handle_testcase(data)
        elif "jbe_testid" in data or "jbe_definitionjson" in data:
            handle_dataverse_record(data)
        else:
            # Unklares Format — z.B. Pack-Manifest oder Metadaten; ueberspringen
            return 0, 0
    elif isinstance(data, list):
        for tc in data:
            if not isinstance(tc, dict):
                continue
            if "testId" in tc or "id" in tc:
                handle_testcase(tc)
            elif "jbe_testid" in tc or "jbe_definitionjson" in tc:
                handle_dataverse_record(tc)

    if migrated > 0 and not dry_run:
        # Einheitliche Formatierung: 2 Leerzeichen Einrueckung, ensure_ascii=False
        path.write_text(
            json.dumps(data, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8"
        )

    return migrated, total


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", help="Verzeichnis mit JSON-Testfaellen (rekursiv)")
    parser.add_argument("--dry-run", action="store_true", help="Nur anzeigen was migriert wuerde")
    args = parser.parse_args()

    root = Path(args.root)
    if not root.exists():
        print(f"Pfad nicht gefunden: {root}", file=sys.stderr)
        return 2

    total_files = 0
    files_migrated = 0
    total_tcs = 0
    tcs_migrated = 0

    for json_path in root.rglob("*.json"):
        # Manifest-Dateien und Demo-Metadata ueberspringen
        name_lower = json_path.name.lower()
        if name_lower in ("manifest.json", "package.json"):
            continue
        if name_lower.startswith("demo-"):
            continue

        total_files += 1
        m, t = migrate_file(json_path, args.dry_run)
        total_tcs += t
        tcs_migrated += m
        if m > 0:
            files_migrated += 1
            marker = "DRY" if args.dry_run else "OK "
            print(f"  [{marker}] {json_path} -- {m}/{t} TestCases migriert")

    print()
    print(f"Dateien gesamt: {total_files}")
    print(f"Dateien mit Migration: {files_migrated}")
    print(f"TestCases gesamt: {total_tcs}")
    print(f"TestCases migriert: {tcs_migrated}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
