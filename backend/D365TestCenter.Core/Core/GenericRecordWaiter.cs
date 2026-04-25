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
    /// <param name="orderBy">Optional comma-separated ordering, e.g. "modifiedon asc, createdon desc". Default asc if direction omitted.</param>
    /// <param name="top">Optional max result count. Default 1.</param>
    public Entity? WaitForRecord(
        IOrganizationService service,
        string entityName,
        List<FilterCondition> filters,
        string[]? columns,
        int timeoutSeconds = 120,
        int pollingIntervalMs = 2000,
        Action<string>? log = null,
        string? orderBy = null,
        int? top = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        // Initial delay to give async plugins time to fire
        Thread.Sleep(Math.Min(pollingIntervalMs, 1500));

        while (DateTime.UtcNow < deadline)
        {
            var query = BuildQuery(entityName, filters, columns, orderBy, top ?? 1);

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
    /// <param name="orderBy">Optional comma-separated order expression, OData style
    /// ("modifiedon asc, createdon desc"). Default sort direction is ascending
    /// if only a field name is given.</param>
    /// <param name="topCount">Optional TopCount override. If null, TopCount stays
    /// at default (QueryExpression default = unbounded). WaitForRecord passes 1.</param>
    public static QueryExpression BuildQuery(
        string entityName,
        List<FilterCondition> filters,
        string[]? columns,
        string? orderBy = null,
        int? topCount = null)
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

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            foreach (var rawToken in orderBy!.Split(','))
            {
                var token = rawToken.Trim();
                if (string.IsNullOrEmpty(token)) continue;
                var parts = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                    throw new InvalidOperationException($"Ungueltiger orderBy-Token: '{token}'");
                var field = parts[0];
                var desc = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
                if (parts.Length > 1 && !desc && !parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Ungueltige Sortierrichtung '{parts[1]}' in orderBy-Token '{token}'. Erlaubt: asc, desc.");
                query.AddOrder(field, desc ? OrderType.Descending : OrderType.Ascending);
            }
        }

        if (topCount.HasValue) query.TopCount = topCount.Value;

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
                case Newtonsoft.Json.Linq.JTokenType.String: return ConvertString((string)jt!);
                case Newtonsoft.Json.Linq.JTokenType.Null: return DBNull.Value;
                default: return jt.ToString();
            }
        }
        if (value is string s) return ConvertString(s);
        return value;
    }

    /// <summary>Tries to convert string values to their native types (Guid, int, decimal, bool).</summary>
    private static object ConvertString(string s)
    {
        if (Guid.TryParse(s, out var guid)) return guid;
        if (int.TryParse(s, out var intVal)) return intVal;
        if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var decVal)) return decVal;
        if (bool.TryParse(s, out var boolVal)) return boolVal;
        return s;
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
