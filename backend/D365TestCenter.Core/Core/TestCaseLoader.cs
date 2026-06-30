using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;

namespace D365TestCenter.Core;

/// <summary>
/// Lädt <c>jbe_testcase</c>-Definitionen aus Dataverse und deserialisiert sie zu
/// <see cref="TestCase"/> (ADR-0009). Zwei Pfade:
///   - <see cref="LoadEnabled"/>: vom Koordinator -- alle aktiven Testfälle, gefiltert
///     (* / id-Liste mit *-Suffix / tag: / category:), identisch zur Alt-Cascade-Semantik.
///   - <see cref="LoadByIds"/>: vom Worker -- nur die (rohen) Test-IDs des Chunk-Snapshots.
///
/// Die JSON-Deserialisierung nutzt <c>MetadataPropertyHandling.Ignore</c> (sonst entfernt
/// Newtonsoft "$type"-Properties, die das ExecuteRequest-$type-System braucht).
/// </summary>
public static class TestCaseLoader
{
    private static readonly JsonSerializerSettings DeserializeSettings = new()
    {
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore
    };

    /// <summary>Lädt alle aktiven (jbe_enabled) Testfälle und wendet den Filter an.</summary>
    public static List<TestCase> LoadEnabled(IOrganizationService service, string? filter,
        Action<string>? log = null)
    {
        var query = new QueryExpression(WorkerSchema.TestCaseEntity)
        {
            ColumnSet = new ColumnSet(
                WorkerSchema.TcTestId, WorkerSchema.TcTitle,
                WorkerSchema.TcDefinition, WorkerSchema.TcEnabled),
            NoLock = true
        };
        query.Criteria.AddCondition(WorkerSchema.TcEnabled, ConditionOperator.Equal, true);

        var testCases = Deserialize(service.RetrieveMultiple(query).Entities, log);
        return ApplyFilter(testCases, filter);
    }

    /// <summary>
    /// Lädt die Testfälle zu einer (rohen) Test-ID-Liste (Chunk-Snapshot). Reihenfolge folgt
    /// der übergebenen ID-Liste (deterministisch für Gruppierung/Cursor). Deaktivierte Fälle
    /// bleiben enthalten -- der Snapshot wurde beim Anlegen gefiltert; der Runner überspringt
    /// deaktivierte ohnehin sauber.
    /// </summary>
    public static List<TestCase> LoadByIds(IOrganizationService service, IList<string> testIds,
        Action<string>? log = null)
    {
        if (testIds == null || testIds.Count == 0) return new List<TestCase>();

        var query = new QueryExpression(WorkerSchema.TestCaseEntity)
        {
            ColumnSet = new ColumnSet(
                WorkerSchema.TcTestId, WorkerSchema.TcTitle,
                WorkerSchema.TcDefinition, WorkerSchema.TcEnabled),
            NoLock = true
        };
        query.Criteria.AddCondition(WorkerSchema.TcTestId, ConditionOperator.In, testIds.Cast<object>().ToArray());

        var loaded = Deserialize(service.RetrieveMultiple(query).Entities, log);
        var byId = loaded
            .GroupBy(tc => tc.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // In der Reihenfolge des Snapshots zurückgeben (deterministisch).
        var ordered = new List<TestCase>();
        foreach (var id in testIds)
            if (byId.TryGetValue(id, out var tc))
                ordered.Add(tc);
        return ordered;
    }

    private static List<TestCase> Deserialize(IEnumerable<Entity> records, Action<string>? log)
    {
        var testCases = new List<TestCase>();
        foreach (var record in records)
        {
            var testId = record.GetAttributeValue<string>(WorkerSchema.TcTestId);
            var defJson = record.GetAttributeValue<string>(WorkerSchema.TcDefinition);
            if (string.IsNullOrWhiteSpace(defJson)) continue;

            try
            {
                var tc = JsonConvert.DeserializeObject<TestCase>(defJson, DeserializeSettings);
                if (tc != null)
                {
                    tc.Id = testId ?? tc.Id;
                    tc.Title = record.GetAttributeValue<string>(WorkerSchema.TcTitle) ?? tc.Title;
                    testCases.Add(tc);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"TestCaseLoader: Deserialisierung {testId} fehlgeschlagen: {ex.Message}");
            }
        }
        return testCases;
    }

    // Filter-Logik zentral in TestCaseFilter (ADR 2026-06-30 1432): Negation
    // (Exclude per !-Präfix) + geteilte Wildcard-/tag:/category:-Semantik. Der
    // Coordinator-Pfad erbt damit dieselbe Filter-Mächtigkeit wie der CLI-Pfad.
    private static List<TestCase> ApplyFilter(List<TestCase> testCases, string? filter)
        => TestCaseFilter.Apply(testCases, filter);
}
