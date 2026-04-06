using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für die AssertionEngine.
/// Prüft die Operator-Auswertung über die private ApplyOperator-Methode
/// (Signatur: TestAssertion, object?, AssertionResult).
/// Operatoren: Equals, NotEquals, IsNull, IsNotNull, Contains, DateSetRecently.
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
