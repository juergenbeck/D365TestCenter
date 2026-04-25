using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer expectFailure / expectException (1b).
/// Siehe D365TestCenter-Workspace/03_implementation/expectfailure-feature.md.
/// </summary>
public class ExpectFailureTests
{
    // ================================================================
    //  JSON-Deserialisierung
    // ================================================================

    [Fact]
    public void TestStep_ExpectFailureBoolean_Deserializes()
    {
        const string json = """
        { "action": "UpdateRecord", "alias": "c",
          "fields": { "statecode": 0 },
          "expectFailure": true }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.True(step!.ExpectFailure);
        Assert.Null(step.ExpectException);
    }

    [Fact]
    public void TestStep_ExpectExceptionObject_Deserializes()
    {
        const string json = """
        { "action": "UpdateRecord", "alias": "c",
          "fields": { "statecode": 0 },
          "expectException": {
            "messageContains": "gdpr admin",
            "errorCode": "0x80040227",
            "httpStatus": 400
          } }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.NotNull(step!.ExpectException);
        Assert.Equal("gdpr admin", step.ExpectException!.MessageContains);
        Assert.Equal("0x80040227", step.ExpectException.ErrorCode);
        Assert.Equal(400, step.ExpectException.HttpStatus);
        Assert.Null(step.ExpectException.MessageMatches);
    }

    // ================================================================
    //  EvaluateExpectException: Match-Logik
    // ================================================================

    [Fact]
    public void Evaluate_NullSpec_AlwaysPasses()
    {
        var (ok, _) = TestRunner.EvaluateExpectException(null, new Exception("any"));
        Assert.True(ok);
    }

    [Fact]
    public void Evaluate_MessageContains_Matches()
    {
        var spec = new ExpectExceptionSpec { MessageContains = "gdpr admin" };
        var ex = new Exception("can only be reactivated by a GDPR admin");

        var (ok, _) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.True(ok);
    }

    [Fact]
    public void Evaluate_MessageContains_CaseInsensitive()
    {
        var spec = new ExpectExceptionSpec { MessageContains = "GDPR ADMIN" };
        var ex = new Exception("Reactivation requires gdpr admin");

        var (ok, _) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.True(ok);
    }

    [Fact]
    public void Evaluate_MessageContains_DoesNotMatch()
    {
        var spec = new ExpectExceptionSpec { MessageContains = "budget admin" };
        var ex = new Exception("Reactivation requires gdpr admin");

        var (ok, reason) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.False(ok);
        Assert.Contains("budget admin", reason);
    }

    [Fact]
    public void Evaluate_MessageMatches_Regex_Matches()
    {
        var spec = new ExpectExceptionSpec { MessageMatches = @"gdpr\s+\w+" };
        var ex = new Exception("requires gdpr admin for this");

        var (ok, _) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.True(ok);
    }

    [Fact]
    public void Evaluate_MessageMatches_InvalidRegex_ReturnsFalseWithReason()
    {
        var spec = new ExpectExceptionSpec { MessageMatches = @"(unclosed" };
        var ex = new Exception("anything");

        var (ok, reason) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.False(ok);
        Assert.Contains("Ungueltiger Regex", reason);
    }

    [Fact]
    public void Evaluate_BothMessageModes_ReturnsValidationError()
    {
        var spec = new ExpectExceptionSpec
        {
            MessageContains = "x",
            MessageMatches = "y"
        };

        var (ok, reason) = TestRunner.EvaluateExpectException(spec, new Exception("whatever"));
        Assert.False(ok);
        Assert.Contains("messageContains und messageMatches", reason);
    }

    [Fact]
    public void Evaluate_ErrorCode_FromMessage()
    {
        // Nested fake exception chain — ErrorCode wird aus Message-Text gelesen
        var spec = new ExpectExceptionSpec { ErrorCode = "0x80040227" };
        var inner = new Exception("Inner: no code here");
        var ex = new Exception("Plugin failed with code 0x80040227: something", inner);

        var (ok, _) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.True(ok);
    }

    [Fact]
    public void Evaluate_ErrorCode_DoesNotMatch()
    {
        var spec = new ExpectExceptionSpec { ErrorCode = "0x80040227" };
        var ex = new Exception("Plugin failed with code 0x80048408");

        var (ok, reason) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.False(ok);
        Assert.Contains("0x80040227", reason);
    }

    [Fact]
    public void Evaluate_AndCombination_AllMustMatch()
    {
        var spec = new ExpectExceptionSpec
        {
            MessageContains = "gdpr",
            ErrorCode = "0x80040227"
        };
        var ex = new Exception("gdpr admin required, code 0x80040227");

        var (ok, _) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.True(ok);
    }

    [Fact]
    public void Evaluate_AndCombination_OneMissingFails()
    {
        var spec = new ExpectExceptionSpec
        {
            MessageContains = "gdpr",
            ErrorCode = "0x80040227"
        };
        var ex = new Exception("gdpr admin required"); // kein ErrorCode im Text

        var (ok, reason) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.False(ok);
        Assert.Contains("0x80040227", reason);
    }

    [Fact]
    public void Evaluate_HttpStatus_FromMessage()
    {
        var spec = new ExpectExceptionSpec { HttpStatus = 400 };
        var ex = new Exception("Request failed with HTTP 400 Bad Request");

        var (ok, _) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.True(ok);
    }

    [Fact]
    public void Evaluate_HttpStatus_DoesNotMatch()
    {
        var spec = new ExpectExceptionSpec { HttpStatus = 400 };
        var ex = new Exception("Request failed with HTTP 404");

        var (ok, reason) = TestRunner.EvaluateExpectException(spec, ex);
        Assert.False(ok);
        Assert.Contains("400", reason);
    }
}
