namespace D365TestCenter.Core;

/// <summary>
/// Abstraction for browser-based UI automation steps (ADR-0006).
///
/// Implementations live OUTSIDE Core (Cli only). Core just defines the
/// contract so TestRunner can dispatch BrowserAction steps without taking
/// a hard dependency on Microsoft.Playwright (which is too large for the
/// Plugin-Sandbox path — netstandard2.0 + 193 MB NuGet + 280 MB browser
/// binaries are not viable inside the Dataverse Plugin Sandbox).
///
/// Plugin path (RunIntegrationTestsApi, RunTestsOnStatusChange) keeps
/// this dependency null — TestRunner skips BrowserAction steps with a
/// clear "not supported in sandbox" message.
///
/// CLI path (D365TestCenter.Cli) injects a Playwright-backed
/// implementation that loads the storage-state file, opens a headless
/// Chromium context, and dispatches the step's Operation.
///
/// See ADR-0006 (UI-Automation-Architektur) for the full architectural
/// rationale and the test-case JSON schema for BrowserAction steps.
/// </summary>
public interface IBrowserActionExecutor : IDisposable
{
    /// <summary>
    /// Executes a single BrowserAction step.
    ///
    /// The executor manages its own browser lifecycle (page, context,
    /// trace) across calls — TestRunner calls this method once per
    /// BrowserAction step within a test, but the underlying browser/page
    /// is reused across steps of the same test for performance.
    ///
    /// On step failure, the implementation is responsible for capturing
    /// diagnostic artefacts (screenshot, trace) and exposing them via
    /// the <see cref="StepDiagnostics"/> result for later persistence
    /// to jbe_testrunresult (Plugin v5.4 schema: jbe_screenshot,
    /// jbe_uitrace).
    /// </summary>
    /// <param name="step">The TestStep with Action="BrowserAction" and a populated Operation.</param>
    /// <param name="ctx">The current TestContext (placeholders, aliases).</param>
    /// <returns>Awaitable Task. Throws on hard failures (test step is marked Failed).</returns>
    Task ExecuteAsync(TestStep step, TestContext ctx);

    /// <summary>
    /// Diagnostic artefacts from the most recent step execution.
    /// Populated when a step fails, otherwise empty/null.
    /// </summary>
    StepDiagnostics? LastDiagnostics { get; }
}

/// <summary>
/// Diagnostic artefacts produced by a BrowserAction step (typically on failure).
/// Mapped to jbe_testrunresult File-fields in Plugin v5.4 schema.
/// </summary>
public sealed class StepDiagnostics
{
    /// <summary>PNG bytes of a screenshot taken at the time of failure.</summary>
    public byte[]? ScreenshotPng { get; set; }

    /// <summary>Playwright trace.zip bytes for post-mortem analysis.</summary>
    public byte[]? TraceZip { get; set; }

    /// <summary>Optional textual context (last URL, console messages, etc.).</summary>
    public string? Context { get; set; }
}
