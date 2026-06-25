using System;
using System.Globalization;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für den geteilten <see cref="ValueComparator"/> (ADR-0011). Pinnt die
/// Operator-Semantik, die Assert UND Step-Condition teilen: jeder feldbasierte
/// Operator gegen native Dataverse-Werte UND gegen bereits aufgelöste Strings
/// (der Condition-Pfad liefert Strings aus der PlaceholderEngine). Insbesondere
/// der ADR-0011-Korrektur-2-Fall: ein Boolean wird als "True"/"False" (groß)
/// formatiert und muss case-insensitiv gegen "true"/"false" matchen.
/// </summary>
public class ValueComparatorTests
{
    private static bool Eval(string op, object? actual, string? expected)
    {
        var handled = ValueComparator.TryEvaluate(op, actual, expected, out var passed);
        Assert.True(handled, $"Operator '{op}' sollte vom ValueComparator behandelt werden.");
        return passed;
    }

    // ── Equals / NotEquals ───────────────────────────────────────

    [Fact]
    public void Equals_StringMatch_CaseInsensitive_Trimmed()
    {
        Assert.True(Eval("Equals", "  Max ", "max"));
        Assert.True(Eval("Equals", "Max", "Max"));
        Assert.False(Eval("Equals", "Max", "Moritz"));
    }

    [Fact]
    public void Equals_NativeTypes_ExtractedCorrectly()
    {
        Assert.True(Eval("Equals", new OptionSetValue(105710001), "105710001"));
        var id = Guid.NewGuid();
        Assert.True(Eval("Equals", new EntityReference("contact", id), id.ToString()));
        Assert.True(Eval("Equals", new Money(42m), "42"));
        Assert.True(Eval("Equals", null, null));
        Assert.False(Eval("Equals", null, "x"));
    }

    [Fact]
    public void Equals_NumericFieldWithScale_ComparesNumerically_FB52()
    {
        // FB-52: Ein aus Dataverse retrievter Money/Decimal trägt die Feld-Precision als
        // decimal-Scale (z.B. 1000.0000000000m). Der frühere reine Stringvergleich verglich
        // "1000.0000000000" gegen den Pack-Wert "1000" und scheiterte. Equals MUSS numerisch
        // vergleichen (wie GreaterThan/LessThan). Dieser Test ist culture-unabhängig
        // deterministisch: der Scale-Mismatch failt OHNE Fix unter JEDER Culture.
        Assert.True(Eval("Equals", new Money(1000.0000000000m), "1000"));
        Assert.True(Eval("Equals", new Money(10000.00m), "10000"));
        Assert.True(Eval("Equals", new Money(20000m), "20000"));
        Assert.True(Eval("Equals", 1234.5600m, "1234.56"));        // nativer decimal mit Scale
        // Negativfall + Inversion bleiben korrekt:
        Assert.False(Eval("Equals", new Money(1000.5m), "1000"));
        Assert.True(Eval("NotEquals", new Money(1000.5m), "1000"));
        Assert.False(Eval("NotEquals", new Money(1000.0000000000m), "1000"));
    }

    [Fact]
    public void ExtractAndCompare_UnderCommaCulture_StayInvariant_FB52()
    {
        // Erzwingt das Dezimal-Komma (de-DE/de-CH = Jürgens Maschine UND der reale
        // LM-DEV-Defekt). Beweist, dass Vergleich UND Substring-Operatoren invariant
        // (Dezimal-Punkt) arbeiten, statt das Komma der Maschinen-Culture zu erben.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            // ExtractString liefert Punkt, nie Komma:
            Assert.Equal("1234.56", ValueComparator.ExtractString(new Money(1234.56m)));
            Assert.Equal("1234.56", ValueComparator.ExtractString(1234.56m));

            // Equals matcht trotz Komma-Culture (numerischer Pfad):
            Assert.True(Eval("Equals", new Money(1000.0000000000m), "1000"));

            // Substring-Operatoren laufen über die invariante String-Form:
            Assert.True(Eval("Contains", new Money(1234.56m), "1234.56"));
            Assert.True(Eval("EndsWith", new Money(1234.56m), ".56"));
            Assert.False(Eval("Contains", new Money(1234.56m), "1234,56"));   // Komma kommt NICHT vor
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Equals_BooleanFormat_IsCaseInsensitive_AdrKorrektur2()
    {
        // {alias.fields.x} liefert "True"/"False" (groß); der Vergleich gegen
        // "true"/"false" MUSS case-insensitiv matchen (ADR-0011 Korrektur 2).
        Assert.True(Eval("Equals", true, "true"));     // nativer bool -> "True"
        Assert.True(Eval("Equals", false, "false"));
        Assert.True(Eval("Equals", "True", "true"));   // bereits aufgelöster String (Condition-Pfad)
        Assert.True(Eval("Equals", "False", "false"));
        Assert.False(Eval("Equals", true, "false"));
    }

    [Fact]
    public void NotEquals_IsInverseOfEquals()
    {
        Assert.True(Eval("NotEquals", "Max", "Moritz"));
        Assert.False(Eval("NotEquals", "Max", "max"));
    }

    // ── IsNull / IsNotNull ───────────────────────────────────────

    [Fact]
    public void IsNull_NullAndWhitespace_True()
    {
        Assert.True(Eval("IsNull", null, null));
        Assert.True(Eval("IsNull", "   ", null));
        Assert.False(Eval("IsNull", "x", null));
        Assert.False(Eval("IsNull", new OptionSetValue(1), null));
    }

    [Fact]
    public void IsNotNull_IsInverseOfIsNull()
    {
        Assert.True(Eval("IsNotNull", "x", null));
        Assert.False(Eval("IsNotNull", null, null));
        Assert.False(Eval("IsNotNull", "  ", null));
    }

    // ── Contains / StartsWith / EndsWith ─────────────────────────

    [Fact]
    public void StringContainmentOperators_CaseInsensitive()
    {
        Assert.True(Eval("Contains", "Hello World", "lo wo"));
        Assert.True(Eval("StartsWith", "Hello", "HE"));
        Assert.True(Eval("EndsWith", "Hello", "LO"));
        Assert.False(Eval("Contains", "Hello", "xyz"));
    }

    // ── GreaterThan / LessThan ───────────────────────────────────

    [Fact]
    public void OrderedOperators_Numeric()
    {
        Assert.True(Eval("GreaterThan", 10, "5"));
        Assert.True(Eval("LessThan", 5, "10"));
        Assert.False(Eval("GreaterThan", 5, "5"));     // gleich -> kein >
        Assert.True(Eval("GreaterThan", new Money(100m), "99.5"));
        Assert.True(Eval("GreaterThan", new OptionSetValue(3), "2"));
    }

    [Fact]
    public void OrderedOperators_DateTime_UtcNormalized()
    {
        var baseUtc = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var later = baseUtc.AddHours(1);
        Assert.True(Eval("GreaterThan", later, baseUtc.ToString("O")));
        Assert.True(Eval("LessThan", baseUtc, later.ToString("O")));
    }

    [Fact]
    public void OrderedOperators_StringFallback()
    {
        Assert.True(Eval("GreaterThan", "b", "a"));
        Assert.True(Eval("LessThan", "a", "b"));
    }

    [Fact]
    public void OrderedOperators_NullActual_DoesNotPass()
    {
        // TryCompareOrdered liefert false bei null -> Operator behandelt, aber passed=false.
        Assert.False(Eval("GreaterThan", null, "5"));
        Assert.False(Eval("LessThan", null, "5"));
    }

    // ── Nicht-Comparator-Operatoren ──────────────────────────────

    [Fact]
    public void UnknownOrAssertOnlyOperators_NotHandled()
    {
        // DateSetRecently/Exists/RecordCount + unbekannte Operatoren gehören NICHT
        // in den geteilten Comparator: TryEvaluate liefert false (Aufrufer entscheidet).
        Assert.False(ValueComparator.TryEvaluate("DateSetRecently", DateTime.UtcNow, "120", out _));
        Assert.False(ValueComparator.TryEvaluate("Exists", null, null, out _));
        Assert.False(ValueComparator.TryEvaluate("Frobnicate", "x", "x", out _));
        Assert.False(ValueComparator.TryEvaluate(null, "x", "x", out _));
    }

    [Fact]
    public void SupportedOperators_CoverTheFieldBasedSet()
    {
        foreach (var op in ValueComparator.SupportedOperators)
            Assert.True(ValueComparator.TryEvaluate(op, "x", "x", out _),
                $"SupportedOperators listet '{op}', aber TryEvaluate behandelt ihn nicht.");
    }
}
