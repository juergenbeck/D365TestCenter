using System.IO;
using System.Text;
using D365TestCenter.Core.Reporting;

namespace D365TestCenter.Cli;

/// <summary>
/// E6 (ADR-0008) CLI wiring for <c>inventory</c>. Walks a directory of Markdown test
/// definitions (reusing the build-pack walk, so archived/draft defs are listed too - the
/// inventory shows the whole landscape with a status column) and builds the management
/// inventory via the Core <see cref="InventoryBuilder"/>. Pure overview, no Dataverse.
/// </summary>
public static class Inventory
{
    /// <summary>Walks the definitions and builds the inventory model (Core InventoryBuilder).</summary>
    public static InventoryModel Build(string defsDir)
        => InventoryBuilder.Build(PackBuild.CollectDefinitions(defsDir));

    /// <summary>Writes the rendered inventory Markdown as UTF-8 without a BOM.</summary>
    public static void WriteReport(string markdown, string outPath)
    {
        var full = Path.GetFullPath(outPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, markdown, new UTF8Encoding(false));
    }
}
