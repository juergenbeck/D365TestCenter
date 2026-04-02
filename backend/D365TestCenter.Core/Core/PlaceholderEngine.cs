using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core;

/// <summary>
/// Central placeholder resolution engine. Replaces placeholders in strings and dictionaries.
/// Generated values are stored and reusable across steps and assertions within a test case.
/// </summary>
public sealed class PlaceholderEngine
{
    private static readonly Regex GeneratedPattern = new(@"\{GENERATED:(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex RecordPattern = new(@"\{RECORD:(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex ResultPattern = new(@"\{RESULT:(\w+)\.(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex FakerPattern = new(@"\{FAKER:(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex CsPattern = new(@"\{CS:(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex BridgePattern = new(@"\{BRIDGE:(\d+)\}", RegexOptions.Compiled);
    private static readonly Regex RowPattern = new(@"\{ROW:(\w+)\}", RegexOptions.Compiled);

    private readonly Bogus.Faker _faker = new Bogus.Faker("de");

    /// <summary>
    /// Resolves all placeholders in a template string using the test context.
    /// </summary>
    public string Resolve(string template, TestContext ctx)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        var result = template;

        // Static/context placeholders
        result = result
            .Replace("{CONTACT_ID}", ctx.ContactId?.ToString() ?? "")
            .Replace("{ACCOUNT_ID}", ctx.AccountId?.ToString() ?? "")
            .Replace("{TESTID}", ctx.TestId)
            .Replace("{PREFIX}", "ITT")
            .Replace("{TIMESTAMP}", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_COMPACT}", DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"))
            .Replace("{GUID}", Guid.NewGuid().ToString("N").Substring(0, 8))
            .Replace("{NOW_UTC}", DateTime.UtcNow.ToString("O"));

        // {CS:alias} -> ContactSource GUID
        result = CsPattern.Replace(result, m =>
        {
            var alias = m.Groups[1].Value;
            if (ctx.ContactSourceIds.TryGetValue(alias, out var csId))
                return csId.ToString();
            return m.Value;
        });

        // {RECORD:alias} -> Record GUID from generic registry
        result = RecordPattern.Replace(result, m =>
        {
            var alias = m.Groups[1].Value;
            if (ctx.Records.TryGetValue(alias, out var record))
                return record.Id.ToString();
            if (ctx.ContactSourceIds.TryGetValue(alias, out var csId))
                return csId.ToString();
            return m.Value;
        });

        // {BRIDGE:n} -> Bridge record GUID by index
        result = BridgePattern.Replace(result, m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var idx) && idx < ctx.BridgeRecordIds.Count)
                return ctx.BridgeRecordIds[idx].ToString();
            return m.Value;
        });

        // {GENERATED:name} -> generate once, reuse thereafter
        result = GeneratedPattern.Replace(result, m =>
        {
            var name = m.Groups[1].Value;
            if (!ctx.GeneratedValues.TryGetValue(name, out var existing))
            {
                existing = Guid.NewGuid().ToString("N").Substring(0, 8);
                ctx.GeneratedValues[name] = existing;
            }
            return existing;
        });

        // {RESULT:alias.field} -> field value from a found record
        result = ResultPattern.Replace(result, m =>
        {
            var alias = m.Groups[1].Value;
            var field = m.Groups[2].Value;
            if (ctx.FoundRecords.TryGetValue(alias, out var entity))
            {
                var val = entity.Contains(field) ? entity[field] : null;
                return FormatValueForPlaceholder(val);
            }
            return m.Value;
        });

        // {FAKER:X} -> Bogus-generated value
        result = FakerPattern.Replace(result, m => ResolveFakerToken(m.Groups[1].Value));

        // {ROW:fieldname} -> data-driven test row value (if set on context)
        if (ctx.CurrentDataRow != null)
        {
            result = RowPattern.Replace(result, m =>
            {
                var fieldName = m.Groups[1].Value;
                if (ctx.CurrentDataRow.TryGetValue(fieldName, out var rowVal))
                    return rowVal?.ToString() ?? "";
                return m.Value;
            });
        }

        return result;
    }

    /// <summary>
    /// Resolves placeholders in all string values of a dictionary.
    /// JToken strings are converted to regular strings.
    /// </summary>
    public Dictionary<string, object?> ResolveAll(
        Dictionary<string, object?> fields, TestContext ctx)
    {
        if (fields == null || fields.Count == 0)
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var resolved = new Dictionary<string, object?>(fields.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in fields)
        {
            resolved[kvp.Key] = kvp.Value switch
            {
                string s => Resolve(s, ctx),
                JToken jt when jt.Type == JTokenType.String
                    => Resolve(jt.Value<string>()!, ctx),
                _ => kvp.Value
            };
        }

        return resolved;
    }

    private string ResolveFakerToken(string tokenName)
    {
        switch (tokenName)
        {
            case "FirstName": return _faker.Name.FirstName();
            case "LastName": return _faker.Name.LastName();
            case "Company": return _faker.Company.CompanyName();
            case "Email": return _faker.Internet.Email();
            case "Phone": return _faker.Phone.PhoneNumber("+49 ### #######");
            case "City": return _faker.Address.City();
            case "Street": return _faker.Address.StreetAddress();
            case "Zip": return _faker.Address.ZipCode();
            default: return $"{{FAKER:{tokenName}}}";
        }
    }

    private static string FormatValueForPlaceholder(object? value)
    {
        if (value == null) return "";
        if (value is string s) return s;
        if (value is OptionSetValue osv) return osv.Value.ToString();
        if (value is EntityReference er) return er.Id.ToString();
        if (value is DateTime dt) return dt.ToString("O");
        if (value is bool b) return b.ToString();
        return value.ToString() ?? "";
    }
}
