using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// E5 (ADR-0008): pure builder for the Zephyr Scale result upload. Maps the
/// D365TestCenter outcomes to Zephyr statuses and builds the two JSON payloads
/// (create a test run / cycle, then bulk-upload its results). No HTTP, no
/// Dataverse, no IO: the CLI (<c>ZephyrSync</c>) owns those.
///
/// Target is Zephyr Scale <b>Data Center / ATM 1.0</b> (NOT the cloud v2 API).
/// Endpoints this feeds (Decision 24, Markant server <c>mnet.markant.de/jira</c>):
///   POST /rest/atm/1.0/testrun                       create cycle, items[]
///   POST /rest/atm/1.0/testrun/{runKey}/testresults  bulk results array
///
/// The result-object shape is verified against the official ATM 1.0 example:
///   { testCaseKey, status, environment, executionTime(ms), comment, scriptResults[] }.
/// </summary>
public static class ZephyrResultBuilder
{
    /// <summary>
    /// One test result destined for Zephyr: a jbe_testrunresult paired with the
    /// <c>zephyr_key</c> (DYN-T####) read from the Markdown front-matter.
    /// </summary>
    public sealed class ResultInput
    {
        /// <summary>Zephyr test-case key, e.g. <c>DYN-T123</c>.</summary>
        public string ZephyrKey { get; set; } = "";
        public TestOutcome Outcome { get; set; }
        /// <summary>Execution time in milliseconds (Zephyr <c>executionTime</c>).</summary>
        public long DurationMs { get; set; }
        /// <summary>Optional free-text comment (e.g. the failure reason).</summary>
        public string? Comment { get; set; }
        /// <summary>
        /// Optional per-step results. When set, rendered as <c>scriptResults[]</c>.
        /// Phase 1 leaves this null (overall status only); Phase 2 fills it from the
        /// jbe_teststep records once the "only first script result lands" stumbling
        /// block is verified live (Decision 24).
        /// </summary>
        public IReadOnlyList<ScriptResultInput>? ScriptResults { get; set; }
    }

    /// <summary>Per-step result inside a <see cref="ResultInput"/> (Zephyr <c>scriptResults[]</c>).</summary>
    public sealed class ScriptResultInput
    {
        public int Index { get; set; }
        public TestOutcome Outcome { get; set; }
        public string? Comment { get; set; }
    }

    /// <summary>
    /// Maps a D365TestCenter <see cref="TestOutcome"/> to the Zephyr ATM status
    /// string. <see cref="TestOutcome.Error"/> maps to <c>Fail</c> (Decision 24,
    /// not <c>Blocked</c>: an errored test did not pass and needs attention).
    /// Valid Zephyr statuses: Pass / Fail / Blocked / Not Executed / In Progress.
    /// </summary>
    public static string MapStatus(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => "Pass",
        TestOutcome.Failed => "Fail",
        TestOutcome.Error => "Fail",
        TestOutcome.Skipped => "Not Executed",
        _ => "Not Executed"
    };

    /// <summary>
    /// Builds the create-test-run (cycle) payload
    /// <c>{ projectKey, name, items: [ { testCaseKey } ] }</c>. <c>items[]</c> are
    /// the distinct Zephyr keys that will receive a result (case-insensitive dedupe,
    /// blanks dropped).
    /// </summary>
    public static JObject BuildTestRunPayload(string projectKey, string name, IEnumerable<string> testCaseKeys)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            throw new ArgumentException("projectKey is required.", nameof(projectKey));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required.", nameof(name));

        var items = new JArray();
        foreach (var key in (testCaseKeys ?? Enumerable.Empty<string>())
                     .Where(k => !string.IsNullOrWhiteSpace(k))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new JObject { ["testCaseKey"] = key });
        }

        return new JObject
        {
            ["projectKey"] = projectKey,
            ["name"] = name,
            ["items"] = items
        };
    }

    /// <summary>
    /// Builds one result object for the bulk testresults array. Optional fields
    /// (<c>environment</c>, <c>executionTime</c>, <c>comment</c>, <c>scriptResults</c>)
    /// are only emitted when present, so an empty value never overwrites a Zephyr field.
    /// </summary>
    public static JObject BuildResult(ResultInput input, string? environment)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (string.IsNullOrWhiteSpace(input.ZephyrKey))
            throw new ArgumentException("ResultInput.ZephyrKey is required.", nameof(input));

        var o = new JObject
        {
            ["testCaseKey"] = input.ZephyrKey,
            ["status"] = MapStatus(input.Outcome)
        };
        if (!string.IsNullOrWhiteSpace(environment)) o["environment"] = environment;
        if (input.DurationMs > 0) o["executionTime"] = input.DurationMs;
        if (!string.IsNullOrWhiteSpace(input.Comment)) o["comment"] = input.Comment;

        if (input.ScriptResults != null && input.ScriptResults.Count > 0)
        {
            var arr = new JArray();
            foreach (var s in input.ScriptResults)
            {
                var so = new JObject
                {
                    ["index"] = s.Index,
                    ["status"] = MapStatus(s.Outcome)
                };
                if (!string.IsNullOrWhiteSpace(s.Comment)) so["comment"] = s.Comment;
                arr.Add(so);
            }
            o["scriptResults"] = arr;
        }
        return o;
    }

    /// <summary>
    /// Builds the bulk testresults payload (array) for
    /// <c>POST /rest/atm/1.0/testrun/{runKey}/testresults</c>, in input order.
    /// </summary>
    public static JArray BuildResultsPayload(IEnumerable<ResultInput> inputs, string? environment)
    {
        var arr = new JArray();
        foreach (var input in inputs ?? Enumerable.Empty<ResultInput>())
            arr.Add(BuildResult(input, environment));
        return arr;
    }
}
