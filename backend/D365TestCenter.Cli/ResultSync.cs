using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;

namespace D365TestCenter.Cli;

/// <summary>
/// E2 (ADR-0008) CLI wiring for the result round-trip. The match/render logic
/// lives in the Core (<see cref="MarkdownResultSync"/>); this class owns the IO
/// the Core must not do: walking the definition tree, matching testId ==
/// front-matter id, and writing files back. The Dataverse result read itself
/// lives in the Core (<see cref="RunResultLoader"/>), shared with the Custom-API plugins.
/// </summary>
public static class ResultSync
{
    static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public sealed class SyncSummary
    {
        public int Scanned { get; set; }
        public int Matched { get; set; }
        public int Updated { get; set; }
        public List<string> UpdatedFiles { get; } = new();
    }

    /// <summary>
    /// Syncs the given per-test results into every *.md definition under
    /// <paramref name="defsDir"/> whose front-matter id matches a result testId.
    /// One run entry per definition (results sharing the id are aggregated).
    /// </summary>
    public static SyncSummary SyncDefinitions(
        IReadOnlyList<TestCaseResult> results, string defsDir, DateTime runDate, string env,
        Action<string>? log = null)
    {
        if (results == null) throw new ArgumentNullException(nameof(results));
        if (!Directory.Exists(defsDir))
            throw new DirectoryNotFoundException($"Definitions directory not found: {defsDir}");

        var byId = results
            .Where(r => !string.IsNullOrWhiteSpace(r.TestId))
            .GroupBy(r => r.TestId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TestCaseResult>)g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        var summary = new SyncSummary();
        foreach (var file in Directory.EnumerateFiles(defsDir, "*.md", SearchOption.AllDirectories))
        {
            summary.Scanned++;
            string content = File.ReadAllText(file);
            var id = MarkdownResultSync.ReadFrontmatterId(content);
            if (string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id!, out var matched))
                continue;

            summary.Matched++;
            var entry = MarkdownResultSync.BuildEntry(matched, runDate, env);

            string updated;
            try
            {
                updated = MarkdownResultSync.Sync(content, entry);
            }
            catch (ArgumentException ex)
            {
                log?.Invoke($"  SKIP {Path.GetFileName(file)}: {ex.Message}");
                continue;
            }

            if (updated != content)
            {
                File.WriteAllText(file, updated, Utf8NoBom);
                summary.Updated++;
                summary.UpdatedFiles.Add(file);
                log?.Invoke($"  OK   {id}  ({entry.Ergebnis})  -> {Path.GetFileName(file)}");
            }
            else
            {
                log?.Invoke($"  =    {id}  (kein Unterschied)");
            }
        }
        return summary;
    }

    /// <summary>
    /// Derives an env label (dev/test/datatest/cdhtest/prod) from the org URL
    /// host, as a default for --env. Most specific match first.
    /// </summary>
    public static string DeriveEnv(string org)
    {
        string host;
        try { host = new Uri(org).Host.ToLowerInvariant(); }
        catch { return "unknown"; }

        if (host.Contains("datatest")) return "datatest";
        if (host.Contains("cdhtest")) return "cdhtest";
        if (host.Contains("test")) return "test";
        if (host.Contains("prod")) return "prod";
        if (host.Contains("dev")) return "dev";
        return "unknown";
    }
}
