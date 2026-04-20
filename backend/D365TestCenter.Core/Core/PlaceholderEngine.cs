using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core;

/// <summary>
/// Zentrale Platzhalter-Engine. Ersetzt Platzhalter in Strings und Dictionaries.
/// Generierte Werte werden pro Testfall gespeichert und wiederverwendet.
/// </summary>
public sealed class PlaceholderEngine
{
    private static readonly Regex GeneratedPattern = new(@"\{GENERATED:(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex RecordPattern = new(@"\{RECORD:(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex ResultPattern = new(@"\{RESULT:(\w+)\.(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex AliasIdPattern = new(@"\{(\w+)\.id\}", RegexOptions.Compiled);
    private static readonly Regex AliasFieldPattern = new(@"\{(\w+)\.fields\.(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex RowPattern = new(@"\{ROW:(\w+)\}", RegexOptions.Compiled);

    private readonly Bogus.Faker _faker = new Bogus.Faker("de");

    /// <summary>
    /// Löst alle Platzhalter in einem Template-String auf.
    /// </summary>
    public string Resolve(string template, TestContext ctx)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        var result = template;

        // Statische Platzhalter
        var now = DateTime.UtcNow;
        result = result
            .Replace("{TESTID}", ctx.TestId)
            .Replace("{PREFIX}", "ITT")
            .Replace("{TIMESTAMP}", now.ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_COMPACT}", now.ToString("yyyyMMddHHmmssfff"))
            .Replace("{TIMESTAMP_MINUS_1H}", now.AddHours(-1).ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_MINUS_2H}", now.AddHours(-2).ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_MINUS_3H}", now.AddHours(-3).ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_MINUS_30M}", now.AddMinutes(-30).ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_MINUS_1D}", now.AddDays(-1).ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_PLUS_1H}", now.AddHours(1).ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_PLUS_2H}", now.AddHours(2).ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_PLUS_1D}", now.AddDays(1).ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TIMESTAMP_ISO}", now.ToString("O"))
            .Replace("{GUID}", Guid.NewGuid().ToString("N").Substring(0, 8))
            .Replace("{NOW_UTC}", now.ToString("O"))
            .Replace("{NOW_MINUS_1H}", now.AddHours(-1).ToString("O"));

        // Legacy-Aliase {CONTACT_ID} und {ACCOUNT_ID} fuer JS-Engine-Kompatibilitaet:
        // Erster registrierter Record vom entsprechenden Typ wird genutzt.
        // Neue Testfaelle sollten {alias.id} verwenden.
        if (result.Contains("{CONTACT_ID}"))
        {
            foreach (var kv in ctx.Records)
            {
                if (kv.Value.EntityName.Equals("contact", StringComparison.OrdinalIgnoreCase))
                {
                    result = result.Replace("{CONTACT_ID}", kv.Value.Id.ToString());
                    break;
                }
            }
        }
        if (result.Contains("{ACCOUNT_ID}"))
        {
            foreach (var kv in ctx.Records)
            {
                if (kv.Value.EntityName.Equals("account", StringComparison.OrdinalIgnoreCase))
                {
                    result = result.Replace("{ACCOUNT_ID}", kv.Value.Id.ToString());
                    break;
                }
            }
        }

        // {RECORD:alias} -> Record-GUID aus dem generischen Registry
        result = RecordPattern.Replace(result, m =>
        {
            var alias = m.Groups[1].Value;
            if (ctx.Records.TryGetValue(alias, out var record))
                return record.Id.ToString();
            return m.Value;
        });

        // {alias.fields.fieldname} -> Feldwert aus FoundRecords (VOR {alias.id})
        result = AliasFieldPattern.Replace(result, m =>
        {
            var alias = m.Groups[1].Value;
            var field = m.Groups[2].Value;
            if (ctx.FoundRecords.TryGetValue(alias, out var entity))
            {
                if (entity.Contains(field))
                {
                    var val = entity[field];
                    if (val is EntityReference er) return er.Id.ToString();
                    if (val is OptionSetValue osv) return osv.Value.ToString();
                    return val?.ToString() ?? "";
                }
                throw new InvalidOperationException(
                    $"Platzhalter '{m.Value}': Feld '{field}' nicht im geladenen Record '{alias}'. " +
                    $"Geladene Felder: [{string.Join(", ", entity.Attributes.Keys)}]");
            }
            if (ctx.Records.ContainsKey(alias))
                throw new InvalidOperationException(
                    $"Platzhalter '{m.Value}' konnte nicht aufgeloest werden: " +
                    $"Alias '{alias}' existiert in Records, aber nicht in FoundRecords. " +
                    $"Verwende 'columns' auf der Precondition/CreateRecord oder " +
                    $"einen 'RetrieveRecord'-Step um Feldwerte nach dem Create zu laden.");
            return m.Value; // Alias unbekannt: Platzhalter unveraendert lassen (koennte spaeter aufgeloest werden)
        });

        // {alias.id} -> Record-GUID (Web Resource Format)
        result = AliasIdPattern.Replace(result, m =>
        {
            var alias = m.Groups[1].Value;
            if (ctx.Records.TryGetValue(alias, out var record))
                return record.Id.ToString();
            return m.Value;
        });

        // {GENERATED:name} -> Typ-spezifische Generierung, einmal erzeugt und wiederverwendet
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

        // {RESULT:alias.field} -> Feldwert aus einem gefundenen Record
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

        // {ROW:fieldname} -> Wert aus der aktuellen Datenzeile (datengetriebene Tests)
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
    /// Löst Platzhalter in allen String-Werten eines Dictionaries auf.
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
    /// Erzeugt typ-spezifische Werte für {GENERATED:name}-Platzhalter.
    /// Bekannte Namen erzeugen realistische Daten, unbekannte einen 8-Zeichen-Hex-String.
    /// </summary>
    private string GenerateTypedValue(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "firstname": return _faker.Name.FirstName();
            case "lastname": return _faker.Name.LastName();
            case "email": return $"jbetest_{Guid.NewGuid().ToString("N").Substring(0, 8)}@example.com";
            case "phone": return _faker.Phone.PhoneNumber("+49 555 #######");
            case "mobile": return _faker.Phone.PhoneNumber("+49 151 ########");
            case "phone_international": return _faker.Phone.PhoneNumber("+## ### ########");
            case "number": return _faker.Random.Number(10000, 99999).ToString();
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

    private static string FormatValueForPlaceholder(object? value)
    {
        if (value == null) return "";
        if (value is string s) return s;
        if (value is OptionSetValue osv) return osv.Value.ToString();
        if (value is EntityReference er) return er.Id.ToString();
        if (value is DateTime dt) return dt.ToString("O");
        if (value is Money m) return m.Value.ToString();
        if (value is bool b) return b.ToString().ToLowerInvariant();
        return value.ToString() ?? "";
    }
}
