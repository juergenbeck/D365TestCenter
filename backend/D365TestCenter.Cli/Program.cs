using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Validation;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Cli;

/// <summary>
/// Headless CLI für das D365 Test Center.
///
/// Seit ADR-0003 (Single-Engine-Architektur) nutzt diese CLI die zentrale Core-
/// Engine via <see cref="TestCenterOrchestrator"/>. Alle Features (ExecuteRequest,
/// WaitForRecord, CallCustomApi, $type-System, Governance-Polling, AutoDate-Fields)
/// kommen automatisch aus der Core-Library.
///
/// Commands:
///   run    - Führt Testfälle gegen eine Dataverse-Umgebung aus.
///   status - Zeigt die letzten Test-Runs an.
///
/// Authentifizierung:
///   Primär: --token (Bearer-Token, z.B. von einem TokenVault-Wrapper übergeben).
///   Alternativ: --client-id + --client-secret + --tenant-id (Service Principal).
///   Optional: --interactive (Browser-Login für Dev/Test).
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand(
            "D365 Test Center CLI - Integration testing for Dynamics 365");

        // Shared options
        var orgOption = new Option<string>("--org",
            "Dataverse organization URL (e.g. https://org.crm4.dynamics.com)")
        { IsRequired = true };
        var clientIdOption = new Option<string>("--client-id",
            "Azure AD App Client ID (for Service Principal auth)");
        var clientSecretOption = new Option<string>("--client-secret",
            "Azure AD App Client Secret");
        var tenantIdOption = new Option<string>("--tenant-id",
            "Azure AD Tenant ID");
        var tokenOption = new Option<string>("--token",
            "Bearer token (alternative to client credentials)");
        var interactiveOption = new Option<bool>("--interactive",
            () => false, "Use interactive browser login");

        // ── run command ──────────────────────────────────────────
        var runCommand = new Command("run",
            "Execute test cases against a Dataverse environment");
        runCommand.AddOption(orgOption);
        runCommand.AddOption(clientIdOption);
        runCommand.AddOption(clientSecretOption);
        runCommand.AddOption(tenantIdOption);
        runCommand.AddOption(tokenOption);
        runCommand.AddOption(interactiveOption);
        runCommand.AddOption(new Option<string>("--filter", () => "*",
            "Test case filter (wildcard on test ID, tag:..., category:..., or comma-separated IDs)"));
        runCommand.AddOption(new Option<bool>("--keep-records", () => false,
            "Keep test data after run"));
        runCommand.AddOption(new Option<string>("--config", () => "standard",
            "Config profile: standard, markant"));
        // ADR-0006: UI automation options. When --browser-state is provided,
        // BrowserAction steps are dispatched via Microsoft.Playwright; otherwise
        // BrowserAction steps are skipped (same behaviour as the Plugin path).
        runCommand.AddOption(new Option<string?>("--browser-state",
            "Path to Playwright storage-state JSON for UI tests (enables BrowserAction)"));
        runCommand.AddOption(new Option<bool>("--browser-headed", () => false,
            "Run browser headed (default headless, set for local debug)"));
        runCommand.AddOption(new Option<string>("--browser-locale", () => "de-DE",
            "Browser locale for UI tests"));
        runCommand.AddOption(new Option<string?>("--browser-trace",
            "Path to write Playwright trace.zip (default: skip tracing)"));
        // E2 (ADR-0008): after the run, write results back into the Markdown
        // test definitions under --sync-defs (front-matter ergebnis_historie SSOT).
        runCommand.AddOption(new Option<string?>("--sync-defs",
            "After the run, sync results into the Markdown test definitions under this directory (E2 round-trip)."));
        runCommand.AddOption(new Option<string?>("--env",
            "Env label for the synced history entry (default: derived from the --org host)."));
        runCommand.Handler = CommandHandler.Create<string, string?, string?, string?, string?, bool, string, bool, string, string?, bool, string, string?, string?, string?>(
            RunTests);
        rootCommand.AddCommand(runCommand);

        // ── status command ───────────────────────────────────────
        var statusCommand = new Command("status",
            "Check status of recent test runs");
        statusCommand.AddOption(orgOption);
        statusCommand.AddOption(clientIdOption);
        statusCommand.AddOption(clientSecretOption);
        statusCommand.AddOption(tenantIdOption);
        statusCommand.AddOption(tokenOption);
        statusCommand.AddOption(interactiveOption);
        statusCommand.AddOption(new Option<int>("--top", () => 5,
            "Number of recent runs to show"));
        statusCommand.AddOption(new Option<string>("--config", () => "standard",
            "Config profile: standard, markant"));
        statusCommand.Handler = CommandHandler.Create<string, string?, string?, string?, string?, bool, int, string>(
            ShowStatus);
        rootCommand.AddCommand(statusCommand);

        // ── sync-results command (E2 / ADR-0008) ─────────────────
        var syncResultsCommand = new Command("sync-results",
            "Write the results of a finished test run back into the Markdown test definitions (E2 round-trip).");
        syncResultsCommand.AddOption(orgOption);
        syncResultsCommand.AddOption(clientIdOption);
        syncResultsCommand.AddOption(clientSecretOption);
        syncResultsCommand.AddOption(tenantIdOption);
        syncResultsCommand.AddOption(tokenOption);
        syncResultsCommand.AddOption(interactiveOption);
        syncResultsCommand.AddOption(new Option<string>("--run",
            "Test run id (jbe_testrun GUID) whose results are synced.") { IsRequired = true });
        syncResultsCommand.AddOption(new Option<string>("--defs",
            "Directory with the Markdown test definitions to update (searched recursively).") { IsRequired = true });
        syncResultsCommand.AddOption(new Option<string?>("--env",
            "Env label for the history entry (default: derived from the --org host)."));
        syncResultsCommand.AddOption(new Option<string>("--config", () => "standard",
            "Config profile: standard, markant"));
        syncResultsCommand.Handler = CommandHandler.Create<string, string?, string?, string?, string?, bool, string, string, string?, string>(
            SyncResults);
        rootCommand.AddCommand(syncResultsCommand);

        // ── validate command (OE-6) ──────────────────────────────
        var validateCommand = new Command("validate",
            "Statically validate a pack JSON for schema and pattern mistakes (no Dataverse call).");
        validateCommand.AddOption(new Option<string>("--pack",
            "Path to a pack JSON file (suite wrapper with testCases, or a single test case JSON).")
        { IsRequired = true });
        validateCommand.AddOption(new Option<bool>("--strict", () => false,
            "Exit with code 1 also when only warnings are reported (default: only errors fail)."));
        // OE-8 Phase 2 (Backlog J): an optional connection enables the
        // metadata-aware rules (ENTITY_UNKNOWN/FIELD_UNKNOWN) against the target
        // env. Without --org, validate stays a pure static (Phase-1) lint.
        validateCommand.AddOption(new Option<string>("--org",
            "Optional Dataverse org URL. When set, enables metadata-aware checks (entity/field existence)."));
        validateCommand.AddOption(new Option<string>("--client-id", "Azure AD App Client ID (with --org)"));
        validateCommand.AddOption(new Option<string>("--client-secret", "Azure AD App Client Secret (with --org)"));
        validateCommand.AddOption(new Option<string>("--tenant-id", "Azure AD Tenant ID (with --org)"));
        validateCommand.AddOption(new Option<string>("--token", "Bearer token (alternative to client credentials)"));
        validateCommand.AddOption(new Option<bool>("--interactive", () => false, "Use interactive browser login (with --org)"));
        validateCommand.Handler = CommandHandler.Create<string, bool, string, string, string, string, string, bool>(ValidatePack);
        rootCommand.AddCommand(validateCommand);

        // ── ui-setup command (ADR-0006) ──────────────────────────
        var uiSetupCommand = new Command("ui-setup",
            "Create a Playwright storage-state by interactive login (UI tests). DEV-only hard-guard.");
        uiSetupCommand.AddOption(orgOption);
        uiSetupCommand.AddOption(new Option<string>("--output", () => "auth/markant-dev-juergen.json",
            "Output path for the storage-state JSON"));
        uiSetupCommand.Handler = CommandHandler.Create<string, string>(
            D365TestCenter.Cli.UiAutomation.StorageStateSetup.RunAsync);
        rootCommand.AddCommand(uiSetupCommand);

        return await rootCommand.InvokeAsync(args);
    }

    // ════════════════════════════════════════════════════════════════
    //  Commands
    // ════════════════════════════════════════════════════════════════

    static Task<int> RunTests(
        string org, string? clientId, string? clientSecret, string? tenantId,
        string? token, bool interactive, string filter, bool keepRecords, string config,
        string? browserState, bool browserHeaded, string browserLocale, string? browserTrace,
        string? syncDefs, string? env)
    {
        WriteHeader(org, filter, config);

        try
        {
            using var client = Connect(org, clientId, clientSecret, tenantId, token, interactive);
            var cfg = GetConfig(config);

            Console.WriteLine($"  Verbunden als: {client.OAuthUserId ?? "Service Principal"}");

            // ADR-0006: BrowserActionExecutor only when --browser-state is provided.
            // Without it, BrowserAction steps are skipped (test still runs API steps).
            using var browser = browserState != null
                ? new D365TestCenter.Cli.UiAutomation.PlaywrightBrowserActionExecutor(
                    storageStatePath: browserState,
                    headless: !browserHeaded,
                    locale: browserLocale,
                    tracePath: browserTrace,
                    log: Console.WriteLine)
                : null;

            if (browser != null)
            {
                Console.WriteLine($"  UI automation: Storage-State={browserState}, Headed={browserHeaded}, Locale={browserLocale}");
            }
            Console.WriteLine();

            // Zentraler Orchestrator aus der Core-Library
            var orchestrator = new TestCenterOrchestrator(
                client,
                cfg,
                log: Console.WriteLine,
                browser: browser);

            var result = orchestrator.RunNewTestRun(filter, keepRecords);

            // E2 (ADR-0008): optional result round-trip into the Markdown definitions.
            if (!string.IsNullOrWhiteSpace(syncDefs))
            {
                var envLabel = !string.IsNullOrWhiteSpace(env) ? env! : ResultSync.DeriveEnv(org);
                Console.WriteLine();
                Console.WriteLine($"  Ergebnis-Sync -> {syncDefs} (env={envLabel}):");
                try
                {
                    var sum = ResultSync.SyncDefinitions(
                        result.Results, syncDefs!, DateTime.Now, envLabel, Console.WriteLine);
                    Console.WriteLine(
                        $"  Sync fertig: {sum.Updated} aktualisiert, {sum.Matched} gematcht, {sum.Scanned} gescannt.");
                }
                catch (Exception syncEx)
                {
                    // Ein Sync-Fehler darf den Testlauf-Exit-Code nicht verfälschen.
                    Console.WriteLine($"  Ergebnis-Sync fehlgeschlagen: {syncEx.Message}");
                }
            }

            // Exit-Code: 0 = alle grün, 1 = mind. ein Failure/Error, 2 = fataler Fehler
            if (result.FailedCount == 0 && result.ErrorCount == 0)
                return Task.FromResult(0);
            return Task.FromResult(1);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"  Fataler Fehler: {ex.Message}");
            if (ex.InnerException != null)
                Console.WriteLine($"    {ex.InnerException.Message}");
            return Task.FromResult(2);
        }
    }

    static Task<int> ShowStatus(
        string org, string? clientId, string? clientSecret, string? tenantId,
        string? token, bool interactive, int top, string config)
    {
        try
        {
            using var client = Connect(org, clientId, clientSecret, tenantId, token, interactive);
            var cfg = GetConfig(config);

            var q = new QueryExpression(cfg.TestRunEntity)
            {
                ColumnSet = new ColumnSet(
                    "jbe_teststatus", "jbe_passed", "jbe_failed", "jbe_total",
                    "jbe_startedon", "jbe_testsummary", "jbe_testcasefilter"),
                Orders = { new OrderExpression("jbe_startedon", OrderType.Descending) },
                TopCount = top
            };

            Console.WriteLine();
            Console.WriteLine($"  Letzte {top} TestRuns auf {org}:");
            Console.WriteLine();

            foreach (var r in client.RetrieveMultiple(q).Entities)
            {
                var statusCode = r.GetAttributeValue<OptionSetValue>("jbe_teststatus")?.Value;
                var status = MapStatusCode(statusCode, cfg);
                var started = r.GetAttributeValue<DateTime?>("jbe_startedon");
                var filter = r.GetAttributeValue<string>("jbe_testcasefilter") ?? "*";
                var passed = r.GetAttributeValue<int?>("jbe_passed") ?? 0;
                var failed = r.GetAttributeValue<int?>("jbe_failed") ?? 0;
                var total = r.GetAttributeValue<int?>("jbe_total") ?? 0;

                Console.WriteLine(
                    $"  {started:yyyy-MM-dd HH:mm}  {status,-12}  " +
                    $"Passed:{passed}  Failed:{failed}  Total:{total}  " +
                    $"Filter:{filter}");
            }

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Fehler: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  sync-results command (E2 / ADR-0008): result round-trip
    // ════════════════════════════════════════════════════════════════

    static Task<int> SyncResults(
        string org, string? clientId, string? clientSecret, string? tenantId,
        string? token, bool interactive, string run, string defs, string? env, string config)
    {
        try
        {
            if (!Guid.TryParse(run, out var runId))
            {
                Console.WriteLine($"  Ungültige Run-Id (kein GUID): {run}");
                return Task.FromResult(2);
            }
            if (!Directory.Exists(defs))
            {
                Console.WriteLine($"  Definitions-Verzeichnis nicht gefunden: {defs}");
                return Task.FromResult(2);
            }

            using var client = Connect(org, clientId, clientSecret, tenantId, token, interactive);
            var cfg = GetConfig(config);
            var envLabel = !string.IsNullOrWhiteSpace(env) ? env! : ResultSync.DeriveEnv(org);

            var results = ResultSync.LoadResultsFromRun(client, cfg, runId);
            if (results.Count == 0)
            {
                Console.WriteLine($"  Keine jbe_testrunresult-Records für Run {runId} gefunden.");
                return Task.FromResult(1);
            }

            Console.WriteLine();
            Console.WriteLine($"  Sync {results.Count} Ergebnisse aus Run {runId} -> {defs} (env={envLabel}):");
            var sum = ResultSync.SyncDefinitions(results, defs, DateTime.Now, envLabel, Console.WriteLine);
            Console.WriteLine();
            Console.WriteLine(
                $"  Fertig: {sum.Updated} aktualisiert, {sum.Matched} gematcht, {sum.Scanned} Dateien gescannt.");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Fehler: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  validate command (OE-6): static pre-run validation
    // ════════════════════════════════════════════════════════════════

    static Task<int> ValidatePack(string pack, bool strict,
        string? org, string? clientId, string? clientSecret, string? tenantId, string? token, bool interactive)
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("  D365 Test Center - Pack Validation (OE-6 / OE-8)");
        Console.WriteLine($"  Pack:   {pack}");
        Console.WriteLine($"  Strict: {strict}");
        Console.WriteLine($"  Mode:   {(string.IsNullOrWhiteSpace(org) ? "static (Phase 1)" : $"metadata-aware against {org} (Phase 2)")}");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        if (!File.Exists(pack))
        {
            Console.WriteLine($"  Pack file not found: {pack}");
            return Task.FromResult(2);
        }

        List<TestCase> testCases;
        try
        {
            testCases = LoadPack(pack);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed to parse pack file: {ex.Message}");
            return Task.FromResult(2);
        }

        if (testCases.Count == 0)
        {
            Console.WriteLine("  Pack contains zero test cases. Nothing to validate.");
            return Task.FromResult(0);
        }

        Console.WriteLine($"  Tests: {testCases.Count}");
        Console.WriteLine();

        var validator = new PackValidator();
        var report = new ValidationReport();
        ServiceClient? client = null;
        try
        {
            EntityMetadataCache? metadata = null;
            if (!string.IsNullOrWhiteSpace(org))
            {
                client = Connect(org, clientId, clientSecret, tenantId, token, interactive);
                metadata = new EntityMetadataCache(client);
            }
            foreach (var tc in testCases)
            {
                var r = metadata != null ? validator.ValidateOne(tc, metadata) : validator.ValidateOne(tc);
                foreach (var f in r.Findings) report.Add(f);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Connection/validation failed: {ex.Message}");
            return Task.FromResult(2);
        }
        finally
        {
            client?.Dispose();
        }

        if (report.Findings.Count == 0)
        {
            Console.WriteLine("  No findings. Pack passes validation.");
            return Task.FromResult(0);
        }

        foreach (var f in report.Findings)
        {
            var sevTag = f.Severity switch
            {
                ValidationSeverity.Error => "[ERROR  ]",
                ValidationSeverity.Warning => "[WARNING]",
                _ => "[INFO   ]"
            };
            var loc = f.StepNumber.HasValue ? $"Step {f.StepNumber.Value,-3}" : "(test)   ";
            Console.WriteLine($"  {sevTag} {f.TestId,-30} {loc}  {f.Code}");
            Console.WriteLine($"             {f.Message}");
            if (!string.IsNullOrEmpty(f.Suggestion))
            {
                Console.WriteLine($"             -> {f.Suggestion}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"  Summary: {report.ErrorCount} Error, {report.WarningCount} Warning, {report.InfoCount} Info");

        if (report.HasErrors) return Task.FromResult(1);
        if (strict && report.WarningCount > 0) return Task.FromResult(1);
        return Task.FromResult(0);
    }

    /// <summary>
    /// Load a pack JSON. Supports the suite-wrapper format
    /// <c>{ "testCases": [...] }</c> used by workspace packs as well as a
    /// bare TestCase JSON. Maps the workspace-pack field <c>testId</c> onto
    /// the model's <c>id</c> so the validator sees the correct test ID.
    /// </summary>
    static List<TestCase> LoadPack(string path)
    {
        var json = File.ReadAllText(path);
        var root = JToken.Parse(json);

        if (root is JArray topArray)
        {
            NormalizeTestIds(topArray);
            return topArray.ToObject<List<TestCase>>() ?? new List<TestCase>();
        }

        if (root is JObject obj)
        {
            if (obj["testCases"] is JArray tcArr)
            {
                NormalizeTestIds(tcArr);
                return tcArr.ToObject<List<TestCase>>() ?? new List<TestCase>();
            }

            // Bare TestCase: pass through, mapping testId -> id if needed.
            if (obj["id"] == null && obj["testId"] is JToken tid)
            {
                obj["id"] = tid;
            }
            var tc = obj.ToObject<TestCase>();
            return tc != null ? new List<TestCase> { tc } : new List<TestCase>();
        }

        return new List<TestCase>();
    }

    static void NormalizeTestIds(JArray testCases)
    {
        foreach (var item in testCases.OfType<JObject>())
        {
            if (item["id"] == null && item["testId"] is JToken tid)
            {
                item["id"] = tid;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    static ServiceClient Connect(
        string org, string? clientId, string? clientSecret, string? tenantId,
        string? token, bool interactive)
    {
        if (!string.IsNullOrEmpty(token))
        {
            var client = new ServiceClient(new Uri(org), _ => Task.FromResult(token));
            if (!client.IsReady)
                throw new Exception($"Connection failed (Token): {client.LastError}");
            return client;
        }

        if (interactive)
        {
            var connStr = $"AuthType=OAuth;Url={org};LoginPrompt=Auto;RequireNewInstance=True";
            var client = new ServiceClient(connStr);
            if (!client.IsReady)
                throw new Exception($"Connection failed (Interactive): {client.LastError}");
            return client;
        }

        if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret))
        {
            var connStr = $"AuthType=ClientSecret;Url={org};ClientId={clientId};ClientSecret={clientSecret}";
            if (!string.IsNullOrEmpty(tenantId))
                connStr += $";Authority=https://login.microsoftonline.com/{tenantId}";
            var client = new ServiceClient(connStr);
            if (!client.IsReady)
                throw new Exception($"Connection failed (ClientSecret): {client.LastError}");
            return client;
        }

        throw new Exception(
            "Keine Authentifizierung angegeben. Verwende --token, " +
            "--client-id + --client-secret, oder --interactive.");
    }

    static ITestCenterConfig GetConfig(string name) => name.ToLowerInvariant() switch
    {
        "markant" => new MarkantConfig(),
        "standard" => new StandardCrmConfig(),
        "lm" => new StandardCrmConfig(),
        _ => throw new ArgumentException(
            $"Unbekanntes Config-Profil: '{name}'. Erlaubt: standard, markant, lm.")
    };

    static string MapStatusCode(int? code, ITestCenterConfig cfg)
    {
        if (code == cfg.StatusPlanned) return "Geplant";
        if (code == cfg.StatusRunning) return "Wird ausgeführt";
        if (code == cfg.StatusCompleted) return "Abgeschlossen";
        if (code == cfg.StatusFailed) return "Fehlgeschlagen";
        return $"? ({code})";
    }

    static void WriteHeader(string org, string filter, string config)
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("  D365 Test Center CLI");
        Console.WriteLine($"  Org:    {org}");
        Console.WriteLine($"  Filter: {filter}");
        Console.WriteLine($"  Config: {config}");
        Console.WriteLine("============================================================");
        Console.WriteLine();
    }
}
