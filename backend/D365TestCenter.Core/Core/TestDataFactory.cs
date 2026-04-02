using Bogus;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace itt.IntegrationTests.Core;

/// <summary>
/// Erzeugt Testdaten in zwei Modi:
/// A) Bogus — zufällige, realistische Daten (deutschsprachig)
/// B) Template+Platzhalter — deterministische Daten mit Platzhalter-Ersetzung
/// </summary>
public sealed class TestDataFactory
{
    private static readonly Regex FakerPattern = new(@"\{FAKER:(\w+)\}", RegexOptions.Compiled);

    private readonly Faker _faker = new("de");

    // ═══════════════════════════════════════════════════════════════════
    //  Modus A: Bogus-basierte Generierung
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Generiert zufällige, realistische Kontaktdaten.</summary>
    public Dictionary<string, object?> GenerateBogusContactData()
    {
        var firstName = _faker.Name.FirstName();
        var lastName = _faker.Name.LastName();

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["firstname"] = firstName,
            ["lastname"] = lastName,
            ["emailaddress1"] = _faker.Internet.Email(firstName, lastName),
            ["telephone1"] = _faker.Phone.PhoneNumber("+49 ### #######"),
            ["jobtitle"] = _faker.PickRandom(
                "EDI responsible person",
                "Key Account Manager",
                "Managing Director",
                "IT Director",
                "Procurement Manager")
        };
    }

    /// <summary>Generiert zufällige Firmendaten.</summary>
    public Dictionary<string, object?> GenerateBogusAccountData()
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = _faker.Company.CompanyName()
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Modus B: Template+Platzhalter-Ersetzung
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ersetzt Platzhalter in einem Template-String.
    /// Unterstützte Platzhalter:
    ///   {TIMESTAMP}    → yyyyMMdd_HHmmss_fff
    ///   {TESTID}       → Test-ID aus dem Kontext
    ///   {GUID}         → 8-stellige GUID
    ///   {PREFIX}       → "ITT"
    ///   {NOW_UTC}      → ISO 8601 UTC
    ///   {CONTACT_ID}   → GUID des erstellten Kontakts
    ///   {FAKER:X}      → Bogus-generierter Wert (FirstName, LastName, Company, Email, Phone)
    /// </summary>
    public string ResolvePlaceholders(string template, TestContext ctx)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        var result = template
            .Replace("{TIMESTAMP}", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"))
            .Replace("{TESTID}", ctx.TestId)
            .Replace("{GUID}", Guid.NewGuid().ToString("N").Substring(0, 8))
            .Replace("{PREFIX}", "ITT")
            .Replace("{NOW_UTC}", DateTime.UtcNow.ToString("O"))
            .Replace("{CONTACT_ID}", ctx.ContactId?.ToString() ?? "");

        result = FakerPattern.Replace(result, m => ResolveFakerToken(m.Groups[1].Value));

        return result;
    }

    /// <summary>
    /// Ersetzt Platzhalter in allen String-Werten eines Dictionaries.
    /// JToken-Strings werden dabei zu regulären Strings aufgelöst.
    /// </summary>
    public Dictionary<string, object?> ResolveTemplateData(
        Dictionary<string, object?> data, TestContext ctx)
    {
        if (data == null || data.Count == 0)
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var resolved = new Dictionary<string, object?>(data.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in data)
        {
            resolved[kvp.Key] = kvp.Value switch
            {
                string s => ResolvePlaceholders(s, ctx),
                JToken jt when jt.Type == JTokenType.String
                    => ResolvePlaceholders(jt.Value<string>()!, ctx),
                _ => kvp.Value
            };
        }

        return resolved;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Intern
    // ═══════════════════════════════════════════════════════════════════

    private string ResolveFakerToken(string tokenName)
    {
        return tokenName switch
        {
            "FirstName" => _faker.Name.FirstName(),
            "LastName" => _faker.Name.LastName(),
            "Company" => _faker.Company.CompanyName(),
            "Email" => _faker.Internet.Email(),
            "Phone" => _faker.Phone.PhoneNumber("+49 ### #######"),
            _ => $"{{FAKER:{tokenName}}}"
        };
    }
}
