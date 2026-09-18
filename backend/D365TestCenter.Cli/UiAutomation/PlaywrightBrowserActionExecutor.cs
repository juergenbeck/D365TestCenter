using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
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
///   - locale: default "de-DE". Override per-test if needed.
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
    private readonly EvidenceCollector? _evidence;

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
        Action<string>? log = null,
        EvidenceCollector? evidence = null)
    {
        _storageStatePath = storageStatePath ?? throw new ArgumentNullException(nameof(storageStatePath));
        _headless = headless;
        _locale = locale;
        _tracePath = tracePath;
        _log = log ?? Console.WriteLine;
        _evidence = evidence;
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

        // A native dialog (alert, confirm, beforeunload) blocks the page's JavaScript thread,
        // so timers inside an evaluated script stop firing. Log every dialog and dismiss it,
        // matching Playwright's default behaviour without a listener, so it becomes visible.
        _page.Dialog += async (_, dialog) =>
        {
            _log($"      browser-dialog: type={dialog.Type} message='{dialog.Message}' -> dismiss");
            try { await dialog.DismissAsync(); } catch { /* already handled */ }
        };
        _page.Crash += (_, _) => _log("      browser-page: CRASH");
        _page.FrameNavigated += (_, frame) =>
        {
            if (frame == _page.MainFrame) _log($"      browser-navigated: {Shorten(frame.Url, 160)}");
        };
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

        // Reset diagnostics from any previous step — only the most recent
        // failure carries diagnostics into the Orchestrator's file-upload.
        LastDiagnostics = null;

        try
        {
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
                    await Screenshot(page, step, ctx);
                    break;
                case "evaluate":
                    await Evaluate(page, step, ctx);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown BrowserAction operation: {operation}");
            }
        }
        catch (Exception)
        {
            // ADR-0006 Phase 1d: capture diagnostics on any failure for later
            // upload to jbe_testrunresult.jbe_screenshot / jbe_uitrace.
            // Re-throw so TestRunner marks the step as Failed/Error.
            await CapturePageDiagnostics(page);
            throw;
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
            // .First avoids Playwright strict-mode violations when the wait-for
            // selector matches multiple elements (e.g. wide attribute selectors
            // like [data-id*='HomePageGrid'] which can resolve to many toolbar
            // children). The wait is just a "loaded?"-probe, not an interaction.
            await page.Locator(step.WaitForSelector)
                .First.WaitForAsync(new LocatorWaitForOptions { Timeout = step.TimeoutSeconds * 1000 });
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
            // .First avoids Playwright strict-mode violations when the wait-for
            // selector matches multiple elements (e.g. wide attribute selectors
            // like [data-id*='HomePageGrid'] which can resolve to many toolbar
            // children). The wait is just a "loaded?"-probe, not an interaction.
            await page.Locator(step.WaitForSelector)
                .First.WaitForAsync(new LocatorWaitForOptions { Timeout = step.TimeoutSeconds * 1000 });
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

    private async Task Screenshot(IPage page, TestStep step, TestContext ctx)
    {
        var name = step.Name ?? $"step-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        bool usesEvidenceFeatures = _evidence != null
            || (step.HighlightFields?.Count ?? 0) > 0
            || step.Clip != null || step.HideOverlays != null || (step.HideSelectors?.Count ?? 0) > 0;

        if (!usesEvidenceFeatures)
        {
            // Previous behaviour (ADR-0006): full page into %TEMP%.
            var path = Path.Combine(Path.GetTempPath(), $"{name}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            _log($"      screenshot: {path}");
            return;
        }

        // ADR 2026-09-16-1957: evidence screenshot with purpose, highlighted fields,
        // actual values, clipped parts and hidden overlays.
        var fields = step.HighlightFields?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>();
        var clip = (step.Clip ?? (fields.Count > 0 ? "section" : "page")).Trim().ToLowerInvariant();
        if (clip != "section" && clip != "viewport" && clip != "page")
            throw new InvalidOperationException($"BrowserAction screenshot: unknown clip '{step.Clip}' (allowed: section, viewport, page)");

        var item = new EvidenceItem
        {
            TestId = ctx.TestId,
            StepNumber = step.StepNumber,
            Caption = !string.IsNullOrWhiteSpace(step.Caption) ? step.Caption!
                : !string.IsNullOrWhiteSpace(step.Description) ? step.Description : name,
            CapturedAtUtc = DateTime.UtcNow,
            Url = page.Url,
            Clip = clip
        };

        PrepareResult prep;
        try
        {
            var argJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                hideOverlays = step.HideOverlays ?? true,
                hideSelectors = step.HideSelectors ?? new List<string>(),
                fields,
                tagSections = clip == "section"
            });
            _log("      screenshot-phase: vorbereiten");
            prep = await EvaluateJsonAsync<PrepareResult>(page, PrepareScript, argJson) ?? new PrepareResult();
        }
        catch (Exception ex)
        {
            prep = new PrepareResult();
            item.Warnings.Add("Vorbereitung der Aufnahme fehlgeschlagen: " + ex.Message.Split('\n')[0]);
        }
        item.HiddenOverlays.AddRange(prep.Hidden ?? new List<string>());
        item.Warnings.AddRange(prep.Warnings ?? new List<string>());
        foreach (var f in prep.Facts ?? new List<PrepareFact>())
            item.Facts.Add(new EvidenceFact { Field = f.Field ?? "", Label = f.Label, Value = f.Value });

        try
        {
            // Let the layout settle after hiding overlays and outlining fields.
            await Task.Delay(700);
            // Fields found at preparation time; they must be rendered again before a capture.
            int expectedFields = fields.Count - item.Warnings.Count(w => w.EndsWith("ist auf dem Formular nicht sichtbar.", StringComparison.Ordinal));
            var sections = prep.Sections ?? new List<PrepareSection>();
            if (clip == "section" && sections.Count > 0)
            {
                // Sections that fit into the visible form area together become one image;
                // otherwise each cluster is captured separately.
                _log("      screenshot-phase: abschnitte gruppieren");
                var clusters = await EvaluateJsonAsync<List<List<int>>>(page, ClusterScript, "null") ?? new List<List<int>>();
                int part = 1;
                foreach (var cluster in clusters.Where(c => c.Count > 0))
                {
                    var label = string.Join(" / ", cluster
                        .Select(i => sections.FirstOrDefault(s => s.Index == i)?.Label)
                        .Where(l => !string.IsNullOrWhiteSpace(l)));
                    var clusterJson = System.Text.Json.JsonSerializer.Serialize(cluster);
                    _log($"      screenshot-phase: scrollen {label}");
                    await EvaluateJsonAsync<bool>(page, ScrollClusterScript, clusterJson);
                    await Task.Delay(700);
                    _log($"      screenshot-phase: felder abwarten {label}");
                    await WaitForFieldsRenderedAsync(page, expectedFields, item, label);
                    var rect = await EvaluateJsonAsync<ClusterRect>(page, MeasureClusterScript, clusterJson);
                    byte[] bytes = Array.Empty<byte>();
                    if (rect != null && rect.Width > 0 && rect.Height > 0)
                    {
                        // The form shifts its content asynchronously (e.g. the AI form-fill bar or the Copilot
                        // card appearing above the sections, measured on a customer TEST org 2026-09-16). Measure,
                        // capture, measure again; retry when the section moved in between.
                        bool stable = false;
                        for (int attempt = 1; attempt <= 4 && !stable; attempt++)
                        {
                            const double pad = 8;
                            double x = Math.Max(0, rect!.X - pad), y = Math.Max(0, rect.Y - pad);
                            double right = Math.Min(rect.ViewportWidth, rect.X + rect.Width + pad);
                            double bottom = Math.Min(rect.ViewportHeight, rect.Y + rect.Height + pad);
                            _log($"      screenshot-ausschnitt: {label} x={rect.X:0} y={rect.Y:0} b={rect.Width:0} h={rect.Height:0} (Versuch {attempt})");
                            bytes = await page.ScreenshotAsync(new PageScreenshotOptions
                            {
                                Clip = new Clip { X = (float)x, Y = (float)y, Width = (float)(right - x), Height = (float)(bottom - y) },
                                Animations = ScreenshotAnimations.Disabled
                            });
                            var after = await EvaluateJsonAsync<ClusterRect>(page, MeasureClusterScript, clusterJson);
                            stable = after != null && Math.Abs(after.X - rect.X) < 3 && Math.Abs(after.Y - rect.Y) < 3
                                     && Math.Abs(after.Height - rect.Height) < 3;
                            if (!stable)
                            {
                                await Task.Delay(1000);
                                await EvaluateJsonAsync<bool>(page, ScrollClusterScript, clusterJson);
                                await Task.Delay(500);
                                await WaitForFieldsRenderedAsync(page, expectedFields, item, label);
                                rect =await EvaluateJsonAsync<ClusterRect>(page, MeasureClusterScript, clusterJson) ?? after ?? rect;
                            }
                        }
                        if (!stable)
                            item.Warnings.Add($"Abschnitt {label} hat sich während der Aufnahme wiederholt verschoben; der Ausschnitt kann ungenau sein.");
                        if (rect!.Y + rect.Height > rect.ViewportHeight)
                            item.Warnings.Add($"Abschnitt {label} ist höher als der sichtbare Bereich und wurde unten abgeschnitten.");
                    }
                    else
                    {
                        var locator = page.Locator($"[data-tc-section=\"{cluster[0]}\"]").First;
                        await locator.ScrollIntoViewIfNeededAsync();
                        bytes = await locator.ScreenshotAsync(new LocatorScreenshotOptions { Animations = ScreenshotAnimations.Disabled });
                    }
                    SavePart(item, name, part++, bytes, label);
                }
            }
            else
            {
                if (clip == "section")
                    item.Warnings.Add("Kein Formularabschnitt zu den hervorgehobenen Feldern gefunden, aufgenommen wurde der sichtbare Bereich.");
                if (fields.Count > 0)
                    await WaitForFieldsRenderedAsync(page, expectedFields, item, clip);
                else
                    await EvaluateJsonAsync<int>(page, HighlightScript, "null"); // re-hide late overlays
                var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    FullPage = clip == "page",
                    Animations = ScreenshotAnimations.Disabled
                });
                SavePart(item, name, 1, bytes, null);
            }
        }
        finally
        {
            // Overlays hidden only right before a capture (appeared after the preparation) count as well.
            try
            {
                var late = await EvaluateJsonAsync<List<string>>(page, "() => window.__tcHidden || []", "null");
                foreach (var h in late ?? new List<string>())
                    if (!item.HiddenOverlays.Contains(h)) item.HiddenOverlays.Add(h);
            }
            catch { /* page may have navigated */ }
            try { await page.EvaluateAsync(RestoreScript); } catch { /* page may have navigated */ }
        }

        if (_evidence != null)
        {
            _evidence.Items.Add(item);
            foreach (var p in item.Parts) _log($"      screenshot: {_evidence.ToFullPath(p.File)}");
        }
        foreach (var w in item.Warnings) _log($"      screenshot-hinweis: {w}");
        if (item.HiddenOverlays.Count > 0) _log($"      ausgeblendet: {string.Join(", ", item.HiddenOverlays)}");
    }

    // After a save the form rebuilds its sections and shows grey loading placeholders for a few
    // seconds (measured on the account form, 2026-09-16). Re-apply the frame until all
    // fields found at preparation time are rendered again, at most 20 seconds.
    private async Task WaitForFieldsRenderedAsync(IPage page, int expected, EvidenceItem item, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int found = 0;
        while (true)
        {
            found = await EvaluateJsonAsync<int>(page, HighlightScript, "null");
            if (found >= expected || DateTime.UtcNow > deadline) break;
            await Task.Delay(500);
        }
        _log($"      screenshot-phase: {found} von {expected} feldern dargestellt");
        if (found < expected)
            item.Warnings.Add($"Im Abschnitt {label} waren nach 20 Sekunden nur {found} von {expected} Feldern dargestellt.");
        await Task.Delay(800);
    }

    // Exchanges data with the page as JSON strings only: the script receives the parsed
    // argument and its result is stringified in the page, then deserialized here. This avoids
    // Playwright's typed result mapping, which failed on nested result objects (measured on
    // measured 2026-09-16: NullReferenceException inside EvaluateAsync<T>).
    private static readonly System.Text.Json.JsonSerializerOptions PageJson = new() { PropertyNameCaseInsensitive = true };

    private static async Task<T?> EvaluateJsonAsync<T>(IPage page, string script, string argJson)
    {
        var wrapper = "(a) => JSON.stringify((" + script + ")(JSON.parse(a)))";
        // page.EvaluateAsync has no timeout of its own; a blocked page would stall the whole run.
        var evaluation = page.EvaluateAsync<string>(wrapper, argJson);
        if (await Task.WhenAny(evaluation, Task.Delay(TimeSpan.FromSeconds(30))) != evaluation)
            throw new TimeoutException("Aufnahme-Skript im Browser hat nach 30 Sekunden nicht geantwortet.");
        var json = await evaluation;
        return string.IsNullOrEmpty(json) || json == "null"
            ? default
            : System.Text.Json.JsonSerializer.Deserialize<T>(json, PageJson);
    }

    private void SavePart(EvidenceItem item, string name, int part, byte[] bytes, string? label)
    {
        var (w, h) = EvidenceCollector.PngSize(bytes);
        if (_evidence != null)
        {
            var rel = _evidence.NewImagePath(item.TestId, item.StepNumber, name, part);
            File.WriteAllBytes(_evidence.ToFullPath(rel), bytes);
            item.Parts.Add(new EvidenceImagePart { File = rel, Label = label, Width = w, Height = h });
        }
        else
        {
            var path = Path.Combine(Path.GetTempPath(), part == 1 ? $"{name}.png" : $"{name}-{part}.png");
            File.WriteAllBytes(path, bytes);
            _log($"      screenshot: {path}");
        }
    }

    internal sealed class PrepareResult
    {
        public List<string>? Hidden { get; set; }
        public List<string>? Warnings { get; set; }
        public List<PrepareFact>? Facts { get; set; }
        public List<PrepareSection>? Sections { get; set; }
    }

    internal sealed class PrepareFact
    {
        public string? Field { get; set; }
        public string? Label { get; set; }
        public string? Value { get; set; }
    }

    internal sealed class PrepareSection
    {
        public int Index { get; set; }
        public string? Label { get; set; }
    }

    internal sealed class ClusterRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double ViewportWidth { get; set; }
        public double ViewportHeight { get; set; }
    }

    // Groups the tagged sections (top to bottom) as long as their combined height fits the
    // form area below the header: the scroll container's visible height minus a margin.
    private const string ClusterScript = @"() => {
  // Effective bottom: a long section tail below its last highlighted field is cut off at capture
  // time (MeasureClusterScript), so clustering uses the same trimmed height.
  const fieldEls = (window.__tcFields || []).map(f => window.__tcFindField ? window.__tcFindField(f) : null).filter(Boolean);
  const secs = Array.from(document.querySelectorAll('[data-tc-section]'))
    .map(e => {
      const rr = e.getBoundingClientRect();
      const inside = fieldEls.filter(f => e.contains(f)).map(f => f.getBoundingClientRect().bottom);
      let bottom = rr.bottom;
      if (inside.length) { const fb = Math.max(...inside); if (rr.bottom - fb > 120) bottom = fb + 24; }
      return { i: +e.getAttribute('data-tc-section'), r: { top: rr.top, bottom } };
    })
    .sort((a, b) => a.r.top - b.r.top);
  let scroller = secs.length ? document.querySelector('[data-tc-section=""' + secs[0].i + '""]') : null;
  while (scroller && scroller !== document.body && !(scroller.scrollHeight > scroller.clientHeight + 4 && getComputedStyle(scroller).overflowY !== 'visible')) scroller = scroller.parentElement;
  const available = scroller && scroller !== document.body ? Math.min(scroller.getBoundingClientRect().height, innerHeight) - 24 : innerHeight - 24;
  const out = []; let cur = null, top = 0, bottom = 0;
  for (const s of secs) {
    if (cur && Math.max(bottom, s.r.bottom) - top <= available) { cur.push(s.i); bottom = Math.max(bottom, s.r.bottom); }
    else { cur = [s.i]; out.push(cur); top = s.r.top; bottom = s.r.bottom; }
  }
  return out;
}";

    private const string ScrollClusterScript = @"(idx) => {
  const els = idx.map(i => document.querySelector('[data-tc-section=""' + i + '""]')).filter(Boolean);
  if (!els.length) return false;
  els.sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top);
  els[0].scrollIntoView({ block: 'start' });
  return true;
}";

    private const string MeasureClusterScript = @"(idx) => {
  const rs = idx.map(i => document.querySelector('[data-tc-section=""' + i + '""]')).filter(Boolean).map(e => e.getBoundingClientRect());
  if (!rs.length) return null;
  const l = Math.min(...rs.map(r => r.left)), t = Math.min(...rs.map(r => r.top));
  const r = Math.max(...rs.map(x => x.right));
  // Cut off long section tails (e.g. subgrids) below the last highlighted field of each section.
  const fieldEls = (window.__tcFields || []).map(f => window.__tcFindField ? window.__tcFindField(f) : null).filter(Boolean);
  const b = Math.max(...idx.map(i => document.querySelector('[data-tc-section=""' + i + '""]')).filter(Boolean).map(e => {
    const rr = e.getBoundingClientRect();
    const inside = fieldEls.filter(f => e.contains(f)).map(f => f.getBoundingClientRect().bottom);
    if (!inside.length) return rr.bottom;
    const fb = Math.max(...inside);
    return rr.bottom - fb > 120 ? fb + 24 : rr.bottom;
  }));
  return { x: l, y: t, width: r - l, height: b - t, viewportWidth: innerWidth, viewportHeight: innerHeight };
}";

    // Known Dynamics 365 overlays, measured 2026-09-16 (ADR 2026-09-16-1957):
    //  - Copilot record summary: card with [data-testid="actionButton-ViewFullInsight"] inside
    //    #outerHeaderContainer_*, appears ~10 s after form load, no data-id, generated CSS classes.
    //    The whole wrapper (direct child of the header container) is hidden.
    //  - "Formularunterstützung" (form fill assist) toggle: [data-id="FormFillBar_Show_Button"].
    //  - Microsoft teaching popovers (measured 2026-09-17 in the trace DOM snapshot):
    //    "Formulare schneller ausfüllen mit KI" is a Fluent v9 .fui-TeachingPopoverSurface anchored
    //    over the form body; "Anrufen ist jetzt noch einfacher!" is a Fluent v8 .ms-TeachingBubble
    //    callout in the top right. Both appear at random and carry no data-id.
    // Hiding is CSS only (display:none, previous value kept in data-tc-hidden and restored after the capture).
    private const string PrepareScript = @"(a) => {
  const res = { hidden: [], warnings: [], facts: [], sections: [] };
  const hide = (el, why) => {
    if (!el || el === document.body || el === document.documentElement || el.hasAttribute('data-tc-hidden')) return;
    el.setAttribute('data-tc-hidden', el.style.display || '');
    el.style.setProperty('display', 'none', 'important');
    if (res.hidden.indexOf(why) < 0) res.hidden.push(why);
  };
  // Kept as a function: overlays can appear after the preparation (e.g. while waiting for fields),
  // so HighlightScript calls it again right before each capture.
  const hideAll = () => {
  if (a.hideOverlays) {
    document.querySelectorAll('[data-testid=""actionButton-ViewFullInsight""]').forEach(btn => {
      let e = btn, found = null;
      for (let i = 0; i < 15 && e && e.parentElement; i++, e = e.parentElement) {
        if (/^outerHeaderContainer/.test(e.parentElement.id || '')) { found = e; break; }
      }
      hide(found || btn.closest('[data-testid=""cardContainer""]'), 'Copilot-Datensatzzusammenfassung');
    });
    document.querySelectorAll('[data-id=""FormFillBar_Show_Button""]').forEach(el => hide(el, 'Formularunterstützung'));
    document.querySelectorAll('.fui-TeachingPopoverSurface, .ms-TeachingBubble').forEach(el => hide(el, 'Microsoft-Hinweisfenster'));
  }
  (a.hideSelectors || []).forEach(sel => {
    try { document.querySelectorAll(sel).forEach(el => hide(el, sel)); }
    catch (e) { if (res.warnings.indexOf('Ungültiger Selektor in hideSelectors: ' + sel) < 0) res.warnings.push('Ungültiger Selektor in hideSelectors: ' + sel); }
  });
  };
  hideAll();
  window.__tcHideAll = hideAll;
  window.__tcHidden = res.hidden;
  const xrm = window.Xrm && window.Xrm.Page && window.Xrm.Page.data ? window.Xrm.Page : null;
  const sections = [];
  const visible = (e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(e).visibility !== 'hidden'; };
  const findField = (f) => {
    // The container carries the control name as data-id. It equals the attribute name for the first
    // control; further controls of the same attribute (e.g. in another section) are named differently.
    // A subgrid control renders as data-id dataSetRoot_<control name> (measured 2026-09-17).
    const names = [f, 'dataSetRoot_' + f];
    try { const at = xrm && xrm.getAttribute(f); if (at && at.controls) at.controls.get().forEach(c => { const n = c.getName(); if (names.indexOf(n) < 0) names.push(n); }); } catch (e) {}
    const hits = [];
    names.forEach(n => document.querySelectorAll('[data-id=""' + n + '""]').forEach(e => { if (visible(e)) hits.push(e); }));
    return hits.find(e => e.closest('section')) || hits[0] || null;
  };
  (a.fields || []).forEach(f => {
    const el = findField(f);
    if (!el) { res.warnings.push('Feld ' + f + ' ist auf dem Formular nicht sichtbar.'); }
    else {
      // Inset frame: an outline is clipped by rows without padding (measured on the lead form).
      el.setAttribute('data-tc-highlight', el.style.boxShadow || '');
      el.style.setProperty('box-shadow', 'inset 0 0 0 3px #e8590c', 'important');
      el.style.setProperty('border-radius', '4px', 'important');
      if (a.tagSections) {
        const sec = el.closest('section');
        if (sec) {
          let idx = sections.indexOf(sec);
          if (idx < 0) { sections.push(sec); idx = sections.length - 1; sec.setAttribute('data-tc-section', String(idx)); res.sections.push({ index: idx, label: sec.getAttribute('aria-label') || '' }); }
        }
      }
    }
    const fact = { field: f, label: null, value: null };
    try {
      if (!xrm) throw new Error('Formular-API nicht verfügbar');
      // Subgrid: the actual value is the number of records the grid holds, e.g. to document
      // 'exactly one lead for this account'. The test must have loaded the grid before (tab focus).
      const sub = xrm.getControl(f);
      if (sub && sub.getControlType && sub.getControlType() === 'subgrid') {
        // The control label can differ from what the form shows (measured: 'Aktionskarten' for a grid
        // titled 'Leads dieser Firma'); the section heading is what the reader sees on the image.
        const subEl = findField(f); const subSec = subEl ? subEl.closest('section') : null;
        fact.label = (subSec && subSec.getAttribute('aria-label')) || (sub.getLabel ? sub.getLabel() : null);
        const grid = sub.getGrid();
        const total = grid.getTotalRecordCount();
        const n = total >= 0 ? total : grid.getRows().getLength();
        fact.value = n === 1 ? '1 Datensatz' : n + ' Datensätze';
        res.facts.push(fact);
        return;
      }
      const attr = xrm.getAttribute(f);
      if (!attr) throw new Error('Attribut nicht auf dem Formular');
      const ctl = xrm.getControl(f);
      if (ctl && ctl.getLabel) fact.label = ctl.getLabel();
      const v = attr.getValue();
      const t = attr.getAttributeType ? attr.getAttributeType() : '';
      if (v === null || v === undefined || v === '') fact.value = '';
      else if (t === 'lookup') fact.value = v.map(x => x.name || x.id).join('; ');
      else if (t === 'optionset' || t === 'boolean' || t === 'multiselectoptionset') fact.value = String(attr.getText ? attr.getText() : v);
      else if (t === 'datetime') fact.value = new Date(v).toLocaleString('de-DE');
      else fact.value = String(v);
    } catch (e) { res.warnings.push('Wert von ' + f + ' nicht lesbar: ' + (e && e.message ? e.message : e)); }
    res.facts.push(fact);
  });
  if (a.fields && a.fields.length) {
    const first = findField(a.fields[0]);
    if (first) first.scrollIntoView({ block: 'center' });
  }
  // Kept for HighlightScript: fields outside the visible area are re-rendered by the form while
  // scrolling and lose inline styles, so the frame is applied again right before each capture.
  window.__tcFields = a.fields || [];
  window.__tcFindField = findField;
  return res;
}";

    private const string HighlightScript = @"() => {
  if (window.__tcHideAll) window.__tcHideAll();
  const find = window.__tcFindField, fields = window.__tcFields || [];
  let n = 0;
  if (!find) return n;
  fields.forEach(f => {
    const el = find(f);
    if (!el) return;
    if (!el.hasAttribute('data-tc-highlight')) el.setAttribute('data-tc-highlight', el.style.boxShadow || '');
    el.style.setProperty('box-shadow', 'inset 0 0 0 3px #e8590c', 'important');
    el.style.setProperty('border-radius', '4px', 'important');
    n++;
  });
  return n;
}";

    private const string RestoreScript = @"() => {
  document.querySelectorAll('[data-tc-hidden]').forEach(el => { el.style.display = el.getAttribute('data-tc-hidden'); el.removeAttribute('data-tc-hidden'); });
  document.querySelectorAll('[data-tc-highlight]').forEach(el => { el.style.removeProperty('box-shadow'); el.style.removeProperty('border-radius'); const o = el.getAttribute('data-tc-highlight'); if (o) el.style.boxShadow = o; el.removeAttribute('data-tc-highlight'); });
  document.querySelectorAll('[data-tc-section]').forEach(el => el.removeAttribute('data-tc-section'));
  delete window.__tcFields; delete window.__tcFindField; delete window.__tcHideAll; delete window.__tcHidden;
  return true;
}";

    private async Task Evaluate(IPage page, TestStep step, TestContext ctx)
    {
        if (string.IsNullOrWhiteSpace(step.Expression))
        {
            throw new InvalidOperationException("BrowserAction operation=evaluate requires 'expression'");
        }

        // Try top-level frame first (Modern UCI is iframe-less for top-level content,
        // verified via PoC selector spike 2026-04-26). If the expected value is set
        // and the top-frame answer mismatches, fall back to scanning sub-frames —
        // some forms initialise their form library (a window.<Library> object) in a Power Apps host iframe
        // rather than the main frame.
        //
        // Each sub-frame run is bounded to 10 seconds, and a step can switch the fallback off
        // (frameFallback: false). Measured 2026-09-17: a form-readiness script
        // returned "formular-nicht-geladen" after 60 s and was then re-run in 16 sub-frames,
        // 60 s each, which stalled the case for 17 minutes and looked like a hanging save.
        // The fallback on a differing value stays the default: ui-smokes-tier1 relies on it
        // (form-library probes that answer 'library-missing' in the top frame).
        var result = await EvaluateWithTimeoutAsync(page, step.Expression, step.TimeoutSeconds);
        if (!string.IsNullOrEmpty(step.Value) && (step.FrameFallback ?? true) &&
            !string.Equals(result?.ToString(), step.Value, StringComparison.Ordinal))
        {
            foreach (var frame in page.Frames)
            {
                if (frame == page.MainFrame) continue;
                try
                {
                    var frameEval = frame.EvaluateAsync<object?>(step.Expression);
                    if (await Task.WhenAny(frameEval, Task.Delay(TimeSpan.FromSeconds(10))) != frameEval)
                    {
                        _ = frameEval.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        _log($"      evaluate in frame '{frame.Name}' nach 10 s ohne Antwort, übersprungen");
                        continue;
                    }
                    var frameResult = await frameEval;
                    if (string.Equals(frameResult?.ToString(), step.Value, StringComparison.Ordinal))
                    {
                        result = frameResult;
                        _log($"      evaluate matched in frame '{frame.Name}'");
                        break;
                    }
                }
                catch { /* frame not accessible — skip */ }
            }
        }

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

        var resultStr = result?.ToString() ?? "null";
        _log($"      evaluate: {step.Expression?.Replace("\n", " ")} => {resultStr}");
        if (_evidence != null)
        {
            var shown = resultStr.Length > 300 ? resultStr.Substring(0, 300) + " ..." : resultStr;
            _evidence.AddNote(ctx.TestId, step.StepNumber, string.IsNullOrEmpty(step.Value)
                ? $"Rückgabe: {shown}"
                : $"Rückgabe: {shown} (erwartet: {step.Value})");
        }

        // Inline assertion: if 'value' is provided, the step asserts result == value.
        // This avoids needing a separate Assert step + a custom assert target=Output
        // (which would require AssertionEngine extension). Pragmatic for UI tests
        // where evaluate-and-assert is the common pattern.
        if (!string.IsNullOrEmpty(step.Value))
        {
            if (!string.Equals(resultStr, step.Value, StringComparison.Ordinal))
            {
                await CapturePageDiagnostics(page);
                throw new InvalidOperationException(
                    $"BrowserAction evaluate assertion failed. Expression: {step.Expression}; " +
                    $"expected '{step.Value}', got '{resultStr}'.");
            }
            _log($"      evaluate-assert: PASSED (matches '{step.Value}')");
        }
    }

    private static string Shorten(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length > max ? s.Substring(0, max) + " ..." : s);

    // page.EvaluateAsync has no timeout of its own. A timer inside the script does not help
    // when the page's JavaScript thread is blocked or the evaluation is lost: measured on
    // 2026-09-17, a save step stayed pending for minutes although the script
    // races the save against a 60-second timer. The step now fails after its timeoutSeconds
    // (default 120) with a probe of the page state instead of stalling the whole run.
    private async Task<object?> EvaluateWithTimeoutAsync(IPage page, string expression, int timeoutSeconds)
    {
        var seconds = timeoutSeconds > 0 ? timeoutSeconds : 120;
        var evaluation = page.EvaluateAsync<object?>(expression);
        if (await Task.WhenAny(evaluation, Task.Delay(TimeSpan.FromSeconds(seconds))) == evaluation)
            return await evaluation;

        _ = evaluation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        var probe = await ProbePageStateAsync(page);
        _log($"      evaluate-timeout nach {seconds}s: {probe}");
        throw new TimeoutException(
            $"BrowserAction evaluate hat nach {seconds} Sekunden nicht geantwortet. Seitenzustand: {probe}");
    }

    private static async Task<string> ProbePageStateAsync(IPage page)
    {
        var parts = new List<string> { "url=" + Shorten(page.Url, 160), "frames=" + page.Frames.Count };
        const string script = @"() => JSON.stringify({
  ready: document.readyState,
  visible: document.visibilityState,
  dialogs: Array.from(document.querySelectorAll('[role=dialog],[role=alertdialog]')).map(d => (d.innerText || '').slice(0, 120).replace(/\s+/g, ' ')),
  xrmEntity: (() => { try { return Xrm.Page.data.entity.getEntityName(); } catch (e) { return 'n/a'; } })(),
  dirty: (() => { try { return Xrm.Page.data.entity.getIsDirty(); } catch (e) { return 'n/a'; } })()
})";
        var js = page.EvaluateAsync<string>(script);
        if (await Task.WhenAny(js, Task.Delay(TimeSpan.FromSeconds(5))) == js)
        {
            try { parts.Add("seite-antwortet " + await js); }
            catch (Exception ex) { parts.Add("seiten-skript-fehler " + Shorten(ex.Message, 200)); }
        }
        else
        {
            _ = js.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            parts.Add("seite-antwortet-nicht (JavaScript-Thread blockiert oder Kontext verloren)");
        }
        return string.Join("; ", parts);
    }

    private async Task CapturePageDiagnostics(IPage page)
    {
        try
        {
            // Bounded: a blocked renderer would otherwise stall the screenshot as well.
            var pngBytes = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true, Timeout = 15000 });
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
