using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using D365TestCenter.Core;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// sync-devops (ADR 2026-06-24): the licence-free E5 pendant to <see cref="ZephyrResultBuilder"/>.
/// Builds the HTML fragment posted as an Azure-DevOps work-item comment: a KPI header
/// (run date / env / pass-fail counts / wall-clock / run-id / filter) followed by one
/// block per test case (outcome + duration + the shared <see cref="AuditCommentBuilder"/>
/// HTML audit block: angelegte Records / geprüfte Asserts / Fehler).
///
/// Pure: no IO, no Dataverse, no HTTP - the CLI (<c>DevOpsSync</c>) owns those. Matches the
/// PoC reference fragment-b-audit.html (work-item #41432, comment 14769701). All dynamic
/// values are HTML-escaped; Azure DevOps renders the comment as HTML (Markdown is NOT
/// rendered, the instance forces format=html).
/// </summary>
public static class DevOpsCommentBuilder
{
    /// <summary>Overall comment length cap (Azure DevOps tolerates large comments, but a runaway
    /// run should not post megabytes). Truncated with a visible hint.</summary>
    const int MaxLength = 30000;

    /// <summary>
    /// Builds the work-item comment HTML fragment for a run.
    /// </summary>
    /// <param name="startedOn">Run start (jbe_testrun), rendered as a local date in the header.</param>
    /// <param name="filter">The run filter (jbe_testrun), shown in the header.</param>
    /// <param name="runId">The jbe_testrun id, shown in the header.</param>
    /// <param name="env">Optional environment label (e.g. "DEV") for the header.</param>
    /// <param name="results">The per-test results (in load order), each rendered as a block.</param>
    /// <param name="wallClock">Wall-clock duration (CompletedOn-StartedOn); falls back to the
    /// sum of per-test durations when absent or non-positive.</param>
    public static string BuildWorkItemComment(
        DateTime? startedOn, string? filter, Guid runId, string? env,
        IReadOnlyList<TestCaseResult> results, TimeSpan? wallClock)
    {
        results ??= Array.Empty<TestCaseResult>();

        int total = results.Count;
        int passed = results.Count(r => r.Outcome == TestOutcome.Passed);
        int failed = results.Count(r => r.Outcome == TestOutcome.Failed);
        int skipped = results.Count(r => r.Outcome == TestOutcome.Skipped);
        int errored = results.Count(r => r.Outcome == TestOutcome.Error);

        var duration = (wallClock.HasValue && wallClock.Value > TimeSpan.Zero)
            ? wallClock.Value
            : TimeSpan.FromMilliseconds(results.Sum(r => r.DurationMs));

        var kpi = new StringBuilder($"{passed}/{total} PASS");
        if (failed > 0) kpi.Append($", {failed} FAIL");
        if (skipped > 0) kpi.Append($", {skipped} SKIP");
        if (errored > 0) kpi.Append($", {errored} ERROR");
        kpi.Append($" ({FormatDuration(duration)})");

        var lauf = startedOn.HasValue
            ? startedOn.Value.ToLocalTime().ToString("yyyy-MM-dd")
            : "-";
        var envPart = string.IsNullOrWhiteSpace(env)
            ? ""
            : $", Env {AuditCommentBuilder.EscapeHtml(env.Trim())}";

        var sb = new StringBuilder();
        sb.Append("<b>D365 Test Center: Durchführungsbericht</b><br>\n");
        sb.Append($"<b>Lauf:</b> {lauf}{envPart} &nbsp; <b>Ergebnis:</b> {kpi}<br>\n");
        sb.Append($"<b>Run-ID:</b> {runId} &nbsp; <b>Filter:</b> {AuditCommentBuilder.EscapeHtml(filter)}<br>\n");
        sb.Append("<br>\n");

        foreach (var r in results)
        {
            sb.Append($"<b>{AuditCommentBuilder.EscapeHtml(r.TestId)}</b>: ")
              .Append(MapOutcome(r.Outcome))
              .Append($" ({FormatDuration(TimeSpan.FromMilliseconds(r.DurationMs))})<br>\n");

            var model = AuditCommentBuilder.BuildModel(r.TrackedRecords, r.StepResults, r.ErrorMessage);
            var audit = AuditCommentBuilder.RenderHtml(model);
            if (audit != null) sb.Append(audit);
        }

        var html = sb.ToString();
        return html.Length > MaxLength
            ? html.Substring(0, MaxLength) + "... (gekürzt)"
            : html;
    }

    /// <summary>Maps a <see cref="TestOutcome"/> to the short header label.</summary>
    static string MapOutcome(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => "PASS",
        TestOutcome.Failed => "FAIL",
        TestOutcome.Skipped => "SKIP",
        TestOutcome.Error => "ERROR",
        _ => "?"
    };

    /// <summary>
    /// Compact duration: whole seconds under a minute ("14s"), else "Nm Ss" (e.g. "2m 14s").
    /// Matches the PoC ("14s", "12s").
    /// </summary>
    static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60)
            return $"{(int)Math.Round(d.TotalSeconds)}s";
        return $"{(int)d.TotalMinutes}m {d.Seconds}s";
    }
}
