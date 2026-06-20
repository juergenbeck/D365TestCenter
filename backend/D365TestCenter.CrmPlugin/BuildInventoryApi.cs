using System;
using Microsoft.Xrm.Sdk;
using D365TestCenter.Core.Reporting;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Custom API: jbe_BuildInventory (ADR-0009 Phase 4). Sync, read-only. Builds the management inventory
/// over the whole jbe_testcase landscape (status/domain roll-ups + a table per domain) via
/// <see cref="DataverseInventorySource"/> and returns it as Markdown. The UI calls this via
/// executeAction and shows/downloads the text. Parity with the CLI <c>inventory</c> subcommand.
///
/// Registration:
///   Message:  jbe_BuildInventory (Custom API, Global/Unbound, Pattern 1 plugintypeid, sync)
///   Input:    Filter (String, optional) -- * / tag: / domain: / id-list
///   Output:   Inventory (String, Markdown), Count (Integer, number of entries)
/// </summary>
public sealed class BuildInventoryApi : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
        var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
        var service = factory.CreateOrganizationService(context.UserId);

        try
        {
            var filter = GetString(context, "Filter");
            trace.Trace("BuildInventoryApi: Filter='{0}'", string.IsNullOrWhiteSpace(filter) ? "*" : filter);

            var model = DataverseInventorySource.Load(service, filter, msg => trace.Trace(msg));
            var markdown = InventoryBuilder.Render(model);

            context.OutputParameters["Inventory"] = markdown;
            context.OutputParameters["Count"] = model.Entries.Count;
            trace.Trace("BuildInventoryApi: {0} Eintraege", model.Entries.Count);
        }
        catch (Exception ex)
        {
            trace.Trace("BuildInventoryApi Fehler: {0}", ex);
            throw new InvalidPluginExecutionException("Fehler beim Inventar-Aufbau: " + ex.Message, ex);
        }
    }

    static string GetString(IPluginExecutionContext ctx, string key)
        => ctx.InputParameters.Contains(key) ? ctx.InputParameters[key] as string : null;
}
