using Microsoft.Playwright;

namespace D365TestCenter.Cli.UiAutomation;

/// <summary>
/// Interactive Playwright storage-state setup for UI tests (ADR-0006).
///
/// Opens a headed Chromium pointed at a Markant DEV, TEST or CDHTEST org URL,
/// waits up to 5 minutes for the user to complete the manual login (with MFA if
/// needed), then persists the cookies + localStorage to a JSON file that can be
/// loaded by --browser-state in the run command.
///
/// Hard-guard: only DEV, TEST and CDHTEST URLs are accepted. PROD, ACCEPT,
/// DATATEST and every other host are refused with a clear error.
///
/// TEST was opened up on 2026-07-26 so that the manual Zephyr tester cases, which
/// are written against TEST and reference fixed TEST records, can be mirrored by
/// automated runs. CDHTEST followed on 2026-09-12 (ADR-2026-09-12-1152) for the
/// same reason: it carries the same standing write approval as TEST since
/// 2026-08-08, and six mirrored cases target it. Both environments permit the
/// read and write steps required by the commissioned test case without a
/// separate approval for each write. DATATEST carries that approval too but has
/// no UI test case, so it stays blocked until one exists. This guard only
/// governs where a login state may be created and does not expand the scope of
/// the commissioned test.
/// </summary>
public static class StorageStateSetup
{
    public static async Task<int> RunAsync(string org, string output)
    {
        if (string.IsNullOrWhiteSpace(org))
        {
            Console.Error.WriteLine("--org is required");
            return 1;
        }

        if (!TryResolveEnvironment(org, out var umgebung))
        {
            Console.Error.WriteLine($"FEHLER: --org '{org}' ist weder eine DEV-, TEST- noch CDHTEST-URL.");
            Console.Error.WriteLine("Storage-State-Setup ist auf DEV, TEST und CDHTEST beschränkt (Markant-Zugriffsmatrix).");
            return 2;
        }

        if (umgebung is "TEST" or "CDHTEST")
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
        Console.WriteLine(">> Skript wartet AUTOMATISCH bis die Markant-Umgebung geladen ist (Timeout 5 Min).");
        Console.WriteLine();

        try
        {
            // Wait for either the topBar (post-login) or the Markant host pattern
            // (we left login.microsoftonline.com behind).
            await page.WaitForFunctionAsync(@"() => {
                if (document.querySelector(""[data-id='topBar']"")) return true;
                if (document.querySelector(""[data-id='shellAppSwitcher']"")) return true;
                if (window.location.hostname.includes('markant') &&
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
        Console.WriteLine($"  D365TestCenter.Cli run --org {org} --browser-state {output} --filter MARKANT-UI-* ...");

        await browser.CloseAsync();
        return 0;
    }

    /// <summary>
    /// Hard guard: maps an org URL to the environment label a storage state may be
    /// created for. DEV, TEST and CDHTEST are accepted, everything else is refused.
    ///
    /// The markers are disjoint on purpose: "markant-cdhtest." does not contain
    /// "-test." (an 'h' precedes "test."), and neither does "markant-datatest.",
    /// so adding CDHTEST does not widen the two existing markers.
    /// </summary>
    internal static bool TryResolveEnvironment(string org, out string umgebung)
    {
        umgebung = string.Empty;
        if (string.IsNullOrWhiteSpace(org)) return false;

        // Only the host counts. A marker in path or query (e.g. "?x=-cdhtest.")
        // must not open the guard for a PROD host.
        if (!Uri.TryCreate(org, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host + ".";

        if (host.Contains("-cdhtest.", StringComparison.OrdinalIgnoreCase)) umgebung = "CDHTEST";
        else if (host.Contains("-dev.", StringComparison.OrdinalIgnoreCase)) umgebung = "DEV";
        else if (host.Contains("-test.", StringComparison.OrdinalIgnoreCase)) umgebung = "TEST";
        else return false;

        return true;
    }

    /// <summary>
    /// Notice shown before the browser starts for an environment that carries the
    /// standing Markant write approval (TEST since 2026-07-26, CDHTEST since
    /// 2026-08-08). <paramref name="umgebung"/> is the environment label.
    /// </summary>
    internal static IReadOnlyList<string> GetWriteEnabledEnvironmentNotice(string umgebung) =>
    [
        $"HINWEIS: Anmeldezustand für {umgebung}.",
        $"  Auf {umgebung} sind die beauftragten UI-Testfälle mit Lese- und Schreibschritten zulässig.",
        "  Schreibschritte benötigen keine gesonderte Freigabe je Aktion.",
        "  Der Anmeldezustand erweitert den beauftragten Testumfang nicht."
    ];
}
