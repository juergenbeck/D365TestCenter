using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für die AssertionEngine.
/// Prüft die Operator-Auswertung über die private ApplyOperator-Methode
/// (Signatur: TestAssertion, object?, AssertionResult).
/// Operatoren: Equals, NotEquals, IsNull, IsNotNull, Contains, StartsWith,
/// EndsWith, GreaterThan, LessThan, DateSetRecently.
/// </summary>
public class AssertionEngineTests
{
    [Fact]
    public void Equals_StringMatch_Passes()
    {
        var result = EvalOperator("Equals", "Max", "Max");
        Assert.True(result.Passed);
    }

    [Fact]
    public void Equals_CaseInsensitive_Passes()
    {
        var result = EvalOperator("Equals", "max", "MAX");
        Assert.True(result.Passed);
    }

    [Fact]
    public void Equals_TrimmedComparison_Passes()
    {
        var result = EvalOperator("Equals", "  Max  ", "Max");
        Assert.True(result.Passed);
    }

    [Fact]
    public void Equals_Mismatch_Fails()
    {
        var result = EvalOperator("Equals", "Max", "Moritz");
        Assert.False(result.Passed);
    }

    [Fact]
    public void Equals_OptionSetValue_ComparesIntValue()
    {
        var result = EvalOperator("Equals", new OptionSetValue(595300001), "595300001");
        Assert.True(result.Passed);
    }

    [Fact]
    public void Equals_EntityReference_ComparesGuid()
    {
        var id = Guid.NewGuid();
        var result = EvalOperator("Equals", new EntityReference("contact", id), id.ToString());
        Assert.True(result.Passed);
    }

    [Fact]
    public void Equals_BothNull_Passes()
    {
        var result = EvalOperator("Equals", null, null);
        Assert.True(result.Passed);
    }

    [Fact]
    public void Equals_MoneyValue_ComparesDecimal()
    {
        var money = new Money(42m);
        var result = EvalOperator("Equals", money, "42");
        Assert.True(result.Passed);
    }

    [Fact]
    public void NotEquals_DifferentValues_Passes()
    {
        var result = EvalOperator("NotEquals", "Max", "Moritz");
        Assert.True(result.Passed);
    }

    [Fact]
    public void NotEquals_SameValues_Fails()
    {
        var result = EvalOperator("NotEquals", "Max", "Max");
        Assert.False(result.Passed);
    }

    [Fact]
    public void IsNull_NullValue_Passes()
    {
        var result = EvalOperator("IsNull", null, null);
        Assert.True(result.Passed);
    }

    [Fact]
    public void IsNull_EmptyString_Passes()
    {
        var result = EvalOperator("IsNull", "", null);
        Assert.True(result.Passed);
    }

    [Fact]
    public void IsNull_WhitespaceString_Passes()
    {
        var result = EvalOperator("IsNull", "   ", null);
        Assert.True(result.Passed);
    }

    [Fact]
    public void IsNull_NonNullValue_Fails()
    {
        var result = EvalOperator("IsNull", "Max", null);
        Assert.False(result.Passed);
    }

    [Fact]
    public void IsNotNull_WithValue_Passes()
    {
        var result = EvalOperator("IsNotNull", "Max", null);
        Assert.True(result.Passed);
    }

    [Fact]
    public void IsNotNull_NullValue_Fails()
    {
        var result = EvalOperator("IsNotNull", null, null);
        Assert.False(result.Passed);
    }

    [Fact]
    public void Contains_Substring_Passes()
    {
        var result = EvalOperator("Contains", "Max Mustermann", "Muster");
        Assert.True(result.Passed);
    }

    [Fact]
    public void Contains_CaseInsensitive_Passes()
    {
        var result = EvalOperator("Contains", "Max Mustermann", "muster");
        Assert.True(result.Passed);
    }

    [Fact]
    public void Contains_NotPresent_Fails()
    {
        var result = EvalOperator("Contains", "Max Mustermann", "Schulze");
        Assert.False(result.Passed);
    }

    // ---------- StartsWith / EndsWith ----------

    [Fact]
    public void StartsWith_Prefix_Passes()
    {
        var result = EvalOperator("StartsWith", "JBE Test GmbH", "JBE");
        Assert.True(result.Passed);
    }

    [Fact]
    public void StartsWith_CaseInsensitive_Passes()
    {
        var result = EvalOperator("StartsWith", "JBE Test GmbH", "jbe");
        Assert.True(result.Passed);
    }

    [Fact]
    public void StartsWith_WrongPrefix_Fails()
    {
        var result = EvalOperator("StartsWith", "JBE Test GmbH", "Test");
        Assert.False(result.Passed);
    }

    [Fact]
    public void EndsWith_Suffix_Passes()
    {
        var result = EvalOperator("EndsWith", "user@example.com", "@example.com");
        Assert.True(result.Passed);
    }

    [Fact]
    public void EndsWith_CaseInsensitive_Passes()
    {
        var result = EvalOperator("EndsWith", "USER@EXAMPLE.COM", "@example.com");
        Assert.True(result.Passed);
    }

    [Fact]
    public void EndsWith_WrongSuffix_Fails()
    {
        var result = EvalOperator("EndsWith", "user@example.com", "@contoso.com");
        Assert.False(result.Passed);
    }

    // ---------- GreaterThan / LessThan ----------

    [Fact]
    public void GreaterThan_IntegerValues_Passes()
    {
        var result = EvalOperator("GreaterThan", 42, "10");
        Assert.True(result.Passed);
    }

    [Fact]
    public void GreaterThan_IntegerEqual_Fails()
    {
        var result = EvalOperator("GreaterThan", 42, "42");
        Assert.False(result.Passed);
    }

    [Fact]
    public void GreaterThan_Money_ComparesDecimal()
    {
        var result = EvalOperator("GreaterThan", new Money(100.50m), "99.99");
        Assert.True(result.Passed);
    }

    [Fact]
    public void GreaterThan_OptionSet_ComparesInt()
    {
        var result = EvalOperator("GreaterThan", new OptionSetValue(105710002), "105710000");
        Assert.True(result.Passed);
    }

    [Fact]
    public void GreaterThan_DateTime_RecentGreaterThanOld_Passes()
    {
        var recent = DateTime.UtcNow;
        var old = DateTime.UtcNow.AddDays(-7).ToString("O");
        var result = EvalOperator("GreaterThan", recent, old);
        Assert.True(result.Passed);
    }

    [Fact]
    public void GreaterThan_NullActual_Fails()
    {
        var result = EvalOperator("GreaterThan", null, "10");
        Assert.False(result.Passed);
    }

    [Fact]
    public void LessThan_IntegerValues_Passes()
    {
        var result = EvalOperator("LessThan", 5, "10");
        Assert.True(result.Passed);
    }

    [Fact]
    public void LessThan_IntegerEqual_Fails()
    {
        var result = EvalOperator("LessThan", 10, "10");
        Assert.False(result.Passed);
    }

    [Fact]
    public void LessThan_StringFallback_Alphabetic_Passes()
    {
        var result = EvalOperator("LessThan", "apple", "banana");
        Assert.True(result.Passed);
    }

    [Fact]
    public void LessThan_DateTime_OldLessThanRecent_Passes()
    {
        var old = DateTime.UtcNow.AddDays(-7);
        var recent = DateTime.UtcNow.ToString("O");
        var result = EvalOperator("LessThan", old, recent);
        Assert.True(result.Passed);
    }

    [Fact]
    public void DateSetRecently_RecentDate_Passes()
    {
        // DateSetRecently nutzt fest 120 Sekunden Toleranz
        var recent = DateTime.UtcNow.AddSeconds(-10);
        var assertion = new TestAssertion
        {
            Target = "Query", Field = "createdon",
            Operator = "DateSetRecently"
        };
        var result = new AssertionResult();

        var method = typeof(AssertionEngine).GetMethod("ApplyOperator",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method!.Invoke(null, new object?[] { assertion, recent, result });

        Assert.True(result.Passed);
    }

    [Fact]
    public void DateSetRecently_OldDate_Fails()
    {
        var old = DateTime.UtcNow.AddMinutes(-10);
        var assertion = new TestAssertion
        {
            Target = "Query", Field = "createdon",
            Operator = "DateSetRecently"
        };
        var result = new AssertionResult();

        var method = typeof(AssertionEngine).GetMethod("ApplyOperator",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method!.Invoke(null, new object?[] { assertion, old, result });

        Assert.False(result.Passed);
    }

    [Fact]
    public void UnknownOperator_FailsWithMessage()
    {
        var result = EvalOperator("InvalidOp", "Max", "Max");
        Assert.False(result.Passed);
        Assert.Contains("Unbekannter Operator", result.Message);
    }

    /// <summary>
    /// Hilfsmethode: Ruft die private ApplyOperator-Methode per Reflection auf.
    /// Signatur: ApplyOperator(TestAssertion, object?, AssertionResult)
    /// </summary>
    private static AssertionResult EvalOperator(string op, object? actual, string? expected)
    {
        var assertion = new TestAssertion
        {
            Target = "Query", Field = "test", Operator = op, Value = expected
        };
        var result = new AssertionResult();

        var method = typeof(AssertionEngine).GetMethod("ApplyOperator",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method!.Invoke(null, new object?[] { assertion, actual, result });

        return result;
    }
}
