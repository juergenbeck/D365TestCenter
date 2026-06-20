using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer TestRunner.BuildChunks (ADR-0009 Phase 2, Koordinator-Chunking).
/// Packt Abhaengigkeits-Gruppen in Chunks der Zielgroesse, ohne je eine Gruppe zu
/// trennen (dependsOn-Affinitaet). Greedy in Reihenfolge, deterministisch.
/// </summary>
public class ChunkPlanningTests
{
    private static TestCase Tc(string id) => new TestCase { Id = id, Title = id };

    /// <summary>Baut eine Gruppe mit Tests g{gi}t0..t(n-1).</summary>
    private static List<TestCase> Group(int gi, int n)
        => Enumerable.Range(0, n).Select(t => Tc($"g{gi}t{t}")).ToList();

    private static List<string> Ids(List<TestCase> chunk) => chunk.Select(t => t.Id).ToList();

    [Fact]
    public void GroupsFitInOneChunk()
    {
        // [2,2,1] = 5 Tests, chunkSize 5 -> ein Chunk.
        var groups = new List<List<TestCase>> { Group(0, 2), Group(1, 2), Group(2, 1) };

        var chunks = TestRunner.BuildChunks(groups, 5);

        Assert.Single(chunks);
        Assert.Equal(5, chunks[0].Count);
    }

    [Fact]
    public void NewChunkWhenNextGroupWouldExceed()
    {
        // [3,3], chunkSize 5 -> 3+3=6 > 5 -> zwei Chunks.
        var groups = new List<List<TestCase>> { Group(0, 3), Group(1, 3) };

        var chunks = TestRunner.BuildChunks(groups, 5);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(3, chunks[0].Count);
        Assert.Equal(3, chunks[1].Count);
    }

    [Fact]
    public void OversizedSingleGroup_FormsOwnChunk()
    {
        // Eine Gruppe groesser als chunkSize kann nicht geteilt werden -> eigener Chunk.
        var groups = new List<List<TestCase>> { Group(0, 8) };

        var chunks = TestRunner.BuildChunks(groups, 5);

        Assert.Single(chunks);
        Assert.Equal(8, chunks[0].Count);
    }

    [Fact]
    public void GroupsNeverSplit_AcrossChunks()
    {
        // [2,2,2], chunkSize 3: jede weitere Gruppe wuerde 4 > 3 ergeben -> 3 Chunks von 2.
        var groups = new List<List<TestCase>> { Group(0, 2), Group(1, 2), Group(2, 2) };

        var chunks = TestRunner.BuildChunks(groups, 3);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(new[] { "g0t0", "g0t1" }, Ids(chunks[0]));
        Assert.Equal(new[] { "g1t0", "g1t1" }, Ids(chunks[1]));
        Assert.Equal(new[] { "g2t0", "g2t1" }, Ids(chunks[2]));
    }

    [Fact]
    public void GreedyPacking_MixedSizes()
    {
        // [2,1,2,1], chunkSize 3 -> [2,1]=3, [2,1]=3 -> 2 Chunks.
        var groups = new List<List<TestCase>> { Group(0, 2), Group(1, 1), Group(2, 2), Group(3, 1) };

        var chunks = TestRunner.BuildChunks(groups, 3);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new[] { "g0t0", "g0t1", "g1t0" }, Ids(chunks[0]));
        Assert.Equal(new[] { "g2t0", "g2t1", "g3t0" }, Ids(chunks[1]));
    }

    [Fact]
    public void ChunkSizeBelowOne_ClampedToOne()
    {
        var groups = new List<List<TestCase>> { Group(0, 1), Group(1, 1) };

        var chunks = TestRunner.BuildChunks(groups, 0);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Single(c));
    }

    [Fact]
    public void EmptyGroups_NoChunks()
    {
        var chunks = TestRunner.BuildChunks(new List<List<TestCase>>(), 5);
        Assert.Empty(chunks);
    }

    [Fact]
    public void EveryTest_AppearsExactlyOnce_OrderPreserved()
    {
        var groups = new List<List<TestCase>> { Group(0, 2), Group(1, 3), Group(2, 1) };

        var chunks = TestRunner.BuildChunks(groups, 4);

        var flat = chunks.SelectMany(Ids).ToList();
        var expected = new[] { "g0t0", "g0t1", "g1t0", "g1t1", "g1t2", "g2t0" };
        Assert.Equal(expected, flat); // Reihenfolge erhalten, jeder genau einmal
    }
}
