using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core;

/// <summary>
/// Wertet Assertions gegen Dataverse-Records aus.
/// Targets: "Record" (per recordRef/alias), "Query" (per entity+filter).
/// Feldbasierte Operatoren: Equals, NotEquals, IsNull, IsNotNull, Contains,
/// StartsWith, EndsWith, GreaterThan, LessThan, DateSetRecently.
/// Query-only-Operatoren: Exists, NotExists, RecordCount.
/// </summary>
public sealed class AssertionEngine
{
    private readonly EntityMetadataCache? _metadataCache;

    /// <summary>
    /// Default-Konstruktor ohne Metadata-Cache. Filter-Conversion arbeitet im
    /// Legacy-Modus (Guid.TryParse vor String).
    /// </summary>
    public AssertionEngine()
    {
        _metadataCache = null;
    }

    /// <summary>
    /// Konstruktor mit Metadata-Cache. Filter-Conversion ist type-aware (FB-32):
    /// GUID-foermige Strings auf String/Memo-Feldern bleiben Strings.
    /// </summary>
    public AssertionEngine(EntityMetadataCache metadataCache)
    {
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// Wertet eine einzelne Assertion aus und gibt das Ergebnis zurück.
    /// </summary>
    public AssertionResult Evaluate(
        TestAssertion assertion, TestContext ctx, IOrganizationService service)
    {
        var result = new AssertionResult { Description = assertion.Description ?? "" };

        try
        {
            // KRITISCH: Platzhalter im Value-Feld auflösen BEVOR die Operator-Prüfung läuft.
            // Beispiel: "value": "{DUP_CONTACT.fields.markant_goldenrecordidnumber}"
            // muss durch den PlaceholderResolver auf die tatsaechliche AutoNumber ersetzt werden,
            // damit die Equals-Prüfung gegen den Record-Wert gelingt.
            // Auch recordRef wird aufgelöst (könnte {RECORD:alias} enthalten).
            if (!string.IsNullOrEmpty(assertion.Value))
            {
                var resolved = new PlaceholderEngine().Resolve(assertion.Value, ctx);
                assertion = new TestAssertion
                {
                    Target = assertion.Target,
                    Field = assertion.Field,
                    Entity = assertion.Entity,
                    RecordRef = assertion.RecordRef,
                    Filter = assertion.Filter,
                    Operator = assertion.Operator,
                    Value = resolved,
                    Description = assertion.Description
                };
            }

            var targetUpper = assertion.Target.ToUpperInvariant();

            if (targetUpper == "RECORD")
            {
                EvaluateGenericRecordAssertion(assertion, ctx, service, result);
            }
            else if (targetUpper == "QUERY")
            {
                EvaluateQueryAssertion(assertion, ctx, service, result);
            }
            else
            {
                result.Passed = false;
                result.Message = $"Unbekanntes Assertion-Ziel: {assertion.Target}";
            }
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Message = $"Assertion-Fehler: {ex.Message}";
        }

        return result;
    }

    // ---------------------------------------------------------------
    //  Record-Assertion (per Alias/recordRef)
    // ---------------------------------------------------------------

    private void EvaluateGenericRecordAssertion(
        TestAssertion assertion, TestContext ctx,
        IOrganizationService service, AssertionResult result)
    {
        var refStr = assertion.RecordRef ?? "";

        if (refStr.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && refStr.EndsWith("}"))
            refStr = refStr.Substring("{RECORD:".Length, refStr.Length - "{RECORD:".Length - 1);

        if (string.IsNullOrEmpty(refStr))
            throw new InvalidOperationException("Record-Assertion benötigt 'recordRef'.");

        Entity entity;

        if (ctx.FoundRecords.TryGetValue(refStr, out var foundEntity))
        {
            if (foundEntity.Contains(assertion.Field))
            {
                entity = foundEntity;
            }
            else
            {
                var entityName = ResolveEntityName(assertion.Entity ?? ctx.ResolveRecordEntityName(refStr));
                entity = service.Retrieve(entityName, foundEntity.Id, new ColumnSet(assertion.Field));
            }
        }
        else
        {
            var recordId = ctx.ResolveRecordId(refStr);
            var entityName = ResolveEntityName(assertion.Entity ?? ctx.ResolveRecordEntityName(refStr));
            entity = service.Retrieve(entityName, recordId, new ColumnSet(assertion.Field));
        }

        var actual = entity.Contains(assertion.Field) ? entity[assertion.Field] : null;
        ApplyOperator(assertion, actual, result);
    }

    // ---------------------------------------------------------------
    //  Query-Assertion (per entity + filter)
    // ---------------------------------------------------------------

    private void EvaluateQueryAssertion(
        TestAssertion assertion, TestContext ctx,
        IOrganizationService service, AssertionResult result)
    {
        var entityName = ResolveEntityName(assertion.Entity
            ?? throw new InvalidOperationException("Query-Assertion benötigt 'entity'."));
        var filters = assertion.Filter
            ?? throw new InvalidOperationException("Query-Assertion benötigt 'filter'.");

        var engine = new PlaceholderEngine();
        var resolvedFilters = new List<FilterCondition>();
        foreach (var f in filters)
        {
            var resolvedValue = f.Value;
            if (resolvedValue is string s)
                resolvedValue = engine.Resolve(s, ctx);
            resolvedFilters.Add(new FilterCondition { Field = f.Field, Operator = f.Operator, Value = resolvedValue });
        }

        var query = GenericRecordWaiter.BuildQuery(
            entityName, resolvedFilters,
            string.IsNullOrEmpty(assertion.Field) ? null : new[] { assertion.Field },
            metadataCache: _metadataCache);
        query.TopCount = 1;

        var results = service.RetrieveMultiple(query);

        // NotExists-Operator: kein Record darf matchen
        if (assertion.Operator.Equals("NotExists", StringComparison.OrdinalIgnoreCase))
        {
            result.Passed = results.Entities.Count == 0;
            result.ExpectedDisplay = "Kein Record";
            result.ActualDisplay = $"{results.Entities.Count} Records";
            result.Message = result.Passed
                ? "OK: Record existiert nicht mehr"
                : $"Record existiert noch ({results.Entities.Count} gefunden)";
            return;
        }

        // Exists-Operator: mindestens ein Record muss matchen
        if (assertion.Operator.Equals("Exists", StringComparison.OrdinalIgnoreCase))
        {
            result.Passed = results.Entities.Count > 0;
            result.ExpectedDisplay = "Mindestens 1 Record";
            result.ActualDisplay = $"{results.Entities.Count} Records";
            result.Message = result.Passed
                ? $"OK: {results.Entities.Count} Records gefunden"
                : "Kein Record gefunden";
            return;
        }

        // RecordCount-Operator: Anzahl der Treffer prüfen
        if (assertion.Operator.Equals("RecordCount", StringComparison.OrdinalIgnoreCase))
        {
            query.TopCount = 5000;
            var allResults = service.RetrieveMultiple(query);
            var expectedCount = int.Parse(assertion.Value ?? "0");
            result.Passed = allResults.Entities.Count == expectedCount;
            result.ExpectedDisplay = $"{expectedCount} Records";
            result.ActualDisplay = $"{allResults.Entities.Count} Records";
            result.Message = result.Passed
                ? $"OK: {allResults.Entities.Count} Records gefunden"
                : $"Erwartet {expectedCount}, gefunden {allResults.Entities.Count}";
            return;
        }

        if (results.Entities.Count == 0)
        {
            result.Passed = false;
            result.Message = $"Query-Assertion: Kein Record in '{entityName}' gefunden.";
            return;
        }

        var actual = results.Entities[0].Contains(assertion.Field)
            ? results.Entities[0][assertion.Field]
            : null;
        ApplyOperator(assertion, actual, result);
    }

    // ---------------------------------------------------------------
    //  Entity-Resolution (Symmetrie zu TestRunner.ResolveEntity)
    // ---------------------------------------------------------------

    /// <summary>
    /// Löst einen EntitySetName (Plural, Web-API-Form) zu LogicalName (Singular, SDK-Form) auf.
    /// Spiegel zu TestRunner.ResolveEntity, damit Test-Autoren beide Formen schreiben dürfen.
    /// Ohne Metadata-Cache (Default-Konstruktor) bleibt der Name unverändert.
    /// </summary>
    private string ResolveEntityName(string entityName)
    {
        return _metadataCache?.ResolveLogicalName(entityName) ?? entityName;
    }

    // ---------------------------------------------------------------
    //  Operator-Auswertung
    // ---------------------------------------------------------------

    private static void ApplyOperator(
        TestAssertion assertion, object? actual, AssertionResult result)
    {
        result.ActualDisplay = FormatValue(actual);
        result.ExpectedDisplay = assertion.Value ?? "<null>";

        var op = assertion.Operator.ToUpperInvariant();

        // Geteilter Vergleichskern fuer die feldbasierten Operatoren (ADR-0011):
        // EINE Quelle der Wahrheit, von Assert UND Step-Condition genutzt.
        if (ValueComparator.TryEvaluate(assertion.Operator, actual, assertion.Value, out var passed))
        {
            result.Passed = passed;
            if (op == "ISNULL") result.ExpectedDisplay = "<null>";
            else if (op == "ISNOTNULL") result.ExpectedDisplay = "<nicht null>";
        }
        else if (op == "DATESETRECENTLY")
        {
            // Zeit-toleranz-spezifisch, Assert-only (nicht im geteilten Comparator).
            // assertion.Value (falls gesetzt) als Sekunden-Toleranz parsen (Default 120s).
            var recentSec = 120;
            if (!string.IsNullOrWhiteSpace(assertion.Value)
                && int.TryParse(assertion.Value, out var parsedSec)
                && parsedSec > 0)
            {
                recentSec = parsedSec;
            }
            EvaluateDateSetRecently(actual, recentSec, result);
        }
        else
        {
            result.Passed = false;
            result.Message = $"Unbekannter Operator: {assertion.Operator}";
            return;
        }

        if (string.IsNullOrEmpty(result.Message))
        {
            result.Message = result.Passed
                ? $"OK: {assertion.Field} = {result.ActualDisplay}"
                : $"Erwartet: {result.ExpectedDisplay}, Aktuell: {result.ActualDisplay}";
        }
    }

    // ---------------------------------------------------------------
    //  Spezial-Operatoren
    // ---------------------------------------------------------------

    private static void EvaluateDateSetRecently(
        object? actual, int withinSeconds, AssertionResult result)
    {
        if (actual is DateTime dt)
        {
            // Dataverse returns DateTime values in UTC. See ValueComparator.NormalizeToUtc
            // for why Kind=Unspecified must not go through ToUniversalTime() (FB-44).
            var utcDt = ValueComparator.NormalizeToUtc(dt);
            var diffSeconds = (DateTime.UtcNow - utcDt).TotalSeconds;
            result.Passed = diffSeconds >= 0 && diffSeconds <= withinSeconds;
            result.ExpectedDisplay = $"Innerhalb der letzten {withinSeconds}s";
            result.ActualDisplay = $"{diffSeconds:F0}s her ({utcDt:O})";
        }
        else
        {
            result.Passed = false;
            result.ExpectedDisplay = $"DateTime innerhalb {withinSeconds}s";
        }

        result.Message = result.Passed
            ? $"OK: Datum liegt innerhalb der letzten {withinSeconds}s"
            : $"Datum nicht innerhalb der Toleranz: {result.ActualDisplay}";
    }

    // ---------------------------------------------------------------
    //  Hilfsmethoden
    // ---------------------------------------------------------------

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            string s when string.IsNullOrWhiteSpace(s) => "<leer>",
            string s => $"\"{s}\"",
            OptionSetValue osv => $"OptionSet({osv.Value})",
            EntityReference er => $"Lookup({er.LogicalName}, {er.Id})",
            Money m => $"Money({m.Value})",
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? "<unbekannt>"
        };
    }
}
