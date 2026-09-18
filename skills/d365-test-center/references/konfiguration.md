# CONFIG-Block und Profile

> Referenz des Skills `d365-test-center`. Quelle: Repo D365TestCenter, `skills/d365-test-center/`.
> Projektspezifisches (Umgebungen, Kennungen, Feldnamen) steht in der `PROJEKT-KONTEXT.md` des jeweiligen Repos.

## 4. CONFIG-Block: Konfiguration

Alle umgebungsspezifischen Einstellungen stehen im `CONFIG`-Objekt am Anfang der HTML.

### 4.1 Basis-Config

```javascript
const CONFIG = {
    version: "5.3.21",
    prefix: "jbe",
    optionSetBase: 105710000,   // = OptionValuePrefix 10571 * 10000; MUSS zur optionSets-Basis passen (nur demo-wirksam, remappt die generischen Pack-Werte)
    entities: { testcase: "jbe_testcase", testrun: "jbe_testrun", ... },
    fields: { testid: "jbe_testid", title: "jbe_title", ... },
    optionSets: { statusPlanned: 105710000, outcomePassed: 105710000, ... },
    actions: { runTests: "jbe_RunIntegrationTests" },
    storyLinkPattern: "",  // z.B. "https://jira.example.com/browse/{key}"
    ...
};
```

### 4.2 Governance-Config (optional, für Plugin-Ketten)

```javascript
CONFIG.governance = {
    sourceEntity: "",        // Quell-Entity, z.B. "contoso_contactsource"
    sourceEntitySet: "",     // z.B. "contoso_contactsources"
    loggingEntitySet: "",    // Logging-EntitySet der Governance-Kette (OData-Query-Name)
    governanceApi: "",       // Default-Custom-API für den Governance-Trigger
    sourceSystemField: "",   // Quellsystem-Feld auf der Quell-Entity
    externalIdField: "",     // ExternalId-Feld (Timestamp-Guard-Erkennung)
    sourceSystems: [],       // Quellsysteme im Visual Editor: [{ value: 1, label: "CRM", alias: "crm" }]
    autoDateFields: {}       // Wertfeld -> Timestamp-Feld, z.B. { "contoso_firstname": "contoso_firstname_modifiedon" }
};
```

**Auslieferungsstand (seit 2026-09-18): leer.** Der Block ist optional; das Produkt trägt keine
projektspezifischen Namen. Ist nichts konfiguriert, blendet der Visual Editor die Governance-Hilfen aus
(keine Feldvorschläge der Quell-Entity, keine Default-Governance-API, leere Quellsystem-Auswahl, leeres
Datums-Mapping). Wer die Hilfen braucht, füllt den Block in der **eigenen Deploy-Kopie** der Web Resource;
die Werte eines Projekts gehören in dessen Repo, nicht ins Produkt. `sourceSystems` ersetzt die früher fest
verdrahtete Quellsystem-Liste (`alias` ist das Kürzel für generierte Aliasnamen, fehlt es, gilt das Label in
Kleinbuchstaben). Der Assert-Zielwert für die Logging-Entity heißt im Editor `GovernanceLogging`.

**Hinweis (seit v5.3 / ADR-0003):** Dieser HTML-Block steuert **nicht** den Live-Lauf. Die Engine läuft im
C#-Core und liest ihn nicht. `CONFIG.governance` wirkt nur im **Demo-Modus** (MockAPI), bei
**Editor-Vorschlägen** und in der **Governance-Anzeige** der Ergebnisse. `optionSetBase` war früher ein
Zahlendreher (`100570000` statt `105710000`) und ist gefixt (nur demo-wirksam: `_remapOptionSets` bildet die
generischen 100000000-basierten Pack-OptionSet-Werte auf die 10571-Basis ab).

### 4.3 Config-Profil der CLI (`--config`)

Es gibt genau ein C#-Config-Profil: `standard` (`StandardCrmConfig`, Default aller CLI-Kommandos). Es trägt
nur die Entity-Namen der Test-Center-Tabellen und die OptionSet-Werte von `jbe_teststatus`/`jbe_testoutcome`
(`ITestCenterConfig`). Frühere projektspezifische Profilnamen verhielten sich identisch und werden weiter
angenommen: die CLI meldet dann `Hinweis: Config-Profil '<name>' ist entfallen, es gilt 'standard'
(identisches Verhalten).` und läuft mit `standard` weiter (`GetConfig` in
`backend/D365TestCenter.Cli/Program.cs`). Das Plugin nutzt immer `StandardCrmConfig`. Eine eigene
Plugin-Kette braucht also weder ein eigenes Profil noch Einträge in diesem Block; sie wird über die Actions
eines Testfalls angesprochen (`ExecuteRequest`, `WaitForRecord`, `Assert` usw.).

---
