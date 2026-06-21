using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// Dataverse source for the management inventory (ADR-0009 Phase 4, <c>jbe_BuildInventory</c>). Reads
/// the whole jbe_testcase landscape (enabled and disabled - the inventory shows everything with a
/// status column) and maps each record to an <see cref="InventoryEntry"/> from the dedicated metadata
/// fields, then feeds the source-agnostic <see cref="InventoryBuilder.Render"/>. Core home so the
/// Custom-API plugin reuses it; the CLI <c>inventory</c> walks the Markdown front-matter instead and
/// feeds the same renderer. The run history / trend columns stay empty in this source (no front-matter
/// ergebnis_historie in Dataverse - the per-test run trend is a later additive step).
/// </summary>
public static class DataverseInventorySource
{
    /// <summary>
    /// Loads the inventory from jbe_testcase, optionally narrowed by <paramref name="filter"/>
    /// (<c>*</c>/empty = all, <c>tag:X</c>, <c>domain:X</c>, or a comma-separated id list with a
    /// trailing <c>*</c> wildcard - mirrors the run/validate filter vocabulary).
    /// </summary>
    public static InventoryModel Load(IOrganizationService service, string? filter, Action<string>? log = null)
    {
        var query = new QueryExpression(WorkerSchema.TestCaseEntity)
        {
            ColumnSet = new ColumnSet(
                WorkerSchema.TcTestId, WorkerSchema.TcTitle, WorkerSchema.TcEnabled,
                WorkerSchema.TcDomain, WorkerSchema.TcLifecycleStatus, WorkerSchema.TcTags,
                WorkerSchema.TcTickets, WorkerSchema.TcTestLevel, WorkerSchema.TcOwner,
                WorkerSchema.TcEstimatedMinutes),
            NoLock = true
        };

        var model = new InventoryModel();
        foreach (var e in service.RetrieveMultiple(query).Entities)
        {
            var entry = MapEntry(e);
            if (entry != null && MatchesFilter(entry, filter))
                model.Entries.Add(entry);
        }
        log?.Invoke($"DataverseInventorySource: {model.Entries.Count} Einträge (Filter: {filter ?? "*"})");
        return model;
    }

    /// <summary>
    /// Maps one jbe_testcase record to an <see cref="InventoryEntry"/>. The lifecycle status label
    /// comes from the record's FormattedValues (picklist, user-language label); the domain and the
    /// other scalar metadata come straight from the dedicated fields (jbe_domain is a free-text
    /// field, generic across projects). Returns null if the record has no jbe_testid.
    /// </summary>
    public static InventoryEntry? MapEntry(Entity e)
    {
        if (e == null) return null;
        var id = e.GetAttributeValue<string>(WorkerSchema.TcTestId);
        if (string.IsNullOrWhiteSpace(id)) return null;

        return new InventoryEntry
        {
            Id = id!,
            Titel = e.GetAttributeValue<string>(WorkerSchema.TcTitle) ?? "",
            Domaene = e.GetAttributeValue<string>(WorkerSchema.TcDomain) ?? "",
            Status = FormattedOrEmpty(e, WorkerSchema.TcLifecycleStatus),
            SuiteTags = SplitCsv(e.GetAttributeValue<string>(WorkerSchema.TcTags)),
            Ticket = e.GetAttributeValue<string>(WorkerSchema.TcTickets) ?? "",
            Stufe = e.GetAttributeValue<int?>(WorkerSchema.TcTestLevel)?.ToString() ?? "",
            Verantwortlich = e.GetAttributeValue<string>(WorkerSchema.TcOwner) ?? "",
            GeschaetztMin = e.GetAttributeValue<int?>(WorkerSchema.TcEstimatedMinutes)?.ToString() ?? ""
            // LaufStatus / History / Quelle / Datei: leer in dieser Quelle (kein Repo-Pfad, kein
            // Frontmatter-ergebnis_historie in Dataverse). Der Per-Test-Lauftrend ist ein additiver
            // Folge-Ausbau (groupby jbe_testid über jbe_testrunresult).
        };
    }

    /// <summary>
    /// Applies the inventory filter to a mapped entry. Empty/<c>*</c> matches all; <c>tag:X</c> matches
    /// a suite tag; <c>domain:X</c> matches the domain label; otherwise a comma-separated id list where
    /// each entry matches by exact id or a trailing <c>*</c> prefix (case-insensitive throughout).
    /// </summary>
    public static bool MatchesFilter(InventoryEntry entry, string? filter)
    {
        if (entry == null) return false;
        if (string.IsNullOrWhiteSpace(filter) || filter!.Trim() == "*") return true;

        var trimmed = filter.Trim();

        if (trimmed.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = trimmed.Substring("tag:".Length).Trim();
            return entry.SuiteTags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        }

        if (trimmed.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("domaene:", StringComparison.OrdinalIgnoreCase))
        {
            var dom = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
            return (entry.Domaene ?? "").Equals(dom, StringComparison.OrdinalIgnoreCase);
        }

        var patterns = trimmed.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim());
        return patterns.Any(p => MatchesPattern(entry.Id, p));
    }

    static bool MatchesPattern(string testId, string pattern)
    {
        if (pattern.EndsWith("*"))
            return testId.StartsWith(pattern.Substring(0, pattern.Length - 1),
                StringComparison.OrdinalIgnoreCase);
        return testId.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    static string FormattedOrEmpty(Entity e, string attribute)
        => e.FormattedValues.Contains(attribute) ? e.FormattedValues[attribute] : "";

    static List<string> SplitCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new List<string>();
        return csv!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}
