using D365TestCenter.Core;
using Microsoft.Playwright;

namespace D365TestCenter.Cli.UiAutomation;

/// <summary>
/// Playwright-backed implementation of <see cref="IBrowserActionExecutor"/> (ADR-0006).
///
/// Manages a single browser+context+page across all BrowserAction steps within a CLI run.
/// On Dispose, the trace.zip is finalised and the browser closed.
///
/// Configuration via constructor:
///   - storageStatePath: required, points to a Playwright storage-state JSON
///     (created via Setup-PlaywrightStorageState.ps1).
///   - headless: default true (CI/CD friendly). Set false for local debug.
///   - locale: default "de-DE" (Markant primary locale). Override per-test if needed.
///   - tracePath: optional path where trace.zip is written on disposal.
///
/// Login-Redirect Detection: any navigation that lands on
/// "login.microsoftonline.com" raises an exception with a clear message —
/// the storage-state has expired and the user must re-run setup.
/// </summary>
public sealed class PlaywrightBrowserActionExecutor : IBrowserActionExecutor
{
    private readonly string _storageStatePath;
    private readonly bool _headless;
    private readonly string _locale;
    private readonly string? _tracePath;
    private readonly Action<string> _log;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _tracingActive;

    public StepDiagnostics? LastDiagnostics { get; private set; }

    public PlaywrightBrowserActionExecutor(
        string storageStatePath,
        bool headless = true,
        string locale = "de-DE",
        string? tracePath = null,
        Action<string>? log = null)
    {
        _storageStatePath = storageStatePath ?? throw new ArgumentNullException(nameof(storageStatePath));
        _headless = headless;
        _locale = locale;
        _tracePath = tracePath;
        _log = log ?? Console.WriteLine;
    }

    private async Task<IPage> EnsurePageAsync()
    {
        if (_page != null) return _page;

        if (!File.Exists(_storageStatePath))
        {
            throw new FileNotFoundException(
                $"Storage-state file not found: {_storageStatePath}. " +
                "Run Setup-PlaywrightStorageState.ps1 first.");
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = _headless });
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            StorageStatePath = _storageStatePath,
            Locale = _locale,
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });

        if (_tracePath != null)
        {
            await _context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
                Sources = true
            });
            _tracingActive = true;
        }

        _page = await _context.NewPageAsync();
        return _page;
    }

    public async Task ExecuteAsync(TestStep step, TestContext ctx)
    {
        if (!string.Equals(step.Action, "BrowserAction", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected Action=BrowserAction, got {step.Action}");
        }

        var operation = step.Operation?.ToLowerInvariant()
            ?? throw new InvalidOperationException("BrowserAction step requires 'operation' (navigate/click/...)");

        var page = await EnsurePageAsync();

        switch (operation)
        {
            case "navigate":
                await Navigate(page, step);
                break;
            case "click":
                await Click(page, step, doubleClick: false);
                break;
            case "doubleclick":
                await Click(page, step, doubleClick: true);
                break;
            case "fill":
                await Fill(page, step);
                break;
            case "delay":
                await Task.Delay(step.DelayMs ?? 500);
                break;
            case "waitfor":
                await WaitFor(page, step);
                break;
            case "screenshot":
                await Screenshot(page, step);
                break;
            case "evaluate":
                await Evaluate(page, step, ctx);
                break;
            default:
                throw new InvalidOperationException($"Unknown BrowserAction operation: {operation}");
        }
    }

    private async Task Navigate(IPage page, TestStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Url))
        {
            throw new InvalidOperationException("BrowserAction operation=navigate requires 'url'");
        }

        await page.GotoAsync(step.Url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = step.TimeoutSeconds * 1000
        });

        // Login-Redirect-Detection (Storage-State expired?)
        var assertNoRedirect = step.AssertNoLoginRedirect ?? true;
        if (assertNoRedirect && page.Url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            await CapturePageDiagnostics(page);
            throw new InvalidOperationException(
                "Storage-state expired — page redirected to login.microsoftonline.com. " +
                "Run Setup-PlaywrightStorageState.ps1 again.");
        }

        if (!string.IsNullOrWhiteSpace(step.WaitForSelector))
        {
            await page.Locator(step.WaitForSelector)
                .WaitForAsync(new LocatorWaitForOptions { Timeout = step.TimeoutSeconds * 1000 });
        }

        _log($"      navigate: {step.Url}");
    }

    private async Task Click(IPage page, TestStep step, bool doubleClick)
    {
        if (string.IsNullOrWhiteSpace(step.Selector))
        {
            throw new InvalidOperationException("BrowserAction operation=click requires 'selector'");
        }

        var locator = page.Locator(step.Selector).First;
        var hasMatch = await locator.CountAsync() > 0;

        if (!hasMatch && !string.IsNullOrWhiteSpace(step.FallbackSelector))
        {
            locator = page.Locator(step.FallbackSelector).First;
            _log($"      primary selector matched 0, using fallback: {step.FallbackSelector}");
        }

        if (doubleClick)
        {
            await locator.DblClickAsync();
        }
        else
        {
            await locator.ClickAsync();
        }

        if (!string.IsNullOrWhiteSpace(step.WaitForSelector))
        {
            await page.Locator(step.WaitForSelector)
                .WaitForAsync(new LocatorWaitForOptions { Timeout = step.TimeoutSeconds * 1000 });
        }

        _log($"      {(doubleClick ? "doubleClick" : "click")}: {step.Selector}");
    }

    private async Task Fill(IPage page, TestStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Selector))
        {
            throw new InvalidOperationException("BrowserAction operation=fill requires 'selector'");
        }
        var value = step.Value ?? "";
        await page.Locator(step.Selector).FillAsync(value);
        _log($"      fill: {step.Selector} = '{value}'");
    }

    private async Task WaitFor(IPage page, TestStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Selector))
        {
            throw new InvalidOperationException("BrowserAction operation=waitFor requires 'selector'");
        }
        await page.Locator(step.Selector)
            .First.WaitForAsync(new LocatorWaitForOptions { Timeout = step.TimeoutSeconds * 1000 });
        _log($"      waitFor: {step.Selector}");
    }

    private async Task Screenshot(IPage page, TestStep step)
    {
        var name = step.Name ?? $"step-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        var path = Path.Combine(Path.GetTempPath(), $"{name}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        _log($"      screenshot: {path}");
    }

    private async Task Evaluate(IPage page, TestStep step, TestContext ctx)
    {
        if (string.IsNullOrWhiteSpace(step.Expression))
        {
            throw new InvalidOperationException("BrowserAction operation=evaluate requires 'expression'");
        }

        // Try top-level frame first (Modern UCI is iframe-less for top-level content,
        // verified via PoC selector spike 2026-04-26).
        var result = await page.EvaluateAsync<object?>(step.Expression);

        // If alias is set, store result in OutputAliases for placeholder resolution
        // in subsequent steps (analogue to ExecuteRequest.OutputAlias from A4).
        if (!string.IsNullOrWhiteSpace(step.OutputAlias))
        {
            if (!ctx.OutputAliases.ContainsKey(step.OutputAlias))
            {
                ctx.OutputAliases[step.OutputAlias] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
            ctx.OutputAliases[step.OutputAlias]["result"] = result;
        }

        _log($"      evaluate: {step.Expression?.Replace("\n", " ")} => {result ?? "null"}");
    }

    private async Task CapturePageDiagnostics(IPage page)
    {
        try
        {
            var pngBytes = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
            LastDiagnostics = new StepDiagnostics
            {
                ScreenshotPng = pngBytes,
                Context = $"URL: {page.Url}; Title: {await page.TitleAsync()}"
            };
        }
        catch
        {
            // Diagnostics are best-effort — never fail the test because of capture issues.
        }
    }

    public void Dispose()
    {
        // Async cleanup over sync entry point — Playwright objects support sync Dispose.
        try
        {
            if (_tracingActive && _context != null && _tracePath != null)
            {
                _context.Tracing.StopAsync(new TracingStopOptions { Path = _tracePath })
                    .GetAwaiter().GetResult();
            }
        }
        catch { /* best-effort */ }

        try { _page?.CloseAsync().GetAwaiter().GetResult(); } catch { }
        try { _context?.CloseAsync().GetAwaiter().GetResult(); } catch { }
        try { _browser?.CloseAsync().GetAwaiter().GetResult(); } catch { }
        _playwright?.Dispose();
    }
}
