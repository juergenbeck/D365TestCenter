using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
/// Endpoints this feeds (Decision 24, a Jira Data Center server):
///   POST /rest/atm/1.0/testrun                       create cycle, items[], optional issueKey
///   POST /rest/atm/1.0/testrun/{runKey}/testresults  bulk results array
///
/// The result-object shape is verified against the official ATM 1.0 example:
///   { testCaseKey, status, environment, executionTime(ms), comment, scriptResults[] }.
/// </summary>
public static class ZephyrResultBuilder
{
    /// <summary>
    /// One test result destined for Zephyr: a jbe_testrunresult paired with the
    /// <c>zephyr_key</c> (PROJ-T####) read from the Markdown front-matter.
    /// </summary>
    public sealed class ResultInput
    {
        /// <summary>Zephyr test-case key, e.g. <c>PROJ-T123</c>.</summary>
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

    /// <summary>Where the cycle's <c>issueKey</c> came from, or why it stays unset.</summary>
    public enum IssueKeySource
    {
        /// <summary>Given explicitly (<c>--issue-key</c>).</summary>
        Explicit,
        /// <summary>All mapped test cases carry the same ticket.</summary>
        Derived,
        /// <summary>Linking switched off explicitly (<c>--issue-key none</c>).</summary>
        Disabled,
        /// <summary>No mapped case, or at least one mapped case without a ticket.</summary>
        MissingTicket,
        /// <summary>The mapped cases belong to more than one ticket.</summary>
        MixedTickets,
        /// <summary>The uniform ticket does not look like a Jira issue key.</summary>
        NotAnIssueKey
    }

    /// <summary>Outcome of <see cref="ResolveIssueKey"/>.</summary>
    public sealed class IssueKeyResolution
    {
        /// <summary>The issue key to send, or null when the cycle stays unlinked.</summary>
        public string? IssueKey { get; set; }
        public IssueKeySource Source { get; set; }
        /// <summary>Distinct tickets found on the mapped cases (first-seen order), for the log.</summary>
        public IReadOnlyList<string> Tickets { get; set; } = Array.Empty<string>();
    }

    /// <summary>Value of <c>--issue-key</c> that switches the issue link off.</summary>
    public const string IssueKeyNone = "none";

    static readonly Regex JiraIssueKey = new Regex(@"^[A-Za-z][A-Za-z0-9_]*-[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Decides which Jira issue the new cycle is linked to. Zephyr allows exactly
    /// one <c>issueKey</c> per cycle, and only a linked cycle shows up on the issue
    /// (section "test runs", with progress and status).
    /// <list type="number">
    /// <item>An explicit key wins; the literal <c>none</c> disables the link.</item>
    /// <item>Otherwise the key is derived from the tickets of the mapped test cases,
    /// but only when every mapped case carries the same ticket. A run spanning
    /// several stories, or containing a case without a ticket, stays unlinked
    /// instead of being attributed to an arbitrary story.</item>
    /// <item>A derived value that is no Jira issue key is not sent: Zephyr would
    /// reject the whole cycle.</item>
    /// </list>
    /// </summary>
    /// <param name="explicitIssueKey">Value of <c>--issue-key</c>, may be null.</param>
    /// <param name="ticketsOfMappedCases">One entry per mapped test case: its primary ticket, or null/blank.</param>
    public static IssueKeyResolution ResolveIssueKey(string? explicitIssueKey, IEnumerable<string?> ticketsOfMappedCases)
    {
        var all = (ticketsOfMappedCases ?? Enumerable.Empty<string?>())
            .Select(t => (t ?? "").Trim()).ToList();
        var distinct = all.Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!string.IsNullOrWhiteSpace(explicitIssueKey))
        {
            var given = explicitIssueKey!.Trim();
            return string.Equals(given, IssueKeyNone, StringComparison.OrdinalIgnoreCase)
                ? new IssueKeyResolution { Source = IssueKeySource.Disabled, Tickets = distinct }
                : new IssueKeyResolution { IssueKey = given, Source = IssueKeySource.Explicit, Tickets = distinct };
        }

        if (distinct.Count > 1)
            return new IssueKeyResolution { Source = IssueKeySource.MixedTickets, Tickets = distinct };
        if (distinct.Count == 0 || all.Any(t => t.Length == 0))
            return new IssueKeyResolution { Source = IssueKeySource.MissingTicket, Tickets = distinct };
        if (!JiraIssueKey.IsMatch(distinct[0]))
            return new IssueKeyResolution { Source = IssueKeySource.NotAnIssueKey, Tickets = distinct };

        // Jira issue keys are upper-case; a ticket typed in lower case is normalised.
        return new IssueKeyResolution
        {
            IssueKey = distinct[0].ToUpperInvariant(), Source = IssueKeySource.Derived, Tickets = distinct
        };
    }

    /// <summary>
    /// Builds the create-test-run (cycle) payload
    /// <c>{ projectKey, name, items: [ { testCaseKey } ], issueKey? }</c>. <c>items[]</c> are
    /// the distinct Zephyr keys that will receive a result (case-insensitive dedupe,
    /// blanks dropped). <c>issueKey</c> links the cycle to a Jira issue and is only
    /// emitted when given (see <see cref="ResolveIssueKey"/>).
    /// </summary>
    public static JObject BuildTestRunPayload(
        string projectKey, string name, IEnumerable<string> testCaseKeys, string? issueKey = null)
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

        var payload = new JObject
        {
            ["projectKey"] = projectKey,
            ["name"] = name,
            ["items"] = items
        };
        if (!string.IsNullOrWhiteSpace(issueKey)) payload["issueKey"] = issueKey!.Trim();
        return payload;
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

    /// <summary>
    /// OE-10: builds a human-readable audit comment for a Zephyr result from the
    /// tracked records (with primary names, CLI-run path) and the assert steps.
    /// Makes every upload self-explanatory - even a PASS, where the failure message
    /// is empty and the comment would otherwise be blank. Returns null when there is
    /// nothing to say. Capped at 1500 chars. Pure: no IO.
    /// </summary>
    public static string? BuildAuditComment(
        IReadOnlyList<TrackedRecord>? trackedRecords,
        IReadOnlyList<StepResult>? assertSteps,
        string? errorMessage)
    {
        // ADR 2026-06-24: the audit logic now lives in the shared AuditCommentBuilder
        // so sync-devops (HTML) and sync-zephyr (plain) render the same facts without
        // drift. This method keeps its signature and plain-text behaviour (Cap 1500),
        // pinned by ZephyrResultBuilderTests.
        var model = AuditCommentBuilder.BuildModel(trackedRecords, assertSteps, errorMessage);
        return AuditCommentBuilder.RenderPlain(model);
    }
}
