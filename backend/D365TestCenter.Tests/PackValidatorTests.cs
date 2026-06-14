using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using D365TestCenter.Core.Validation;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests for OE-6 Phase 1 pack validator. Each rule has at least one positive
/// (rule fires) and one negative (rule stays silent) coverage. Plus three
/// integration tests against TestRunner that prove the engine aborts the test
/// with Outcome=Error when the validator reports an Error finding.
/// </summary>
public class PackValidatorTests
{
    // ════════════════════════════════════════════════════════════════
    //  ACTION_UNKNOWN
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ActionUnknown_Typo_FlagsWithSuggestion()
    {
        var tc = TestCaseWith(new TestStep { StepNumber = 1, Action = "CreateRecrd" });
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "ACTION_UNKNOWN");
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Contains("CreateRecord", finding.Suggestion);
    }

    [Fact]
    public void ActionUnknown_FarOff_NoSuggestion()
    {
        var tc = TestCaseWith(new TestStep { StepNumber = 1, Action = "ZebraStripes" });
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "ACTION_UNKNOWN");
        Assert.DoesNotContain("Did you mean", finding.Suggestion ?? "");
    }

    [Fact]
    public void ActionEmpty_IsError()
    {
        var tc = TestCaseWith(new TestStep { StepNumber = 1, Action = "" });
        var report = new PackValidator().ValidateOne(tc);

        AssertSingle(report, "ACTION_UNKNOWN");
    }

    [Fact]
    public void KnownActions_DoNotFlag()
    {
        foreach (var action in PackValidator.KnownActions)
        {
            var step = new TestStep
            {
                StepNumber = 1,
                Action = action,
                Entity = "accounts",
                RequestName = "Stub", // satisfy R4
                Fields = new Dictionary<string, object?>(),
                RecordRef = "{a.id}", // satisfy R7 Record
                Filter = new List<FilterCondition> { new FilterCondition { Field = "name", Operator = "eq", Value = "x" } },
                Target = "Query"
            };
            var tc = TestCaseWith(step);
            var report = new PackValidator().ValidateOne(tc);
            Assert.DoesNotContain(report.Findings, f => f.Code == "ACTION_UNKNOWN");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  FILTER_FIELD_NOT_LOGICAL
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void FilterField_ODataFormat_IsError()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "WaitForRecord",
            Entity = "contacts",
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "_markant_contactid_value", Operator = "eq", Value = "{c.id}" }
            }
        });
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "FILTER_FIELD_NOT_LOGICAL");
        Assert.Contains("markant_contactid", finding.Suggestion);
    }

    [Fact]
    public void FilterField_LogicalName_Ok()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "WaitForRecord",
            Entity = "contacts",
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "markant_contactid", Operator = "eq", Value = "{c.id}" }
            }
        });
        var report = new PackValidator().ValidateOne(tc);
        Assert.DoesNotContain(report.Findings, f => f.Code == "FILTER_FIELD_NOT_LOGICAL");
    }

    // ════════════════════════════════════════════════════════════════
    //  FILTER_OPERATOR_VALUE_NULL
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void FilterOperator_EqValueNull_SuggestsIsnull()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "WaitForRecord",
            Entity = "contacts",
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "applicationid", Operator = "eq", Value = null }
            }
        });
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "FILTER_OPERATOR_VALUE_NULL");
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Contains("isnull", finding.Suggestion);
    }

    [Fact]
    public void FilterOperator_NeValueNull_SuggestsIsnotnull()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "WaitForRecord",
            Entity = "contacts",
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "applicationid", Operator = "ne", Value = null }
            }
        });
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "FILTER_OPERATOR_VALUE_NULL");
        Assert.Contains("isnotnull", finding.Suggestion);
    }

    [Fact]
    public void FilterOperator_IsNull_Ok()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "WaitForRecord",
            Entity = "contacts",
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "applicationid", Operator = "isnull" }
            }
        });
        var report = new PackValidator().ValidateOne(tc);
        Assert.DoesNotContain(report.Findings, f => f.Code == "FILTER_OPERATOR_VALUE_NULL");
    }

    // ════════════════════════════════════════════════════════════════
    //  EXECUTEREQUEST_MISSING_NAME
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ExecuteRequest_AllNameSourcesEmpty_IsError()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteRequest",
            Fields = new Dictionary<string, object?> { ["LeadId"] = "x" }
        });
        var report = new PackValidator().ValidateOne(tc);

        AssertSingle(report, "EXECUTEREQUEST_MISSING_NAME");
    }

    [Fact]
    public void ExecuteRequest_EntityFallback_Ok()
    {
        // ADR-0007: 'entity' is part of the fallback chain for the request name
        // (legacy CallCustomApi packs). Must NOT be flagged.
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "CallCustomApi",
            Entity = "lm_CancelInvoice",
            Fields = new Dictionary<string, object?> { ["InvoiceId"] = "x" }
        });
        var report = new PackValidator().ValidateOne(tc);
        Assert.DoesNotContain(report.Findings, f => f.Code == "EXECUTEREQUEST_MISSING_NAME");
    }

    [Fact]
    public void ExecuteAction_OnlyApiName_Ok()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteAction",
            ApiName = "lm_CancelInvoice",
            Parameters = new Dictionary<string, object?> { ["InvoiceId"] = "x" }
        });
        var report = new PackValidator().ValidateOne(tc);
        Assert.DoesNotContain(report.Findings, f => f.Code == "EXECUTEREQUEST_MISSING_NAME");
    }

    // ════════════════════════════════════════════════════════════════
    //  LOOKUP_BIND_FORMAT
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void LookupBind_ODataFormat_IsWarning()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "CreateRecord",
            Entity = "contacts",
            Fields = new Dictionary<string, object?>
            {
                ["_parentcustomerid_value@odata.bind"] = "/accounts({a.id})"
            }
        });
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "LOOKUP_BIND_FORMAT");
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
        Assert.Contains("parentcustomerid@odata.bind", finding.Suggestion);
    }

    [Fact]
    public void LookupBind_LogicalName_Ok()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "CreateRecord",
            Entity = "contacts",
            Fields = new Dictionary<string, object?>
            {
                ["parentcustomerid_account@odata.bind"] = "/accounts({a.id})"
            }
        });
        var report = new PackValidator().ValidateOne(tc);
        Assert.DoesNotContain(report.Findings, f => f.Code == "LOOKUP_BIND_FORMAT");
    }

    // ════════════════════════════════════════════════════════════════
    //  STATECODE_STATUSCODE_HINT
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Statuscode_WithoutStatecode_IsWarning()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "UpdateRecord",
            Alias = "task1",
            Fields = new Dictionary<string, object?> { ["statuscode"] = 5 }
        });
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "STATECODE_STATUSCODE_HINT");
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void Statuscode_WithStatecode_Ok()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "UpdateRecord",
            Alias = "task1",
            Fields = new Dictionary<string, object?>
            {
                ["statuscode"] = 5,
                ["statecode"] = 1
            }
        });
        var report = new PackValidator().ValidateOne(tc);
        Assert.DoesNotContain(report.Findings, f => f.Code == "STATECODE_STATUSCODE_HINT");
    }

    // ════════════════════════════════════════════════════════════════
    //  ASSERT_TARGET_INCOMPLETE
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Assert_TargetQueryWithoutEntity_IsError()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "Assert",
            Target = "Query",
            Field = "name",
            Operator = "Equals",
            Value = "X"
        });
        var report = new PackValidator().ValidateOne(tc);
        AssertSingle(report, "ASSERT_TARGET_INCOMPLETE");
    }

    [Fact]
    public void Assert_TargetRecordWithoutRecordRef_IsError()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "Assert",
            Target = "Record",
            Field = "name",
            Operator = "Equals",
            Value = "X"
        });
        var report = new PackValidator().ValidateOne(tc);
        AssertSingle(report, "ASSERT_TARGET_INCOMPLETE");
    }

    [Fact]
    public void Assert_TargetQueryComplete_Ok()
    {
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "Assert",
            Target = "Query",
            Entity = "accounts",
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "name", Operator = "eq", Value = "X" }
            },
            Field = "websiteurl",
            Operator = "IsNotNull"
        });
        var report = new PackValidator().ValidateOne(tc);
        Assert.DoesNotContain(report.Findings, f => f.Code == "ASSERT_TARGET_INCOMPLETE");
    }

    // ════════════════════════════════════════════════════════════════
    //  STEP_NUMBER_DUPLICATE
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void StepNumberDuplicate_IsWarning()
    {
        var tc = new TestCase
        {
            Id = "TC-DUP-01",
            Title = "duplicate step numbers",
            Steps = new List<TestStep>
            {
                new TestStep { StepNumber = 1, Action = "Wait", WaitSeconds = 1 },
                new TestStep { StepNumber = 1, Action = "Wait", WaitSeconds = 1 }
            }
        };
        var report = new PackValidator().ValidateOne(tc);
        AssertSingle(report, "STEP_NUMBER_DUPLICATE");
    }

    // ════════════════════════════════════════════════════════════════
    //  Multiple findings + canonical pack
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CanonicalPack_NoFindings()
    {
        var tc = new TestCase
        {
            Id = "CANON-01",
            Title = "canonical",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1, Action = "CreateRecord", Entity = "accounts", Alias = "a",
                    Fields = new Dictionary<string, object?> { ["name"] = "Demo" }
                },
                new TestStep
                {
                    StepNumber = 2, Action = "Assert", Target = "Record",
                    RecordRef = "{a.id}", Field = "name", Operator = "Equals", Value = "Demo"
                }
            }
        };
        var report = new PackValidator().ValidateOne(tc);
        Assert.Empty(report.Findings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void MultipleTests_FindingsCarryTestId()
    {
        var a = TestCaseWith(new TestStep { StepNumber = 1, Action = "WrongAction" });
        a.Id = "AAA";
        var b = TestCaseWith(new TestStep
        {
            StepNumber = 1, Action = "WaitForRecord", Entity = "x",
            Filter = new List<FilterCondition> { new FilterCondition { Field = "_x_value", Operator = "eq", Value = "v" } }
        });
        b.Id = "BBB";

        var report = new PackValidator().Validate(new[] { a, b });

        Assert.Equal(2, report.ErrorCount);
        Assert.Contains(report.Findings, f => f.TestId == "AAA" && f.Code == "ACTION_UNKNOWN");
        Assert.Contains(report.Findings, f => f.TestId == "BBB" && f.Code == "FILTER_FIELD_NOT_LOGICAL");
    }

    // ════════════════════════════════════════════════════════════════
    //  R10 PRECONDITIONS_OBSOLETE / ASSERTIONS_OBSOLETE (Bridge T6)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ObsoleteAssertionsArray_FromJson_IsError()
    {
        // Top-level assertions[] is the pre-ADR-0004 schema. Newtonsoft drops it
        // (TestCase only has Steps); [JsonExtensionData] keeps it visible to R10.
        const string json = """
        {
            "id": "OBS-ASSERT-01", "title": "obsolete top-level assertions",
            "steps": [
                { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "fields": { "name": "x" } }
            ],
            "assertions": [
                { "field": "name", "operator": "Equals", "value": "x" }
            ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "ASSERTIONS_OBSOLETE");
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
        Assert.Null(finding.StepNumber);
        Assert.Contains("assertions", finding.Message);
    }

    [Fact]
    public void ObsoletePreconditionsArray_FromJson_IsError()
    {
        const string json = """
        {
            "id": "OBS-PRE-01", "title": "obsolete top-level preconditions",
            "preconditions": [
                { "action": "CreateRecord", "entity": "accounts" }
            ],
            "steps": [
                { "stepNumber": 1, "action": "Assert", "target": "Record", "recordRef": "{a.id}", "field": "name", "operator": "Equals", "value": "x" }
            ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "PRECONDITIONS_OBSOLETE");
        Assert.Equal(ValidationSeverity.Error, finding.Severity);
    }

    [Fact]
    public void BothObsoleteArrays_FromJson_TwoErrors()
    {
        const string json = """
        {
            "id": "OBS-BOTH-01", "title": "both obsolete arrays",
            "preconditions": [ { "action": "CreateRecord" } ],
            "assertions": [ { "field": "x", "operator": "Equals", "value": "y" } ],
            "steps": [ { "stepNumber": 1, "action": "Wait", "waitSeconds": 1 } ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;
        var report = new PackValidator().ValidateOne(tc);

        Assert.Contains(report.Findings, f => f.Code == "PRECONDITIONS_OBSOLETE");
        Assert.Contains(report.Findings, f => f.Code == "ASSERTIONS_OBSOLETE");
    }

    [Fact]
    public void ModernStepsOnly_FromJson_NoObsoleteFinding()
    {
        const string json = """
        {
            "id": "MOD-01", "title": "modern steps-only",
            "steps": [
                { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "fields": { "name": "x" } }
            ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;
        var report = new PackValidator().ValidateOne(tc);

        Assert.DoesNotContain(report.Findings, f => f.Code == "ASSERTIONS_OBSOLETE" || f.Code == "PRECONDITIONS_OBSOLETE");
    }

    [Fact]
    public void ObsoleteRule_DirectlyConstructedTestCase_NullSafe()
    {
        // A TestCase built in code (not deserialized) has AdditionalData=null.
        // The rule must not throw.
        var tc = new TestCase { Id = "X", Title = "code-built", Steps = new List<TestStep>() };
        var report = new PackValidator().ValidateOne(tc);

        Assert.DoesNotContain(report.Findings, f => f.Code == "ASSERTIONS_OBSOLETE" || f.Code == "PRECONDITIONS_OBSOLETE");
    }

    // ════════════════════════════════════════════════════════════════
    //  STEP_KEY_UNKNOWN (Backlog N)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UnknownStepKey_WithinSeconds_FromJson_IsWarning()
    {
        // FB-45: 'withinSeconds' is not a step key; the DateSetRecently tolerance
        // belongs in 'value'. Newtonsoft drops it; [JsonExtensionData] on TestStep
        // keeps it visible so STEP_KEY_UNKNOWN can warn.
        const string json = """
        {
            "id": "UK-WS-01", "title": "unknown step key withinSeconds",
            "steps": [
                { "stepNumber": 1, "action": "Assert", "target": "Record", "recordRef": "{a.id}",
                  "field": "modifiedon", "operator": "DateSetRecently", "withinSeconds": 160 }
            ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "STEP_KEY_UNKNOWN");
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
        Assert.Equal(1, finding.StepNumber);
        Assert.Contains("withinSeconds", finding.Message);
    }

    [Fact]
    public void UnknownStepKey_CloseTypo_SuggestsKnownKey()
    {
        // 'timeoutSecond' is Levenshtein distance 1 from 'timeoutSeconds'.
        const string json = """
        {
            "id": "UK-TYPO-01", "title": "close typo",
            "steps": [
                { "stepNumber": 1, "action": "WaitForNotExists", "entity": "contacts", "timeoutSecond": 90 }
            ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "STEP_KEY_UNKNOWN");
        Assert.Contains("timeoutSeconds", finding.Suggestion);
    }

    [Fact]
    public void UnknownStepKey_TimeoutMs_RealWorld_IsWarning()
    {
        // Empirical Backlog-N finding: BrowserAction waitFor with 'timeoutMs' instead
        // of 'timeoutSeconds'. Wrong concept (far from any key), so generic hint.
        const string json = """
        {
            "id": "UK-TMS-01", "title": "timeoutMs typo",
            "steps": [
                { "stepNumber": 1, "action": "BrowserAction", "operation": "waitFor", "selector": "[role='row']", "timeoutMs": 30000 }
            ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;
        var report = new PackValidator().ValidateOne(tc);

        var finding = AssertSingle(report, "STEP_KEY_UNKNOWN");
        Assert.Equal(ValidationSeverity.Warning, finding.Severity);
        Assert.Contains("timeoutMs", finding.Message);
    }

    [Fact]
    public void KnownStepKeysOnly_FromJson_NoUnknownKeyFinding()
    {
        // All keys are part of the TestStep schema -> no STEP_KEY_UNKNOWN.
        const string json = """
        {
            "id": "UK-OK-01", "title": "all known keys",
            "steps": [
                { "stepNumber": 1, "action": "WaitForNotExists", "entity": "contacts",
                  "filter": [ { "field": "contactid", "operator": "eq", "value": "{c.id}" } ],
                  "timeoutSeconds": 90, "pollingIntervalMs": 2000, "maxDurationMs": 60000, "description": "wait" }
            ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;
        var report = new PackValidator().ValidateOne(tc);

        Assert.DoesNotContain(report.Findings, f => f.Code == "STEP_KEY_UNKNOWN");
    }

    [Fact]
    public void UnknownStepKey_CommentAnnotation_NotFlagged()
    {
        // 'comment' is an intentional inline-documentation key (allow-listed),
        // empirically used in the Markant UI packs. Must not be flagged.
        const string json = """
        {
            "id": "UK-CMT-01", "title": "comment annotation",
            "steps": [
                { "stepNumber": 1, "action": "Wait", "waitSeconds": 1, "comment": "inline note" }
            ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;
        var report = new PackValidator().ValidateOne(tc);

        Assert.DoesNotContain(report.Findings, f => f.Code == "STEP_KEY_UNKNOWN");
    }

    [Fact]
    public void UnknownStepKey_DirectlyConstructedStep_NullSafe()
    {
        // A code-built TestStep has AdditionalData=null. The rule must not throw
        // and must not produce a finding.
        var tc = TestCaseWith(new TestStep { StepNumber = 1, Action = "Wait", WaitSeconds = 1 });
        var report = new PackValidator().ValidateOne(tc);

        Assert.DoesNotContain(report.Findings, f => f.Code == "STEP_KEY_UNKNOWN");
    }

    // ════════════════════════════════════════════════════════════════
    //  TestRunner integration (OE-6 Engine-Integration)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void TestRunner_PreRunValidation_AbortsOnError()
    {
        var svc = new StubOrgService();
        var runner = new TestRunner(svc);
        var bad = TestCaseWith(new TestStep
        {
            StepNumber = 1,
            Action = "WaitForRecord",
            Entity = "contacts",
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "_markant_contactid_value", Operator = "eq", Value = "x" }
            }
        });

        var result = runner.RunAll(new List<TestCase> { bad });

        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(0, result.PassedCount);
        Assert.Equal(0, svc.ExecuteCallCount);
        Assert.Contains("FILTER_FIELD_NOT_LOGICAL", result.Results[0].ErrorMessage ?? "");
    }

    [Fact]
    public void TestRunner_PreRunValidation_WarningDoesNotAbort()
    {
        var svc = new StubOrgService();
        var runner = new TestRunner(svc);
        // Only a warning-level finding (statuscode without statecode). Test must still run.
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1, Action = "UpdateRecord", Alias = "a",
            Fields = new Dictionary<string, object?> { ["statuscode"] = 5 }
        });
        tc.Id = "WARN-ONLY";

        // Register alias 'a' so UpdateRecord can find it. We mimic this by
        // letting the first step Create instead.
        tc.Steps.Insert(0, new TestStep
        {
            StepNumber = 0, Action = "CreateRecord", Entity = "accounts", Alias = "a",
            Fields = new Dictionary<string, object?> { ["name"] = "x" }
        });

        var result = runner.RunAll(new List<TestCase> { tc });

        // The test runs at least the Create step (no abort), then UpdateRecord
        // would route through the service. The point of this assertion is that
        // the validator's warning did NOT short-circuit the run.
        Assert.NotEqual(TestOutcome.Error, result.Results[0].Outcome);
    }

    [Fact]
    public void TestRunner_PreRunValidation_ErrorMessage_ContainsCodeAndSuggestion()
    {
        var runner = new TestRunner(new StubOrgService());
        var tc = TestCaseWith(new TestStep
        {
            StepNumber = 1, Action = "CreateRecrd",
            Entity = "accounts", Fields = new Dictionary<string, object?>()
        });

        var result = runner.RunAll(new List<TestCase> { tc });

        var msg = result.Results[0].ErrorMessage ?? "";
        Assert.Contains("ACTION_UNKNOWN", msg);
        Assert.Contains("CreateRecord", msg);
    }

    [Fact]
    public void TestRunner_PreRunValidation_AbortsOnObsoleteAssertions()
    {
        // End-to-end: a test-level R10 Error (obsolete top-level assertions[])
        // must abort the run before any service call, just like step-level errors.
        var svc = new StubOrgService();
        var runner = new TestRunner(svc);
        const string json = """
        {
            "id": "OBS-RUN-01", "title": "obsolete assertions abort",
            "steps": [ { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "fields": { "name": "x" } } ],
            "assertions": [ { "field": "name", "operator": "Equals", "value": "x" } ]
        }
        """;
        var tc = Newtonsoft.Json.JsonConvert.DeserializeObject<TestCase>(json)!;

        var result = runner.RunAll(new List<TestCase> { tc });

        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(0, svc.ExecuteCallCount);
        Assert.Contains("ASSERTIONS_OBSOLETE", result.Results[0].ErrorMessage ?? "");
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    private static TestCase TestCaseWith(params TestStep[] steps) => new TestCase
    {
        Id = "TC-PV-01",
        Title = "pack validator test",
        Enabled = true,
        Steps = new List<TestStep>(steps)
    };

    private static ValidationFinding AssertSingle(ValidationReport report, string code)
    {
        var matches = report.Findings.Where(f => f.Code == code).ToList();
        Assert.Single(matches);
        return matches[0];
    }

    /// <summary>
    /// Minimal IOrganizationService double that just counts Execute calls.
    /// Used to prove the pre-run validator aborts before any service call.
    /// </summary>
    private sealed class StubOrgService : IOrganizationService
    {
        public int ExecuteCallCount { get; private set; }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            ExecuteCallCount++;
            // Return a minimal response so the runner can proceed if it reaches here.
            return new OrganizationResponse();
        }

        public Guid Create(Entity entity) { ExecuteCallCount++; return Guid.NewGuid(); }
        public Entity Retrieve(string entityName, Guid id, Microsoft.Xrm.Sdk.Query.ColumnSet columnSet) { ExecuteCallCount++; return new Entity(entityName, id); }
        public void Update(Entity entity) { ExecuteCallCount++; }
        public void Delete(string entityName, Guid id) { ExecuteCallCount++; }
        public Microsoft.Xrm.Sdk.EntityCollection RetrieveMultiple(Microsoft.Xrm.Sdk.Query.QueryBase query) { ExecuteCallCount++; return new Microsoft.Xrm.Sdk.EntityCollection(); }
        public void Associate(string entityName, Guid entityId, Microsoft.Xrm.Sdk.Relationship relationship, Microsoft.Xrm.Sdk.EntityReferenceCollection relatedEntities) { ExecuteCallCount++; }
        public void Disassociate(string entityName, Guid entityId, Microsoft.Xrm.Sdk.Relationship relationship, Microsoft.Xrm.Sdk.EntityReferenceCollection relatedEntities) { ExecuteCallCount++; }
    }
}
