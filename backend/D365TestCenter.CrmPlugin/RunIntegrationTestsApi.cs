using Microsoft.Xrm.Sdk;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Custom API: jbe_RunIntegrationTests
///
/// Browser-Einstiegspunkt für die Test-Ausführung. Der Browser legt einen
/// jbe_testrun-Record mit Status "Geplant" und jbe_testcasefilter an, dann ruft
/// er diese Custom API auf. Die API lädt die Tests aus der jbe_testcase-Entity
/// (nicht aus einem JSON-Blob im TestRun), filtert sie, führt sie aus und
/// schreibt jbe_testrunresult + jbe_teststep Records.
///
/// Seit ADR-0003 (Single-Engine-Architektur) nutzt die API den zentralen
/// <see cref="TestCenterOrchestrator"/> - genau wie die CLI. Dadurch ist das
/// Verhalten zwischen Browser und Headless identisch.
///
/// Registrierung:
///   MessageName: jbe_RunIntegrationTests
///   Binding:     Unbound
///   Input:       TestRunId (EntityReference auf jbe_testrun)
///   Output:      Success (bool), Summary (string)
/// </summary>
public sealed class RunIntegrationTestsApi : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider
            .GetService(typeof(IPluginExecutionContext));
        var tracingService = (ITracingService)serviceProvider
            .GetService(typeof(ITracingService));
        var factory = (IOrganizationServiceFactory)serviceProvider
            .GetService(typeof(IOrganizationServiceFactory));
        var rawService = factory.CreateOrganizationService(context.UserId);

        // ADR-0005 / FB-31b: Im Sync-Plugin-Pfad alle Service-Calls via
        // SandboxSafeOrganizationService routen. Damit fängt das Plugin
        // niemals direkt eine Exception aus IOrganizationService — Faults
        // kommen als InvalidPluginExecutionException, die als managed
        // Exception kein Sandbox-Wachter-Verstoß sind. Das deckt auch
        // den FindRecord-auf-systemuser-Pfad ab (FB-31-Variante 2b).
        var service = new SandboxSafeOrganizationService(rawService);

        tracingService.Trace("RunIntegrationTestsApi: Start (SandboxSafe wrapper aktiv)");

        try
        {
            // ── Input: TestRunId ───────────────────────────────────
            if (!context.InputParameters.Contains("TestRunId"))
                throw new InvalidPluginExecutionException(
                    "Input-Parameter 'TestRunId' fehlt.");

            var testRunRef = context.InputParameters["TestRunId"]
                as EntityReference
                ?? throw new InvalidPluginExecutionException(
                    "TestRunId muss ein EntityReference sein.");

            var testRunId = testRunRef.Id;
            tracingService.Trace("TestRunId: {0}", testRunId);

            // ── Config: Umgebungs-Detection per Metadata-Query ──────
            // Wenn die Markant-Governance-Entity existiert, nutze MarkantConfig
            // (mit AutoDateFields, Governance-Polling). Sonst StandardCrmConfig.
            // Dies garantiert korrektes Verhalten auf Markant DEV/TEST/DATATEST
            // und bleibt generisch für LM, ZastrPay etc.
            ITestCenterConfig config = ConfigDetector.Detect(service, tracingService);

            // ── Orchestrator aufrufen ───────────────────────────────
            var orchestrator = new TestCenterOrchestrator(
                service,
                config,
                log: msg => tracingService.Trace(msg));

            var result = orchestrator.RunExistingTestRun(testRunId);

            // ── Output setzen ───────────────────────────────────────
            var success = result.FailedCount == 0 && result.ErrorCount == 0;
            var summary = $"{result.PassedCount}/{result.TotalCount} bestanden, " +
                         $"{result.FailedCount} fehlgeschlagen, " +
                         $"{result.ErrorCount} Fehler";

            if (context.OutputParameters.Contains("Success"))
                context.OutputParameters["Success"] = success;
            if (context.OutputParameters.Contains("Summary"))
                context.OutputParameters["Summary"] = summary;

            tracingService.Trace("RunIntegrationTestsApi abgeschlossen: {0}", summary);
        }
        catch (InvalidPluginExecutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            tracingService.Trace("RunIntegrationTestsApi Fehler: {0}", ex.Message);
            throw new InvalidPluginExecutionException(
                "Fehler bei der Testausführung: " + ex.Message, ex);
        }
    }

}
