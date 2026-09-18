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
        // EINE Zahl-Quelle mit CompareValues (Equals/NotEquals): TryExtractNumber (FB-52).
        if (TryExtractNumber(actual, out var actualNum)
            && decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var expectedNum))
        {
            comparison = actualNum.CompareTo(expectedNum);
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
        // Numerische Gleichheit für native Zahl-Typen (Money/Decimal/Double/Float/
        // OptionSet/Integer): Gleichheit auf einem numerischen Feld ist numerisch, nicht
        // textuell. Das löst zwei Defekte in einem (FB-52): die Culture-Abhängigkeit
        // (Dezimal-Komma unter de-DE/de-CH) UND die Skalierung aus Dataverse -- ein
        // retrievter Money trägt die Feld-Precision (z.B. "1000.0000000000"), die ein
        // reiner Stringvergleich gegen "1000" fälschlich als FAIL wertet. Spiegelt die
        // numerische Semantik von TryCompareOrdered (GreaterThan/LessThan). String-actuals
        // (Text-Felder, Condition-Pfad) bleiben bewusst textuell -- "00123" ist nicht "123".
        if (expected != null
            && TryExtractNumber(actual, out var actualNum)
            && decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out var expectedNum))
        {
            return actualNum == expectedNum;
        }

        var actualStr = ExtractString(actual);

        if (actualStr == null && expected == null) return true;
        if (actualStr == null || expected == null) return false;
        if (expected == "" && string.IsNullOrWhiteSpace(actualStr)) return true;

        return string.Equals(
            actualStr.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extrahiert einen nativen numerischen Dataverse-Wert als decimal (Money, OptionSetValue,
    /// int, long, decimal, double, float). Liefert false für alle anderen Typen (inkl. String):
    /// String-actuals werden bewusst NICHT als Zahl interpretiert, damit Text-Felder textuell
    /// vergleichen. EINE Zahl-Quelle für CompareValues (Equals/NotEquals) und TryCompareOrdered
    /// (GreaterThan/LessThan), beide dadurch kulturunabhängig und skalierungs-robust (FB-52).
    /// </summary>
    public static bool TryExtractNumber(object? value, out decimal number)
    {
        switch (value)
        {
            case Money m: number = m.Value; return true;
            case OptionSetValue osv: number = osv.Value; return true;
            case int i: number = i; return true;
            case long l: number = l; return true;
            case decimal d: number = d; return true;
            case double dbl: number = (decimal)dbl; return true;
            case float f: number = (decimal)f; return true;
            default: number = 0m; return false;
        }
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
    ///
    /// KULTURUNABHÄNGIG (InvariantCulture): Der Comparator vergleicht Maschinenwerte gegen den
    /// erwarteten String aus dem Pack, keine lokalisierte UI-Anzeige. Numerische Typen MÜSSEN
    /// invariant (Dezimal-Punkt) formatiert werden, sonst bricht Contains/StartsWith/EndsWith auf
    /// einem Money-/Decimal-Feld unter einer Komma-Culture (de-DE/de-CH): decimal.ToString() ohne
    /// CultureInfo liefert dort "1000,5" statt "1000.5" (FB-52). Gilt für ALLE Aufrufpfade,
    /// besonders den Plugin-/Worker-Pfad (geteilter Core, ADR-0003), dessen Server-Culture nicht
    /// kontrollierbar ist. Equals/NotEquals laufen vorrangig über den numerischen Pfad
    /// (TryExtractNumber); diese String-Form ist der Fallback und der Pfad der Substring-Operatoren.
    /// </summary>
    public static string? ExtractString(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            OptionSetValue osv => osv.Value.ToString(CultureInfo.InvariantCulture),
            EntityReference er => er.Id.ToString(),
            Money m => m.Value.ToString(CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            bool b => b.ToString(),
            JToken jt => jt.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }
}
