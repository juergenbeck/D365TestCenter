# Negative-Path-Tests mit `expectFailure`

Manchmal ist das **erwartete Ergebnis ein Fehler**: ein Plugin-Guard
soll eine Reaktivierung blockieren, ein ReadOnly-Feld soll ein Update
ablehnen, eine Validation-Rule soll greifen. Solche Tests sind genauso
wichtig wie Happy-Path-Tests, weil sie die Schutzmaßnahmen der
Anwendung beweisen.

Das Test Center kennt dafür ab Plugin v5.3 zwei optionale Felder auf
allen Non-Assert-Steps: `expectFailure` und `expectException`.

## Variante 1 — `expectFailure: true` (irgendein Fehler reicht)

```json
{ "stepNumber": 9, "action": "UpdateRecord",
  "alias": "testContact",
  "fields": { "statecode": 0, "statuscode": 1 },
  "expectFailure": true,
  "description": "Reaktivierung muss durch GDPR-Guard blockiert werden" }
```

**Semantik:**

| Laufzeit | Step-Status | Test-Outcome-Beitrag |
|---|---|---|
| Step wirft Exception | `Passed` | kein Failed-Beitrag |
| Step läuft ohne Exception | `Failed` mit Message "Expected exception but action succeeded" | Test-Outcome wird `Failed` |

`expectFailure` impliziert dass der Test **nach** diesem Step weiter
laufen soll (kein impliziter Test-Abbruch). Wenn man explizit nach dem
erwarteten Fehler abbrechen will, kombiniert man mit `"onError": "stop"`.

## Variante 2 — `expectException` (mit Match auf die Fehlermeldung)

Wenn man die Exception genauer prüfen will (welcher Guard greift,
welcher Error-Code, welcher HTTP-Status):

```json
{ "stepNumber": 9, "action": "UpdateRecord",
  "alias": "testContact",
  "fields": { "statecode": 0, "statuscode": 1 },
  "expectException": {
    "messageContains": "can only be reactivated by a GDPR admin",
    "errorCode": "0x80040227"
  },
  "description": "GDPR-Reaktivierungs-Guard greift mit konkretem Code" }
```

`expectException` impliziert `expectFailure: true` automatisch.

### Match-Felder

Alle Felder optional, mehrere gesetzte Felder werden mit **AND**
verknüpft.

| Feld | Typ | Semantik |
|---|---|---|
| `messageContains` | String | Substring im Message-Text (case-insensitive) |
| `messageMatches` | Regex | Regex-Match im Message-Text (case-insensitive) |
| `errorCode` | String | Dataverse-Error-Code z.B. `0x80040227` |
| `httpStatus` | Integer | HTTP-Status der API-Antwort |

**Exklusiv:** `messageContains` und `messageMatches` dürfen nicht
gleichzeitig gesetzt sein — beim Parsen kommt sonst ein
Validierungsfehler.

### Wann welche Match-Form?

- **`messageContains`** — wenn der Message-Text stabil ist und du nur
  ein Stichwort prüfen willst. Häufigster Fall.
- **`messageMatches`** — wenn der Text Variationen hat (z.B. mit
  GUID/Datum), aber das Muster gleich ist.
- **`errorCode`** — robust gegen Textänderungen. Empfohlen für
  langfristig stabile Tests.
- **`httpStatus`** — selten nötig. Sinnvoll bei Web-API-spezifischen
  Tests die zwischen 400 (Validierungsfehler) und 403 (Berechtigung)
  unterscheiden müssen.

**Empfehlung:** `errorCode` + `messageContains` zusammen ist die stärkste
Kombination — Code für Stabilität, Message für Lesbarkeit.

```json
"expectException": {
  "errorCode": "0x80040227",
  "messageContains": "GDPR admin"
}
```

## Vollständiges Beispiel — DSGVO-Negative-Path mit EnvVar

Kombiniert mit `SetEnvironmentVariable` ergibt sich ein vollständiger
Negative-Path-Test:

```json
{
  "testId": "GDPR-G-NEG-C01",
  "title": "ReactivationGuard blockiert Nicht-Admin",
  "steps": [
    { "stepNumber": 1, "action": "SetEnvironmentVariable",
      "schemaName": "markant_gdpr_sysadmin_is_gdpr_admin",
      "value": "false", "alias": "envSnap" },
    { "stepNumber": 2, "action": "CreateRecord", "entity": "contacts",
      "alias": "c", "fields": { "firstname": "Test", "lastname": "User" } },
    { "stepNumber": 3, "action": "UpdateRecord", "alias": "c",
      "fields": { "markant_gdprstatuscode": 288260001,
                  "statecode": 1, "statuscode": 2 },
      "description": "Pseudonymize anstoßen, statecode auf 1 (inaktiv)" },
    { "stepNumber": 4, "action": "Wait", "waitSeconds": 2 },
    { "stepNumber": 5, "action": "UpdateRecord", "alias": "c",
      "fields": { "statecode": 0, "statuscode": 1 },
      "expectException": {
        "messageContains": "can only be reactivated by a GDPR admin"
      },
      "description": "Reaktivierung muss durch Guard blockiert werden" },
    { "stepNumber": 6, "action": "Assert", "target": "Record",
      "recordRef": "{RECORD:c}",
      "field": "statecode", "operator": "Equals", "value": "1",
      "description": "statecode bleibt 1 (Guard hat blockiert)" }
  ]
}
```

**Was hier passiert:**

1. EnvVar setzt den Guard-Modus auf "kein Admin" (Snapshot-Alias für
   Auto-Restore am Testende).
2. Contact wird angelegt und auf Pseudonymized gesetzt.
3. Wait gibt dem Plugin Zeit zu reagieren.
4. Reaktivierungs-Versuch: `expectException` matcht den Guard-Text →
   Step Passed.
5. Assertion: statecode ist immer noch 1 (der Guard hat geblockt).

Am Testende (automatisch durch `keeprecords: false`):

- EnvVar `markant_gdpr_sysadmin_is_gdpr_admin` zurück auf Original.
- Contact `c` gelöscht.

## Was nicht von `expectFailure` abgedeckt wird

- **Assert-Steps**: dort gibt es bereits `NotEquals`, `IsNull`,
  `NotExists` als negative Operatoren. `expectFailure` auf einem
  Assert-Step wird ignoriert.
- **Netzwerk-/Infrastruktur-Fehler**: Timeout, HTTP 5xx,
  Connection-Errors werden nicht als erwartete Exception gewertet
  (sonst würde ein instabiles Netzwerk Tests grün färben). Solche
  Fehler machen den Test trotz `expectFailure` als `Error`.
- **Exception-Typ-Match**: bewusst nicht implementiert (zu
  implementierungsnah, bricht bei Refactorings). Stattdessen Message-
  oder ErrorCode-Match.

## Engine-Tests die das Feature absichern

Im D365TestCenter-Repo decken 15 Unit-Tests die Match-Logik ab:
JSON-Deserialisierung, MessageContains case-insensitive + miss,
MessageMatches Regex + invalid, Both-Modes-Validation, ErrorCode aus
Message, AND-Kombination, HttpStatus-Match.

---

Weiter mit den [Coverage-Regeln](06-coverage-regeln.md) oder den
[Rezepten](07-rezepte.md).
