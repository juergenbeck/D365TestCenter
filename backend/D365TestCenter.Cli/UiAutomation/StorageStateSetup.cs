using Microsoft.Playwright;

namespace D365TestCenter.Cli.UiAutomation;

/// <summary>
/// Interactive Playwright storage-state setup for UI tests (ADR-0006).
///
/// Opens a headed Chromium pointed at a non-production org URL, waits up to
/// 5 minutes for the user to complete the manual login (with MFA if needed),
/// then persists the cookies + localStorage to a JSON file that can be loaded
/// by --browser-state in the run command.
///
/// Hard-guard: only hosts whose first label ends with "-dev" or "-test" are
/// accepted by default. Further environments are opened explicitly per call via
/// --allow-env (e.g. --allow-env uat for "contoso-uat.crm4.dynamics.com"), so the
/// decision which environment may carry a login state stays with the project
/// that runs the tests. Every other host (PROD in particular) is refused.
/// </summary>
public static class StorageStateSetup
{
    public static async Task<int> RunAsync(string org, string output, string[]? allowEnv = null)
    {
        if (string.IsNullOrWhiteSpace(org))
        {
            Console.Error.WriteLine("--org is required");
            return 1;
        }

        if (!TryResolveEnvironment(org, allowEnv, out var umgebung))
        {
            Console.Error.WriteLine($"FEHLER: --org '{org}' ist keine zugelassene Nicht-Produktions-URL.");
            Console.Error.WriteLine("Zugelassen sind Hosts mit '-dev.' oder '-test.' sowie per --allow-env freigegebene Umgebungen.");
            return 2;
        }

        if (umgebung != "DEV")
        {
            foreach (var line in GetWriteEnabledEnvironmentNotice(umgebung))
            {
                Console.WriteLine(line);
            }
            Console.WriteLine();
        }

        Console.WriteLine($"==> Storage-State-Setup");
        Console.WriteLine($"    Org:    {org}");
        Console.WriteLine($"    Output: {output}");
        Console.WriteLine();
        Console.WriteLine("WICHTIG: idealerweise im Inkognito-Browser-Modus einloggen,");
        Console.WriteLine("um Token-Spillover auf andere Tenants/Apps zu vermeiden.");
        Console.WriteLine();

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");

        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var startUrl = org.TrimEnd('/') + "/main.aspx";
        await page.GotoAsync(startUrl);

        Console.WriteLine();
        Console.WriteLine(">> Bitte jetzt im geöffneten Browser einloggen (MFA ggf.).");
        Console.WriteLine(">> Skript wartet AUTOMATISCH bis die Ziel-Umgebung geladen ist (Timeout 5 Min).");
        Console.WriteLine();

        try
        {
            // Wait for either the topBar (post-login) or the org host with the app
            // shell loaded (we left login.microsoftonline.com behind).
            await page.WaitForFunctionAsync(@"() => {
                if (document.querySelector(""[data-id='topBar']"")) return true;
                if (document.querySelector(""[data-id='shellAppSwitcher']"")) return true;
                if (window.location.hostname.endsWith('.dynamics.com') &&
                    !window.location.pathname.includes('signin') &&
                    document.querySelector(""[role='banner'], iframe[name^='ContentFrame']"")) {
                    return true;
                }
                return false;
            }", null,
            new PageWaitForFunctionOptions { Timeout = 300000, PollingInterval = 2000 });
            Console.WriteLine("    OK: Login erkannt. Speichere Storage-State...");
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("    TIMEOUT (5 Min): Login nicht erkannt.");
            Console.Error.WriteLine("    Prüfen: Bist du auf der Hauptseite der Ziel-Umgebung?");
            await browser.CloseAsync();
            return 3;
        }
        catch (PlaywrightException ex)
        {
            // Fenster vom Benutzer geschlossen oder Browser weggebrochen. Ohne
            // diesen Zweig endet der Befehl mit einer unbehandelten Ausnahme und
            // einem Stapelabzug statt einer lesbaren Meldung.
            Console.Error.WriteLine();
            Console.Error.WriteLine("    ABBRUCH: Browser wurde geschlossen, bevor der Login erkannt war.");
            Console.Error.WriteLine($"    Meldung: {ex.Message}");
            return 4;
        }

        await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = output });
        Console.WriteLine();
        Console.WriteLine($"==> Storage-State gespeichert: {output}");
        Console.WriteLine($"    Lebensdauer: ~24h für SPA-Flow (Microsoft Identity Platform Default).");
        Console.WriteLine();
        Console.WriteLine($"Run UI tests via:");
        Console.WriteLine($"  D365TestCenter.Cli run --org {org} --browser-state {output} --filter <UI-TestId-Muster> ...");

        await browser.CloseAsync();
        return 0;
    }

    /// <summary>
    /// Hard guard: maps an org URL to the environment label a storage state may be
    /// created for. "-dev." and "-test." are always accepted; <paramref name="allowEnv"/>
    /// adds further markers (letters and digits only, e.g. "uat" matches "-uat.").
    /// Everything else is refused. Only the host counts, and the markers are matched
    /// with the leading hyphen and trailing dot, so "-perftest." does not contain
    /// "-test." and stays refused unless "perftest" is allowed explicitly.
    /// </summary>
    // Never accepted via --allow-env: a login state for production is out of scope.
    private static readonly HashSet<string> ProductionMarkers = ["prod", "prd", "production", "live"];

    internal static bool TryResolveEnvironment(string org, IEnumerable<string>? allowEnv, out string umgebung)
    {
        umgebung = string.Empty;
        if (string.IsNullOrWhiteSpace(org)) return false;

        // Only the host counts. A marker in path or query (e.g. "?x=-test.")
        // must not open the guard for a PROD host.
        if (!Uri.TryCreate(org, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host + ".";

        var markers = (allowEnv ?? [])
            .Select(m => m.Trim().ToLowerInvariant())
            .Where(m => m.Length > 0 && m.All(char.IsLetterOrDigit) && !ProductionMarkers.Contains(m))
            .Concat(["dev", "test"])
            .Distinct();

        foreach (var marker in markers)
        {
            if (host.Contains($"-{marker}.", StringComparison.OrdinalIgnoreCase))
            {
                umgebung = marker.ToUpperInvariant();
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Notice shown before the browser starts for any environment other than DEV.
    /// <paramref name="umgebung"/> is the environment label.
    /// </summary>
    internal static IReadOnlyList<string> GetWriteEnabledEnvironmentNotice(string umgebung) =>
    [
        $"HINWEIS: Anmeldezustand für {umgebung}.",
        $"  Welche Lese- und Schreibschritte auf {umgebung} zulässig sind, regelt das Projekt, das die Tests beauftragt.",
        "  Der Anmeldezustand erweitert den beauftragten Testumfang nicht."
    ];
}
