using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using D365TestCenter.Core.Config;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// Loads the <c>jbe_testrunresult</c> records of a run from Dataverse and maps them to
/// <see cref="TestCaseResult"/> (including OE-10 TrackedRecords and the Assert StepResults).
/// Core home so both the CLI (report / sync-results / sync-zephyr) and the Custom-API plugins
/// (<c>jbe_GenerateReport</c>, ADR-0009 Phase 4) share one path - the CrmPlugin references the
/// Core only. Pure Dataverse read, no file system.
/// </summary>
public static class RunResultLoader
{
    /// <summary>
    /// Loads the jbe_testrunresult records of a run and maps them to TestCaseResults.
    /// Includes jbe_errormessage so the report can show the failure reason, plus the OE-10
    /// jbe_trackedrecords and jbe_assertionresults for the audit comment.
    /// </summary>
    public static List<TestCaseResult> LoadResultsFromRun(
        IOrganizationService service, ITestCenterConfig cfg, Guid runId)
    {
        var q = new QueryExpression(cfg.TestRunResultEntity)
        {
            ColumnSet = new ColumnSet(
                "jbe_testid", "jbe_outcome", "jbe_durationms", "jbe_errormessage",
                // OE-10: TrackedRecords + AssertionResults für den sync-zephyr-Audit-Kommentar.
                "jbe_trackedrecords", "jbe_assertionresults"),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression("jbe_testrunid", ConditionOperator.Equal, runId) }
            }
        };

        var list = new List<TestCaseResult>();
        foreach (var e in service.RetrieveMultiple(q).Entities)
        {
            list.Add(new TestCaseResult
            {
                TestId = e.GetAttributeValue<string>("jbe_testid") ?? "",
                Outcome = MapOutcome(e.GetAttributeValue<OptionSetValue>("jbe_outcome")?.Value, cfg),
                DurationMs = e.GetAttributeValue<int?>("jbe_durationms") ?? 0,
                ErrorMessage = e.GetAttributeValue<string>("jbe_errormessage"),
                TrackedRecords = ParseTrackedRecords(e.GetAttributeValue<string>("jbe_trackedrecords")),
                StepResults = ParseAssertSteps(e.GetAttributeValue<string>("jbe_assertionresults"))
            });
        }
        return list;
    }

    /// <summary>OE-10: deserialisiert jbe_trackedrecords-JSON zu TrackedRecord-Liste (leer bei null/Fehler).</summary>
    static List<TrackedRecord> ParseTrackedRecords(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<TrackedRecord>();
        try { return JsonConvert.DeserializeObject<List<TrackedRecord>>(json!) ?? new List<TrackedRecord>(); }
        catch { return new List<TrackedRecord>(); }
    }

    /// <summary>OE-10: deserialisiert jbe_assertionresults-JSON zu Assert-StepResults (leer bei null/Fehler).</summary>
    static List<StepResult> ParseAssertSteps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<StepResult>();
        try
        {
            var dtos = JsonConvert.DeserializeObject<List<AssertResultDto>>(json!) ?? new List<AssertResultDto>();
            return dtos.Select(d => new StepResult
            {
                Action = "Assert",
                Description = d.description ?? "",
                Success = d.passed,
                Message = d.message,
                ExpectedDisplay = d.expectedDisplay,
                ActualDisplay = d.actualDisplay
            }).ToList();
        }
        catch { return new List<StepResult>(); }
    }

    /// <summary>DTO für das jbe_assertionresults-JSON (Orchestrator-Schreibformat).</summary>
    sealed class AssertResultDto
    {
        public string? description { get; set; }
        public bool passed { get; set; }
        public string? message { get; set; }
        public string? expectedDisplay { get; set; }
        public string? actualDisplay { get; set; }
    }

    /// <summary>Maps a jbe_outcome OptionSet value back to the TestOutcome enum.</summary>
    public static TestOutcome MapOutcome(int? code, ITestCenterConfig cfg)
    {
        if (code == cfg.OutcomePassed) return TestOutcome.Passed;
        if (code == cfg.OutcomeFailed) return TestOutcome.Failed;
        if (code == cfg.OutcomeSkipped) return TestOutcome.Skipped;
        return TestOutcome.Error;
    }
}
