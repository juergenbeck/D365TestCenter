# Deployment von Engine, Plugin, CLI und Webresource

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Feldnamen) steht in der `PROJEKT-KONTEXT.md` des jeweiligen Repos.

## 5. Deployment

### 5.1 Generisch (Produkt-Repo)

Erstinstallation und Entwicklungsumgebung: Paket aus dem Quellstand bauen und importieren.

```powershell
pac auth create --environment https://<umgebung>.crm4.dynamics.com
pac solution pack --zipfile solution/out/D365TestCenter.zip --folder solution/src --packagetype Unmanaged
pac solution import --path solution/out/D365TestCenter.zip --publish-changes --activate-plugins
```

Test- und Produktivumgebungen bekommen eine managed Solution aus der Entwicklungsumgebung (5.5). Der
Recovery-Flow ist nicht Teil der Solution und wird je Umgebung mit `scripts/Create-RecurrenceFlow.ps1` angelegt.

**Stillgelegt (seit 2026-09-18):** `Deploy-Solution.ps1`, `Deploy-ProSolution.ps1`, `Create-TestingEntities.ps1`
und `deploy-config.json`. Sie legten Publisher, Tabellen und OptionSets einzeln per Web API an, waren gegenüber
der Solution veraltet (Outcome ohne Error, fehlende Tabellen und OptionSets) und scheiterten auf Umgebungen mit
einer managed Solution am schreibgeschützten Publisher. Ein Wrapper, der sie noch aufruft, ist auf den
Solution-Import umzustellen.

### 5.4 WICHTIG: Plugin-Package gehört zur Solution, kein separater Deploy-Schritt

Häufiger Irrtum: "nach dem Solution-Import muss ich noch das Plugin-Package
nachziehen". Das ist **falsch**. Das PluginPackage `jbe_D365TestCenter`
(Version aktuell 5.3.1) ist als Solution-Komponente Typ 10080 fester
Bestandteil der Solution. Der `pac solution import` deployed in einer
Operation:

- 4 Entities (jbe_testcase, jbe_testrun, jbe_testrunresult, jbe_teststep)
  + Attribute, Relationships, Views, Forms.
- WebResources inkl. `handbuch.html`.
- AppModule + SiteMap.
- **PluginPackage** mit dem `.nupkg`-Binary aus
  `solution/src/pluginpackages/jbe_D365TestCenter/package/`.
- **SDK Message Processing Steps** für `RunTestsOnStatusChange`
  (Create + Update auf jbe_testrun) und CustomApi-Step für
  `jbe_RunIntegrationTests`.

Es gibt **keine** separate Plugin-Push-Operation, kein `pac plugin
register` o.ä. nach dem Solution-Import. Wenn eine Sitzung in einem
Nutzer-Repo solche Schritte vorschlägt, Irrtum, korrigieren mit
Verweis auf diese Sektion.

### 5.5 Korrekte Sequenz für Managed-Env-Deploys (TEST, weitere Test-Umgebungen, PROD)

Vor dem Deploy: Goldene Regel 1, explizite Freigabe.

```powershell
# 1. Managed-Export aus DEV
pac auth select --name <projekt>-dev
pac solution export --name D365TestCenter --managed true \
  --path solution/out/D365TestCenter_managed_v<X.Y.Z.W>.zip --async

# 2. Stage-and-Upgrade-Import auf TEST bzw. eine weitere Test-Umgebung
pac auth select --name <projekt>-test
pac solution import --path …managed_v<X.Y.Z.W>.zip \
  --stage-and-upgrade --publish-changes --activate-plugins --async
```

**Pflicht-Verifikation nach jedem Managed-Import** (sonst FB-Schwarm):

| Prüfung | Wie | Wenn fehlerhaft |
|---|---|---|
| Solution-Version aktuell | `GET /solutions?$filter=uniquename eq 'D365TestCenter'&$select=version,modifiedon` | Re-Import (FB-33: parallele Imports gegen mehrere Envs verlieren manchmal) |
| Plugin-Version aktuell | `GET /pluginpackages?$filter=name eq 'jbe_D365TestCenter'&$select=version,modifiedon` | csproj+pluginpackage.xml-Versions-Check (FB-28) |
| Plugin-Steps aktiv | `GET /sdkmessageprocessingsteps?$filter=_plugintypeid_value eq <ptid>&$select=name,statecode,statuscode` | PATCH `statecode=0, statuscode=1` (FB-27) |
| Schema-Cleanup | `GET /EntityDefinitions(LogicalName='jbe_teststep')/Attributes(LogicalName='jbe_phase')` | Stage-and-Upgrade hat das automatisch erledigt; HTTP 404 ist Soll |
| End-to-End-Smoke | jbe_testrun mit Status=Geplant + simpler Testcase | siehe Pack `smoketest-*` im jeweiligen Projekt |

**Kein "Plugin nachziehen"**: wenn Plugin-Version oder modifiedon nicht
stimmen, ist es ein Versions-Deklarations-Problem (FB-28, beide
Versions-Stellen synchron erhöhen) oder ein Race-Condition-Problem
(FB-33, sequenziell deployen statt parallel), nicht ein "Push"-Problem.

---
