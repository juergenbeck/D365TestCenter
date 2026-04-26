using Microsoft.Playwright;

namespace D365TestCenter.Cli.UiAutomation;

/// <summary>
/// Interactive Playwright storage-state setup for UI tests (ADR-0006).
///
/// Opens a headed Chromium pointed at the Markant DEV org URL, waits up to
/// 5 minutes for the user to complete the manual login (with MFA if needed),
/// then persists the cookies + localStorage to a JSON file that can be loaded
/// by --browser-state in the run command.
///
/// Hard-guard: only DEV URLs are accepted. PROD/TEST/DATATEST setups are
/// refused with a clear error — the Markant access matrix forbids non-DEV
/// UI test traffic.
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

        // DEV-only hard-guard. Markant access matrix: PROD/TEST/DATATEST = READONLY.
        if (!org.Contains("-dev.", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"FEHLER: --org '{org}' ist nicht eine DEV-URL.");
            Console.Error.WriteLine("Storage-State-Setup ist auf DEV beschraenkt (Markant-Zugriffsmatrix).");
            return 2;
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
        Console.WriteLine(">> Bitte jetzt im geoeffneten Browser einloggen (MFA ggf.).");
        Console.WriteLine(">> Skript wartet AUTOMATISCH bis Markant DEV geladen ist (Timeout 5 Min).");
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
            Console.Error.WriteLine("    Pruefen: Bist du auf der Markant-DEV-Hauptseite?");
            await browser.CloseAsync();
            return 3;
        }

        await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = output });
        Console.WriteLine();
        Console.WriteLine($"==> Storage-State gespeichert: {output}");
        Console.WriteLine($"    Lebensdauer: ~24h fuer SPA-Flow (Microsoft Identity Platform Default).");
        Console.WriteLine();
        Console.WriteLine($"Run UI tests via:");
        Console.WriteLine($"  D365TestCenter.Cli run --org {org} --browser-state {output} --filter MARKANT-UI-* ...");

        await browser.CloseAsync();
        return 0;
    }
}
