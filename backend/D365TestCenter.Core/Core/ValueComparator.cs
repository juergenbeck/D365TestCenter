using System.Globalization;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core;

/// <summary>
/// Geteilter Vergleichskern für feldbasierte Operatoren. EINE Quelle der Wahrheit
/// für <see cref="AssertionEngine"/> (Assert-Action) UND die Step-Condition
/// (ADR-0011, konditionale Step-Ausführung): beide vergleichen denselben Satz
/// Operatoren mit identischer Semantik (case-insensitiv, native + String-Inputs).
///
/// Extrahiert aus <see cref="AssertionEngine"/> (Session 2026-06-21). Umfang = exakt die
/// feldbasierten Assert-Operatoren: Equals, NotEquals, IsNull, IsNotNull, Contains,
/// StartsWith, EndsWith, GreaterThan, LessThan. NICHT enthalten (bewusst):
///  - DateSetRecently: zeit-toleranz-spezifisch, bleibt Assert-only.
///  - Exists/NotExists/RecordCount: Query-only, kein Wert-Vergleich.
///  - In/NotIn: existieren nur im Filter-Set (GenericRecordWaiter), nie in der
///    AssertionEngine; sie hier aufzunehmen wäre eine zweite Comparator-Quelle.
/// </summary>
public static class ValueComparator
{
    /// <summary>
    /// Die von diesem Comparator unterstützten feldbasierten Operatoren (kanonische
    /// Schreibweise). Quelle für den PackValidator-Lint (CONDITION_MALFORMED).
    /// </summary>
    public static readonly IReadOnlyCollection<string> SupportedOperators = new[]
    {
        "Equals", "NotEquals", "IsNull", "IsNotNull",
        "Contains", "StartsWith", "EndsWith", "GreaterThan", "LessThan"
    };

    /// <summary>
    /// Wertet einen feldbasierten Vergleich aus. <paramref name="actual"/> darf ein
    /// nativer Dataverse-Wert (Assert) oder ein aufgelöster String (Condition) sein;
    /// <paramref name="expected"/> ist der erwartete Wert als String (Platzhalter bereits
    /// aufgelöst). Liefert true, wenn der Operator behandelt wurde (Ergebnis in
    /// <paramref name="passed"/>), und false bei einem nicht-feldbasierten/unbekannten
    /// Operator -- der Aufrufer entscheidet dann (DateSetRecently in AssertionEngine,
    /// Error in der Condition-Auswertung).
    /// </summary>
    public static bool TryEvaluate(string? @operator, object? actual, string? expected, out bool passed)
    {
        passed = false;
        switch ((@operator ?? "").ToUpperInvariant())
        {
            case "EQUALS":
                passed = CompareValues(actual, expected);
                return true;

            case "NOTEQUALS":
                passed = !CompareValues(actual, expected);
                return true;

            case "ISNULL":
                passed = IsNullOrEmpty(actual);
                return true;

            case "ISNOTNULL":
                passed = !IsNullOrEmpty(actual);
                return true;

            case "CONTAINS":
                passed = (ExtractString(actual) ?? "").Contains(
                    expected ?? "", StringComparison.OrdinalIgnoreCase);
                return true;

            case "STARTSWITH":
                passed = (ExtractString(actual) ?? "").StartsWith(
                    expected ?? "", StringComparison.OrdinalIgnoreCase);
                return true;

            case "ENDSWITH":
                passed = (ExtractString(actual) ?? "").EndsWith(
                    expected ?? "", StringComparison.OrdinalIgnoreCase);
                return true;

            case "GREATERTHAN":
                passed = TryCompareOrdered(actual, expected, out var gtCmp) && gtCmp > 0;
                return true;

            case "LESSTHAN":
                passed = TryCompareOrdered(actual, expected, out var ltCmp) && ltCmp < 0;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Normalisiert einen DateTime auf UTC. Dataverse liefert Zeitwerte mit
    /// Kind=Unspecified; diese sind bereits UTC und dürfen NICHT über
    /// ToUniversalTime() laufen, das sie als Lokalzeit interpretiert und den
    /// Offset abzieht (beobachtet als +7200s-Drift in CEST, FB-44). Nur echte
    /// Local-Werte werden umgerechnet. Einzige Quelle für alle zeitbasierten
    /// Operatoren (DateSetRecently in der AssertionEngine, GreaterThan/LessThan hier).
    /// </summary>
    public static DateTime NormalizeToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        _ => dt.ToUniversalTime()
    };

    /// <summary>
    /// Geordneter Vergleich für GreaterThan/LessThan. Versucht nacheinander:
    /// decimal (invariant), DateTime (RoundtripKind, invariant), dann String
    /// (Ordinal, case-insensitive). Liefert false wenn beide Werte null oder
    /// der Actualwert nicht extrahierbar ist. -1 actual&lt;expected, 0 gleich,
    /// +1 actual&gt;expected.
    /// </summary>
    public static bool TryCompareOrdered(object? actual, string? expected, out int comparison)
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
            comparison = NormalizeToUtc(actualDt).CompareTo(NormalizeToUtc(expectedDt));
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
            comparison = NormalizeToUtc(ad).CompareTo(NormalizeToUtc(ed));
            return true;
        }

        comparison = string.Compare(actualStr, expected, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>
    /// Gleichheitsvergleich: extrahiert den Actualwert als String und vergleicht
    /// getrimmt, case-insensitiv. Leerer Erwartungswert matcht Whitespace-Actual.
    /// Boolean wird über ExtractString zu "True"/"False" -- der case-insensitive
    /// Vergleich matcht daher auch gegen "true"/"false" (ADR-0011 Korrektur 2).
    /// </summary>
    public static bool CompareValues(object? actual, string? expected)
    {
        var actualStr = ExtractString(actual);

        if (actualStr == null && expected == null) return true;
        if (actualStr == null || expected == null) return false;
        if (expected == "" && string.IsNullOrWhiteSpace(actualStr)) return true;

        return string.Equals(
            actualStr.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True wenn der Wert null oder ein reiner Whitespace-String ist.</summary>
    public static bool IsNullOrEmpty(object? value)
    {
        if (value == null) return true;
        if (value is string s) return string.IsNullOrWhiteSpace(s);
        return false;
    }

    /// <summary>
    /// Zieht einen vergleichbaren String aus einem nativen Dataverse-Wert. OptionSetValue
    /// -> Zahl, EntityReference -> GUID, Money -> Betrag, DateTime -> ISO-O, bool -> "True"/"False".
    /// </summary>
    public static string? ExtractString(object? value)
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
}
