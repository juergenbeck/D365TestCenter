using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace D365TestCenter.Cli;

/// <summary>
/// E4 (ADR-0008): renders a self-contained HTML string to a PDF file via
/// Playwright Chromium (PdfAsync). Playwright is already a CLI dependency
/// (ADR-0006 UI tests); this path needs no storage-state and no network -
/// the HTML is set directly with SetContentAsync. Requires the Chromium
/// browser binary (playwright install chromium); a missing browser yields a
/// clear hint instead of a raw Playwright stack.
/// </summary>
public static class PdfRenderer
{
    public static async Task RenderHtmlToPdfAsync(string html, string outPath)
    {
        IPlaywright? pw = null;
        IBrowser? browser = null;
        try
        {
            pw = await Playwright.CreateAsync();
            browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync();
            // Self-contained HTML (inline CSS, no external assets) -> Load is enough.
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load });
            await page.PdfAsync(new PagePdfOptions
            {
                Path = outPath,
                Format = "A4",
                PrintBackground = true,
                Margin = new Margin { Top = "1.5cm", Bottom = "1.5cm", Left = "1.4cm", Right = "1.4cm" }
            });
        }
        catch (PlaywrightException ex) when (
            ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PDF-Erzeugung benötigt den Chromium-Browser von Playwright. " +
                "Einmalig installieren mit: pwsh backend/publish/cli/playwright.ps1 install chromium " +
                "(oder 'dotnet <Cli.dll> ...' nach 'playwright install chromium'). " +
                "Alternativ --format html nutzen und im Browser als PDF drucken.", ex);
        }
        finally
        {
            if (browser != null) await browser.CloseAsync();
            pw?.Dispose();
        }
    }
}
