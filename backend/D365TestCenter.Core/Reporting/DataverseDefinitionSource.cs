using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// One jbe_testcase record reduced to the facts an export-defs Markdown mirror needs: the
/// front-matter scalars (status as the lifecycle KEYWORD, not the localized label), the
/// documentation Markdown and the executable definition JSON. The CSV fields (tickets, env_scope,
/// suite_tags) carry the raw column value; the <see cref="DefinitionMdWriter"/> splits/formats them.
/// </summary>
public sealed class DefinitionMirror
{
    public string Id { get; set; } = "";
    public string Titel { get; set; } = "";
    public string Status { get; set; } = "";          // lifecycle keyword (entwurf/aktiv/...); empty if unset
    public string Domaene { get; set; } = "";
    public string Stufe { get; set; } = "";
    public string Verantwortlich { get; set; } = "";
    public string Tickets { get; set; } = "";         // CSV (ticket + weitere_tickets)
    public string EnvScope { get; set; } = "";        // CSV
    public string GeschaetztMin { get; set; } = "";
    public string ZephyrKey { get; set; } = "";
    public string SuiteTags { get; set; } = "";       // CSV (jbe_tags)
    public string Documentation { get; set; } = "";   // jbe_documentation (## sections, verbatim)
    public string DefinitionJson { get; set; } = "";  // jbe_definitionjson (executable engine definition)
}

/// <summary>
/// MVP-3 (ADR-0009 Phase 6b) Dataverse source for <c>export-defs</c>: reads the jbe_testcase landscape
/// and maps each record to a <see cref="DefinitionMirror"/> (the inverse of import-pack's A.1 mapping).
/// Mirror of <see cref="DataverseInventorySource"/> - same query/filter vocabulary - but it carries the
/// documentation and the definition JSON so the CLI can write a faithful Markdown mirror back to disk.
/// The lifecycle status is taken from the OptionSet VALUE via <see cref="WorkerSchema.LifecycleKeywordFromValue"/>
/// (language-independent), NOT from the FormattedValues label.
/// </summary>
public static class DataverseDefinitionSource
{
    /// <summary>
    /// Loads the definitions from jbe_testcase, optionally narrowed by <paramref name="filter"/>
    /// (<c>*</c>/empty = all, <c>tag:X</c>, <c>domain:X</c>/<c>domaene:X</c>, or a comma-separated id
    /// list with a trailing <c>*</c> wildcard - same vocabulary as inventory/validate/run).
    /// </summary>
    public static List<DefinitionMirror> Load(IOrganizationService service, string? filter, Action<string>? log = null)
    {
        var query = new QueryExpression(WorkerSchema.TestCaseEntity)
        {
            ColumnSet = new ColumnSet(
                WorkerSchema.TcTestId, WorkerSchema.TcTitle, WorkerSchema.TcLifecycleStatus,
                WorkerSchema.TcDomain, WorkerSchema.TcTestLevel, WorkerSchema.TcOwner,
                WorkerSchema.TcTickets, WorkerSchema.TcEnvScope, WorkerSchema.TcEstimatedMinutes,
                WorkerSchema.TcZephyrKey, WorkerSchema.TcTags, WorkerSchema.TcDocumentation,
                WorkerSchema.TcDefinition),
            NoLock = true
        };

        var result = new List<DefinitionMirror>();
        foreach (var e in service.RetrieveMultiple(query).Entities)
        {
            var m = MapMirror(e);
            if (m != null && MatchesFilter(m, filter))
                result.Add(m);
        }
        log?.Invoke($"DataverseDefinitionSource: {result.Count} Definitionen (Filter: {filter ?? "*"})");
        return result;
    }

    /// <summary>
    /// Maps one jbe_testcase record to a <see cref="DefinitionMirror"/>. The lifecycle keyword comes
    /// from the OptionSet value (empty when unset or unknown); the int fields are stringified; the
    /// CSV/text fields pass straight through. Returns null when the record has no jbe_testid.
    /// </summary>
    public static DefinitionMirror? MapMirror(Entity e)
    {
        if (e == null) return null;
        var id = e.GetAttributeValue<string>(WorkerSchema.TcTestId);
        if (string.IsNullOrWhiteSpace(id)) return null;

        var statusOption = e.GetAttributeValue<OptionSetValue>(WorkerSchema.TcLifecycleStatus);
        var status = statusOption != null ? (WorkerSchema.LifecycleKeywordFromValue(statusOption.Value) ?? "") : "";

        return new DefinitionMirror
        {
            Id = id!,
            Titel = e.GetAttributeValue<string>(WorkerSchema.TcTitle) ?? "",
            Status = status,
            Domaene = e.GetAttributeValue<string>(WorkerSchema.TcDomain) ?? "",
            Stufe = e.GetAttributeValue<int?>(WorkerSchema.TcTestLevel)?.ToString() ?? "",
            Verantwortlich = e.GetAttributeValue<string>(WorkerSchema.TcOwner) ?? "",
            Tickets = e.GetAttributeValue<string>(WorkerSchema.TcTickets) ?? "",
            EnvScope = e.GetAttributeValue<string>(WorkerSchema.TcEnvScope) ?? "",
            GeschaetztMin = e.GetAttributeValue<int?>(WorkerSchema.TcEstimatedMinutes)?.ToString() ?? "",
            ZephyrKey = e.GetAttributeValue<string>(WorkerSchema.TcZephyrKey) ?? "",
            SuiteTags = e.GetAttributeValue<string>(WorkerSchema.TcTags) ?? "",
            Documentation = e.GetAttributeValue<string>(WorkerSchema.TcDocumentation) ?? "",
            DefinitionJson = e.GetAttributeValue<string>(WorkerSchema.TcDefinition) ?? ""
        };
    }

    /// <summary>
    /// Applies the same filter vocabulary as the inventory source to a mapped mirror. Empty/<c>*</c>
    /// matches all; <c>tag:X</c> matches a suite tag (jbe_tags CSV); <c>domain:X</c>/<c>domaene:X</c>
    /// matches the domain; otherwise a comma-separated id list, each by exact id or trailing <c>*</c>.
    /// </summary>
    public static bool MatchesFilter(DefinitionMirror m, string? filter)
    {
        if (m == null) return false;
        if (string.IsNullOrWhiteSpace(filter) || filter!.Trim() == "*") return true;

        var trimmed = filter.Trim();

        if (trimmed.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = trimmed.Substring("tag:".Length).Trim();
            return SplitCsv(m.SuiteTags).Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        }

        if (trimmed.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("domaene:", StringComparison.OrdinalIgnoreCase))
        {
            var dom = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
            return (m.Domaene ?? "").Equals(dom, StringComparison.OrdinalIgnoreCase);
        }

        return trimmed.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Any(p => MatchesPattern(m.Id, p));
    }

    static bool MatchesPattern(string testId, string pattern)
    {
        if (pattern.EndsWith("*"))
            return testId.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase);
        return testId.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    static List<string> SplitCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new List<string>();
        return csv!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }
}
