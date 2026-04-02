# D365 Test Center

**Ende-zu-Ende Integrationstests für Dynamics 365 / Dataverse**

D365 Test Center ist ein browserbasierter Testrunner, der JSON-definierte Testfälle gegen eine lebende Dynamics-365-Umgebung ausführt. Es füllt die Lücke zwischen UI-Tests (EasyRepro) und isolierten Unit-Tests (FakeXrmEasy), indem es komplette Plugin-Ketten, asynchrone Workflows und Field-Governance-Logik in Echtzeit validiert.

## Hauptmerkmale

- **JSON-definierte Testfälle** mit drei Phasen: Preconditions, Steps, Assertions
- **Live-Ausführung** gegen Dataverse Web API v9.2 (OData v4)
- **Async-Plugin-Polling** wartet auf Plugin-Ketten bevor Assertions laufen
- **Visueller Testfall-Editor** mit Drag-Drop, Templates, Alias-Autocomplete
- **Dashboard** mit Trend-Sparklines, Regressionserkennung, Flaky-Test-Detektion
- **Demo-Modus** funktioniert außerhalb von Dynamics 365 mit generierten Mock-Daten
- **Zero Dependencies** reines Vanilla JS, eine einzige HTML-Datei, kein Build-Prozess
- **10-Minuten-Deployment** auf jede Dynamics-365-Umgebung weltweit

## Architektur

```
D365TestCenter/
+-- webresource/              # Single-File Web Resource + JSON Test-Packs
+-- backend/                  # C#-Solution (Core, CRM Plugin, Tests)
+-- scripts/                  # PowerShell Deployment-Skripte
+-- docs/                     # Produktdokumentation
```

### Frontend

Eine einzige HTML-Datei (~8700 Zeilen), deployed als Dataverse Web Resource. Enthält die komplette UI (Dashboard, Testrunner, visueller Editor, Metadaten-Explorer) und die Execution Engine (Placeholder Resolver, Step Executor, Record Tracker, Governance Polling).

### Backend

.NET Framework 4.7.2 Solution mit drei Projekten:

| Projekt | Zweck |
|---------|-------|
| `D365TestCenter.Core` | Testrunner, Assertion Engine, Placeholder Engine, Models |
| `D365TestCenter.CrmPlugin` | Custom Action Wrapper für serverseitige Ausführung |
| `D365TestCenter.Tests` | Unit-Tests |

## Schnellstart

### 1. Deployment

```powershell
# deploy-config.json mit der eigenen Umgebungs-URL anpassen
.\scripts\Deploy-Solution.ps1
```

### 2. Testfälle importieren

```powershell
.\scripts\Import-TestCases.ps1
```

### 3. Test Center öffnen

Web Resource `d365testcenter.html` in der Umgebung öffnen.

### 4. Demo-Modus ausprobieren

`webresource/d365testcenter.html` als lokale Datei im Browser öffnen. Die App erkennt automatisch, dass sie außerhalb von Dynamics 365 läuft und zeigt Demo-Daten.

## Lizenz

[MIT](LICENSE)
