using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core;

/// <summary>
/// Generic polling-based waiter: waits until a record matching given filter criteria
/// appears in any Dataverse table, or until a known record reaches a specific field value.
/// </summary>
public sealed class GenericRecordWaiter
{
    /// <summary>
    /// Polls until a record in the specified entity matches all filter conditions.
    /// Returns the found entity or null on timeout.
    /// </summary>
    public Entity? WaitForRecord(
        IOrganizationService service,
        string entityName,
        List<FilterCondition> filters,
        string[]? columns,
        int timeoutSeconds = 120,
        int pollingIntervalMs = 2000,
        Action<string>? log = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        // Initial delay to give async plugins time to fire
        Thread.Sleep(Math.Min(pollingIntervalMs, 1500));

        while (DateTime.UtcNow < deadline)
        {
            var query = BuildQuery(entityName, filters, columns);
            query.TopCount = 1;

            var results = service.RetrieveMultiple(query);
            if (results.Entities.Count > 0)
            {
                log?.Invoke($"WaitForRecord: Record in '{entityName}' gefunden nach " +
                    $"{(DateTime.UtcNow.AddSeconds(-timeoutSeconds) - deadline.AddSeconds(-timeoutSeconds)).TotalSeconds:F1}s");
                return results.Entities[0];
            }

            Thread.Sleep(pollingIntervalMs);
        }

        log?.Invoke($"WaitForRecord: Timeout ({timeoutSeconds}s) für '{entityName}' erreicht");
        return null;
    }

    /// <summary>
    /// Polls until a known record has a specific field value.
    /// Returns true when the value matches, false on timeout.
    /// </summary>
    public bool WaitForFieldValue(
        IOrganizationService service,
        string entityName,
        Guid recordId,
        string fieldName,
        object expectedValue,
        int timeoutSeconds = 120,
        int pollingIntervalMs = 2000,
        Action<string>? log = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        Thread.Sleep(Math.Min(pollingIntervalMs, 1500));

        while (DateTime.UtcNow < deadline)
        {
            var record = service.Retrieve(entityName, recordId, new ColumnSet(fieldName));
            var actual = record.Contains(fieldName) ? record[fieldName] : null;

            if (ValuesMatch(actual, expectedValue))
            {
                log?.Invoke($"WaitForFieldValue: '{fieldName}' hat den erwarteten Wert erreicht");
                return true;
            }

            Thread.Sleep(pollingIntervalMs);
        }

        log?.Invoke($"WaitForFieldValue: Timeout ({timeoutSeconds}s) für '{fieldName}' auf '{entityName}' erreicht");
        return false;
    }

    /// <summary>
    /// Builds a QueryExpression from a list of FilterConditions.
    /// </summary>
    public static QueryExpression BuildQuery(
        string entityName,
        List<FilterCondition> filters,
        string[]? columns)
    {
        var query = new QueryExpression(entityName)
        {
            ColumnSet = columns != null && columns.Length > 0
                ? new ColumnSet(columns)
                : new ColumnSet(true)
        };

        foreach (var filter in filters)
        {
            var op = ResolveOperator(filter.Operator);
            var value = ConvertFilterValue(filter.Value);

            if (op == ConditionOperator.Null || op == ConditionOperator.NotNull)
            {
                query.Criteria.AddCondition(filter.Field, op);
            }
            else if (op == ConditionOperator.In || op == ConditionOperator.NotIn)
            {
                // Value should be a comma-separated string or array
                var values = ParseInValues(value);
                query.Criteria.AddCondition(filter.Field, op, values);
            }
            else
            {
                query.Criteria.AddCondition(filter.Field, op, value);
            }
        }

        return query;
    }

    private static ConditionOperator ResolveOperator(string op)
    {
        switch ((op ?? "eq").ToLowerInvariant())
        {
            case "eq":
            case "equals":
                return ConditionOperator.Equal;
            case "ne":
            case "notequals":
                return ConditionOperator.NotEqual;
            case "gt":
            case "greaterthan":
                return ConditionOperator.GreaterThan;
            case "ge":
            case "greaterorequal":
                return ConditionOperator.GreaterEqual;
            case "lt":
            case "lessthan":
                return ConditionOperator.LessThan;
            case "le":
            case "lessorequal":
                return ConditionOperator.LessEqual;
            case "like":
                return ConditionOperator.Like;
            case "contains":
                return ConditionOperator.Contains;
            case "beginswith":
            case "startswith":
                return ConditionOperator.BeginsWith;
            case "endswith":
                return ConditionOperator.EndsWith;
            case "null":
            case "isnull":
                return ConditionOperator.Null;
            case "notnull":
            case "isnotnull":
                return ConditionOperator.NotNull;
            case "in":
                return ConditionOperator.In;
            case "notin":
                return ConditionOperator.NotIn;
            default:
                throw new InvalidOperationException($"Unbekannter Filter-Operator: '{op}'");
        }
    }

    private static object ConvertFilterValue(object? value)
    {
        if (value == null) return DBNull.Value;
        if (value is Newtonsoft.Json.Linq.JToken jt)
        {
            switch (jt.Type)
            {
                case Newtonsoft.Json.Linq.JTokenType.Integer: return (int)(long)jt;
                case Newtonsoft.Json.Linq.JTokenType.Float: return (decimal)(double)jt;
                case Newtonsoft.Json.Linq.JTokenType.Boolean: return (bool)jt;
                case Newtonsoft.Json.Linq.JTokenType.String: return (string)jt!;
                case Newtonsoft.Json.Linq.JTokenType.Null: return DBNull.Value;
                default: return jt.ToString();
            }
        }
        return value;
    }

    private static object[] ParseInValues(object value)
    {
        var str = value?.ToString() ?? "";
        return str.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool ValuesMatch(object? actual, object expectedRaw)
    {
        var expected = ConvertFilterValue(expectedRaw);
        if (actual == null && (expected == null || expected == DBNull.Value)) return true;
        if (actual == null || expected == null) return false;

        // OptionSetValue comparison
        if (actual is OptionSetValue osv)
        {
            if (expected is int i) return osv.Value == i;
            if (expected is long l) return osv.Value == (int)l;
            if (int.TryParse(expected.ToString(), out var parsed)) return osv.Value == parsed;
            return false;
        }

        // EntityReference comparison
        if (actual is EntityReference er)
        {
            if (expected is Guid g) return er.Id == g;
            if (Guid.TryParse(expected.ToString(), out var parsedGuid)) return er.Id == parsedGuid;
            return false;
        }

        // String comparison (case-insensitive, trimmed)
        var actualStr = actual.ToString()?.Trim();
        var expectedStr = expected.ToString()?.Trim();
        return string.Equals(actualStr, expectedStr, StringComparison.OrdinalIgnoreCase);
    }
}
