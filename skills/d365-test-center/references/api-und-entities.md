# API-Objekt, Entitäten und OptionSets

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Feldnamen) steht in der `PROJEKT-KONTEXT.md` des jeweiligen Repos.

## 6. API-Objekt (JavaScript)

Alle Dataverse-Zugriffe in der HTML laufen über das `API`-Objekt.

| Methode | Signatur | Beschreibung |
|---------|----------|-------------|
| `fetch` | `API.fetch(url, options)` | Basis-Fetch, URL braucht führenden `/` |
| `create` | `API.create(entitySet, data)` | POST mit Retry bei 429/503 |
| `update` | `API.update(entitySet, id, data)` | PATCH |
| `del` | `API.del(entitySet, id)` | DELETE |
| `getMany` | `API.getMany(entitySet, query)` | GET, gibt Array zurück |
| `getOne` | `API.getOne(entitySet, id, select)` | GET einzelner Record |
| `executeAction` | `API.executeAction(name, params)` | POST Custom Action |

**WICHTIG:** Kein `API.query()`. Die Methode heißt `API.getMany()`.
**WICHTIG:** `API.fetch(url)` erwartet einen führenden Slash: `/accounts?$filter=...`

---

## 7. Entities und OptionSets

### Diagnostik-Felder (v5.3.6)

Pro TestRun und TestRunResult werden seit Plugin v5.3.6 Diagnose-Memos
befüllt:

| Feld | Auf Entity | Inhalt |
|---|---|---|
| `jbe_fulllog` | `jbe_testrun` | Engine-Log (Step-Verlauf, Cleanup-Summary) plus Plugin-Trace-Logs der projekteigenen Custom-Plugins seit Test-Start (max 100k chars). Erfordert `PluginTraceLogSetting` ≠ Off für Plugin-Traces. |
| `jbe_trackedrecords` | `jbe_testrunresult` | JSON-Array `[{entity, id, alias}]` aller vom Test angelegten Records. Bei `keepRecords=true` die Liste zum manuellen Cleanup; sonst Audit was geräumt wurde. |

Vor v5.3.6 waren beide Felder bekannt-leer (FB-34).

### Entities (Publisher: itt, Prefix: 10571)

| Entity | EntitySetName | Beschreibung |
|--------|--------------|-------------|
| `jbe_testcase` | `jbe_testcases` | Testfalldefinition (JSON) |
| `jbe_testrun` | `jbe_testruns` | Ein Testlauf |
| `jbe_testrunresult` | `jbe_testrunresults` | Ergebnis pro Testfall |
| `jbe_teststep` | `jbe_teststeps` | Einzelner Schritt-Log |

### Wichtige OptionSets

**jbe_testoutcome:** Passed (105710000), Failed (105710001), **Skipped (105710002), Error (105710003)** (per OptionSet verifiziert, Goldene Regel 10). Die frühere hier dokumentierte Vertauschung (Error=...002, Skipped=...003) war FB-50: code-seitig in Plugin v5.3.21 gefixt und die Alt-Records migriert.

**jbe_teststatus:** Planned (105710000), Running (105710001), Completed (105710002), Error (105710003)

**ACHTUNG:** OptionSet-Werte sind umgebungsspezifisch (PublisherOptionValuePrefix). Immer `CONFIG.optionSets.*` verwenden, nie Zahlen hardcoden.

---
