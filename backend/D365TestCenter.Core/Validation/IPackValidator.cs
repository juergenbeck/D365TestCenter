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
    /// <summary>Validate every test in the list.</summary>
    ValidationReport Validate(IEnumerable<TestCase> testCases);

    /// <summary>Validate a single test case. Findings carry its <see cref="TestCase.Id"/>.</summary>
    ValidationReport ValidateOne(TestCase testCase);
}
