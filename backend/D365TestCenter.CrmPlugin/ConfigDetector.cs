using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using D365TestCenter.Core.Config;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Detects the right <see cref="ITestCenterConfig"/> for the current org by a metadata probe:
/// if the Markant governance entity (markant_cdhcontactsource) exists, it is a Markant environment
/// (CDH Field Governance) -> <see cref="MarkantConfig"/> (AutoDateFields, governance polling);
/// otherwise <see cref="StandardCrmConfig"/>. Shared by the Custom-API plugins that need the
/// config (RunIntegrationTests for execution, GenerateReport for the outcome mapping), so the
/// detection lives in exactly one place.
/// </summary>
internal static class ConfigDetector
{
    public static ITestCenterConfig Detect(IOrganizationService service, ITracingService trace)
    {
        try
        {
            service.Execute(new RetrieveEntityRequest
            {
                LogicalName = "markant_cdhcontactsource",
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            });
            trace.Trace("Config: MarkantConfig (CDH-Entity erkannt)");
            return new MarkantConfig();
        }
        catch
        {
            trace.Trace("Config: StandardCrmConfig (keine CDH-Entity)");
            return new StandardCrmConfig();
        }
    }
}
