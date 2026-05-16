using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace D365TestCenter.Core.Validation;

/// <summary>
/// Aggregated validator output for a pack (one or more test cases). Carries
/// all findings plus convenience counters and a HasErrors flag used by the
/// TestRunner to decide whether to abort the test or to log Warning/Info.
/// </summary>
public sealed class ValidationReport
{
    [JsonProperty("findings")]
    public List<ValidationFinding> Findings { get; } = new List<ValidationFinding>();

    [JsonIgnore]
    public int ErrorCount => Findings.Count(f => f.Severity == ValidationSeverity.Error);

    [JsonIgnore]
    public int WarningCount => Findings.Count(f => f.Severity == ValidationSeverity.Warning);

    [JsonIgnore]
    public int InfoCount => Findings.Count(f => f.Severity == ValidationSeverity.Info);

    [JsonIgnore]
    public bool HasErrors => ErrorCount > 0;

    public void Add(ValidationFinding finding) => Findings.Add(finding);

    /// <summary>
    /// Returns all findings for a given test ID, ordered by step number then by code.
    /// </summary>
    public IEnumerable<ValidationFinding> ForTest(string testId)
        => Findings
            .Where(f => f.TestId == testId)
            .OrderBy(f => f.StepNumber ?? int.MinValue)
            .ThenBy(f => f.Code);
}
