using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;

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
                var entityName = assertion.Entity ?? ctx.ResolveRecordEntityName(refStr);
                entity = service.Retrieve(entityName, foundEntity.Id, new ColumnSet(assertion.Field));
            }
        }
        else
        {
            var recordId = ctx.ResolveRecordId(refStr);
            var entityName = assertion.Entity ?? ctx.ResolveRecordEntityName(refStr);
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
        var entityName = assertion.Entity
            ?? throw new InvalidOperationException("Query-Assertion benötigt 'entity'.");
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
            string.IsNullOrEmpty(assertion.Field) ? null : new[] { assertion.Field });
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
    //  Operator-Auswertung
    // ---------------------------------------------------------------

    private static void ApplyOperator(
        TestAssertion assertion, object? actual, AssertionResult result)
    {
        result.ActualDisplay = FormatValue(actual);
        result.ExpectedDisplay = assertion.Value ?? "<null>";

        switch (assertion.Operator.ToUpperInvariant())
        {
            case "EQUALS":
                result.Passed = CompareValues(actual, assertion.Value);
                break;

            case "NOTEQUALS":
                result.Passed = !CompareValues(actual, assertion.Value);
                break;

            case "ISNULL":
                result.Passed = IsNullOrEmpty(actual);
                result.ExpectedDisplay = "<null>";
                break;

            case "ISNOTNULL":
                result.Passed = !IsNullOrEmpty(actual);
                result.ExpectedDisplay = "<nicht null>";
                break;

            case "CONTAINS":
                var actualStr = ExtractString(actual) ?? "";
                result.Passed = actualStr.Contains(
                    assertion.Value ?? "", StringComparison.OrdinalIgnoreCase);
                break;

            case "STARTSWITH":
                var startsActual = ExtractString(actual) ?? "";
                result.Passed = startsActual.StartsWith(
                    assertion.Value ?? "", StringComparison.OrdinalIgnoreCase);
                break;

            case "ENDSWITH":
                var endsActual = ExtractString(actual) ?? "";
                result.Passed = endsActual.EndsWith(
                    assertion.Value ?? "", StringComparison.OrdinalIgnoreCase);
                break;

            case "GREATERTHAN":
                result.Passed = TryCompareOrdered(actual, assertion.Value, out var gtCmp)
                    && gtCmp > 0;
                break;

            case "LESSTHAN":
                result.Passed = TryCompareOrdered(actual, assertion.Value, out var ltCmp)
                    && ltCmp < 0;
                break;

            case "DATESETRECENTLY":
                // assertion.Value (falls gesetzt) als Sekunden-Toleranz parsen (Default 120s).
                // Fix für RV-Review: expected-Wert wurde vorher ignoriert.
                var recentSec = 120;
                if (!string.IsNullOrWhiteSpace(assertion.Value)
                    && int.TryParse(assertion.Value, out var parsedSec)
                    && parsedSec > 0)
                {
                    recentSec = parsedSec;
                }
                EvaluateDateSetRecently(actual, recentSec, result);
                break;

            default:
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
            var utcDt = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
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

    /// <summary>
    /// Geordneter Vergleich für GreaterThan/LessThan. Versucht nacheinander:
    /// decimal (invariant), DateTime (RoundtripKind, invariant), dann String
    /// (Ordinal, case-insensitive). Liefert false wenn beide Werte null oder
    /// der Actualwert nicht extrahierbar ist. -1 actual&lt;expected, 0 gleich,
    /// +1 actual&gt;expected.
    /// </summary>
    private static bool TryCompareOrdered(object? actual, string? expected, out int comparison)
    {
        comparison = 0;
        if (actual == null || expected == null) return false;

        // Money/OptionSetValue/int direkt behandeln (kein Umweg über ExtractString-String-Format).
        decimal? actualNum = actual switch
        {
            Money m => m.Value,
            OptionSetValue osv => osv.Value,
            int i => i,
            long l => l,
            decimal d => d,
            double dbl => (decimal)dbl,
            float f => (decimal)f,
            _ => null
        };

        if (actualNum.HasValue
            && decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var expectedNum))
        {
            comparison = actualNum.Value.CompareTo(expectedNum);
            return true;
        }

        if (actual is DateTime actualDt
            && DateTime.TryParse(expected, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expectedDt))
        {
            var au = actualDt.Kind == DateTimeKind.Utc ? actualDt : actualDt.ToUniversalTime();
            var eu = expectedDt.Kind == DateTimeKind.Utc ? expectedDt : expectedDt.ToUniversalTime();
            comparison = au.CompareTo(eu);
            return true;
        }

        var actualStr = ExtractString(actual);
        if (actualStr == null) return false;

        // String-Fallback: erst Zahl, dann DateTime, dann Text.
        if (decimal.TryParse(actualStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var e))
        {
            comparison = a.CompareTo(e);
            return true;
        }

        if (DateTime.TryParse(actualStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ad)
            && DateTime.TryParse(expected, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ed))
        {
            var au = ad.Kind == DateTimeKind.Utc ? ad : ad.ToUniversalTime();
            var eu = ed.Kind == DateTimeKind.Utc ? ed : ed.ToUniversalTime();
            comparison = au.CompareTo(eu);
            return true;
        }

        comparison = string.Compare(actualStr, expected, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool CompareValues(object? actual, string? expected)
    {
        var actualStr = ExtractString(actual);

        if (actualStr == null && expected == null) return true;
        if (actualStr == null || expected == null) return false;
        if (expected == "" && string.IsNullOrWhiteSpace(actualStr)) return true;

        return string.Equals(
            actualStr.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNullOrEmpty(object? value)
    {
        if (value == null) return true;
        if (value is string s) return string.IsNullOrWhiteSpace(s);
        return false;
    }

    private static string? ExtractString(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            OptionSetValue osv => osv.Value.ToString(),
            EntityReference er => er.Id.ToString(),
            Money m => m.Value.ToString(),
            DateTime dt => dt.ToString("O"),
            bool b => b.ToString(),
            JToken jt => jt.ToString(),
            _ => value.ToString()
        };
    }

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
