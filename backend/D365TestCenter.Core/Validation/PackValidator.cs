using System;
using System.Collections.Generic;
using System.Linq;

namespace D365TestCenter.Core.Validation;

/// <summary>
/// OE-6 Phase 1: static pack validator. Detects common schema and pattern
/// mistakes without requiring a Dataverse connection.
///
/// Rules implemented (Phase 1 MVP):
///   ACTION_UNKNOWN              - action name not in the known whitelist (Levenshtein-suggested)
///   FILTER_FIELD_NOT_LOGICAL    - filter.field uses OData "_xxx_value" instead of the logical name
///   FILTER_OPERATOR_VALUE_NULL  - operator eq/ne with value=null instead of isnull/isnotnull
///   EXECUTEREQUEST_MISSING_NAME - ExecuteRequest/CallCustomApi/ExecuteAction without name source
///   LOOKUP_BIND_FORMAT          - field "_xxx_value@odata.bind" instead of "xxx@odata.bind"
///   STATECODE_STATUSCODE_HINT   - statuscode without statecode in the same Create/Update fields
///   ASSERT_TARGET_INCOMPLETE    - Assert target=Query without entity+filter, or target=Record without recordRef
///   STEP_NUMBER_DUPLICATE       - two steps share the same stepNumber inside a test case
///
/// Phase 2 (separate decision) would add metadata-aware checks (logical-name
/// existence, polymorph resolution, optionset plausibility).
/// </summary>
public sealed class PackValidator : IPackValidator
{
    /// <summary>
    /// Action whitelist. Must stay in sync with the switch in
    /// <c>TestRunner.ExecuteSteps</c>. Compared case-insensitively because
    /// the runner uppercases before switching.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KnownActions = new[]
    {
        "CreateRecord",
        "UpdateRecord",
        "DeleteRecord",
        "ExecuteRequest",
        "CallCustomApi",       // legacy alias (ADR-0007)
        "ExecuteAction",       // legacy alias (ADR-0007)
        "RetrieveRecord",
        "WaitForRecord",
        "FindRecord",
        "WaitForFieldValue",
        "AssertEnvironment",
        "Assert",
        "Wait",
        "Delay",
        "SetEnvironmentVariable",
        "RetrieveEnvironmentVariable",
        "BrowserAction",
    };

    /// <summary>
    /// Filter operators understood by <c>GenericRecordWaiter.ResolveOperator</c>
    /// and the assertion engine for target=Query. Used to detect typos in
    /// filter conditions and to know whether an operator expects a value.
    /// </summary>
    private static readonly HashSet<string> _knownFilterOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "equals",
        "ne", "notequals",
        "gt", "ge", "lt", "le",
        "like", "contains", "beginswith", "startswith", "endswith",
        "null", "isnull",
        "notnull", "isnotnull",
        "in", "notin"
    };

    private static readonly HashSet<string> _executeRequestVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ExecuteRequest", "CallCustomApi", "ExecuteAction"
    };

    public ValidationReport Validate(IEnumerable<TestCase> testCases)
    {
        if (testCases == null) throw new ArgumentNullException(nameof(testCases));

        var report = new ValidationReport();
        foreach (var tc in testCases)
        {
            ValidateOneInto(tc, report);
        }
        return report;
    }

    public ValidationReport ValidateOne(TestCase testCase)
    {
        if (testCase == null) throw new ArgumentNullException(nameof(testCase));

        var report = new ValidationReport();
        ValidateOneInto(testCase, report);
        return report;
    }

    private void ValidateOneInto(TestCase tc, ValidationReport report)
    {
        // STEP_NUMBER_DUPLICATE on the test level. Skip stepNumber=0: that is
        // the JSON deserialization default for legacy pack files that did not
        // set stepNumber, which would otherwise flood the report with false
        // positives.
        var duplicateStepNumbers = tc.Steps
            .Where(s => s.StepNumber > 0)
            .GroupBy(s => s.StepNumber)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var dupNumber in duplicateStepNumbers)
        {
            report.Add(new ValidationFinding
            {
                TestId = tc.Id,
                StepNumber = dupNumber,
                Severity = ValidationSeverity.Warning,
                Code = "STEP_NUMBER_DUPLICATE",
                Message = $"Step number {dupNumber} occurs more than once in the test case. " +
                          "Each step should have a unique stepNumber so the result trace stays readable.",
                Suggestion = "Renumber the duplicated steps so each stepNumber is unique."
            });
        }

        foreach (var step in tc.Steps)
        {
            ValidateStep(tc, step, report);
        }
    }

    private void ValidateStep(TestCase tc, TestStep step, ValidationReport report)
    {
        // R1 ACTION_UNKNOWN
        var action = step.Action ?? "";
        if (string.IsNullOrWhiteSpace(action))
        {
            report.Add(new ValidationFinding
            {
                TestId = tc.Id,
                StepNumber = step.StepNumber,
                Severity = ValidationSeverity.Error,
                Code = "ACTION_UNKNOWN",
                Message = "Step has no action set. Every step needs an 'action' property.",
                Suggestion = "Set 'action' to one of: " + string.Join(", ", KnownActions.Take(8)) + ", ..."
            });
        }
        else if (!KnownActions.Any(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase)))
        {
            var suggestion = SuggestAction(action);
            report.Add(new ValidationFinding
            {
                TestId = tc.Id,
                StepNumber = step.StepNumber,
                Severity = ValidationSeverity.Error,
                Code = "ACTION_UNKNOWN",
                Message = $"Unknown step action '{action}'. The TestRunner would throw at runtime.",
                Suggestion = suggestion != null
                    ? $"Did you mean '{suggestion}'?"
                    : "Allowed actions: " + string.Join(", ", KnownActions)
            });
        }

        // R2 FILTER_FIELD_NOT_LOGICAL / FILTER_OPERATOR_VALUE_NULL
        if (step.Filter != null)
        {
            foreach (var f in step.Filter)
            {
                CheckFilterField(tc, step, f, report);
                CheckFilterOperatorValue(tc, step, f, report);
            }
        }

        // R4 EXECUTEREQUEST_MISSING_NAME
        if (!string.IsNullOrEmpty(action) && _executeRequestVerbs.Contains(action))
        {
            var anyNameSet =
                !string.IsNullOrWhiteSpace(step.RequestName) ||
                !string.IsNullOrWhiteSpace(step.ActionName) ||
                !string.IsNullOrWhiteSpace(step.ApiName) ||
                !string.IsNullOrWhiteSpace(step.Entity);

            if (!anyNameSet)
            {
                report.Add(new ValidationFinding
                {
                    TestId = tc.Id,
                    StepNumber = step.StepNumber,
                    Severity = ValidationSeverity.Error,
                    Code = "EXECUTEREQUEST_MISSING_NAME",
                    Message = $"{action} step has no request name. The fallback chain " +
                              "requestName -> actionName -> apiName -> entity is empty.",
                    Suggestion = "Set 'requestName' to the SDK message (e.g. 'Merge', 'QualifyLead', " +
                                 "'markant_RunFieldGovernanceForContact')."
                });
            }
        }

        // R5 LOOKUP_BIND_FORMAT (field-key check on Fields plus Parameters)
        CheckLookupBind(tc, step, step.Fields, report);
        if (step.Parameters != null) CheckLookupBind(tc, step, step.Parameters, report);

        // R6 STATECODE_STATUSCODE_HINT (only for CreateRecord and UpdateRecord)
        if (IsCreateOrUpdate(action))
        {
            var hasStatus = step.Fields.Keys.Any(k => string.Equals(k, "statuscode", StringComparison.OrdinalIgnoreCase));
            var hasState = step.Fields.Keys.Any(k => string.Equals(k, "statecode", StringComparison.OrdinalIgnoreCase));
            if (hasStatus && !hasState)
            {
                report.Add(new ValidationFinding
                {
                    TestId = tc.Id,
                    StepNumber = step.StepNumber,
                    Severity = ValidationSeverity.Warning,
                    Code = "STATECODE_STATUSCODE_HINT",
                    Message = "Setting 'statuscode' without 'statecode'. The platform validates the " +
                              "(state, status) pair and rejects mismatched combinations with " +
                              "\"<n> is not a valid status code for state code <S>\".",
                    Suggestion = "Add 'statecode' with the matching state value (e.g. statecode=1 for inactive)."
                });
            }
        }

        // R7 ASSERT_TARGET_INCOMPLETE
        if (string.Equals(action, "Assert", StringComparison.OrdinalIgnoreCase))
        {
            var target = step.Target ?? "Query";
            if (string.Equals(target, "Query", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(step.Entity) || step.Filter == null || step.Filter.Count == 0)
                {
                    report.Add(new ValidationFinding
                    {
                        TestId = tc.Id,
                        StepNumber = step.StepNumber,
                        Severity = ValidationSeverity.Error,
                        Code = "ASSERT_TARGET_INCOMPLETE",
                        Message = "Assert with target=Query requires both 'entity' and a non-empty 'filter'.",
                        Suggestion = "Add an entity set name (e.g. 'accounts') and at least one filter condition."
                    });
                }
            }
            else if (string.Equals(target, "Record", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(step.RecordRef) && string.IsNullOrWhiteSpace(step.Alias))
                {
                    report.Add(new ValidationFinding
                    {
                        TestId = tc.Id,
                        StepNumber = step.StepNumber,
                        Severity = ValidationSeverity.Error,
                        Code = "ASSERT_TARGET_INCOMPLETE",
                        Message = "Assert with target=Record requires 'recordRef' (or an alias).",
                        Suggestion = "Set 'recordRef' to '{alias.id}' or to an output-alias path."
                    });
                }
            }
        }
    }

    private static void CheckFilterField(TestCase tc, TestStep step, FilterCondition f, ValidationReport report)
    {
        var field = f.Field ?? "";
        if (field.Length > 7 && field.StartsWith("_") && field.EndsWith("_value"))
        {
            // Strip leading underscore + trailing "_value" to get the logical name suggestion.
            var inner = field.Substring(1, field.Length - 7);
            report.Add(new ValidationFinding
            {
                TestId = tc.Id,
                StepNumber = step.StepNumber,
                Severity = ValidationSeverity.Error,
                Code = "FILTER_FIELD_NOT_LOGICAL",
                Message = $"Filter field '{field}' uses the OData lookup format. The internal " +
                          "QueryExpression builder expects the logical name (Singular, without '_..._value' wrapping).",
                Suggestion = $"Use '{inner}' instead of '{field}'."
            });
        }
    }

    private static void CheckFilterOperatorValue(TestCase tc, TestStep step, FilterCondition f, ValidationReport report)
    {
        var op = f.Operator ?? "";
        if (string.IsNullOrEmpty(op)) return;

        var isEqNe = string.Equals(op, "eq", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(op, "equals", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(op, "ne", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(op, "notequals", StringComparison.OrdinalIgnoreCase);

        if (isEqNe && f.Value == null)
        {
            var positive = op.StartsWith("e", StringComparison.OrdinalIgnoreCase);
            report.Add(new ValidationFinding
            {
                TestId = tc.Id,
                StepNumber = step.StepNumber,
                Severity = ValidationSeverity.Error,
                Code = "FILTER_OPERATOR_VALUE_NULL",
                Message = $"Operator '{op}' with value=null is invalid. The async ConditionExpression builder " +
                          "rejects DBNull for typed fields and throws " +
                          "\"expected argument(s) of type 'System.Guid' but received 'System.DBNull'\".",
                Suggestion = positive ? "Use operator 'isnull' instead." : "Use operator 'isnotnull' instead."
            });
        }
    }

    private static void CheckLookupBind(TestCase tc, TestStep step, IDictionary<string, object?> fields, ValidationReport report)
    {
        const string suffix = "@odata.bind";
        foreach (var key in fields.Keys)
        {
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            var preBind = key.Substring(0, key.Length - suffix.Length);
            // OData lookup wrapping inside @odata.bind keys is wrong:
            // valid: contactid@odata.bind, customerid_account@odata.bind
            // wrong: _contactid_value@odata.bind
            if (preBind.StartsWith("_") && preBind.EndsWith("_value"))
            {
                var inner = preBind.Substring(1, preBind.Length - 7);
                report.Add(new ValidationFinding
                {
                    TestId = tc.Id,
                    StepNumber = step.StepNumber,
                    Severity = ValidationSeverity.Warning,
                    Code = "LOOKUP_BIND_FORMAT",
                    Message = $"Field key '{key}' wraps the logical name in OData lookup format. " +
                              "@odata.bind expects the logical lookup name, not the OData getter shape.",
                    Suggestion = $"Use '{inner}@odata.bind' instead of '{key}'."
                });
            }
        }
    }

    private static bool IsCreateOrUpdate(string? action)
        => string.Equals(action, "CreateRecord", StringComparison.OrdinalIgnoreCase)
        || string.Equals(action, "UpdateRecord", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Suggest a known action that is within Levenshtein distance 2 of the
    /// input. Returns null if no close match exists.
    /// </summary>
    internal static string? SuggestAction(string actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) return null;

        var best = (Name: (string?)null, Distance: int.MaxValue);
        foreach (var candidate in KnownActions)
        {
            var d = LevenshteinDistance(actual.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (d < best.Distance)
            {
                best = (candidate, d);
            }
        }
        return best.Distance <= 2 ? best.Name : null;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
