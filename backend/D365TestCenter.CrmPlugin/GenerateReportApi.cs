using System;
using Microsoft.Xrm.Sdk;
using D365TestCenter.Core.Reporting;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Custom API: jbe_GenerateReport (ADR-0009 Phase 4). Sync, read-only. Builds the run report for a
/// jbe_testrun from Dataverse (header + jbe_testrunresult + jbe_testcase docs) via
/// <see cref="DataverseReportSource"/> and renders it as Markdown or self-contained HTML. The UI calls
/// this via executeAction and shows/downloads the text. PDF stays CLI-only (Chromium carve-out).
///
/// Registration:
///   Message:  jbe_GenerateReport (Custom API, Global/Unbound, Pattern 1 plugintypeid, sync)
///   Input:    RunId (String, required, GUID), Detail (String, optional: compact|full),
///             Format (String, optional: md|html)
///   Output:   Report (String), Format (String, echoed effective format)
/// </summary>
public sealed class GenerateReportApi : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
        var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
        var service = factory.CreateOrganizationService(context.UserId);

        try
        {
            var runIdStr = GetString(context, "RunId");
            if (string.IsNullOrWhiteSpace(runIdStr) || !Guid.TryParse(runIdStr, out var runId))
                throw new InvalidPluginExecutionException("Input 'RunId' fehlt oder ist keine gueltige GUID.");

            var detail = string.Equals(GetString(context, "Detail"), "full", StringComparison.OrdinalIgnoreCase)
                ? ReportDetail.Full : ReportDetail.Compact;
            var format = (GetString(context, "Format") ?? "md").Trim().ToLowerInvariant();
            if (format != "md" && format != "html") format = "md";

            trace.Trace("GenerateReportApi: Run={0} Detail={1} Format={2}", runId, detail, format);

            var cfg = ConfigDetector.Detect(service, trace);
            var model = DataverseReportSource.BuildModel(service, cfg, runId, env: null, msg => trace.Trace(msg));

            var report = format == "html"
                ? HtmlReportRenderer.Render(model, detail)
                : MarkdownReportGenerator.Render(model, detail);

            context.OutputParameters["Report"] = report;
            context.OutputParameters["Format"] = format;
            trace.Trace("GenerateReportApi: {0}/{1} Tests, {2} Zeichen ({3})",
                model.Passed, model.Total, report.Length, format);
        }
        catch (InvalidPluginExecutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            trace.Trace("GenerateReportApi Fehler: {0}", ex);
            throw new InvalidPluginExecutionException("Fehler bei der Report-Erstellung: " + ex.Message, ex);
        }
    }

    static string GetString(IPluginExecutionContext ctx, string key)
        => ctx.InputParameters.Contains(key) ? ctx.InputParameters[key] as string : null;
}
