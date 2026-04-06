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
    // Web Resource format: {alias.id} for record ID, {alias.fields.fieldname} for field values
    private static readonly Regex AliasIdPattern = new(@"\{(\w+)\.id\}", RegexOptions.Compiled);
    private static readonly Regex AliasFieldPattern = new(@"\{(\w+)\.fields\.(\w+)\}", RegexOptions.Compiled);
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

        // {alias.fields.fieldname} -> Field value from FoundRecords (must come BEFORE {alias.id})
        result = AliasFieldPattern.Replace(result, m =>
        {
            var alias = m.Groups[1].Value;
            var field = m.Groups[2].Value;
            if (ctx.FoundRecords.TryGetValue(alias, out var entity) && entity.Contains(field))
            {
                var val = entity[field];
                if (val is EntityReference er) return er.Id.ToString();
                if (val is OptionSetValue osv) return osv.Value.ToString();
                return val?.ToString() ?? "";
            }
            return m.Value;
        });

        // {alias.id} -> Record GUID (Web Resource format, same as {RECORD:alias})
        result = AliasIdPattern.Replace(result, m =>
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

        // {GENERATED:name} -> generate once, reuse thereafter (type-aware)
        result = GeneratedPattern.Replace(result, m =>
        {
            var name = m.Groups[1].Value;
            if (!ctx.GeneratedValues.TryGetValue(name, out var existing))
            {
                existing = GenerateTypedValue(name);
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

    /// <summary>
    /// Generates a type-aware value for {GENERATED:name} placeholders.
    /// Known names (firstname, lastname, email, phone, company, text, guid) produce realistic data.
    /// Unknown names produce a random 8-char hex string.
    /// </summary>
    private string GenerateTypedValue(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "firstname": return _faker.Name.FirstName();
            case "lastname": return _faker.Name.LastName();
            case "email": return $"jbetest_{Guid.NewGuid().ToString("N").Substring(0, 8)}@example.com";
            case "phone": return _faker.Phone.PhoneNumber("+49 555 #######");
            case "company": return $"JBE Test {_faker.Company.CompanyName()}";
            case "text": return $"JBE Test {Guid.NewGuid().ToString("N").Substring(0, 12)}";
            case "guid": return Guid.NewGuid().ToString("N").Substring(0, 8);
            case "city": return _faker.Address.City();
            case "street": return _faker.Address.StreetAddress();
            case "zip": return _faker.Address.ZipCode();
            case "jobtitle": return _faker.Name.JobTitle();
            case "website": return $"https://jbetest-{Guid.NewGuid().ToString("N").Substring(0, 8)}.example.com";
            default: return Guid.NewGuid().ToString("N").Substring(0, 8);
        }
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
