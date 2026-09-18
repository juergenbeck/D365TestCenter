using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace D365TestCenter.Core.Reporting;

// ADR 2026-09-16-1957: evidence documentation of a CLI run.
// One EvidenceDocument per run, persisted as evidence.json in the evidence directory.
// HTML (Core, EvidenceHtmlRenderer) and DOCX (CLI) are both rendered from it, so the
// two formats cannot drift apart.

/// <summary>Root of evidence.json: one documented test run.</summary>
public sealed class EvidenceDocument
{
    [JsonProperty("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>When the document was assembled (UTC). Rendered instead of "now" so a re-render is byte-identical.</summary>
    [JsonProperty("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [JsonProperty("organization")]
    public string? Organization { get; set; }

    [JsonProperty("environment")]
    public string? Environment { get; set; }

    [JsonProperty("testRunId")]
    public Guid? TestRunId { get; set; }

    [JsonProperty("filter")]
    public string? Filter { get; set; }

    [JsonProperty("startedAtUtc")]
    public DateTime StartedAtUtc { get; set; }

    [JsonProperty("completedAtUtc")]
    public DateTime CompletedAtUtc { get; set; }

    [JsonProperty("totalCount")]
    public int TotalCount { get; set; }

    [JsonProperty("passedCount")]
    public int PassedCount { get; set; }

    [JsonProperty("failedCount")]
    public int FailedCount { get; set; }

    [JsonProperty("errorCount")]
    public int ErrorCount { get; set; }

    [JsonProperty("skippedCount")]
    public int SkippedCount { get; set; }

    [JsonProperty("testCases")]
    public List<EvidenceTestCase> TestCases { get; set; } = new();
}

/// <summary>One executed test case with its steps and evidence items.</summary>
public sealed class EvidenceTestCase
{
    [JsonProperty("testId")]
    public string TestId { get; set; } = "";

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonProperty("outcome")]
    public TestOutcome Outcome { get; set; }

    [JsonProperty("durationMs")]
    public long DurationMs { get; set; }

    [JsonProperty("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonProperty("docSteps")]
    public List<DocStepDefinition> DocSteps { get; set; } = new();

    [JsonProperty("steps")]
    public List<EvidenceStep> Steps { get; set; } = new();

    [JsonProperty("items")]
    public List<EvidenceItem> Items { get; set; } = new();
}

/// <summary>Execution state of a single technical step as shown in the documentation.</summary>
public enum EvidenceStepStatus
{
    Passed,
    Failed,
    Skipped,
    NotExecuted
}

/// <summary>One technical step of a test case, joined from definition and result.</summary>
public sealed class EvidenceStep
{
    [JsonProperty("stepNumber")]
    public int StepNumber { get; set; }

    /// <summary>Effective documented step (inherited from the previous numbered step; 0 = preparation).</summary>
    [JsonProperty("docStep")]
    public int DocStep { get; set; }

    [JsonProperty("action")]
    public string Action { get; set; } = "";

    [JsonProperty("operation")]
    public string? Operation { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("status")]
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public EvidenceStepStatus Status { get; set; }

    [JsonProperty("durationMs")]
    public long DurationMs { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }
}

/// <summary>One evidence screenshot: caption, actual values and one or more image parts.</summary>
public sealed class EvidenceItem
{
    [JsonProperty("testId")]
    public string TestId { get; set; } = "";

    [JsonProperty("stepNumber")]
    public int StepNumber { get; set; }

    [JsonProperty("caption")]
    public string Caption { get; set; } = "";

    [JsonProperty("capturedAtUtc")]
    public DateTime CapturedAtUtc { get; set; }

    [JsonProperty("url")]
    public string? Url { get; set; }

    [JsonProperty("clip")]
    public string Clip { get; set; } = "page";

    [JsonProperty("facts")]
    public List<EvidenceFact> Facts { get; set; } = new();

    [JsonProperty("parts")]
    public List<EvidenceImagePart> Parts { get; set; } = new();

    /// <summary>Overlays that were present and hidden at capture time (for traceability).</summary>
    [JsonProperty("hiddenOverlays")]
    public List<string> HiddenOverlays { get; set; } = new();

    /// <summary>Problems while preparing the capture (e.g. a highlight field not found). Never fails the test.</summary>
    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new();
}

/// <summary>A form field value read at capture time.</summary>
public sealed class EvidenceFact
{
    [JsonProperty("field")]
    public string Field { get; set; } = "";

    [JsonProperty("label")]
    public string? Label { get; set; }

    [JsonProperty("value")]
    public string? Value { get; set; }
}

/// <summary>One image file of an evidence item, relative to the evidence directory.</summary>
public sealed class EvidenceImagePart
{
    [JsonProperty("file")]
    public string File { get; set; } = "";

    /// <summary>Human label of the part (e.g. the form section name).</summary>
    [JsonProperty("label")]
    public string? Label { get; set; }

    [JsonProperty("width")]
    public int Width { get; set; }

    [JsonProperty("height")]
    public int Height { get; set; }
}

/// <summary>Note recorded by the browser executor for a step (e.g. the value an evaluate returned).</summary>
public sealed class EvidenceStepNote
{
    public string TestId { get; set; } = "";
    public int StepNumber { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>A documented step with the technical steps and evidence items assigned to it.</summary>
public sealed class EvidenceSection
{
    /// <summary>0 = preparation (or the whole procedure when the test has no docSteps).</summary>
    public int Number { get; set; }

    public string Heading { get; set; } = "";

    public string? Action { get; set; }

    public string? Expected { get; set; }

    public List<EvidenceStep> Steps { get; set; } = new();

    public List<EvidenceItem> Items { get; set; } = new();

    public EvidenceStepStatus Status { get; set; }
}

/// <summary>Builds the evidence document and groups steps for rendering. Pure logic.</summary>
public static class EvidenceDocumentBuilder
{
    /// <summary>
    /// Joins the run result with the executed test case definitions, the captured
    /// evidence items and the executor notes into one <see cref="EvidenceDocument"/>.
    /// </summary>
    public static EvidenceDocument Build(
        TestRunResult run,
        IEnumerable<TestCase> definitions,
        IEnumerable<EvidenceItem> items,
        IEnumerable<EvidenceStepNote> notes,
        string? organization,
        string? environment,
        string? filter,
        DateTime createdAtUtc)
    {
        if (run == null) throw new ArgumentNullException(nameof(run));
        var defs = (definitions ?? Enumerable.Empty<TestCase>())
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var itemList = (items ?? Enumerable.Empty<EvidenceItem>()).ToList();
        var noteList = (notes ?? Enumerable.Empty<EvidenceStepNote>()).ToList();

        var doc = new EvidenceDocument
        {
            CreatedAtUtc = createdAtUtc,
            Organization = organization,
            Environment = environment,
            TestRunId = run.TestRunId,
            Filter = filter,
            StartedAtUtc = run.StartedAt,
            CompletedAtUtc = run.CompletedAt,
            TotalCount = run.TotalCount,
            PassedCount = run.PassedCount,
            FailedCount = run.FailedCount,
            ErrorCount = run.ErrorCount,
            SkippedCount = run.SkippedCount
        };

        foreach (var result in run.Results)
        {
            defs.TryGetValue(result.TestId, out var def);
            var tc = new EvidenceTestCase
            {
                TestId = result.TestId,
                Title = !string.IsNullOrWhiteSpace(result.Title) ? result.Title : def?.Title ?? result.TestId,
                Description = def?.Description,
                Tags = def?.Tags?.ToList() ?? new List<string>(),
                Outcome = result.Outcome,
                DurationMs = result.DurationMs,
                ErrorMessage = result.ErrorMessage,
                DocSteps = def?.DocSteps?.OrderBy(d => d.Number).ToList() ?? new List<DocStepDefinition>()
            };

            var stepResults = result.StepResults
                .GroupBy(s => s.StepNumber)
                .ToDictionary(g => g.Key, g => g.Last());
            var definedSteps = def?.Steps ?? new List<TestStep>();
            int current = 0;
            var seen = new HashSet<int>();
            foreach (var step in definedSteps.OrderBy(s => s.StepNumber))
            {
                if (step.DocStep.HasValue) current = step.DocStep.Value;
                seen.Add(step.StepNumber);
                stepResults.TryGetValue(step.StepNumber, out var sr);
                tc.Steps.Add(ToEvidenceStep(step.StepNumber, current, step.Action, step.Operation,
                    string.IsNullOrWhiteSpace(step.Description) ? sr?.Description ?? "" : step.Description,
                    sr, NoteFor(noteList, result.TestId, step.StepNumber)));
            }
            // Results without a definition (definition not available): keep them visible.
            foreach (var sr in result.StepResults.Where(s => !seen.Contains(s.StepNumber)).OrderBy(s => s.StepNumber))
            {
                tc.Steps.Add(ToEvidenceStep(sr.StepNumber, current, sr.Action, null, sr.Description, sr,
                    NoteFor(noteList, result.TestId, sr.StepNumber)));
            }

            tc.Items = itemList
                .Where(i => string.Equals(i.TestId, result.TestId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.StepNumber)
                .ToList();
            doc.TestCases.Add(tc);
        }

        return doc;
    }

    static string? NoteFor(List<EvidenceStepNote> notes, string testId, int stepNumber) =>
        notes.LastOrDefault(n => n.StepNumber == stepNumber &&
                                 string.Equals(n.TestId, testId, StringComparison.OrdinalIgnoreCase))?.Text;

    static EvidenceStep ToEvidenceStep(int number, int docStep, string action, string? operation,
        string description, StepResult? sr, string? note)
    {
        var status = sr == null ? EvidenceStepStatus.NotExecuted
            : sr.Skipped ? EvidenceStepStatus.Skipped
            : sr.Success ? EvidenceStepStatus.Passed
            : EvidenceStepStatus.Failed;
        string? message = sr?.Message;
        if (string.IsNullOrWhiteSpace(message)) message = note;
        else if (!string.IsNullOrWhiteSpace(note) && status != EvidenceStepStatus.Failed) message = note + " " + message;
        return new EvidenceStep
        {
            StepNumber = number,
            DocStep = docStep,
            Action = action ?? "",
            Operation = operation,
            Description = description ?? "",
            Status = status,
            DurationMs = sr?.DurationMs ?? 0,
            Message = string.IsNullOrWhiteSpace(message) ? null : message!.Trim()
        };
    }

    /// <summary>
    /// Groups a test case into documented sections: preparation (0) first, then the
    /// documented steps in ascending order. Documented steps without technical steps
    /// are kept (they show that nothing was automated for them).
    /// </summary>
    public static List<EvidenceSection> Group(EvidenceTestCase tc)
    {
        if (tc == null) throw new ArgumentNullException(nameof(tc));
        bool documented = tc.DocSteps.Count > 0 || tc.Steps.Any(s => s.DocStep > 0);
        var numbers = new SortedSet<int>(tc.DocSteps.Select(d => d.Number));
        foreach (var s in tc.Steps) numbers.Add(s.DocStep);

        var sections = new List<EvidenceSection>();
        foreach (var n in numbers)
        {
            var steps = tc.Steps.Where(s => s.DocStep == n).ToList();
            var def = tc.DocSteps.FirstOrDefault(d => d.Number == n);
            if (n == 0 && steps.Count == 0 && def == null) continue;
            var stepNumbers = new HashSet<int>(steps.Select(s => s.StepNumber));
            sections.Add(new EvidenceSection
            {
                Number = n,
                Heading = n == 0 && def == null
                    ? (documented ? "Vorbereitung" : "Ablauf")
                    : Heading(n, def),
                Action = def?.Action,
                Expected = def?.Expected,
                Steps = steps,
                Items = tc.Items.Where(i => stepNumbers.Contains(i.StepNumber)).ToList(),
                Status = SectionStatus(steps)
            });
        }
        return sections;
    }

    // Heading: the short title if given; otherwise a short, single-line action; otherwise just the number.
    static string Heading(int n, DocStepDefinition? def)
    {
        if (def == null) return $"Schritt {n}";
        if (!string.IsNullOrWhiteSpace(def.Title)) return $"Schritt {n}: {def.Title!.Trim()}";
        var action = (def.Action ?? "").Trim();
        return action.Length > 0 && action.Length <= 80 && action.IndexOf('\n') < 0 ? $"Schritt {n}: {action}" : $"Schritt {n}";
    }

    /// <summary>True when the action text is not already fully shown in the heading.</summary>
    public static bool ShowActionSeparately(EvidenceSection section) =>
        !string.IsNullOrWhiteSpace(section.Action) && !section.Heading.EndsWith(": " + section.Action!.Trim(), StringComparison.Ordinal);

    static EvidenceStepStatus SectionStatus(List<EvidenceStep> steps)
    {
        if (steps.Count == 0) return EvidenceStepStatus.NotExecuted;
        if (steps.Any(s => s.Status == EvidenceStepStatus.Failed)) return EvidenceStepStatus.Failed;
        if (steps.Any(s => s.Status == EvidenceStepStatus.NotExecuted)) return EvidenceStepStatus.NotExecuted;
        if (steps.All(s => s.Status == EvidenceStepStatus.Skipped)) return EvidenceStepStatus.Skipped;
        return EvidenceStepStatus.Passed;
    }

    /// <summary>German label of a step status, shared by the HTML and DOCX renderers.</summary>
    public static string StatusLabel(EvidenceStepStatus s) => s switch
    {
        EvidenceStepStatus.Passed => "Bestanden",
        EvidenceStepStatus.Failed => "Fehlgeschlagen",
        EvidenceStepStatus.Skipped => "Übersprungen",
        _ => "Nicht ausgeführt"
    };

    /// <summary>German label of a test outcome, shared by the HTML and DOCX renderers.</summary>
    public static string OutcomeLabel(TestOutcome o) => o switch
    {
        TestOutcome.Passed => "Bestanden",
        TestOutcome.Failed => "Fehlgeschlagen",
        TestOutcome.Error => "Fehler",
        TestOutcome.Skipped => "Übersprungen",
        _ => o.ToString()
    };

    /// <summary>Formats a UTC timestamp as local time dd.MM.yyyy HH:mm:ss.</summary>
    public static string FormatLocal(DateTime utc)
    {
        if (utc == default) return "";
        var u = utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc;
        return u.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a duration in milliseconds for humans (e.g. "850 ms", "12,3 s").</summary>
    public static string FormatDuration(long ms)
    {
        if (ms < 1000) return ms + " ms";
        return (ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " s";
    }
}
