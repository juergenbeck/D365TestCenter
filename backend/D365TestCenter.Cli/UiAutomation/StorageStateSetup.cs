using Microsoft.Playwright;

namespace D365TestCenter.Cli.UiAutomation;

/// <summary>
/// Interactive Playwright storage-state setup for UI tests (ADR-0006).
///
/// Opens a headed Chromium pointed at a Markant DEV or TEST org URL, waits up to
/// 5 minutes for the user to complete the manual login (with MFA if needed),
/// then persists the cookies + localStorage to a JSON file that can be loaded
/// by --browser-state in the run command.
///
/// Hard-guard: only DEV and TEST URLs are accepted. PROD, DATATEST and CDHTEST
/// setups are refused with a clear error.
///
/// TEST was opened up on 2026-07-26 so that the manual Zephyr tester cases, which
/// are written against TEST and reference fixed TEST records, can be mirrored by
/// automated runs. TEST permits the read and write steps required by the
/// commissioned test case without a separate approval for each write. This guard
/// only governs where a login state may be created and does not expand the scope
/// of the commissioned test.
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

        // Hard guard: DEV and TEST are accepted. PROD, DATATEST, CDHTEST and
        // all other hosts remain blocked.
        var istDev = org.Contains("-dev.", StringComparison.OrdinalIgnoreCase);
        var istTest = org.Contains("-test.", StringComparison.OrdinalIgnoreCase);

        if (!istDev && !istTest)
        {
            Console.Error.WriteLine($"FEHLER: --org '{org}' ist weder eine DEV- noch eine TEST-URL.");
            Console.Error.WriteLine("Storage-State-Setup ist auf DEV und TEST beschränkt (Markant-Zugriffsmatrix).");
            return 2;
        }

        if (istTest)
        {
            foreach (var line in GetTestEnvironmentNotice())
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

    internal static IReadOnlyList<string> GetTestEnvironmentNotice() =>
    [
        "HINWEIS: Anmeldezustand für TEST.",
        "  Auf TEST sind die beauftragten UI-Testfälle mit Lese- und Schreibschritten zulässig.",
        "  Schreibschritte benötigen keine gesonderte Freigabe je Aktion.",
        "  Der Anmeldezustand erweitert den beauftragten Testumfang nicht."
    ];
}
