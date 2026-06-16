using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
///   PRECONDITIONS_OBSOLETE      - obsolete pre-ADR-0004 top-level preconditions[] array (R10)
///   ASSERTIONS_OBSOLETE         - obsolete pre-ADR-0004 top-level assertions[] array (R10)
///   STEP_KEY_UNKNOWN            - unknown step-level key silently dropped on parse (e.g. withinSeconds/timeoutMs), Warning (Backlog N)
///   ALIAS_UNDEFINED             - alias reference ({alias.id}/{RECORD:alias}/...) with no prior defining step, Warning (OE-6 Phase 2 symbol-table, Backlog J)
///
/// Phase 2 metadata-aware checks run only when an EntityMetadataCache for the
/// target env is supplied (CLI 'validate --org'); they never run in the
/// service-call-free TestRunner pre-validation. Implemented (OE-8, Backlog J):
///   ENTITY_UNKNOWN              - entity has no loadable metadata on the target env, Warning
///   FIELD_UNKNOWN               - field is not an attribute of the entity (Levenshtein-suggested), Warning
///   OPTIONSET_VALUE_IMPLAUSIBLE - Create/Update value not among the field's option values, Warning
///   POLYMORPH_TARGET_INVALID    - @odata.bind target outside the lookup's allowed targets, Warning
/// The symbol-table half of Phase 2 (ALIAS_UNDEFINED) is static and runs always.
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
        "WaitForNotExists",
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

    /// <summary>
    /// All known step-level JSON keys, reflected from the [JsonProperty] attributes on
    /// <see cref="TestStep"/>. Stays automatically in sync with the model (driftproof),
    /// unlike a hand-maintained list. Used by STEP_KEY_UNKNOWN to flag keys that
    /// Newtonsoft silently drops (MissingMemberHandling default Ignore).
    /// </summary>
    private static readonly HashSet<string> _knownStepKeys = BuildKnownStepKeys();

    /// <summary>
    /// Step keys that are intentional inline documentation, not a typo, so
    /// STEP_KEY_UNKNOWN does not flag them. 'comment' is used as an inline note in
    /// existing UI packs; the others are common conventions.
    /// </summary>
    private static readonly HashSet<string> _stepKeyAnnotationAllowlist =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "comment", "note", "_comment", "$comment" };

    private static HashSet<string> BuildKnownStepKeys()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in typeof(TestStep).GetProperties())
        {
            var attr = prop.GetCustomAttribute<JsonPropertyAttribute>();
            if (!string.IsNullOrEmpty(attr?.PropertyName)) set.Add(attr!.PropertyName!);
        }
        return set;
    }

    public ValidationReport Validate(IEnumerable<TestCase> testCases)
    {
        if (testCases == null) throw new ArgumentNullException(nameof(testCases));

        var report = new ValidationReport();
        foreach (var tc in testCases)
        {
            ValidateOneInto(tc, report, null);
        }
        return report;
    }

    public ValidationReport ValidateOne(TestCase testCase) => ValidateOne(testCase, null);

    public ValidationReport ValidateOne(TestCase testCase, EntityMetadataCache? metadata)
    {
        if (testCase == null) throw new ArgumentNullException(nameof(testCase));

        var report = new ValidationReport();
        ValidateOneInto(testCase, report, metadata);
        return report;
    }

    private void ValidateOneInto(TestCase tc, ValidationReport report, EntityMetadataCache? metadata)
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

        // R10 PRECONDITIONS_OBSOLETE / ASSERTIONS_OBSOLETE: pre-ADR-0004 schema.
        // These top-level arrays are silently dropped by Newtonsoft (TestCase only
        // has 'Steps'); [JsonExtensionData] on TestCase keeps them visible here so a
        // test that asserts nothing (null-assertion) does not slip through green.
        CheckObsoleteTopLevelArray(tc, "preconditions", "PRECONDITIONS_OBSOLETE", report);
        CheckObsoleteTopLevelArray(tc, "assertions", "ASSERTIONS_OBSOLETE", report);

        foreach (var step in tc.Steps)
        {
            ValidateStep(tc, step, report, metadata);
        }

        // ALIAS_UNDEFINED (OE-6 Phase 2, symbol-table half / Backlog J)
        CheckAliasSymbolTable(tc, report);
    }

    /// <summary>
    /// R10: flags an obsolete pre-ADR-0004 top-level array (preconditions/assertions).
    /// Since ADR-0004 a test is a single ordered steps[] list; Models.cs knows only
    /// 'Steps', so Newtonsoft silently drops a top-level 'preconditions[]'/'assertions[]'.
    /// Such a test parses clean but asserts nothing. [JsonExtensionData] on TestCase
    /// preserves the dropped keys in AdditionalData so this rule can see them.
    /// </summary>
    private static void CheckObsoleteTopLevelArray(
        TestCase tc, string key, string code, ValidationReport report)
    {
        if (tc.AdditionalData == null) return;
        if (!tc.AdditionalData.TryGetValue(key, out var token)) return;

        var count = token is JArray arr ? arr.Count : 0;
        report.Add(new ValidationFinding
        {
            TestId = tc.Id,
            StepNumber = null,
            Severity = ValidationSeverity.Error,
            Code = code,
            Message = $"Test case carries an obsolete top-level '{key}[]' array ({count} entries). " +
                      "Since ADR-0004 a test is a single ordered 'steps[]' list; the engine knows only " +
                      $"'Steps' and silently ignores '{key}[]', so these entries run as a no-op and the " +
                      "test asserts nothing.",
            Suggestion = string.Equals(key, "assertions", StringComparison.OrdinalIgnoreCase)
                ? "Move each assertion into the 'steps[]' list as an 'Assert' action."
                : "Move each precondition into the 'steps[]' list as a 'CreateRecord' (or matching) action."
        });
    }

    // ── OE-6 Phase 2 (symbol-table half, Backlog J) ─────────────────────────
    // Alias references resolved by the PlaceholderEngine from records/outputs that
    // a PRIOR step registered. Pattern -> resolution source:
    //   {alias.id}            record alias  (step 'alias')
    //   {alias.fields.X}      record alias  (step 'alias' + loaded columns)
    //   {RECORD:alias}        record alias  (step 'alias')
    //   {RESULT:alias.X}      record alias  (step 'alias')
    //   {alias.outputs.X}     output alias  (ExecuteRequest 'outputAlias')
    // Non-alias tokens ({TESTID}, {TIMESTAMP*}, {GENERATED:*}, {GUID}, {ROW:*},
    // {CONTACT_ID}/{ACCOUNT_ID}) do not match these shapes and are not flagged.
    private static readonly Regex _aliasRefId = new(@"\{(\w+)\.id\}", RegexOptions.Compiled);
    private static readonly Regex _aliasRefFields = new(@"\{(\w+)\.fields\.\w+\}", RegexOptions.Compiled);
    private static readonly Regex _aliasRefOutputs = new(@"\{(\w+)\.outputs\.\w+(?:\[type=\w+\])?\}", RegexOptions.Compiled);
    private static readonly Regex _aliasRefRecord = new(@"\{RECORD:(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex _aliasRefResult = new(@"\{RESULT:(\w+)\.\w+\}", RegexOptions.Compiled);

    /// <summary>
    /// ALIAS_UNDEFINED (OE-6 Phase 2, symbol-table). Flags an alias reference whose
    /// alias is not defined by any earlier step via 'alias'/'outputAlias'. Such a
    /// placeholder stays an unresolved literal at runtime (usually a typo, e.g.
    /// '{con.id}' when the alias was 'contact').
    ///
    /// Conservative by design: aliases can also flow cross-test via 'sharedContext'
    /// or 'dependsOn', which a single-test static pass cannot resolve. A test that
    /// uses either is skipped so the rule never false-positives on a shared alias.
    /// Severity Warning, so a residual false positive never aborts a run.
    /// </summary>
    private static void CheckAliasSymbolTable(TestCase tc, ValidationReport report)
    {
        if (!string.IsNullOrWhiteSpace(tc.SharedContext)) return;
        if (tc.DependsOn != null && tc.DependsOn.Count > 0) return;

        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in tc.Steps)
        {
            string json;
            try { json = JsonConvert.SerializeObject(step); }
            catch { json = ""; }

            foreach (var (alias, kind) in ExtractAliasReferences(json))
            {
                if (defined.Contains(alias)) continue;
                // Dedup per (alias, step) so a reference used twice in one step
                // produces one finding.
                if (!reported.Add($"{alias}|{step.StepNumber}|{kind}")) continue;

                report.Add(new ValidationFinding
                {
                    TestId = tc.Id,
                    StepNumber = step.StepNumber,
                    Severity = ValidationSeverity.Warning,
                    Code = "ALIAS_UNDEFINED",
                    Message = $"Step references alias '{alias}' via {kind}, but no earlier step defines " +
                              "it via 'alias' or 'outputAlias'. The placeholder stays unresolved at runtime.",
                    Suggestion = "Define the alias on a prior CreateRecord/RetrieveRecord/FindRecord step " +
                                 "(or 'outputAlias' on an ExecuteRequest), or fix a typo in the reference."
                });
            }

            // Aliases this step registers become visible to LATER steps only.
            if (!string.IsNullOrWhiteSpace(step.Alias)) defined.Add(step.Alias!.Trim());
            if (!string.IsNullOrWhiteSpace(step.OutputAlias)) defined.Add(step.OutputAlias!.Trim());
        }
    }

    private static IEnumerable<(string Alias, string Kind)> ExtractAliasReferences(string json)
    {
        foreach (Match m in _aliasRefId.Matches(json)) yield return (m.Groups[1].Value, "{alias.id}");
        foreach (Match m in _aliasRefFields.Matches(json)) yield return (m.Groups[1].Value, "{alias.fields.*}");
        foreach (Match m in _aliasRefOutputs.Matches(json)) yield return (m.Groups[1].Value, "{alias.outputs.*}");
        foreach (Match m in _aliasRefRecord.Matches(json)) yield return (m.Groups[1].Value, "{RECORD:alias}");
        foreach (Match m in _aliasRefResult.Matches(json)) yield return (m.Groups[1].Value, "{RESULT:alias.*}");
    }

    private void ValidateStep(TestCase tc, TestStep step, ValidationReport report, EntityMetadataCache? metadata)
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

        // R11 STEP_KEY_UNKNOWN: keys that are not part of the step schema are silently
        // dropped by Newtonsoft (MissingMemberHandling default Ignore). [JsonExtensionData]
        // on TestStep preserves them in AdditionalData so a typo like 'withinSeconds' or
        // 'timeoutMs' is flagged instead of running with the schema default (FB-45).
        // Warning severity; intentional inline documentation keys are allow-listed.
        if (step.AdditionalData != null)
        {
            foreach (var key in step.AdditionalData.Keys)
            {
                if (_stepKeyAnnotationAllowlist.Contains(key)) continue;

                var suggestion = SuggestStepKey(key);
                report.Add(new ValidationFinding
                {
                    TestId = tc.Id,
                    StepNumber = step.StepNumber,
                    Severity = ValidationSeverity.Warning,
                    Code = "STEP_KEY_UNKNOWN",
                    Message = $"Unknown step key '{key}'. It is not part of the step schema and is " +
                              "silently ignored on parse, so its value has no effect (the schema default applies).",
                    Suggestion = suggestion != null
                        ? $"Did you mean '{suggestion}'?"
                        : "Remove the key or use a documented step property. Inline notes can use 'comment'."
                });
            }
        }

        // ENTITY_UNKNOWN / FIELD_UNKNOWN (OE-8 Phase 2 metadata-aware, Backlog J).
        // Runs only when a metadata cache for the target env was provided.
        if (metadata != null)
        {
            CheckEntityAndFields(tc, step, report, metadata);
        }
    }

    // ── OE-8 Phase 2: metadata-aware checks (Backlog J) ─────────────────────
    // Entity- and field-existence against the target env's metadata. The cache
    // is the same shared EntityMetadataCache the engine uses at runtime, so a
    // metadata-load failure degrades gracefully (no finding) rather than
    // false-flagging. Severity Warning: a pack may legitimately run on a
    // different env where the entity/field exists.
    private static readonly HashSet<string> _entityBearingActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CreateRecord", "UpdateRecord", "DeleteRecord", "RetrieveRecord",
        "WaitForRecord", "WaitForNotExists", "FindRecord", "WaitForFieldValue"
    };

    private static void CheckEntityAndFields(TestCase tc, TestStep step, ValidationReport report, EntityMetadataCache metadata)
    {
        var action = step.Action ?? "";
        var isQueryAssert = string.Equals(action, "Assert", StringComparison.OrdinalIgnoreCase)
            && string.Equals(step.Target ?? "Query", "Query", StringComparison.OrdinalIgnoreCase);
        var entityBearing = _entityBearingActions.Contains(action) || isQueryAssert;
        if (!entityBearing || string.IsNullOrWhiteSpace(step.Entity)) return;

        // Entity references can carry placeholders; skip those (resolved at runtime).
        if (step.Entity!.Contains('{')) return;

        var logical = metadata.ResolveLogicalName(step.Entity);
        var info = metadata.GetMetadata(logical);
        if (info == null)
        {
            report.Add(new ValidationFinding
            {
                TestId = tc.Id,
                StepNumber = step.StepNumber,
                Severity = ValidationSeverity.Warning,
                Code = "ENTITY_UNKNOWN",
                Message = $"Entity '{step.Entity}' (resolved logical name '{logical}') has no loadable metadata " +
                          "on the target env. The step would fail at runtime if the entity does not exist here.",
                Suggestion = "Check the entity name (EntitySetName plural or LogicalName singular) and that the " +
                             "solution carrying it is installed on this env."
            });
            return; // no field checks without entity metadata
        }

        // FIELD_UNKNOWN: plain field keys / filter fields / assert field that are
        // not attributes on the entity. @odata.bind keys are skipped (navigation-
        // property names differ from logical attributes; covered by LOOKUP_BIND_FORMAT).
        foreach (var key in CollectFieldNames(step))
        {
            if (info.AttributeTypes.ContainsKey(key)) continue;
            var suggestion = SuggestField(key, info);
            report.Add(new ValidationFinding
            {
                TestId = tc.Id,
                StepNumber = step.StepNumber,
                Severity = ValidationSeverity.Warning,
                Code = "FIELD_UNKNOWN",
                Message = $"Field '{key}' is not an attribute of '{logical}' on the target env. " +
                          "It would be silently ignored (Create/Update) or fail the query at runtime.",
                Suggestion = suggestion != null ? $"Did you mean '{suggestion}'?" : "Check the field logical name."
            });
        }

        // OPTIONSET_VALUE_IMPLAUSIBLE + POLYMORPH_TARGET_INVALID (OE-8 Phase 2 rest, Backlog J).
        CheckOptionSetValues(tc, step, report, logical, info);
        CheckPolymorphTargets(tc, step, report, info, metadata);
    }

    // ── OE-8 Phase 2 rest: optionset plausibility (Backlog J) ───────────────
    // Flags a numeric value written to a Picklist/State/Status/MultiSelectPicklist
    // field that is not among the option values defined on the target env. Only
    // checks Create/Update field values: those carry raw option values, whereas
    // filter/assert values may legitimately compare against labels or placeholders.
    // Skips placeholders and non-numeric values (a label is not an error here).
    // Severity Warning: a pack may target a different env where the option exists.
    private static void CheckOptionSetValues(
        TestCase tc, TestStep step, ValidationReport report, string logical, EntityMetadataInfo info)
    {
        if (!IsCreateOrUpdate(step.Action)) return;
        if (info.OptionSetValues.Count == 0) return;

        foreach (var kvp in step.Fields)
        {
            var key = kvp.Key;
            if (key.IndexOf("@odata.bind", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (!info.OptionSetValues.TryGetValue(key, out var allowed)) continue;
            if (!TryGetOptionSetInt(kvp.Value, out var value)) continue; // placeholder / label / null
            if (allowed.Contains(value)) continue;

            var allowedList = string.Join(", ", allowed.OrderBy(v => v).Take(12));
            if (allowed.Count > 12) allowedList += ", ...";
            report.Add(new ValidationFinding
            {
                TestId = tc.Id,
                StepNumber = step.StepNumber,
                Severity = ValidationSeverity.Warning,
                Code = "OPTIONSET_VALUE_IMPLAUSIBLE",
                Message = $"Value {value} on optionset field '{key}' of '{logical}' is not a defined option " +
                          "on the target env. The platform would reject it at runtime.",
                Suggestion = $"Use one of the defined option values: {allowedList}."
            });
        }
    }

    /// <summary>
    /// Extracts an integer option value from a JSON field value. Newtonsoft yields
    /// long for JSON numbers and string for quoted numbers; both are accepted.
    /// Returns false for placeholders, labels (non-numeric strings) and null.
    /// </summary>
    private static bool TryGetOptionSetInt(object? raw, out int value)
    {
        value = 0;
        switch (raw)
        {
            case null:
                return false;
            case int i:
                value = i; return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                value = (int)l; return true;
            case string s:
                s = s.Trim();
                if (s.Length == 0 || s.IndexOf('{') >= 0) return false; // placeholder
                return int.TryParse(s, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    // ── OE-8 Phase 2 rest: polymorph lookup target check (Backlog J) ────────
    // Validates @odata.bind keys against the lookup's allowed target entities.
    // Catches (1) a navigation-property suffix that names an invalid target,
    // (2) a suffix that disagrees with the bound entity (e.g. customerid_account
    // bound to /contacts), and (3) a bound entity outside the lookup's targets.
    // Conservative: if the nav-property cannot be mapped to a known lookup
    // attribute, or the bind value is a placeholder/unparseable, nothing is
    // flagged. Severity Warning.
    private static void CheckPolymorphTargets(
        TestCase tc, TestStep step, ValidationReport report, EntityMetadataInfo info, EntityMetadataCache metadata)
    {
        if (info.LookupAllTargets.Count == 0) return;
        CheckPolymorphTargetsIn(tc, step, step.Fields, report, info, metadata);
        if (step.Parameters != null) CheckPolymorphTargetsIn(tc, step, step.Parameters, report, info, metadata);
    }

    private static void CheckPolymorphTargetsIn(
        TestCase tc, TestStep step, IDictionary<string, object?> fields, ValidationReport report,
        EntityMetadataInfo info, EntityMetadataCache metadata)
    {
        const string suffix = "@odata.bind";
        foreach (var kvp in fields)
        {
            var key = kvp.Key;
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            var navProp = key.Substring(0, key.Length - suffix.Length);
            // Skip the malformed OData getter shape (_x_value@odata.bind); LOOKUP_BIND_FORMAT owns that.
            if (navProp.StartsWith("_") && navProp.EndsWith("_value")) continue;

            var (lookupAttr, navTarget) = ResolveLookupNav(navProp, info);
            if (lookupAttr == null) continue; // unknown nav-property -> graceful skip

            var boundTarget = ParseBindTarget(kvp.Value, metadata);
            if (boundTarget == null) continue; // placeholder / unparseable

            var allowed = info.LookupAllTargets[lookupAttr];
            var allowedList = string.Join(", ", allowed);

            // (1)+(2) explicit nav-property suffix
            if (navTarget != null)
            {
                if (!allowed.Any(t => string.Equals(t, navTarget, StringComparison.OrdinalIgnoreCase)))
                {
                    report.Add(new ValidationFinding
                    {
                        TestId = tc.Id,
                        StepNumber = step.StepNumber,
                        Severity = ValidationSeverity.Warning,
                        Code = "POLYMORPH_TARGET_INVALID",
                        Message = $"Lookup '{lookupAttr}' has no target '{navTarget}' (from nav-property " +
                                  $"'{navProp}'). Allowed targets: {allowedList}.",
                        Suggestion = $"Use one of '{lookupAttr}_<target>@odata.bind' with target in: {allowedList}."
                    });
                    continue;
                }
                if (!string.Equals(navTarget, boundTarget, StringComparison.OrdinalIgnoreCase))
                {
                    report.Add(new ValidationFinding
                    {
                        TestId = tc.Id,
                        StepNumber = step.StepNumber,
                        Severity = ValidationSeverity.Warning,
                        Code = "POLYMORPH_TARGET_INVALID",
                        Message = $"Nav-property '{navProp}' disambiguates lookup '{lookupAttr}' to target " +
                                  $"'{navTarget}', but the bound record is a '{boundTarget}'.",
                        Suggestion = $"Bind to a '{navTarget}' record, or use '{lookupAttr}_{boundTarget}@odata.bind'."
                    });
                    continue;
                }
            }

            // (3) bound entity must be an allowed target of the lookup
            if (!allowed.Any(t => string.Equals(t, boundTarget, StringComparison.OrdinalIgnoreCase)))
            {
                report.Add(new ValidationFinding
                {
                    TestId = tc.Id,
                    StepNumber = step.StepNumber,
                    Severity = ValidationSeverity.Warning,
                    Code = "POLYMORPH_TARGET_INVALID",
                    Message = $"Lookup '{lookupAttr}' is bound to a '{boundTarget}' record, which is not an " +
                              $"allowed target. Allowed targets: {allowedList}.",
                    Suggestion = $"Bind to a record of one of: {allowedList}."
                });
            }
        }
    }

    /// <summary>
    /// Maps an @odata.bind navigation-property name to a lookup attribute and an
    /// optional disambiguation target. A direct match (navProp is itself a lookup
    /// attribute) yields no target suffix. A polymorph nav-property has the shape
    /// '&lt;lookupAttr&gt;_&lt;targetLogicalName&gt;'; the longest matching lookup
    /// attribute prefix wins so attributes whose names share a prefix resolve
    /// unambiguously. Returns (null, null) when no lookup attribute matches.
    /// </summary>
    private static (string? LookupAttr, string? NavTarget) ResolveLookupNav(string navProp, EntityMetadataInfo info)
    {
        if (info.LookupAllTargets.ContainsKey(navProp)) return (navProp, null);

        string? bestAttr = null;
        string? bestTarget = null;
        foreach (var attr in info.LookupAllTargets.Keys)
        {
            if (navProp.Length <= attr.Length + 1) continue;
            if (!navProp.StartsWith(attr + "_", StringComparison.OrdinalIgnoreCase)) continue;
            if (bestAttr == null || attr.Length > bestAttr.Length)
            {
                bestAttr = attr;
                bestTarget = navProp.Substring(attr.Length + 1);
            }
        }
        return (bestAttr, bestTarget);
    }

    /// <summary>
    /// Extracts the bound entity's logical name from an @odata.bind value such as
    /// "/accounts(guid)" or "accounts(guid)". Returns null for placeholders, empty
    /// values, or shapes that do not carry an entity set.
    /// </summary>
    private static string? ParseBindTarget(object? raw, EntityMetadataCache metadata)
    {
        if (raw is not string s) return null;
        s = s.Trim();
        if (s.Length == 0 || s.IndexOf('{') >= 0) return null; // placeholder
        s = s.TrimStart('/');
        var paren = s.IndexOf('(');
        var entitySet = paren > 0 ? s.Substring(0, paren) : s;
        entitySet = entitySet.Trim();
        if (entitySet.Length == 0 || entitySet.IndexOf('/') >= 0) return null;
        return metadata.ResolveLogicalName(entitySet);
    }

    private static IEnumerable<string> CollectFieldNames(TestStep step)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in step.Fields.Keys)
        {
            // Skip lookup-bind keys (navigation property names, not logical attributes).
            if (key.IndexOf("@odata.bind", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (key.Contains('{')) continue;
            if (seen.Add(key)) yield return key;
        }

        if (step.Filter != null)
        {
            foreach (var f in step.Filter)
            {
                var field = f.Field;
                if (string.IsNullOrWhiteSpace(field) || field!.Contains('{')) continue;
                // Skip the OData lookup shape (flagged separately by FILTER_FIELD_NOT_LOGICAL).
                if (field.StartsWith("_") && field.EndsWith("_value")) continue;
                if (seen.Add(field)) yield return field;
            }
        }

        if (!string.IsNullOrWhiteSpace(step.Field) && !step.Field!.Contains('{') && seen.Add(step.Field))
            yield return step.Field;
    }

    private static string? SuggestField(string actual, EntityMetadataInfo info)
    {
        if (string.IsNullOrWhiteSpace(actual)) return null;
        var best = (Name: (string?)null, Distance: int.MaxValue);
        foreach (var candidate in info.AttributeTypes.Keys)
        {
            var d = LevenshteinDistance(actual.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (d < best.Distance) best = (candidate, d);
        }
        return best.Distance <= 2 ? best.Name : null;
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

    /// <summary>
    /// Suggest a known step key within Levenshtein distance 2 of the input. Returns
    /// null if no close match exists (most unknown keys are wrong concepts, not typos,
    /// e.g. 'timeoutMs' is far from 'timeoutSeconds', so the generic hint is shown).
    /// </summary>
    internal static string? SuggestStepKey(string actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) return null;

        var best = (Name: (string?)null, Distance: int.MaxValue);
        foreach (var candidate in _knownStepKeys)
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
