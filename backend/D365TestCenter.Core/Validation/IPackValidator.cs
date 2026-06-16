using System.Collections.Generic;

namespace D365TestCenter.Core.Validation;

/// <summary>
/// OE-6: static pre-run validation for test packs. Catches schema and
/// pattern mistakes (filter field format, lookup-bind shape, missing
/// ExecuteRequest name, etc.) before any service call runs. Phase 1 is
/// metadata-free; Phase 2 (separate decision) would add metadata-aware
/// checks.
/// </summary>
public interface IPackValidator
{
    /// <summary>Validate every test in the list (static Phase-1 rules only).</summary>
    ValidationReport Validate(IEnumerable<TestCase> testCases);

    /// <summary>Validate a single test case (static Phase-1 rules only). Findings carry its <see cref="TestCase.Id"/>.</summary>
    ValidationReport ValidateOne(TestCase testCase);

    /// <summary>
    /// Validate a single test case with optional metadata-aware Phase-2 rules
    /// (OE-8). When <paramref name="metadata"/> is non-null, entity- and
    /// field-existence checks run against the target env's metadata; when null,
    /// only the static Phase-1 rules run (identical to <see cref="ValidateOne(TestCase)"/>).
    /// </summary>
    ValidationReport ValidateOne(TestCase testCase, EntityMetadataCache? metadata);
}
