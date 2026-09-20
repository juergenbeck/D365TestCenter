using System.Linq;
using D365TestCenter.Core.Validation;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests for <see cref="PackFileReader"/>, the shared pack reader behind the CLI
/// <c>validate</c> command.
///
/// The reason it exists is the record pack: its entries carry the Dataverse columns of
/// <c>jbe_testcase</c> with the executable definition inside <c>jbe_definitionjson</c>.
/// Read as a plain test case such an entry yields no steps at all, so a pre-ADR-0004
/// <c>preconditions</c>/<c>assertions</c> pair inside the definition passed validation
/// unseen - exactly the shape of the demo packs shipped with the product.
/// The three older pack shapes must keep reading identically, which the rest of the tests pin.
/// </summary>
public class PackFileReaderTests
{
    // Record pack entry, as webresource/packs/standard.json spells it: Dataverse columns
    // plus an embedded definition object that still carries the pre-ADR-0004 schema.
    private const string RecordPackWithObsoleteSchema = """
    {
      "packId": "demo",
      "testCases": [
        {
          "jbe_testcaseid": "d1d2d3d4-0001-4000-8000-100000000001",
          "jbe_testid": "STD-TC01",
          "jbe_title": "Lead-Qualifizierung",
          "jbe_category": 100000005,
          "jbe_tags": "Lead,Opportunity,Sales",
          "jbe_enabled": true,
          "jbe_definitionjson": {
            "preconditions": { "createAccount": true },
            "steps": [
              { "stepNumber": 1, "action": "CreateRecord", "entity": "lead", "alias": "lead1",
                "fields": { "lastname": "{GENERATED:lastname}" } }
            ],
            "assertions": [
              { "target": "Record:lead1", "field": "lastname", "operator": "IsNotNull" }
            ]
          }
        }
      ]
    }
    """;

    // ════════════════════════════════════════════════════════════════
    //  Record pack (the defect this type fixes)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void RecordPack_ObsoleteSchemaInsideDefinition_IsReportedByValidator()
    {
        var testCases = PackFileReader.Read(RecordPackWithObsoleteSchema);

        var tc = Assert.Single(testCases);
        var report = new PackValidator().ValidateOne(tc);
        var codes = report.Findings.Select(f => f.Code).ToList();

        Assert.Contains("PRECONDITIONS_OBSOLETE", codes);
        Assert.Contains("ASSERTIONS_OBSOLETE", codes);
    }

    [Fact]
    public void RecordPack_ObsoletePreconditionsObject_MessageNamesTheFlagCount()
    {
        var tc = Assert.Single(PackFileReader.Read(RecordPackWithObsoleteSchema));
        var report = new PackValidator().ValidateOne(tc);

        var finding = report.Findings.Single(f => f.Code == "PRECONDITIONS_OBSOLETE");
        // The object form carries one flag. Counting only JArray entries used to report
        // "(0 entries)" here, which reads like an empty leftover instead of a live flag.
        Assert.Contains("1", finding.Message);
        Assert.DoesNotContain("(0 ", finding.Message);
    }

    [Fact]
    public void RecordPack_UnpacksDefinitionAndFillsColumnsIntoTheTestCase()
    {
        var tc = Assert.Single(PackFileReader.Read(RecordPackWithObsoleteSchema));

        Assert.Equal("STD-TC01", tc.Id);
        Assert.Equal("Lead-Qualifizierung", tc.Title);
        Assert.Equal("100000005", tc.Category);
        Assert.Equal(new[] { "Lead", "Opportunity", "Sales" }, tc.Tags);
        Assert.True(tc.Enabled);
        var step = Assert.Single(tc.Steps);
        Assert.Equal("CreateRecord", step.Action);
        Assert.Equal("lead1", step.Alias);
    }

    [Fact]
    public void RecordPack_DefinitionAsEmbeddedJsonString_IsUnpackedToo()
    {
        // A plain Dataverse export writes jbe_definitionjson as a string, not as an object.
        const string json = """
        { "testCases": [ {
            "jbe_testid": "EXP-01",
            "jbe_title": "Export",
            "jbe_definitionjson": "{\"steps\":[{\"stepNumber\":1,\"action\":\"Wait\",\"waitSeconds\":1}]}"
        } ] }
        """;

        var tc = Assert.Single(PackFileReader.Read(json));

        Assert.Equal("EXP-01", tc.Id);
        Assert.Equal("Wait", Assert.Single(tc.Steps).Action);
    }

    [Fact]
    public void RecordPack_DefinitionOfTheRecordWins_OverTheColumns()
    {
        // The definition is the executable truth: import-pack writes the test id into it.
        // A column is only a fallback for what the definition does not carry itself.
        const string json = """
        { "testCases": [ {
            "jbe_testid": "COLUMN-ID",
            "jbe_title": "Column title",
            "jbe_definitionjson": { "id": "DEFINITION-ID", "title": "Definition title", "steps": [] }
        } ] }
        """;

        var tc = Assert.Single(PackFileReader.Read(json));

        Assert.Equal("DEFINITION-ID", tc.Id);
        Assert.Equal("Definition title", tc.Title);
    }

    [Fact]
    public void RecordPack_EmptyDefinition_IsSkippedInsteadOfYieldingAnEmptyTestCase()
    {
        const string json = """
        { "testCases": [ { "jbe_testid": "EMPTY-01", "jbe_definitionjson": null } ] }
        """;

        Assert.Empty(PackFileReader.Read(json));
    }

    // ════════════════════════════════════════════════════════════════
    //  The three pack shapes that already worked - pinned unchanged
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void SuiteWrapper_MapsTestIdOntoId()
    {
        const string json = """
        { "suiteId": "s1", "testCases": [
            { "testId": "SUI-01", "title": "First", "steps": [ { "stepNumber": 1, "action": "Wait", "waitSeconds": 1 } ] },
            { "testId": "SUI-02", "title": "Second", "steps": [] }
        ] }
        """;

        var testCases = PackFileReader.Read(json);

        Assert.Equal(new[] { "SUI-01", "SUI-02" }, testCases.Select(t => t.Id));
        Assert.Equal("First", testCases[0].Title);
        Assert.Equal("Wait", Assert.Single(testCases[0].Steps).Action);
    }

    [Fact]
    public void BareArray_MapsTestIdOntoId()
    {
        const string json = """
        [ { "testId": "ARR-01", "steps": [ { "stepNumber": 1, "action": "Wait", "waitSeconds": 1 } ] } ]
        """;

        Assert.Equal("ARR-01", Assert.Single(PackFileReader.Read(json)).Id);
    }

    [Fact]
    public void BareTestCase_IsReadAsASingleTestCase()
    {
        const string json = """
        { "testId": "ONE-01", "title": "Single", "steps": [ { "stepNumber": 1, "action": "Wait", "waitSeconds": 1 } ] }
        """;

        var tc = Assert.Single(PackFileReader.Read(json));

        Assert.Equal("ONE-01", tc.Id);
        Assert.Equal("Single", tc.Title);
    }

    [Fact]
    public void ExplicitId_IsNotOverwrittenByTestId()
    {
        const string json = """
        { "testCases": [ { "id": "REAL-01", "testId": "OTHER-01", "steps": [] } ] }
        """;

        Assert.Equal("REAL-01", Assert.Single(PackFileReader.Read(json)).Id);
    }

    [Fact]
    public void ObsoleteTopLevelArrays_StillReachTheValidator_InASuitePack()
    {
        // Regress guard: the pre-existing path (obsolete arrays directly on the test case)
        // must keep firing R10 after the reader was extracted out of the CLI.
        const string json = """
        { "testCases": [ {
            "testId": "OLD-01",
            "preconditions": [ { "entity": "account", "alias": "acc1" } ],
            "steps": [],
            "assertions": [ { "target": "Record:acc1", "field": "name", "operator": "IsNotNull" } ]
        } ] }
        """;

        var tc = Assert.Single(PackFileReader.Read(json));
        var codes = new PackValidator().ValidateOne(tc).Findings.Select(f => f.Code).ToList();

        Assert.Contains("PRECONDITIONS_OBSOLETE", codes);
        Assert.Contains("ASSERTIONS_OBSOLETE", codes);
    }

    [Fact]
    public void NonObjectRoot_YieldsNoTestCases()
    {
        Assert.Empty(PackFileReader.Read("\"just a string\""));
    }
}
