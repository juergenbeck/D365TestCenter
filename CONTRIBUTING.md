# Contributing to D365 Test Center

Thank you for your interest in contributing! This document explains how to get involved.

## Development Setup

### Prerequisites

- .NET SDK 4.7.2+ (for the C# backend)
- PowerShell 5.1+ (for deployment scripts)
- A modern browser (for the Web Resource frontend)
- A Dynamics 365 / Dataverse environment (for live testing)

### Getting Started

```bash
git clone https://github.com/juergenbeck/D365TestCenter.git
cd D365TestCenter
```

#### Frontend (Web Resource)

No build step required. Open `webresource/d365testcenter.html` directly in a browser for demo mode:

```bash
# Option 1: Direct file
start webresource/d365testcenter.html

# Option 2: Local HTTP server (for pack loading)
cd webresource
python -m http.server 8765
# Then open http://localhost:8765/d365testcenter.html
```

#### Backend (C#)

```bash
cd backend
dotnet build D365TestCenter.sln
dotnet test D365TestCenter.Tests/D365TestCenter.Tests.csproj
```

### Project Structure

```
D365TestCenter/
+-- webresource/                # Single-file Web Resource + JSON packs
|   +-- d365testcenter.html     # Main application (~8700 lines)
|   +-- packs/                  # Test pack definitions (JSON)
+-- backend/                    # C# solution
|   +-- D365TestCenter.Core/    # Test runner, assertion engine, models
|   +-- D365TestCenter.CrmPlugin/ # Custom Action wrapper
|   +-- D365TestCenter.Tests/   # Unit tests
+-- scripts/                    # PowerShell deployment scripts
+-- docs/                       # Product documentation
```

## Architecture Principles

1. **Zero Dependencies** - No external JS libraries. Pure Vanilla JS.
2. **Single File** - Everything in one HTML file (except JSON packs).
3. **Idempotent Deployment** - Scripts check existence before creating.
4. **CONFIG-driven** - All environment-specific values in the `CONFIG` block.
5. **Demo Mode** - Works outside Dynamics 365 with mock data automatically.

## Making Changes

### Frontend

The frontend is a single HTML file with embedded CSS and JavaScript. Key sections:

| Section | Description |
|---------|-------------|
| `CONFIG` | Environment configuration (entities, option sets, governance) |
| `LANG` | Localization strings (i18n) |
| `_sharedIttMeta` | Demo metadata for offline mode |
| `PackLoader` | Dynamic JSON pack loading |
| `StepExecutor` | Test step execution engine |
| `TestRunner` | Test orchestration |
| `TemplateLibrary` | Visual editor templates |

### Governance Configuration

The `CONFIG.governance` block controls async plugin polling behavior. To adapt for your plugin chain, modify the entity names, field mappings, and polling settings in this block.

### Adding Test Packs

1. Create a JSON file in `webresource/packs/`
2. Register it in `webresource/packs/manifest.json`
3. Follow the test case format documented in `docs/04_testfall-spezifikation.md`

### Adding Demo Metadata

1. Create a `demo-*.json` file in `webresource/packs/`
2. Use the format with `additionalEntities` and `additionalAttributes`
3. The demo loader picks it up automatically on startup

## Localization (i18n)

UI strings are in the `LANG` object near the top of the HTML file. To add a new language:

1. Copy the `LANG` object
2. Translate all values
3. Replace the default `LANG` object, or add a language switcher

## Commit Convention

```
<type>: <description>
```

Types: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`

Examples:
- `feat: add parallel test execution`
- `fix: governance polling timeout on slow environments`
- `docs: add deployment guide for Azure DevOps`

## Deployment

See `docs/05_deployment-handbuch.md` for detailed instructions.

Quick start:
1. Edit `scripts/deploy-config.json` with your environment URL
2. Set `$headers` with a valid Bearer token
3. Run `scripts/Deploy-Solution.ps1`

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
