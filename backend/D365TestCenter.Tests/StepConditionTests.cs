using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für die Step-Condition (ADR-0011, konditionale Step-Ausführung). Pinnt
/// das Verhalten durch die ECHTE Wirkung (Regel 11 / Test prüft echte Änderung):
/// ein geskippter CreateRecord-Step fasst den Service NICHT an (CreateCount bleibt 0),
/// ein ausgeführter Step legt an (CreateCount steigt). Deckt: Skip-bei-false,
/// Run-bei-true, unaufgelöster Platzhalter -> Error, all/any, all-Asserts-skipped
/// -> Outcome=Skipped, Skipped-Flag/SkipReason.
/// </summary>
public class StepConditionTests
{
    // CreateRecord mit leeren fields umgeht den Metadata-Pfad; entity "accounts"
    // mappt über KnownEntitySetNames ohne Service-Call zu "account".
    private static TestCase CreateRecordWith(StepCondition? condition) => new()
    {
        Id = "COND-CREATE",
        Title = "CreateRecord mit condition",
        Enabled = true,
        Steps = new List<TestStep>
        {
            new() { StepNumber = 1, Action = "CreateRecord", Entity = "accounts", Condition = condition }
        }
    };

    private static StepConditionClause Clause(string? left, string op, string? right)
        => new() { Left = left, Operator = op, Right = right };

    // ── Einfachklausel: Skip vs. Run (Wirkungsnachweis über CreateCount) ──

    [Fact]
    public void Condition_False_SkipsStep_ServiceUntouched()
    {
        var svc = new StubOrgService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            CreateRecordWith(new StepCondition { Left = "false", Operator = "Equals", Right = "true" })
        });

        Assert.Equal(0, svc.CreateCount); // echter Beleg: Action lief NICHT
        var step = result.Results[0].StepResults[0];
        Assert.True(step.Skipped);
        Assert.False(string.IsNullOrEmpty(step.SkipReason));
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void Condition_True_RunsStep_ServiceCalled()
    {
        var svc = new StubOrgService();
        var runner = new TestRunner(svc);

        runner.RunAll(new List<TestCase>
        {
            CreateRecordWith(new StepCondition { Left = "true", Operator = "Equals", Right = "true" })
        });

        Assert.Equal(1, svc.CreateCount); // echter Beleg: Action lief
    }

    [Fact]
    public void Condition_NoCondition_RunsStep_Unchanged()
    {
        var svc = new StubOrgService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase> { CreateRecordWith(null) });

        Assert.Equal(1, svc.CreateCount);
        Assert.False(result.Results[0].StepResults[0].Skipped);
    }

    // ── Festlegung 3: unaufgelöster Platzhalter -> harter Fehler (kein stiller Skip) ──

    [Fact]
    public void Condition_UnresolvedPlaceholder_IsError_NotSilentSkip()
    {
        var svc = new StubOrgService();
        var runner = new TestRunner(svc);

        // Unbekannter Alias bleibt wörtlich stehen (PlaceholderEngine wirft dort nicht).
        var result = runner.RunAll(new List<TestCase>
        {
            CreateRecordWith(new StepCondition
            {
                Left = "{unknownalias.fields.flag}", Operator = "Equals", Right = "true"
            })
        });

        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(TestOutcome.Error, result.Results[0].Outcome);
        Assert.Equal(0, svc.CreateCount); // weder ausgeführt noch still übersprungen
        Assert.False(result.Results[0].StepResults[0].Skipped);
    }

    [Fact]
    public void Condition_UnknownOperator_IsError()
    {
        var svc = new StubOrgService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            CreateRecordWith(new StepCondition { Left = "a", Operator = "Frobnicate", Right = "b" })
        });

        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(0, svc.CreateCount);
    }

    // ── all (AND) / any (OR) ──

    [Fact]
    public void Condition_All_AllTrue_Runs_OneFalse_Skips()
    {
        var svcRun = new StubOrgService();
        new TestRunner(svcRun).RunAll(new List<TestCase>
        {
            CreateRecordWith(new StepCondition { All = new()
                { Clause("true", "Equals", "true"), Clause("1", "Equals", "1") } })
        });
        Assert.Equal(1, svcRun.CreateCount);

        var svcSkip = new StubOrgService();
        new TestRunner(svcSkip).RunAll(new List<TestCase>
        {
            CreateRecordWith(new StepCondition { All = new()
                { Clause("true", "Equals", "true"), Clause("false", "Equals", "true") } })
        });
        Assert.Equal(0, svcSkip.CreateCount);
    }

    [Fact]
    public void Condition_Any_OneTrue_Runs_NoneTrue_Skips()
    {
        var svcRun = new StubOrgService();
        new TestRunner(svcRun).RunAll(new List<TestCase>
        {
            CreateRecordWith(new StepCondition { Any = new()
                { Clause("false", "Equals", "true"), Clause("true", "Equals", "true") } })
        });
        Assert.Equal(1, svcRun.CreateCount);

        var svcSkip = new StubOrgService();
        new TestRunner(svcSkip).RunAll(new List<TestCase>
        {
            CreateRecordWith(new StepCondition { Any = new()
                { Clause("false", "Equals", "true"), Clause("false", "Equals", "true") } })
        });
        Assert.Equal(0, svcSkip.CreateCount);
    }

    // ── Test-Outcome: alle Asserts condition-geskippt -> Skipped (nicht falsch-grün) ──

    [Fact]
    public void Outcome_AllAssertsSkipped_IsSkipped_NotPassed()
    {
        var svc = new StubOrgService();
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "COND-ASSERT-SKIP",
            Title = "Einziger Assert ist condition-geskippt",
            Enabled = true,
            Steps = new List<TestStep>
            {
                new()
                {
                    StepNumber = 1, Action = "Assert", Target = "Query", Entity = "accounts",
                    Filter = new List<FilterCondition> { new() { Field = "accountid", Operator = "eq", Value = "x" } },
                    Field = "name", Operator = "Equals", Value = "X",
                    Condition = new StepCondition { Left = "false", Operator = "Equals", Right = "true" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(TestOutcome.Skipped, result.Results[0].Outcome);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.PassedCount);
        Assert.True(result.Results[0].StepResults[0].Skipped);
    }

    [Fact]
    public void Outcome_OneAssertRuns_OneSkipped_IsPassed_PartialCoverage()
    {
        // Ein Assert läuft (condition true, Query trifft), einer ist geskippt.
        // Mind. ein Assert ausgeführt -> Passed (bewusste Teilabdeckung), NICHT Skipped.
        var svc = new StubOrgService { QueryResult = MakeAccount("Hit") };
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "COND-PARTIAL",
            Title = "Ein Assert läuft, einer geskippt",
            Enabled = true,
            Steps = new List<TestStep>
            {
                new()
                {
                    StepNumber = 1, Action = "Assert", Target = "Query", Entity = "accounts",
                    Filter = new List<FilterCondition> { new() { Field = "accountid", Operator = "eq", Value = "x" } },
                    Field = "name", Operator = "Equals", Value = "Hit",
                    Condition = new StepCondition { Left = "true", Operator = "Equals", Right = "true" }
                },
                new()
                {
                    StepNumber = 2, Action = "Assert", Target = "Query", Entity = "accounts",
                    Filter = new List<FilterCondition> { new() { Field = "accountid", Operator = "eq", Value = "x" } },
                    Field = "name", Operator = "Equals", Value = "Other",
                    Condition = new StepCondition { Left = "false", Operator = "Equals", Right = "true" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(TestOutcome.Passed, result.Results[0].Outcome);
        Assert.False(result.Results[0].StepResults[0].Skipped); // lief
        Assert.True(result.Results[0].StepResults[1].Skipped);  // geskippt
    }

    // ── Test doubles ─────────────────────────────────────────────

    private static Entity MakeAccount(string name)
    {
        var e = new Entity("account", Guid.NewGuid());
        e["name"] = name;
        return e;
    }

    private sealed class StubOrgService : IOrganizationService
    {
        public int CreateCount;
        public Entity? QueryResult;

        public Guid Create(Entity entity) { CreateCount++; return Guid.NewGuid(); }
        public void Update(Entity entity) { }
        public void Delete(string entityName, Guid id) { }
        public OrganizationResponse Execute(OrganizationRequest request) => new OrganizationResponse();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => new Entity(entityName, id);
        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            var ec = new EntityCollection();
            // Nur für Assert-Query-Tests: plugintracelog-Queries (Cleanup-Diagnostik)
            // bleiben leer, damit sie das Ergebnis nicht stören.
            var entityName = (query as QueryExpression)?.EntityName;
            if (QueryResult != null && entityName != "plugintracelog")
                ec.Entities.Add(QueryResult);
            return ec;
        }
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
    }
}
