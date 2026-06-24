using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using D365TestCenter.Core;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// Shared core of the per-test audit block (OE-10: angelegte Records / geprüfte
/// Asserts / Fehler). Extracted (ADR 2026-06-24) so the two result sinks render the
/// SAME facts without drift: <see cref="ZephyrResultBuilder.BuildAuditComment"/>
/// (E5, Zephyr) uses the plain-text renderer; <see cref="DevOpsCommentBuilder"/>
/// (sync-devops, the licence-free E5 pendant) uses the HTML renderer. Pure: no IO.
///
/// The neutral <see cref="AuditModel"/> carries the structured parts (record label
/// fields, assert what/value/ok, error) so the HTML renderer can escape the dynamic
/// values while keeping the structural punctuation ("name", [alias], (id), " = ",
/// " (OK)") literal - identical structure to the plain renderer.
/// </summary>
public static class AuditCommentBuilder
{
    /// <summary>One created record (OE-10 tracked record) in neutral form.</summary>
    public sealed class RecordLine
    {
        public string Entity { get; set; } = "";
        public string? Name { get; set; }
        public string? Alias { get; set; }
        public Guid Id { get; set; }
    }

    /// <summary>One checked assert in neutral form: what was checked, its value, pass/fail.</summary>
    public sealed class AssertLine
    {
        /// <summary>Description, falling back to the asserted field name. May be null/empty.</summary>
        public string? What { get; set; }
        /// <summary>Actual display value, falling back to the expected display value. May be null.</summary>
        public string? Value { get; set; }
        public bool Ok { get; set; }
    }

    /// <summary>Neutral audit model: created records, checked asserts, error. Renderer-agnostic.</summary>
    public sealed class AuditModel
    {
        public IReadOnlyList<RecordLine> Created { get; set; } = Array.Empty<RecordLine>();
        public IReadOnlyList<AssertLine> Checked { get; set; } = Array.Empty<AssertLine>();
        public string? Error { get; set; }

        /// <summary>True when there is nothing to render (no records, no asserts, no error).</summary>
        public bool IsEmpty =>
            Created.Count == 0 && Checked.Count == 0 && string.IsNullOrWhiteSpace(Error);
    }

    /// <summary>
    /// Builds the neutral model from the OE-10 raw data. Selection and order are
    /// identical to the original BuildAuditComment: only Assert steps
    /// (<c>Action == "Assert"</c>), what = Description else AssertField, value =
    /// ActualDisplay else ExpectedDisplay, error = the trimmed errorMessage.
    /// </summary>
    public static AuditModel BuildModel(
        IReadOnlyList<TrackedRecord>? trackedRecords,
        IReadOnlyList<StepResult>? assertSteps,
        string? errorMessage)
    {
        var created = (trackedRecords ?? Array.Empty<TrackedRecord>())
            .Select(t => new RecordLine
            {
                Entity = t.Entity,
                Name = t.Name,
                Alias = t.Alias,
                Id = t.Id
            }).ToList();

        var asserts = (assertSteps ?? Array.Empty<StepResult>())
            .Where(s => string.Equals(s.Action, "Assert", StringComparison.OrdinalIgnoreCase))
            .Select(a => new AssertLine
            {
                What = string.IsNullOrWhiteSpace(a.Description) ? a.AssertField : a.Description,
                Value = !string.IsNullOrWhiteSpace(a.ActualDisplay) ? a.ActualDisplay : a.ExpectedDisplay,
                Ok = a.Success
            }).ToList();

        return new AuditModel
        {
            Created = created,
            Checked = asserts,
            Error = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage
        };
    }

    // ── Plain-text renderer (Zephyr, OE-10 behaviour, Cap 1500) ──────────────

    /// <summary>
    /// Renders the model to the plain-text audit comment (Zephyr): "Angelegt: …",
    /// "Geprüft: a; b; c", "Fehler: …", newline-joined, capped at 1500 chars.
    /// Byte-for-byte the original <see cref="ZephyrResultBuilder.BuildAuditComment"/>
    /// output (verhaltensneutral). Null when there is nothing to say.
    /// </summary>
    public static string? RenderPlain(AuditModel model)
    {
        if (model == null) return null;
        var lines = new List<string>();

        if (model.Created.Count > 0)
            lines.Add("Angelegt: " + string.Join(", ", model.Created.Select(PlainRecord)));

        if (model.Checked.Count > 0)
            lines.Add("Geprüft: " + string.Join("; ", model.Checked.Select(PlainAssert)));

        if (!string.IsNullOrWhiteSpace(model.Error))
            lines.Add("Fehler: " + model.Error);

        if (lines.Count == 0) return null;
        var comment = string.Join("\n", lines);
        return comment.Length > 1500 ? comment.Substring(0, 1500) + "..." : comment;
    }

    static string PlainRecord(RecordLine r)
    {
        var label = string.IsNullOrWhiteSpace(r.Name) ? r.Entity : $"{r.Entity} \"{r.Name}\"";
        if (!string.IsNullOrWhiteSpace(r.Alias)) label += $" [{r.Alias}]";
        return $"{label} ({r.Id})";
    }

    static string PlainAssert(AssertLine a)
    {
        var ok = a.Ok ? "OK" : "FAIL";
        return string.IsNullOrWhiteSpace(a.Value) ? $"{a.What} ({ok})" : $"{a.What} = {a.Value} ({ok})";
    }

    // ── HTML renderer (sync-devops, Azure-DevOps comment, escaped) ───────────

    /// <summary>
    /// Renders the model to an HTML fragment for the Azure-DevOps work-item comment:
    /// <code>
    ///   &lt;b&gt;Angelegt:&lt;/b&gt; {records}&lt;br&gt;
    ///   &lt;b&gt;Geprüft:&lt;/b&gt;&lt;br&gt;
    ///   &amp;nbsp;&amp;nbsp;- {what} = {value} (OK|FAIL)&lt;br&gt;   (per assert)
    ///   &lt;b&gt;Fehler:&lt;/b&gt; {error}&lt;br&gt;
    /// </code>
    /// All dynamic values (entity, name, alias, id, what, value, error) are HTML-escaped;
    /// the structural punctuation ("name", [alias], (id), " = ", " (OK)") is literal.
    /// Matches the PoC reference fragment-b-audit.html. Null when there is nothing to say.
    /// </summary>
    public static string? RenderHtml(AuditModel model)
    {
        if (model == null || model.IsEmpty) return null;
        var sb = new StringBuilder();

        if (model.Created.Count > 0)
            sb.Append("<b>Angelegt:</b> ")
              .Append(string.Join(", ", model.Created.Select(HtmlRecord)))
              .Append("<br>\n");

        if (model.Checked.Count > 0)
        {
            sb.Append("<b>Geprüft:</b><br>\n");
            foreach (var a in model.Checked)
                sb.Append("&nbsp;&nbsp;- ").Append(HtmlAssert(a)).Append("<br>\n");
        }

        if (!string.IsNullOrWhiteSpace(model.Error))
            sb.Append("<b>Fehler:</b> ").Append(EscapeHtml(model.Error)).Append("<br>\n");

        return sb.Length == 0 ? null : sb.ToString();
    }

    static string HtmlRecord(RecordLine r)
    {
        var label = string.IsNullOrWhiteSpace(r.Name)
            ? EscapeHtml(r.Entity)
            : $"{EscapeHtml(r.Entity)} \"{EscapeHtml(r.Name)}\"";
        if (!string.IsNullOrWhiteSpace(r.Alias)) label += $" [{EscapeHtml(r.Alias)}]";
        return $"{label} ({EscapeHtml(r.Id.ToString())})";
    }

    static string HtmlAssert(AssertLine a)
    {
        var ok = a.Ok ? "OK" : "FAIL";
        var what = EscapeHtml(a.What);
        return string.IsNullOrWhiteSpace(a.Value)
            ? $"{what} ({ok})"
            : $"{what} = {EscapeHtml(a.Value)} ({ok})";
    }

    /// <summary>
    /// HTML-escapes a dynamic value: &amp; &lt; &gt; &quot; (ampersand first to avoid
    /// double-escaping). Shared with <see cref="DevOpsCommentBuilder"/> (TestId/Env/Filter).
    /// </summary>
    internal static string EscapeHtml(string? s) => (s ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
