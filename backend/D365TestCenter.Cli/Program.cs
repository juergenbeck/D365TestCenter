using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Linq;
using System.Threading.Tasks;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

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
        runCommand.Handler = CommandHandler.Create<string, string?, string?, string?, string?, bool, string, bool, string, string?, bool, string, string?>(
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
        string? browserState, bool browserHeaded, string browserLocale, string? browserTrace)
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
