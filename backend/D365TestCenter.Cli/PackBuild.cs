using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using D365TestCenter.Core.Reporting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Cli;

/// <summary>
/// B5 (ADR-0008) CLI wiring for <c>build-pack</c>. Walks a directory of Markdown test
/// definitions, builds an importable suite pack via the Core <see cref="PackBuilder"/>
/// (documentation + userStories enriched), and writes it as UTF-8 (no BOM). The walk and
/// file IO live here; the pack assembly and lint are pure Core logic. Replaces the Markant
/// PowerShell tooling (Build-D365TC-Pack.ps1 / Test-Definitionen.ps1).
/// </summary>
public static class PackBuild
{
    // Directory segments that never contain test definitions (Markant convention + generic).
    static readonly string[] ExcludedDirNames = { "archiv", "archive" };

    /// <summary>
    /// Enumerates the test-definition Markdown files under <paramref name="defsDir"/>,
    /// ordered by relative path for a deterministic pack. Skips "_"-prefixed directories
    /// (e.g. _generated, _shared), archive directories and README files. Returns
    /// (relativePath, markdown) tuples; the relative path is the lint source label.
    /// </summary>
    public static List<(string Source, string Markdown)> CollectDefinitions(string defsDir)
    {
        if (!Directory.Exists(defsDir))
            throw new DirectoryNotFoundException($"Definitions directory not found: {defsDir}");

        var defs = new List<(string Source, string Markdown)>();
        foreach (var file in Directory.EnumerateFiles(defsDir, "*.md", SearchOption.AllDirectories))
        {
            if (IsExcluded(defsDir, file)) continue;
            var rel = Path.GetRelativePath(defsDir, file).Replace('\\', '/');
            defs.Add((rel, File.ReadAllText(file)));
        }
        return defs.OrderBy(d => d.Source, StringComparer.Ordinal).ToList();
    }

    static bool IsExcluded(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)   // directory segments only
        {
            var seg = parts[i];
            if (seg.StartsWith("_", StringComparison.Ordinal)) return true;
            if (ExcludedDirNames.Contains(seg, StringComparer.OrdinalIgnoreCase)) return true;
        }
        var name = parts.Length > 0 ? parts[parts.Length - 1] : "";
        return name.StartsWith("README", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Walks the definitions and builds the suite pack (Core PackBuilder).</summary>
    public static PackBuildResult Build(string defsDir, string packName)
        => PackBuilder.BuildPack(CollectDefinitions(defsDir), packName);

    /// <summary>Writes the pack as indented UTF-8 JSON without a BOM.</summary>
    public static void WritePack(JObject pack, string outPath)
    {
        var full = Path.GetFullPath(outPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, pack.ToString(Formatting.Indented), new UTF8Encoding(false));
    }
}
