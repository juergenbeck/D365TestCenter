using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core;

/// <summary>
/// Liest Dataverse-Environment-Variables (jbe_use_worker, jbe_chunksize,
/// jbe_worker_budget_seconds) für die Worker-Plugins. Effektiver Wert = Value-Record, sonst
/// DefaultValue der Definition, sonst der übergebene Code-Default. Schlägt jede Abfrage fehl
/// (Feld fehlt o.Ae.), wird still der Code-Default genommen (robust gegen halb-deployte Envs).
/// </summary>
public static class WorkerEnvironment
{
    public static bool ReadBool(IOrganizationService service, string schemaName, bool fallback)
    {
        var raw = ReadRaw(service, schemaName);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        raw = raw!.Trim();
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || raw == "1";
    }

    public static int ReadInt(IOrganizationService service, string schemaName, int fallback)
    {
        var raw = ReadRaw(service, schemaName);
        return int.TryParse(raw, out var v) ? v : fallback;
    }

    private static string? ReadRaw(IOrganizationService service, string schemaName)
    {
        try
        {
            var defQuery = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("defaultvalue"),
                TopCount = 1,
                NoLock = true
            };
            defQuery.Criteria.AddCondition("schemaname", ConditionOperator.Equal, schemaName);
            var def = service.RetrieveMultiple(defQuery).Entities.FirstOrDefault();
            if (def == null) return null;

            var valQuery = new QueryExpression("environmentvariablevalue")
            {
                ColumnSet = new ColumnSet("value"),
                TopCount = 1,
                NoLock = true
            };
            valQuery.Criteria.AddCondition("environmentvariabledefinitionid",
                ConditionOperator.Equal, def.Id);
            var val = service.RetrieveMultiple(valQuery).Entities.FirstOrDefault();

            var value = val?.GetAttributeValue<string>("value");
            return string.IsNullOrEmpty(value)
                ? def.GetAttributeValue<string>("defaultvalue")
                : value;
        }
        catch
        {
            return null;
        }
    }
}
