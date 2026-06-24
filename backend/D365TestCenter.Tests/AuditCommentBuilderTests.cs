using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// ADR 2026-06-24: the shared audit core extracted from ZephyrResultBuilder.BuildAuditComment.
/// RenderPlain must reproduce the original plain-text format byte-for-byte (the Zephyr regress
/// is also covered by ZephyrResultBuilderTests). RenderHtml escapes the dynamic values for the
/// Azure-DevOps comment (sync-devops) while keeping the structural punctuation literal.
/// </summary>
public class AuditCommentBuilderTests
{
    static List<TrackedRecord> SampleRecords() => new()
    {
        new() { Entity = "account", Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Alias = "acc", Name = "JBE Test GmbH" },
        new() { Entity = "lead", Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Alias = "lead" }   // no name
    };

    static List<StepResult> SampleAsserts() => new()
    {
        new() { Action = "Assert", Description = "Firma abgeleitet", Success = true, ActualDisplay = "JBE Test GmbH" },
        new() { Action = "CreateRecord", Description = "non-assert ignored", Success = true },
        new() { Action = "Assert", Description = "companyname gesetzt", Success = false, ActualDisplay = "leer" }
    };

    // ── BuildModel selection (verhaltensgleich zum alten BuildAuditComment) ──

    [Fact]
    public void BuildModel_SelectsOnlyAssertSteps_AndPicksWhatAndValueFallbacks()
    {
        var asserts = new List<StepResult>
        {
            // no Description -> What falls back to AssertField; no ActualDisplay -> Value falls back to ExpectedDisplay
            new() { Action = "Assert", AssertField = "name", Success = true, ExpectedDisplay = "exp" },
            new() { Action = "CreateRecord", Description = "ignored", Success = true }
        };
        var model = AuditCommentBuilder.BuildModel(null, asserts, null);

        Assert.Single(model.Checked);
        Assert.Equal("name", model.Checked[0].What);
        Assert.Equal("exp", model.Checked[0].Value);
        Assert.True(model.Checked[0].Ok);
        Assert.Empty(model.Created);
        Assert.Null(model.Error);
    }

    // ── RenderPlain: byte-for-byte regress pin of the legacy format ──

    [Fact]
    public void RenderPlain_RecordsAndAsserts_ReproducesLegacyFormat()
    {
        var model = AuditCommentBuilder.BuildModel(SampleRecords(), SampleAsserts(), null);
        var plain = AuditCommentBuilder.RenderPlain(model);

        Assert.Equal(
            "Angelegt: account \"JBE Test GmbH\" [acc] (11111111-1111-1111-1111-111111111111), " +
            "lead [lead] (22222222-2222-2222-2222-222222222222)\n" +
            "Geprüft: Firma abgeleitet = JBE Test GmbH (OK); companyname gesetzt = leer (FAIL)",
            plain);
    }

    [Fact]
    public void RenderPlain_StaysIdenticalToBuildAuditComment()
    {
        // The two paths must agree (BuildAuditComment now delegates to BuildModel+RenderPlain).
        var tracked = SampleRecords();
        var asserts = SampleAsserts();
        var legacy = ZephyrResultBuilder.BuildAuditComment(tracked, asserts, "boom");
        var shared = AuditCommentBuilder.RenderPlain(AuditCommentBuilder.BuildModel(tracked, asserts, "boom"));
        Assert.Equal(legacy, shared);
    }

    [Fact]
    public void RenderPlain_EmptyModel_ReturnsNull()
        => Assert.Null(AuditCommentBuilder.RenderPlain(
            AuditCommentBuilder.BuildModel(null, null, null)));

    [Fact]
    public void RenderPlain_CapsAt1500()
    {
        var huge = new string('x', 5000);
        var model = AuditCommentBuilder.BuildModel(null, null, huge);
        var plain = AuditCommentBuilder.RenderPlain(model)!;
        // "Fehler: " + 1500-cap then "..." -> overall length is 1500 + 3
        Assert.Equal(1503, plain.Length);
        Assert.EndsWith("...", plain);
    }

    // ── RenderHtml: escaped, indented, structural punctuation literal ──

    [Fact]
    public void RenderHtml_EscapesDynamicValues_AndIndentsAsserts()
    {
        var tracked = new List<TrackedRecord>
        {
            new() { Entity = "account", Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Alias = "acc", Name = "Tom & \"Jerry\" <Co>" }
        };
        var asserts = new List<StepResult>
        {
            new() { Action = "Assert", Description = "name", Success = true, ActualDisplay = "a<b>&c" }
        };
        var model = AuditCommentBuilder.BuildModel(tracked, asserts, "Boom <error> & \"x\"");
        var html = AuditCommentBuilder.RenderHtml(model)!;

        Assert.NotNull(html);
        // Record name escaped; the structural quotes around the name stay literal.
        Assert.Contains(
            "<b>Angelegt:</b> account \"Tom &amp; &quot;Jerry&quot; &lt;Co&gt;\" [acc] (11111111-1111-1111-1111-111111111111)<br>",
            html);
        // Assert indented with &nbsp;&nbsp;- and the actual value escaped.
        Assert.Contains("<b>Geprüft:</b><br>", html);
        Assert.Contains("&nbsp;&nbsp;- name = a&lt;b&gt;&amp;c (OK)<br>", html);
        // Error escaped.
        Assert.Contains("<b>Fehler:</b> Boom &lt;error&gt; &amp; &quot;x&quot;<br>", html);
        // Raw (unescaped) values must not leak through.
        Assert.DoesNotContain("<Co>", html);
        Assert.DoesNotContain("a<b>&c", html);
    }

    [Fact]
    public void RenderHtml_RecordWithoutName_OmitsQuotes()
    {
        var tracked = new List<TrackedRecord>
        {
            new() { Entity = "lead", Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Alias = "lead" }
        };
        var html = AuditCommentBuilder.RenderHtml(AuditCommentBuilder.BuildModel(tracked, null, null))!;
        Assert.Contains("<b>Angelegt:</b> lead [lead] (22222222-2222-2222-2222-222222222222)<br>", html);
        Assert.DoesNotContain("\"\"", html);   // no empty quote pair
    }

    [Fact]
    public void RenderHtml_EmptyModel_ReturnsNull()
        => Assert.Null(AuditCommentBuilder.RenderHtml(
            AuditCommentBuilder.BuildModel(new List<TrackedRecord>(), new List<StepResult>(), null)));
}
