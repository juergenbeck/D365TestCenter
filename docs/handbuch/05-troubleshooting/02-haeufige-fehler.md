# Haeufige Fehler

Die Top-Liste der wiederkehrenden Probleme. Jeder Eintrag hat Symptom,
Ursache, Fix, und ein verwandtes Kapitel im Handbuch.

## 1. "Unbekannte Step-Action: Assert" oder aehnlich

**Symptom:** Test scheitert sofort in Step 1 oder 2 mit einer Meldung
ueber "Unbekannte Action".

**Ursache:** Platform-Problem. Der auf der Umgebung installierte Plugin-
Code ist zu alt und kennt die Action nicht. Nicht dein Fehler.

**Fix:** Projekt-Owner kontaktieren. Kannst du nichts machen.

## 2. "Lookup attribute XYZ does not exist"

**Symptom:**

```
HTTP 400: Attribute 'parentcustomerid_account' does not exist on type 'contact'.
```

**Ursache:** Der Lookup-Feldname in `fields` ist nicht korrekt formatiert.

**Fix:** Das Muster ist `<lookupname>_<zielentity>@odata.bind`. Also
z.B.:

```json
// RICHTIG
"parentcustomerid_account@odata.bind": "/accounts({acc.id})"

// FALSCH (fehlt Suffix)
"parentcustomerid@odata.bind": "/accounts({acc.id})"

// FALSCH (Zielentity im Singular)
"parentcustomerid_account@odata.bind": "/account({acc.id})"
```

Siehe [../02-testfall-schreiben/04-lookup-und-binding.md](../02-testfall-schreiben/04-lookup-und-binding.md).

## 3. Umlaute werden zu Fragezeichen oder ae/oe/ue

**Symptom:** Du schreibst `"name": "Müller"` ins JSON, der Record hat
aber `"name": "M?ller"` oder `"name": "Mueller"`.

**Ursache:** Das Feld `jbe_definitionjson` hat eine UTF-8-Codierung im
Browser gesendet, irgendwo aber ist ein Zwischenschritt mit falscher
Codierung.

**Fix:** 

- Text direkt im Browser-Formular eintippen (nicht pasten aus einem
  cp1252-Tool).
- Oder: aus VS Code (UTF-8 default) kopieren.
- Pruefe `$PSVersionTable.PSVersion`: wenn PowerShell < 7.4, laesst sich
  das Problem durch explizites `-ContentType "application/json; charset=utf-8"`
  loesen — betrifft dich aber nur wenn du PS-Skripte fuer das Anlegen
  verwendest.

## 4. "Alias 'xxx' existiert nicht"

**Symptom:**

```
Step 3: UpdateRecord: Alias 'con' existiert nicht oder wurde nicht
erfolgreich registriert.
```

**Ursache 1:** Tippfehler im Alias.

**Ursache 2:** Der Create-Step, der den Alias vergibt, ist vorher selbst
gescheitert (oft sichtbar als Error in einer frueheren Step).

**Fix:** Alias-Schreibweisen im JSON ueberall vergleichen (case-
sensitive!). Die frueheren Steps auf rotes Ergebnis pruefen — bei Error
ist `con` nie angelegt worden.

## 5. `Assert` mit `target: Record` liefert `null`, obwohl Feld gesetzt ist

**Symptom:** Der Record existiert im Browser, das Feld hat einen Wert.
Der Assert sagt aber:

```
Erwartet:     Anna
Tatsaechlich: null
```

**Ursache:** Bei `target: Record` verlaesst sich die Engine oft auf den
Cache vom letzten `CreateRecord`. Wenn dazwischen ein Plugin das Feld
geaendert hat, sieht der Record-Assert den alten Cache.

**Fix:** 

- Option A: `RetrieveRecord` vor dem Assert einschieben, `columns` mit
  dem Feld setzen.
- Option B: auf `target: Query` umstellen — Query liest immer frisch.

```json
{ "action": "RetrieveRecord", "alias": "con", "columns": ["firstname"] },
{ "action": "Assert", "target": "Record", "recordRef": "{RECORD:con}",
  "field": "firstname", "operator": "Equals", "value": "Anna" }
```

## 6. `Wait` von 3 Sekunden reicht nicht, aber 30 sind zu viel

**Symptom:** Test funktioniert manchmal, manchmal nicht. Mit 3s `Wait`
schlaegt er haeufig fehl, mit 30s wird er super langsam.

**Ursache:** Blindes `Wait` ist nicht das richtige Werkzeug fuer
asynchrone Plugins.

**Fix:** Umstellen auf `WaitForFieldValue`:

```json
{ "action": "WaitForFieldValue", "alias": "con",
  "fields": { "markant_department": "Einkauf" },
  "timeoutSeconds": 30 }
```

Das Polling wartet **so lange wie noetig** aber nicht laenger.

Siehe [../02-testfall-schreiben/02-actions-referenz.md#waitforfieldvalue](../02-testfall-schreiben/02-actions-referenz.md#waitforfieldvalue).

## 7. "JSON-Parse-Fehler: line X, col Y"

**Symptom:** Tests sind Skipped mit einer Parse-Fehlermeldung.

**Ursache:** JSON-Syntaxfehler im `jbe_definitionjson`.

**Fix:**

1. Feld aus der App kopieren.
2. In VS Code / JSONLint einfuegen.
3. Typische Fehler:
   - Fehlende Kommas zwischen Array-Elementen
   - Einfache statt doppelte Anfuehrungszeichen (`'foo'` -> `"foo"`)
   - Trailing Comma nach letztem Element (`[1,2,]` -> `[1,2]`)
   - Kommentare `//` oder `/* */` (JSON kennt keine)
   - Escaping fehlt: `"text mit \"Anfuehrungszeichen\""`

## 8. "enabled = false" und der Test laeuft nicht

**Symptom:** Dein Filter trifft den Test, aber er taucht im Testrun-
Ergebnis nicht auf. `Gesamt` ist kleiner als erwartet.

**Ursache:** `jbe_enabled = Nein` im Testcase-Record oder `"enabled":
false` im JSON.

**Fix:** In der App das Feld **Aktiviert** anhaken. Oder im JSON
`"enabled": true` setzen.

## 9. Record-Assert nach Delete wirft Fehler

**Symptom:**

```
Step 4: Assert (target: Record): recordRef '...' konnte nicht geladen werden.
HTTP 404: Record not found.
```

**Ursache:** Du hast den Record vorher geloescht und versuchst jetzt auf
ihn zu assertieren. `target: Record` kann geloeschte Records nicht lesen.

**Fix:** Umstellen auf `target: Query` mit `operator: NotExists`:

```json
{ "action": "Assert", "target": "Query", "entity": "tasks",
  "filter": [ { "field": "activityid", "operator": "eq", "value": "{tsk.id}" } ],
  "operator": "NotExists",
  "description": "Task ist weg", "onError": "continue" }
```

## 10. Assert ist "gruen" obwohl der Test falsch ist

**Symptom:** Der Test ist `Passed`, aber du weisst aus dem Browser dass
das Verhalten kaputt ist.

**Ursache:** Test-Coverage ist zu duenn. Alle Asserts "zufaellig" OK,
weil sie das Nicht-funktionierende gar nicht pruefen.

**Fix:** Mehr Asserts, besonders **negative Erwartungen** (`IsNull`,
`NotExists`, `!= alter Wert`). Siehe
[../02-testfall-schreiben/06-coverage-regeln.md](../02-testfall-schreiben/06-coverage-regeln.md).

## 11. Filter findet zu viele Treffer

**Symptom:** Du hast Filter `STD-01` gemeint, aber der Run laeuft mit
20 Tests.

**Ursache:** Filter-Matching ist **Praefix-basiert** bei Wildcards.
`STD-01` matcht exakt `STD-01`. `STD-*` matcht alles was mit `STD-`
anfaengt: `STD-01`, `STD-02`, `STD-01-v2`, ...

**Fix:** Pruefe den Filter genau. Fuer exakte Mehrfach-Treffer:
`STD-01,STD-02` (Komma-getrennt).

## 12. Cleanup-Step ist rot

**Symptom:** Dein Test ist Passed, aber Step 9000 (Cleanup) zeigt
"Fehler".

**Ursache:** Ein Record konnte nicht geloescht werden. Oft weil:

- Ein Plugin hat Reject auf Delete (z.B. Merge-Plugin bei
  deaktiviertem Duplicate-Record).
- Der Record war bereits weg (Cascade hat ihn entfernt).
- Permissions.

**Fix:** Meistens ignorieren. Der Cleanup-Fehler beeintraechtigt dein
Test-Ergebnis nicht. Wenn dich die Record-Leichen stoeren: einzeln im
Browser loeschen, oder den Projekt-Owner bitten die Cleanup-Logik
anzupassen.

## Fuer Infrastruktur-Probleme

Wenn du hier nichts findest und der Fehler mysterioes ist — insbesondere
bei wiederholten Sandbox-Timeouts oder "Plugin nicht gefunden" —
Projekt-Owner kontaktieren. Diese Klasse von Fehlern ist nichts was
Test-Autoren reparieren koennen.
