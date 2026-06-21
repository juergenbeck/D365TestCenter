using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;

namespace D365TestCenter.Cli;

/// <summary>
/// MVP-3 (ADR-0009 Phase 6b) CLI wiring for <c>export-defs</c>: reads the jbe_testcase landscape from
/// Dataverse (<see cref="DataverseDefinitionSource"/>) and writes a derived Markdown mirror to disk -
/// one file per test case, grouped by domain (<c>&lt;out&gt;/&lt;domaene&gt;/&lt;testId&gt;.md</c>),
/// rendered by the pure <see cref="DefinitionMdWriter"/>. The original repo path is not stored in
/// Dataverse, so this is a backup/review mirror (decision: domain-grouped), not a 1:1 source spiegel;
/// build-pack re-reads it recursively. Only the directory walk and file IO live here.
/// </summary>
public static class ExportDefs
{
    static readonly Regex InvalidPathChars = new Regex("[/\\\\:*?\"<>|]", RegexOptions.Compiled);
    // Folder for definitions without a domain. Must NOT start with "_" and must not be archiv/archive:
    // build-pack's CollectDefinitions skips "_"-prefixed and archive directories, so a "_"-prefixed
    // name here would make the whole no-domain mirror invisible to a re-import (round-trip break).
    const string NoDomainFolder = "ohne-domaene";

    public sealed class ExportSummary
    {
        public int Written { get; set; }
        public int Skipped { get; set; }            // records without a jbe_testid
        public List<string> Domains { get; } = new();
        public List<string> Collisions { get; } = new();
    }

    /// <summary>
    /// Exports the definitions (optionally narrowed by <paramref name="filter"/>) into
    /// <paramref name="outDir"/>, grouped by sanitized domain folder. UTF-8 without BOM, LF endings.
    /// A second test case that sanitizes to the same domain+file path is reported as a collision and
    /// overwrites (a data error worth surfacing, not silently dropping).
    /// </summary>
    public static ExportSummary Export(
        IOrganizationService service, ITestCenterConfig cfg, string outDir, string? filter, Action<string>? log = null)
    {
        var mirrors = DataverseDefinitionSource.Load(service, filter, log);
        var summary = new ExportSummary();
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in mirrors)
        {
            if (string.IsNullOrWhiteSpace(m.Id)) { summary.Skipped++; continue; }

            var domainFolder = Sanitize(string.IsNullOrWhiteSpace(m.Domaene) ? NoDomainFolder : m.Domaene!);
            var fileName = Sanitize(m.Id) + ".md";
            var relPath = domainFolder + "/" + fileName;
            var dir = Path.Combine(outDir, domainFolder);
            Directory.CreateDirectory(dir);
            var fullPath = Path.Combine(dir, fileName);

            if (!writtenPaths.Add(relPath))
            {
                summary.Collisions.Add(relPath);
                log?.Invoke($"  KOLLISION {relPath} (ueberschrieben - testId/Domaene-Konflikt)");
            }

            File.WriteAllText(fullPath, DefinitionMdWriter.Render(m), new UTF8Encoding(false));
            summary.Written++;
            domains.Add(domainFolder);
            log?.Invoke($"  WRITE {relPath}");
        }

        foreach (var d in domains.OrderBy(d => d, StringComparer.Ordinal)) summary.Domains.Add(d);
        return summary;
    }

    /// <summary>Replaces path-illegal characters so a domain or test id is safe as a folder/file name.</summary>
    static string Sanitize(string s) => InvalidPathChars.Replace(s.Trim(), "_");
}
