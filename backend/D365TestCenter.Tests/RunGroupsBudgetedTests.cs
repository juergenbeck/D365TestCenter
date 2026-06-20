using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer TestRunner.RunGroupsBudgeted (ADR-0009 Phase 1, Befund 3,
/// Gruppen-Grenzen-Continuation). Pinnt: Budget-Check vor jeder Gruppe, mindestens
/// eine Gruppe pro Aufruf (Cursor schreitet fort), Resume ab Cursor, atomare Gruppe,
/// und dass eine frische Instanz gruppeninternes dependsOn korrekt aufloest (KEIN
/// Re-Seed noetig). Zeitquelle (clock) injiziert fuer deterministisches Mock-Budget.
/// </summary>
public class RunGroupsBudgetedTests
{
    private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Clock, die die uebergebenen Zeitstempel der Reihe nach liefert (letzter wiederholt sich).</summary>
    private static Func<DateTime> SeqClock(params DateTime[] times)
    {
        int i = 0;
        return () =>
        {
            var t = times[Math.Min(i, times.Length - 1)];
            i++;
            return t;
        };
    }

    private static TestCase WaitTest(string id, string[]? dependsOn = null)
        => new TestCase
        {
            Id = id,
            Title = id,
            DependsOn = dependsOn?.ToList(),
            Steps = new List<TestStep>
            {
                new TestStep { StepNumber = 1, Action = "Wait", WaitSeconds = 0, Description = id }
            }
        };

    private static List<List<TestCase>> Groups(params TestCase[][] groups)
        => groups.Select(g => g.ToList()).ToList();

    private static TestRunner NewRunner() => new TestRunner(new StubOrgService());

    private static List<string> RanIds(BudgetedRunResult r)
        => r.Run.Results.Select(x => x.TestId).ToList();

    [Fact]
    public void AllGroupsFitBudget_RunsAll_DoneTrue()
    {
        var groups = Groups(
            new[] { WaitTest("A") },
            new[] { WaitTest("B") },
            new[] { WaitTest("C") });

        var r = NewRunner().RunGroupsBudgeted(groups, 0, budgetSeconds: 1000, clock: SeqClock(T0));

        Assert.True(r.Done);
        Assert.Equal(3, r.NextGroupIndex);
        Assert.Equal(new[] { "A", "B", "C" }, RanIds(r));
        Assert.All(r.Run.Results, res => Assert.Equal(TestOutcome.Passed, res.Outcome));
    }

    [Fact]
    public void BudgetExceededBeforeSecondGroup_StopsAtGroupBoundary_NotDone()
    {
        var groups = Groups(
            new[] { WaitTest("A") },
            new[] { WaitTest("B") },
            new[] { WaitTest("C") });

        // start=T0, Check vor Gruppe 2 -> T0+100s (>= Budget 80) -> stop.
        var r = NewRunner().RunGroupsBudgeted(
            groups, 0, budgetSeconds: 80,
            clock: SeqClock(T0, T0.AddSeconds(100), T0.AddSeconds(100)));

        Assert.False(r.Done);
        Assert.Equal(1, r.NextGroupIndex);
        Assert.Equal(new[] { "A" }, RanIds(r));
    }

    [Fact]
    public void ZeroBudget_StillRunsExactlyOneGroup()
    {
        // Selbst bei Budget 0 muss mindestens eine Gruppe laufen (Cursor schreitet fort).
        var groups = Groups(
            new[] { WaitTest("A") },
            new[] { WaitTest("B") });

        var r = NewRunner().RunGroupsBudgeted(groups, 0, budgetSeconds: 0, clock: SeqClock(T0));

        Assert.False(r.Done);
        Assert.Equal(1, r.NextGroupIndex);
        Assert.Equal(new[] { "A" }, RanIds(r));
    }

    [Fact]
    public void ResumeFromCursor_RunsRemainingGroups()
    {
        var groups = Groups(
            new[] { WaitTest("A") },
            new[] { WaitTest("B") },
            new[] { WaitTest("C") });

        // Resume ab Gruppe 1 (Gruppe 0 lief in einer frueheren Welle).
        var r = NewRunner().RunGroupsBudgeted(groups, 1, budgetSeconds: 1000, clock: SeqClock(T0));

        Assert.True(r.Done);
        Assert.Equal(3, r.NextGroupIndex);
        Assert.Equal(new[] { "B", "C" }, RanIds(r));
    }

    [Fact]
    public void GroupWithDependsOn_FreshInstance_DependentRuns_NotSkipped()
    {
        // DER Kern-Korrektheitstest: eine frische Instanz, die eine Gruppe [A, B(dep A)]
        // ausfuehrt, ueberspringt B NICHT -- A laeuft zuerst, _passedTestIds bekommt A,
        // B laeuft. Kein Re-Seed noetig (Gruppen-Grenzen-Continuation).
        var groups = Groups(new[] { WaitTest("A"), WaitTest("B", dependsOn: new[] { "A" }) });

        var r = NewRunner().RunGroupsBudgeted(groups, 0, budgetSeconds: 1000, clock: SeqClock(T0));

        Assert.True(r.Done);
        Assert.Equal(new[] { "A", "B" }, RanIds(r));
        Assert.All(r.Run.Results, res => Assert.Equal(TestOutcome.Passed, res.Outcome));
    }

    [Fact]
    public void EmptyGroups_DoneTrue_NoResults()
    {
        var r = NewRunner().RunGroupsBudgeted(new List<List<TestCase>>(), 0, budgetSeconds: 1000, clock: SeqClock(T0));

        Assert.True(r.Done);
        Assert.Equal(0, r.NextGroupIndex);
        Assert.Empty(r.Run.Results);
    }

    /// <summary>Minimaler IOrganizationService-Stub: liefert leere Treffer (fuer den
    /// Plugin-Trace-Log-Retrieve am Testende), alle anderen Operationen werden von den
    /// Wait-only-Testfaellen nie aufgerufen.</summary>
    private sealed class StubOrgService : IOrganizationService
    {
        public EntityCollection RetrieveMultiple(QueryBase query) => new EntityCollection();

        public Guid Create(Entity entity) => throw new NotSupportedException();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotSupportedException();
        public void Update(Entity entity) => throw new NotSupportedException();
        public void Delete(string entityName, Guid id) => throw new NotSupportedException();
        public OrganizationResponse Execute(OrganizationRequest request) => throw new NotSupportedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotSupportedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotSupportedException();
    }
}
