# Pack-System und Lokalisierung

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Feldnamen) steht in der `PROJEKT-KONTEXT.md` des jeweiligen Repos.

## 9. Pack-System

Testfälle werden in JSON-Pack-Dateien organisiert.

- `manifest.json` listet alle verfügbaren Packs
- Ein Pack-JSON enthält ein Array von Testfall-Definitionen
- Packs werden als Dataverse Web Resources deployed
- Im Demo-Modus werden sie per HTTP geladen (lokale Dateien)

### Neues Pack erstellen

1. JSON-Datei in `webresource/packs/` anlegen (Produkt-Repo) oder im Pack-Ordner des Projekt-Repos (Ort laut `PROJEKT-KONTEXT.md`)
2. In `manifest.json` registrieren
3. Als Web Resource deployen (Deploy-Skript oder manuell)

### Demo-Metadaten-Packs

Dateien mit dem Muster `demo-*.json` werden automatisch beim App-Start geladen und in den Metadaten-Explorer gemerged:

```json
{
  "name": "My Custom Demo Metadata",
  "additionalEntities": [ ... ],
  "additionalAttributes": {
    "account": [ ... ],
    "contact": [ ... ]
  }
}
```

---

## 12. Lokalisierung (i18n)

UI-Texte stehen im `LANG`-Objekt am Anfang der HTML. Standard: Englisch.

```javascript
const LANG = {
    nav_dashboard: "Dashboard",
    nav_testcases: "Test Cases",
    dash_testcases: "TEST CASES",
    // ... ~80 Schlüssel
};
```

Navigation und Demo-Banner nutzen bereits LANG. Die restlichen Dashboard- und Detail-Labels werden schrittweise migriert.

---
