# CLAUDE.md - D365TestCenter

This is the **product repo** for D365TestCenter.
Architecture, decisions, reviews, and skills live in the separate **D365TestCenter-Workspace** repo.

## Workspace Reference

Full collaboration rules, architecture docs, session state, and skills are in:
`C:\Users\Juerg\source\repos\D365TestCenter-Workspace\`

At session start, always read the Workspace's `CLAUDE.md` and `ZUSAMMENARBEIT.md` first.

## Code Quality

### Frontend (Web Resource)
- Single-file HTML, Vanilla JS, zero dependencies, no build process
- All CRUD via the `API` object (Dataverse Web API v9.2)
- Demo mode auto-activates outside Dynamics 365 (MockAPI)

### Backend (C#)
- .NET Framework 4.7.2, Strong-Name signed
- Every change must pass: `dotnet build` / `dotnet test`
- No hardcoded environment URLs or tokens

### Scripts (PowerShell)
- Deployment scripts must be idempotent (create-or-update)
- No hardcoded auth (accept headers/token as parameter)

## Architecture

- **Frontend:** Single HTML file as Dataverse Web Resource (~8700 lines)
- **Backend:** .NET Solution with Core library, CRM Plugin, and Tests
- **Test Packs:** JSON files with preconditions, steps, assertions
- **Deployment:** PowerShell scripts for Solution setup + Web Resource publishing

## Language

- Code, comments, docs, commit messages: **English**
- Communication with Juergen: **German with proper umlauts (ae, oe, ue, ss)**
