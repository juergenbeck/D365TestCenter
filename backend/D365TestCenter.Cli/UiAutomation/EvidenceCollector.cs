using D365TestCenter.Core.Reporting;

namespace D365TestCenter.Cli.UiAutomation;

/// <summary>
/// ADR 2026-09-16-1957: collects the evidence of one CLI run (screenshot items and
/// step notes) and owns the evidence directory layout:
/// <c>&lt;dir&gt;/evidence.json</c>, <c>&lt;dir&gt;/testdokumentation.html</c>,
/// <c>&lt;dir&gt;/testdokumentation.docx</c> and the images under <c>&lt;dir&gt;/bilder/</c>.
/// </summary>
public sealed class EvidenceCollector
{
    public const string ImagesFolder = "bilder";

    public EvidenceCollector(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Evidence directory required", nameof(directory));
        Directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, ImagesFolder));
    }

    public string Directory { get; }

    public List<EvidenceItem> Items { get; } = new();

    public List<EvidenceStepNote> Notes { get; } = new();

    /// <summary>Relative (forward-slash) path for a new image of the given test step, unique within the run.</summary>
    public string NewImagePath(string testId, int stepNumber, string? name, int part)
    {
        var baseName = $"{Slug(testId)}-s{stepNumber:00}" + (string.IsNullOrWhiteSpace(name) ? "" : "-" + Slug(name!)) + $"-{part}";
        var rel = $"{ImagesFolder}/{baseName}.png";
        int n = 2;
        while (File.Exists(ToFullPath(rel)))
            rel = $"{ImagesFolder}/{baseName}-{n++}.png";
        return rel;
    }

    public string ToFullPath(string relative) =>
        Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));

    public void AddNote(string testId, int stepNumber, string text) =>
        Notes.Add(new EvidenceStepNote { TestId = testId, StepNumber = stepNumber, Text = text });

    static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length > 60 ? slug.Substring(0, 60).TrimEnd('-') : slug;
    }

    /// <summary>Reads width and height from a PNG header (IHDR). Returns (0,0) for non-PNG data.</summary>
    public static (int Width, int Height) PngSize(byte[] png)
    {
        if (png == null || png.Length < 24 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
            return (0, 0);
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (w, h);
    }
}
