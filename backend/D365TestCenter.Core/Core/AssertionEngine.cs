using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;

namespace itt.IntegrationTests.Core;

/// <summary>
/// Wertet Assertions gegen Dataverse-Entities und CDH-Logs aus.
/// Unterstützt: Equals, NotEquals, IsNull, IsNotNull, Contains,
/// DateSetRecently, Unchanged, Changed, Exists, ContactUpdated.
/// Voll kompatibel mit FGTestTool-Assertion-Format (ContactSource:{alias}).
/// </summary>
public sealed class AssertionEngine
{
    private const string Contact = "contact";
    private const string ContactSource = "markant_fg_contactsource";
    private const string PlatformBridge = "markant_bridge_pf_record";

    private const string CdhLogging = "markant_fg_logging";
    private const string CdhLogContactLookup = "markant_contactid";
    private const string CdhLogCreatedOn = "createdon";
    private const string CdhLogName = "markant_name";

    /// <summary>
    /// Wertet eine einzelne Assertion aus und gibt das Ergebnis zurück.
    /// </summary>
    public AssertionResult Evaluate(
        TestAssertion assertion, TestContext ctx, IOrganizationService service)
    {
        var result = new AssertionResult { Description = assertion.Description ?? "" };

        try
        {
            var targetUpper = assertion.Target.ToUpperInvariant();

            if (targetUpper == "CONTACT")
            {
                EvaluateContactAssertion(assertion, ctx, service, result);
            }
            else if (targetUpper.StartsWith("CONTACTSOURCE:"))
            {
                // FGTestTool-Format: "ContactSource:{alias}"
                var alias = assertion.Target.Substring("ContactSource:".Length);
                EvaluateContactSourceByAlias(assertion, alias, ctx, service, result);
            }
            else if (targetUpper == "CONTACTSOURCE")
            {
                // Neues Format: "ContactSource" + sourceSystem als int
                EvaluateContactSourceBySystem(assertion, ctx, service, result);
            }
            else if (targetUpper == "CDHLOGGING")
            {
                EvaluateCdhLoggingAssertion(assertion, ctx, service, result);
            }
            else if (targetUpper == "PLATFORMBRIDGE")
            {
                EvaluatePlatformBridgeAssertion(assertion, ctx, service, result);
            }
            // ── Generische Targets ────────────────────────────────
            else if (targetUpper == "RECORD")
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

    // ═══════════════════════════════════════════════════════════════════
    //  Contact-Assertions
    // ═══════════════════════════════════════════════════════════════════

    private void EvaluateContactAssertion(
        TestAssertion assertion, TestContext ctx,
        IOrganizationService service, AssertionResult result)
    {
        if (!ctx.ContactId.HasValue)
            throw new InvalidOperationException("Kein Contact im TestContext vorhanden.");

        var entity = service.Retrieve(Contact, ctx.ContactId.Value, new ColumnSet(assertion.Field));
        var actual = entity.Contains(assertion.Field) ? entity[assertion.Field] : null;

        ApplyOperator(assertion, actual, ctx, result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ContactSource-Assertions (Alias-basiert, FGTestTool-kompatibel)
    // ═══════════════════════════════════════════════════════════════════

    private void EvaluateContactSourceByAlias(
        TestAssertion assertion, string alias, TestContext ctx,
        IOrganizationService service, AssertionResult result)
    {
        var csId = ctx.ResolveContactSourceId(alias, null);

        var entity = service.Retrieve(ContactSource, csId, new ColumnSet(assertion.Field));
        var actual = entity.Contains(assertion.Field) ? entity[assertion.Field] : null;

        ApplyOperator(assertion, actual, ctx, result);
    }

    private void EvaluateContactSourceBySystem(
        TestAssertion assertion, TestContext ctx,
        IOrganizationService service, AssertionResult result)
    {
        var sourceSystem = assertion.SourceSystem
            ?? throw new InvalidOperationException(
                "SourceSystem fehlt für ContactSource-Assertion (verwende 'ContactSource:{alias}' oder setze sourceSystem).");

        var csId = ctx.ResolveContactSourceId(null, sourceSystem);

        var entity = service.Retrieve(ContactSource, csId, new ColumnSet(assertion.Field));
        var actual = entity.Contains(assertion.Field) ? entity[assertion.Field] : null;

        ApplyOperator(assertion, actual, ctx, result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PlatformBridge-Assertions
    // ═══════════════════════════════════════════════════════════════════

    private void EvaluatePlatformBridgeAssertion(
        TestAssertion assertion, TestContext ctx,
        IOrganizationService service, AssertionResult result)
    {
        if (ctx.BridgeRecordIds.Count == 0)
            throw new InvalidOperationException("Keine PlatformBridge-Records im Kontext.");

        var bridgeId = ctx.BridgeRecordIds[ctx.BridgeRecordIds.Count - 1];
        var entity = service.Retrieve(PlatformBridge, bridgeId, new ColumnSet(assertion.Field));
        var actual = entity.Contains(assertion.Field) ? entity[assertion.Field] : null;

        ApplyOperator(assertion, actual, ctx, result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CdhLogging-Assertions
    // ═══════════════════════════════════════════════════════════════════

    private void EvaluateCdhLoggingAssertion(
        TestAssertion assertion, TestContext ctx,
        IOrganizationService service, AssertionResult result)
    {
        var logs = ctx.CdhLogs;

        if (logs.Count == 0 && ctx.ContactId.HasValue)
        {
            var query = new QueryExpression(CdhLogging)
            {
                ColumnSet = new ColumnSet(CdhLogName, CdhLogEntry.DiagnosticsFieldName, CdhLogCreatedOn),
                TopCount = 50
            };
            query.Criteria.AddCondition(
                CdhLogContactLookup, ConditionOperator.Equal, ctx.ContactId.Value);
            query.Criteria.AddCondition(
                CdhLogCreatedOn, ConditionOperator.GreaterEqual, ctx.TestStartUtc);
            query.Orders.Add(new OrderExpression(CdhLogCreatedOn, OrderType.Descending));

            logs = service.RetrieveMultiple(query).Entities.ToList();
        }

        switch (assertion.Operator.ToUpperInvariant())
        {
            case "EXISTS":
                result.Passed = logs.Count > 0;
                result.ExpectedDisplay = "Mindestens 1 CDH-Log-Eintrag";
                result.ActualDisplay = $"{logs.Count} Einträge";
                result.Message = result.Passed
                    ? $"OK: {logs.Count} CDH-Log-Einträge gefunden"
                    : "Keine CDH-Log-Einträge gefunden";
                break;

            case "CONTACTUPDATED":
                var expectedUpdated = string.Equals(
                    assertion.Value, "True", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(assertion.Value, "true", StringComparison.OrdinalIgnoreCase);
                var marker = $"ContactUpdated={expectedUpdated}";
                result.Passed = logs.Any(l =>
                    (l.GetAttributeValue<string>(CdhLogEntry.DiagnosticsFieldName) ?? "")
                        .Contains(marker, StringComparison.OrdinalIgnoreCase));
                result.ExpectedDisplay = marker;
                result.ActualDisplay = result.Passed ? "Gefunden" : "Nicht gefunden";
                result.Message = result.Passed
                    ? $"OK: {marker} in CDH-Logs gefunden"
                    : $"{marker} nicht in CDH-Logs gefunden";
                break;

            case "CONTAINS":
                var logColumn = ResolveLogField(assertion.LogField);
                var searchText = assertion.Value ?? "";
                result.Passed = logs.Any(l =>
                    (l.GetAttributeValue<string>(logColumn) ?? "")
                        .Contains(searchText, StringComparison.OrdinalIgnoreCase));
                result.ExpectedDisplay = $"CDH-Log {assertion.LogField ?? "Diagnostics"} enthält '{searchText}'";
                result.ActualDisplay = result.Passed ? "Gefunden" : "Nicht gefunden";
                result.Message = result.Passed
                    ? $"OK: '{searchText}' in CDH-Logs gefunden"
                    : $"'{searchText}' nicht in CDH-Logs gefunden";
                break;

            default:
                result.Passed = false;
                result.Message = $"Unbekannter CDH-Log-Operator: {assertion.Operator}";
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Generische Assertions (target = "Record" oder "Query")
    // ═══════════════════════════════════════════════════════════════════

    private void EvaluateGenericRecordAssertion(
        TestAssertion assertion, TestContext ctx,
        IOrganizationService service, AssertionResult result)
    {
        var refStr = assertion.RecordRef ?? "";

        // Strip {RECORD:...} wrapper if present
        if (refStr.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && refStr.EndsWith("}"))
            refStr = refStr.Substring("{RECORD:".Length, refStr.Length - "{RECORD:".Length - 1);

        if (string.IsNullOrEmpty(refStr))
            throw new InvalidOperationException("Record-Assertion benötigt 'recordRef'.");

        Entity entity;

        // Try FoundRecords first (has loaded fields from WaitForRecord)
        if (ctx.FoundRecords.TryGetValue(refStr, out var foundEntity))
        {
            // If the needed field is in the cached entity, use it directly
            if (foundEntity.Contains(assertion.Field))
            {
                entity = foundEntity;
            }
            else
            {
                // Reload with the specific field
                var entityName = assertion.Entity ?? ctx.ResolveRecordEntityName(refStr);
                entity = service.Retrieve(entityName, foundEntity.Id, new ColumnSet(assertion.Field));
            }
        }
        else
        {
            // Load from Records registry
            var recordId = ctx.ResolveRecordId(refStr);
            var entityName = assertion.Entity ?? ctx.ResolveRecordEntityName(refStr);
            entity = service.Retrieve(entityName, recordId, new ColumnSet(assertion.Field));
        }

        var actual = entity.Contains(assertion.Field) ? entity[assertion.Field] : null;
        ApplyOperator(assertion, actual, ctx, result);
    }

    private void EvaluateQueryAssertion(
        TestAssertion assertion, TestContext ctx,
        IOrganizationService service, AssertionResult result)
    {
        var entityName = assertion.Entity
            ?? throw new InvalidOperationException("Query-Assertion benötigt 'entity'.");
        var filters = assertion.AssertionFilter
            ?? throw new InvalidOperationException("Query-Assertion benötigt 'filter'.");

        // Resolve placeholders in filter values
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

        if (assertion.Operator.Equals("RecordCount", StringComparison.OrdinalIgnoreCase))
        {
            // Special: count all matching records
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
        ApplyOperator(assertion, actual, ctx, result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Operator-Auswertung (für Contact/ContactSource/PlatformBridge/Record/Query)
    // ═══════════════════════════════════════════════════════════════════

    private static void ApplyOperator(
        TestAssertion assertion, object? actual,
        TestContext ctx, AssertionResult result)
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

            case "DATESETRECENTLY":
                EvaluateDateSetRecently(actual, assertion.WithinSeconds ?? 120, result);
                break;

            case "UNCHANGED":
                EvaluateUnchanged(assertion.Field, actual, ctx, result);
                break;

            // FGTestTool-Kompatibilität: Changed-Operator (inverse von Unchanged)
            case "CHANGED":
                EvaluateUnchanged(assertion.Field, actual, ctx, result);
                result.Passed = !result.Passed;
                result.Message = result.Passed
                    ? $"OK: {assertion.Field} hat sich geändert"
                    : $"Feld {assertion.Field} ist unverändert (Änderung erwartet)";
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

    // ═══════════════════════════════════════════════════════════════════
    //  Spezial-Operatoren
    // ═══════════════════════════════════════════════════════════════════

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

    private static void EvaluateUnchanged(
        string field, object? currentValue,
        TestContext ctx, AssertionResult result)
    {
        if (ctx.CurrentContact == null)
        {
            result.Passed = false;
            result.Message = "Kein Contact-Snapshot vorhanden für Unchanged-Vergleich.";
            result.ExpectedDisplay = "<unverändert>";
            return;
        }

        var snapshotValue = ctx.CurrentContact.Contains(field)
            ? ctx.CurrentContact[field]
            : null;

        var snapshotStr = ExtractString(snapshotValue);
        var currentStr = ExtractString(currentValue);

        result.Passed = string.Equals(snapshotStr, currentStr, StringComparison.OrdinalIgnoreCase);
        result.ExpectedDisplay = $"Unverändert: {FormatValue(snapshotValue)}";
        result.ActualDisplay = FormatValue(currentValue);
        result.Message = result.Passed
            ? $"OK: {field} ist unverändert ({result.ActualDisplay})"
            : $"Feld {field} hat sich geändert: vorher={FormatValue(snapshotValue)}, jetzt={FormatValue(currentValue)}";
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Hilfsmethoden
    // ═══════════════════════════════════════════════════════════════════

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

    private static string ResolveLogField(string? logField)
    {
        return (logField?.ToUpperInvariant()) switch
        {
            "NAME" => CdhLogName,
            "DIAGNOSTICS" or null => CdhLogEntry.DiagnosticsFieldName,
            _ => logField!
        };
    }
}
