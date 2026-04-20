using System.Collections.Generic;
using D365TestCenter.Core;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Unit-Tests für <see cref="TestCenterOrchestrator"/>.
/// Fokus: Filter-Logik (ApplyFilter, MatchesAny), weil das die rein statische
/// Geschäftslogik ist. Integrationstests für RunNewTestRun / RunExistingTestRun
/// würden einen IOrganizationService-Mock brauchen (FakeXrmEasy o.ä.).
/// </summary>
public class TestCenterOrchestratorTests
{
    private static List<TestCase> MakeCases(params string[] ids)
    {
        var list = new List<TestCase>();
        foreach (var id in ids)
        {
            list.Add(new TestCase { Id = id, Title = id + " Title", Enabled = true });
        }
        return list;
    }

    [Fact]
    public void ApplyFilter_Star_ReturnsAllEnabled()
    {
        var cases = MakeCases("MGR01", "MGR02", "STD01");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "*");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ApplyFilter_EmptyString_ReturnsAllEnabled()
    {
        var cases = MakeCases("MGR01", "STD01");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ApplyFilter_Null_ReturnsAllEnabled()
    {
        var cases = MakeCases("MGR01", "STD01");
        var result = TestCenterOrchestrator.ApplyFilter(cases, null);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ApplyFilter_IgnoresDisabledCases()
    {
        var cases = MakeCases("MGR01", "MGR02");
        cases[1].Enabled = false;
        var result = TestCenterOrchestrator.ApplyFilter(cases, "*");
        Assert.Single(result);
        Assert.Equal("MGR01", result[0].Id);
    }

    [Fact]
    public void ApplyFilter_ExactId_ReturnsOne()
    {
        var cases = MakeCases("MGR01", "MGR02", "STD01");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "MGR01");
        Assert.Single(result);
        Assert.Equal("MGR01", result[0].Id);
    }

    [Fact]
    public void ApplyFilter_ExactId_CaseInsensitive()
    {
        var cases = MakeCases("MGR01", "STD01");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "mgr01");
        Assert.Single(result);
        Assert.Equal("MGR01", result[0].Id);
    }

    [Fact]
    public void ApplyFilter_WildcardPrefix_MatchesAllMgr()
    {
        var cases = MakeCases("MGR01", "MGR02", "MGR07", "STD01");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "MGR*");
        Assert.Equal(3, result.Count);
        Assert.All(result, tc => Assert.StartsWith("MGR", tc.Id));
    }

    [Fact]
    public void ApplyFilter_WildcardSuffix_MatchesAllEnd01()
    {
        var cases = MakeCases("MGR01", "STD01", "MGR02");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "*01");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ApplyFilter_WildcardBoth_MatchesContains()
    {
        var cases = MakeCases("ABC01", "XYZ02", "ABC03");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "*BC*");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ApplyFilter_CommaSeparated_ReturnsMultiple()
    {
        var cases = MakeCases("MGR01", "MGR02", "MGR03", "STD01");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "MGR01,MGR02,STD01");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ApplyFilter_CommaSeparatedWithWildcards_MatchesBoth()
    {
        var cases = MakeCases("MGR01", "MGR02", "STD01", "STD02");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "MGR*,STD01");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ApplyFilter_TagPrefix_FiltersByTag()
    {
        var cases = MakeCases("MGR01", "MGR02", "STD01");
        cases[0].Tags = new List<string> { "merge", "DYN-8113" };
        cases[1].Tags = new List<string> { "merge" };
        cases[2].Tags = new List<string> { "crud" };
        var result = TestCenterOrchestrator.ApplyFilter(cases, "tag:merge");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ApplyFilter_TagPrefix_CaseInsensitive()
    {
        var cases = MakeCases("MGR01");
        cases[0].Tags = new List<string> { "Merge" };
        var result = TestCenterOrchestrator.ApplyFilter(cases, "tag:merge");
        Assert.Single(result);
    }

    [Fact]
    public void ApplyFilter_CategoryPrefix_FiltersByCategory()
    {
        var cases = MakeCases("MGR01", "STD01");
        cases[0].Category = "Merge";
        cases[1].Category = "CRUD";
        var result = TestCenterOrchestrator.ApplyFilter(cases, "category:Merge");
        Assert.Single(result);
        Assert.Equal("MGR01", result[0].Id);
    }

    [Fact]
    public void ApplyFilter_CategoryPrefix_CaseInsensitive()
    {
        var cases = MakeCases("MGR01");
        cases[0].Category = "Merge";
        var result = TestCenterOrchestrator.ApplyFilter(cases, "category:merge");
        Assert.Single(result);
    }

    [Fact]
    public void ApplyFilter_UnknownId_ReturnsEmpty()
    {
        var cases = MakeCases("MGR01", "STD01");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "XYZ99");
        Assert.Empty(result);
    }

    [Fact]
    public void ApplyFilter_WithWhitespace_Trimmed()
    {
        var cases = MakeCases("MGR01", "MGR02");
        var result = TestCenterOrchestrator.ApplyFilter(cases, "  MGR01  ,  MGR02  ");
        Assert.Equal(2, result.Count);
    }
}
