using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für den async-Job-Quiescence-Wait (ADR 2026-06-28, WaitForAsyncCompletion):
///   - GenericRecordWaiter.BuildOpenAsyncOperationsQuery (Query-Struktur: Zeitfenster + optional regobj)
///   - GenericRecordWaiter.WaitForAsyncQuiescence (Stabilitätsfenster, Leerlauf, Timeout-Gegenprobe)
///   - TestRunner-Dispatch: CLI-only-Skip im Plugin-Sandbox-Pfad (AllowAsyncOperationPolling)
///
/// Korrelation primär über ein Zeitfenster (createdon >= windowStart), nicht über ein festes
/// regardingobjectid-Set (verpasst Folge-Jobs auf erzeugten Records -> Falsch-Grün). Befund LM DEV
/// 2026-06-28 in
/// 03_implementation/vorgaenge/2026-06-27-engine-waitforasynccompletion/korrelation-zeitfenster-statt-regobj.md.
/// </summary>
public class WaitForAsyncCompletionTests
{
    private static readonly Guid R1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid R2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime W = new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);

    // ── BuildOpenAsyncOperationsQuery ────────────────────────────────────────

    [Fact]
    public void BuildQuery_TargetsAsyncOperation_CountOnly_Top1()
    {
        var q = GenericRecordWaiter.BuildOpenAsyncOperationsQuery(W);

        Assert.Equal("asyncoperation", q.EntityName);
        Assert.False(q.ColumnSet.AllColumns);
        Assert.Empty(q.ColumnSet.Columns);
        Assert.Equal(1, q.TopCount);
    }

    [Fact]
    public void BuildQuery_FiltersOpenStatecodes_AndCreatedonWindow()
    {
        var q = GenericRecordWaiter.BuildOpenAsyncOperationsQuery(W);

        // Ohne regardingIds: genau zwei Bedingungen (statecode + createdon), KEIN regobj.
        Assert.Equal(2, q.Criteria.Conditions.Count);

        var statecode = q.Criteria.Conditions.Single(c => c.AttributeName == "statecode");
        Assert.Equal(ConditionOperator.In, statecode.Operator);
        Assert.Equal(3, statecode.Values.Count);
        Assert.Contains(0, statecode.Values);
        Assert.Contains(1, statecode.Values);
        Assert.Contains(2, statecode.Values);
        Assert.DoesNotContain(3, statecode.Values); // Completed darf NICHT als "offen" zählen

        var createdon = q.Criteria.Conditions.Single(c => c.AttributeName == "createdon");
        Assert.Equal(ConditionOperator.OnOrAfter, createdon.Operator);
        Assert.Equal(W, createdon.Values.Single());

        Assert.DoesNotContain(q.Criteria.Conditions, c => c.AttributeName == "regardingobjectid");
    }

    [Fact]
    public void BuildQuery_WithRegardingIds_AddsNarrowingCondition()
    {
        var q = GenericRecordWaiter.BuildOpenAsyncOperationsQuery(W, new[] { R1, R2 });

        Assert.Equal(3, q.Criteria.Conditions.Count); // statecode + createdon + regardingobjectid
        var regarding = q.Criteria.Conditions.Single(c => c.AttributeName == "regardingobjectid");
        Assert.Equal(ConditionOperator.In, regarding.Operator);
        Assert.Equal(2, regarding.Values.Count);
        Assert.Contains(R1, regarding.Values);
        Assert.Contains(R2, regarding.Values);
    }

    // ── WaitForAsyncQuiescence: Verhalten ────────────────────────────────────

    [Fact]
    public void Idle_ReturnsTrueAfterStableWindow()
    {
        // Leerlauf (Akzeptanzkriterium 4): keine offenen Jobs -> Quiescence nach stableChecks Polls.
        var svc = new FakeAsyncOpService(0);
        var ok = new GenericRecordWaiter().WaitForAsyncQuiescence(
            svc, W, null, timeoutSeconds: 5,
            pollingIntervalMs: 1, stableChecks: 3, initialWaitMs: 0);

        Assert.True(ok);
        Assert.Equal(3, svc.Calls);
    }

    [Fact]
    public void OpenThenClears_ReturnsTrue()
    {
        // zwei offene Wellen, dann Ruhe: 1,1,0,0 mit stableChecks=2 -> true nach 4 Polls.
        var svc = new FakeAsyncOpService(1, 1, 0, 0);
        var ok = new GenericRecordWaiter().WaitForAsyncQuiescence(
            svc, W, null, timeoutSeconds: 5,
            pollingIntervalMs: 1, stableChecks: 2, initialWaitMs: 0);

        Assert.True(ok);
        Assert.Equal(4, svc.Calls);
    }

    [Fact]
    public void TransientGapReopens_ResetsWindow_NoFalseQuiescence()
    {
        // Inter-Wellen-Lücke: 0,1,0,0 mit stableChecks=2. Der erste leere Poll zählt 1, dann setzt
        // der offene Job das Fenster zurück; Quiescence erst nach dem letzten Poll (Call 4). Beweist:
        // eine einzelne leere Messung löst KEIN Falsch-Grün aus (Akzeptanzkriterium 3).
        var svc = new FakeAsyncOpService(0, 1, 0, 0);
        var ok = new GenericRecordWaiter().WaitForAsyncQuiescence(
            svc, W, null, timeoutSeconds: 5,
            pollingIntervalMs: 1, stableChecks: 2, initialWaitMs: 0);

        Assert.True(ok);
        Assert.Equal(4, svc.Calls);
    }

    [Fact]
    public void StaysOpen_TimesOutFalse()
    {
        // sticky offen -> nie Quiescence -> Timeout false. Gegenprobe: kein blindes true.
        var svc = new FakeAsyncOpService(1);
        var ok = new GenericRecordWaiter().WaitForAsyncQuiescence(
            svc, W, null, timeoutSeconds: 1,
            pollingIntervalMs: 50, stableChecks: 2, initialWaitMs: 0);

        Assert.False(ok);
        Assert.True(svc.Calls >= 1);
    }

    [Fact]
    public void Poll_CarriesCreatedonWindowIntoQuery()
    {
        var svc = new FakeAsyncOpService(0);
        new GenericRecordWaiter().WaitForAsyncQuiescence(
            svc, W, null, timeoutSeconds: 5,
            pollingIntervalMs: 1, stableChecks: 1, initialWaitMs: 0);

        Assert.NotNull(svc.LastQuery);
        var createdon = svc.LastQuery!.Criteria.Conditions.Single(c => c.AttributeName == "createdon");
        Assert.Equal(W, createdon.Values.Single());
    }

    // ── TestRunner-Dispatch: CLI-only-Skip im Plugin-Sandbox-Pfad ────────────

    [Fact]
    public void Dispatch_PluginPath_SkipsWithoutExecuting()
    {
        // Ohne AllowAsyncOperationPolling (Plugin-Sandbox-Default) wird der Step geskippt, BEVOR die
        // Alias-Auflösung läuft. Ein nicht existierender Alias würde sonst Error werfen — bleibt er
        // aus, ist der Skip belegt. Kein Failure/Error.
        var runner = new TestRunner(new MinimalOrgService()); // AllowAsyncOperationPolling default false
        var result = runner.RunAll(new List<TestCase> { GhostAliasTest() });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public void Dispatch_CliPath_Executes()
    {
        // Mit AllowAsyncOperationPolling=true (CLI-Pfad) wird der Step ausgeführt: die Auflösung des
        // nicht existierenden Alias 'ghost' wirft -> Outcome Error. Beweist: NICHT geskippt.
        var runner = new TestRunner(new MinimalOrgService()) { AllowAsyncOperationPolling = true };
        var result = runner.RunAll(new List<TestCase> { GhostAliasTest() });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.ErrorCount);
    }

    private static TestCase GhostAliasTest() => new()
    {
        Id = "ASYNCWAIT-DISPATCH",
        Title = "WaitForAsyncCompletion dispatch",
        Enabled = true,
        Steps = new List<TestStep>
        {
            new()
            {
                StepNumber = 1,
                Action = "WaitForAsyncCompletion",
                Aliases = new List<string> { "ghost" },  // nicht registriert
                TimeoutSeconds = 1,
                PollingIntervalMs = 1,
                InitialWaitMs = 0
            }
        }
    };

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// IOrganizationService-Double für den Quiescence-Wait: RetrieveMultiple liefert eine Sequenz
    /// von Match-Counts (sticky am letzten Wert), produziert so viele asyncoperation-Entities und
    /// captured die letzte Query.
    /// </summary>
    private sealed class FakeAsyncOpService : IOrganizationService
    {
        private readonly Queue<int> _counts;
        private readonly int _last;
        public int Calls { get; private set; }
        public QueryExpression? LastQuery { get; private set; }

        public FakeAsyncOpService(params int[] counts)
        {
            _counts = new Queue<int>(counts);
            _last = counts.Length > 0 ? counts[^1] : 0;
        }

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            Calls++;
            LastQuery = query as QueryExpression;
            var count = _counts.Count > 0 ? _counts.Dequeue() : _last;
            var ec = new EntityCollection();
            for (var i = 0; i < count; i++) ec.Entities.Add(new Entity("asyncoperation", Guid.NewGuid()));
            return ec;
        }

        public OrganizationResponse Execute(OrganizationRequest request) => throw new NotImplementedException();
        public Guid Create(Entity entity) => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotImplementedException();
        public void Update(Entity entity) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
    }

    /// <summary>Minimaler Service für den Dispatch-Test (Metadata-Execute leer, kein echter Call nötig).</summary>
    private sealed class MinimalOrgService : IOrganizationService
    {
        public OrganizationResponse Execute(OrganizationRequest request) => new OrganizationResponse();
        public EntityCollection RetrieveMultiple(QueryBase query) => new EntityCollection();
        public Guid Create(Entity entity) => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotImplementedException();
        public void Update(Entity entity) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
    }
}
