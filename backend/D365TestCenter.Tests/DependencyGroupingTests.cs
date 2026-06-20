using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer TestRunner.BuildDependencyGroups (ADR-0009 Phase 1, Befund 3).
/// Connected Components ueber dependsOn (+ sharedContext defensiv). Pinnt die
/// Gruppierung, die der Koordinator fuers Chunking und der Worker fuer den
/// Gruppen-Grenzen-Continuation-Cursor braucht.
/// </summary>
public class DependencyGroupingTests
{
    private static TestCase Tc(string id, string[]? dependsOn = null, string? sharedContext = null)
        => new TestCase
        {
            Id = id,
            Title = id,
            DependsOn = dependsOn?.ToList(),
            SharedContext = sharedContext
        };

    /// <summary>Gibt die IDs einer Gruppe zurueck (Reihenfolge erhalten).</summary>
    private static List<string> Ids(List<TestCase> group) => group.Select(t => t.Id).ToList();

    [Fact]
    public void AllIndependent_EachOwnGroup_InputOrderPreserved()
    {
        var tests = new List<TestCase> { Tc("A"), Tc("B"), Tc("C") };

        var groups = TestRunner.BuildDependencyGroups(tests);

        Assert.Equal(3, groups.Count);
        Assert.Equal(new[] { "A" }, Ids(groups[0]));
        Assert.Equal(new[] { "B" }, Ids(groups[1]));
        Assert.Equal(new[] { "C" }, Ids(groups[2]));
    }

    [Fact]
    public void DependsOnChain_FormsSingleGroup_InInputOrder()
    {
        var tests = new List<TestCase>
        {
            Tc("A"),
            Tc("B", dependsOn: new[] { "A" }),
            Tc("C", dependsOn: new[] { "B" })
        };

        var groups = TestRunner.BuildDependencyGroups(tests);

        Assert.Single(groups);
        Assert.Equal(new[] { "A", "B", "C" }, Ids(groups[0]));
    }

    [Fact]
    public void SharedContext_FormsSingleGroup_EvenWithoutDependsOn()
    {
        var tests = new List<TestCase>
        {
            Tc("A", sharedContext: "grp1"),
            Tc("B", sharedContext: "grp1")
        };

        var groups = TestRunner.BuildDependencyGroups(tests);

        Assert.Single(groups);
        Assert.Equal(new[] { "A", "B" }, Ids(groups[0]));
    }

    [Fact]
    public void MixedIndependentAndGroups_OrderedByMinIndex()
    {
        // [A, B(dep A), C, D(ctx=g), E(ctx=g)]
        // -> [A,B] (min 0), [C] (min 2), [D,E] (min 3)
        var tests = new List<TestCase>
        {
            Tc("A"),
            Tc("B", dependsOn: new[] { "A" }),
            Tc("C"),
            Tc("D", sharedContext: "g"),
            Tc("E", sharedContext: "g")
        };

        var groups = TestRunner.BuildDependencyGroups(tests);

        Assert.Equal(3, groups.Count);
        Assert.Equal(new[] { "A", "B" }, Ids(groups[0]));
        Assert.Equal(new[] { "C" }, Ids(groups[1]));
        Assert.Equal(new[] { "D", "E" }, Ids(groups[2]));
    }

    [Fact]
    public void UnknownDependency_OutsideSet_ProducesNoEdge()
    {
        // A haengt von einem Test ausserhalb der Liste ab -> eigene Singleton-Gruppe.
        var tests = new List<TestCase>
        {
            Tc("A", dependsOn: new[] { "ZZZ-NOT-IN-SET" }),
            Tc("B")
        };

        var groups = TestRunner.BuildDependencyGroups(tests);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "A" }, Ids(groups[0]));
        Assert.Equal(new[] { "B" }, Ids(groups[1]));
    }

    [Fact]
    public void DiamondDependency_AllInOneGroup()
    {
        // A; B,C depend on A; D depends on B and C -> ein Connected Component.
        var tests = new List<TestCase>
        {
            Tc("A"),
            Tc("B", dependsOn: new[] { "A" }),
            Tc("C", dependsOn: new[] { "A" }),
            Tc("D", dependsOn: new[] { "B", "C" })
        };

        var groups = TestRunner.BuildDependencyGroups(tests);

        Assert.Single(groups);
        Assert.Equal(new[] { "A", "B", "C", "D" }, Ids(groups[0]));
    }

    [Fact]
    public void TwoSeparateChains_TwoGroups()
    {
        var tests = new List<TestCase>
        {
            Tc("A"),
            Tc("B", dependsOn: new[] { "A" }),
            Tc("X"),
            Tc("Y", dependsOn: new[] { "X" })
        };

        var groups = TestRunner.BuildDependencyGroups(tests);

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "A", "B" }, Ids(groups[0]));
        Assert.Equal(new[] { "X", "Y" }, Ids(groups[1]));
    }

    [Fact]
    public void DependsOn_IsCaseInsensitive_LikePassedTestIds()
    {
        var tests = new List<TestCase>
        {
            Tc("A"),
            Tc("B", dependsOn: new[] { "a" })  // andere Schreibung
        };

        var groups = TestRunner.BuildDependencyGroups(tests);

        Assert.Single(groups);
        Assert.Equal(new[] { "A", "B" }, Ids(groups[0]));
    }

    [Fact]
    public void EveryTest_AppearsExactlyOnce_CountPreserved()
    {
        var tests = new List<TestCase>
        {
            Tc("A"),
            Tc("B", dependsOn: new[] { "A" }),
            Tc("C"),
            Tc("D", sharedContext: "g"),
            Tc("E", sharedContext: "g")
        };

        var groups = TestRunner.BuildDependencyGroups(tests);

        var flat = groups.SelectMany(g => g.Select(t => t.Id)).ToList();
        Assert.Equal(tests.Count, flat.Count);
        Assert.Equal(new[] { "A", "B", "C", "D", "E" }.OrderBy(x => x),
                     flat.OrderBy(x => x));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var groups = TestRunner.BuildDependencyGroups(new List<TestCase>());
        Assert.Empty(groups);
    }
}
