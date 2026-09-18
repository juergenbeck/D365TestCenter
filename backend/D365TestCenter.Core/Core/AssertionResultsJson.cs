using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace D365TestCenter.Core;

/// <summary>
/// Single source of the jbe_assertionresults JSON blob shared by the three result
/// sinks (CLI orchestrator, worker ChunkResultWriter, CRUD-trigger plugin) so the
/// format cannot drift — before this helper each sink carried its own copy of the
/// Assert projection.
///
/// Besides the executed Assert steps the blob carries FAILED cleanup steps
/// (action:"Cleanup", passed:false) so the audit comment (sync-zephyr /
/// sync-devops, both loading via RunResultLoader) can report leftover test data
/// — e.g. a restrict-delete-blocked account whose server-side created invoices
/// survived the run. Assert entries keep the exact legacy shape (no 'action'
/// property) for backward compatibility with existing parsers (WebResource
/// history tab); readers default a missing 'action' to "Assert".
/// </summary>
public static class AssertionResultsJson
{
    static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.None,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
    };

    /// <summary>Builds the JSON array for jbe_assertionresults; "[]" on any error.</summary>
    public static string Build(TestCaseResult tcResult)
    {
        try
        {
            var entries = new List<object>();

            foreach (var s in tcResult.StepResults)
            {
                if (string.Equals(s.Action, "Assert", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(new
                    {
                        description = s.Description,
                        passed = s.Success && !s.Skipped,
                        skipped = s.Skipped,
                        message = s.Message,
                        expectedDisplay = s.ExpectedDisplay,
                        actualDisplay = s.ActualDisplay
                    });
                }
                else if (string.Equals(s.Action, "Cleanup", StringComparison.OrdinalIgnoreCase) && !s.Success)
                {
                    entries.Add(new
                    {
                        action = "Cleanup",
                        description = s.Description,
                        passed = false,
                        skipped = false,
                        message = s.Message
                    });
                }
            }

            return JsonConvert.SerializeObject(entries, JsonSettings);
        }
        catch
        {
            return "[]";
        }
    }
}
