using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace D365TestCenter.Core.Validation;

/// <summary>
/// Severity of a single validator finding. Mirrors the standard linter
/// taxonomy: Error blocks the run, Warning surfaces a smell, Info is a hint.
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// One pack validation finding. Produced by <see cref="PackValidator"/> for
/// a specific test case (and optionally a specific step). Findings are
/// gathered into a <see cref="ValidationReport"/>; the TestRunner aborts
/// the test if any finding has <see cref="ValidationSeverity.Error"/>.
/// </summary>
public sealed class ValidationFinding
{
    /// <summary>Test case the finding belongs to.</summary>
    [JsonProperty("testId")]
    public string TestId { get; set; } = "";

    /// <summary>Step number inside the test case, or null for test-level findings.</summary>
    [JsonProperty("stepNumber")]
    public int? StepNumber { get; set; }

    [JsonProperty("severity")]
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.Info;

    /// <summary>
    /// Stable machine-readable code. Used for filtering in CI and for the
    /// OE-6 documentation. Examples: ACTION_UNKNOWN, FILTER_FIELD_NOT_LOGICAL,
    /// FILTER_OPERATOR_VALUE_NULL, EXECUTEREQUEST_MISSING_NAME, LOOKUP_BIND_FORMAT,
    /// STATECODE_STATUSCODE_HINT, ASSERT_TARGET_INCOMPLETE, STEP_NUMBER_DUPLICATE.
    /// </summary>
    [JsonProperty("code")]
    public string Code { get; set; } = "";

    /// <summary>Human-readable description of what is wrong.</summary>
    [JsonProperty("message")]
    public string Message { get; set; } = "";

    /// <summary>Optional suggestion how to fix the finding.</summary>
    [JsonProperty("suggestion")]
    public string? Suggestion { get; set; }
}
