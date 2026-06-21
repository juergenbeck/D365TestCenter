using System;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using D365TestCenter.Core;
using D365TestCenter.Core.Validation;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Custom API: jbe_ValidatePack (ADR-0009 Phase 4). Sync, read-only. Loads the enabled jbe_testcase
/// definitions (optionally filtered) and runs the static <see cref="PackValidator"/> (symbol table:
/// ALIAS_UNDEFINED etc.), returning the findings as JSON. The UI calls this via executeAction and
/// shows/downloads the result. Parity with the CLI <c>validate</c> subcommand (the --org metadata
/// rules stay CLI-only for now -- the symbol-table checks need no org metadata).
///
/// Registration:
///   Message:  jbe_ValidatePack (Custom API, Global/Unbound, Pattern 1 plugintypeid, sync)
///   Input:    TestFilter (String, optional) -- * / id-list / tag: / category:
///   Output:   Findings (String, JSON: {"findings":[...]}), ErrorCount (Integer)
/// </summary>
public sealed class ValidatePackApi : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
        var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
        var service = factory.CreateOrganizationService(context.UserId);

        try
        {
            var filter = GetString(context, "TestFilter");
            trace.Trace("ValidatePackApi: Filter='{0}'", string.IsNullOrWhiteSpace(filter) ? "(alle)" : filter);

            var testCases = TestCaseLoader.LoadEnabled(service, filter, msg => trace.Trace(msg));
            var report = new PackValidator().Validate(testCases);

            context.OutputParameters["Findings"] = JsonConvert.SerializeObject(report);
            context.OutputParameters["ErrorCount"] = report.ErrorCount;
            trace.Trace("ValidatePackApi: {0} Findings ({1} Errors) über {2} Testfälle",
                report.Findings.Count, report.ErrorCount, testCases.Count);
        }
        catch (Exception ex)
        {
            trace.Trace("ValidatePackApi Fehler: {0}", ex);
            throw new InvalidPluginExecutionException("Fehler bei der Pack-Validierung: " + ex.Message, ex);
        }
    }

    static string GetString(IPluginExecutionContext ctx, string key)
        => ctx.InputParameters.Contains(key) ? ctx.InputParameters[key] as string : null;
}
