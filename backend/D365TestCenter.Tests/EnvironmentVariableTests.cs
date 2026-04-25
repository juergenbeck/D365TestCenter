using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer die EnvironmentVariable-Actions SetEnvironmentVariable und
/// RetrieveEnvironmentVariable.
/// Siehe D365TestCenter-Workspace/03_implementation/envvar-handling-in-tests.md.
/// </summary>
public class EnvironmentVariableTests
{
    // ================================================================
    //  JSON-Deserialisierung der neuen Felder
    // ================================================================

    [Fact]
    public void TestStep_SetEnvironmentVariable_DeserializesFromJson()
    {
        const string json = """
        {
            "stepNumber": 1,
            "action": "SetEnvironmentVariable",
            "schemaName": "markant_GdprRetentionDays",
            "value": "1",
            "target": "effective",
            "alias": "envSnap"
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal("SetEnvironmentVariable", step!.Action);
        Assert.Equal("markant_GdprRetentionDays", step.SchemaName);
        Assert.Equal("1", step.Value);
        Assert.Equal("effective", step.Target);
        Assert.Equal("envSnap", step.Alias);
    }

    [Fact]
    public void TestStep_RetrieveEnvironmentVariable_DeserializesFromJson()
    {
        const string json = """
        {
            "stepNumber": 2,
            "action": "RetrieveEnvironmentVariable",
            "schemaName": "markant_GdprRetentionDays",
            "source": "currentValue",
            "alias": "env"
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal("RetrieveEnvironmentVariable", step!.Action);
        Assert.Equal("markant_GdprRetentionDays", step.SchemaName);
        Assert.Equal("currentValue", step.Source);
        Assert.Equal("env", step.Alias);
    }

    [Fact]
    public void TestStep_NewFields_NullWhenNotSet()
    {
        const string json = """
        {
            "action": "CreateRecord",
            "entity": "accounts",
            "fields": { "name": "Test" }
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Null(step!.SchemaName);
        Assert.Null(step.Source);
    }

    // ================================================================
    //  EnvVarSnapshot-Model
    // ================================================================

    [Fact]
    public void EnvVarSnapshot_DefaultsAreSensible()
    {
        var snap = new EnvVarSnapshot();

        Assert.Equal("", snap.SchemaName);
        Assert.Equal(Guid.Empty, snap.DefinitionId);
        Assert.Equal("", snap.ResolvedTarget);
        Assert.False(snap.ValueRecordExistedBefore);
        Assert.Null(snap.ValueRecordId);
        Assert.Null(snap.OriginalValue);
        Assert.Null(snap.OriginalDefaultValue);
    }

    [Fact]
    public void TestContext_EnvVarSnapshots_IsEmptyList()
    {
        var ctx = new TestContext();

        Assert.NotNull(ctx.EnvVarSnapshots);
        Assert.Empty(ctx.EnvVarSnapshots);
    }

    // ================================================================
    //  Handler-Logik per Fake-IOrganizationService
    // ================================================================

    [Fact]
    public void SetEnvironmentVariable_EffectiveNoValueRecord_WritesDefaultValue()
    {
        var fake = new FakeOrgService();
        var defId = Guid.NewGuid();
        fake.SeedDefinition(defId, "markant_TestFlag", "originalDefault");

        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "SetEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Value = "newValue",
            Target = "effective",
            Alias = "envSnap"
        };

        InvokeSet(runner, step, ctx);

        Assert.Single(ctx.EnvVarSnapshots);
        var snap = ctx.EnvVarSnapshots[0];
        Assert.Equal("defaultValue", snap.ResolvedTarget);
        Assert.Equal("originalDefault", snap.OriginalDefaultValue);
        Assert.False(snap.ValueRecordExistedBefore);

        Assert.Equal("newValue", fake.GetDefaultValue(defId));
        Assert.False(fake.HasValueRecord(defId));
    }

    [Fact]
    public void SetEnvironmentVariable_EffectiveWithValueRecord_WritesCurrentValue()
    {
        var fake = new FakeOrgService();
        var defId = Guid.NewGuid();
        var valueId = Guid.NewGuid();
        fake.SeedDefinition(defId, "markant_TestFlag", "originalDefault");
        fake.SeedValueRecord(valueId, defId, "markant_TestFlag", "originalCurrent");

        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "SetEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Value = "newValue",
            Target = "effective",
            Alias = "envSnap"
        };

        InvokeSet(runner, step, ctx);

        Assert.Single(ctx.EnvVarSnapshots);
        var snap = ctx.EnvVarSnapshots[0];
        Assert.Equal("currentValue", snap.ResolvedTarget);
        Assert.True(snap.ValueRecordExistedBefore);
        Assert.Equal("originalCurrent", snap.OriginalValue);

        Assert.Equal("newValue", fake.GetValueRecord(valueId));
        Assert.Equal("originalDefault", fake.GetDefaultValue(defId));
    }

    [Fact]
    public void SetEnvironmentVariable_ExplicitCurrentValue_CreatesRecordIfMissing()
    {
        var fake = new FakeOrgService();
        var defId = Guid.NewGuid();
        fake.SeedDefinition(defId, "markant_TestFlag", "originalDefault");

        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "SetEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Value = "newValue",
            Target = "currentValue",
            Alias = "envSnap"
        };

        InvokeSet(runner, step, ctx);

        Assert.Single(ctx.EnvVarSnapshots);
        var snap = ctx.EnvVarSnapshots[0];
        Assert.Equal("currentValue", snap.ResolvedTarget);
        Assert.False(snap.ValueRecordExistedBefore);
        Assert.NotNull(snap.ValueRecordId);

        Assert.True(fake.HasValueRecord(defId));
        Assert.Equal("originalDefault", fake.GetDefaultValue(defId));
    }

    [Fact]
    public void SetEnvironmentVariable_NoAlias_StillCreatesSnapshot()
    {
        // FB-30 Fix (Plugin v5.3.1): Snapshot wird IMMER erstellt, nicht
        // nur wenn alias gesetzt ist. Verhindert dauerhafte EnvVar-Aenderungen
        // durch Tests die alias vergessen haben.
        var fake = new FakeOrgService();
        var defId = Guid.NewGuid();
        var valueId = Guid.NewGuid();
        fake.SeedDefinition(defId, "markant_TestFlag", "originalDefault");
        fake.SeedValueRecord(valueId, defId, "markant_TestFlag", "originalCurrent");

        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "SetEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Value = "newValue",
            Target = "effective"
            // KEIN alias gesetzt!
        };

        InvokeSet(runner, step, ctx);

        Assert.Single(ctx.EnvVarSnapshots);
        var snap = ctx.EnvVarSnapshots[0];
        Assert.Equal("currentValue", snap.ResolvedTarget);
        Assert.True(snap.ValueRecordExistedBefore);
        Assert.Equal("originalCurrent", snap.OriginalValue);
        Assert.Equal("newValue", fake.GetValueRecord(valueId));
    }

    [Fact]
    public void SetEnvironmentVariable_UnknownTarget_Throws()
    {
        var fake = new FakeOrgService();
        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "SetEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Value = "x",
            Target = "bogus"
        };

        fake.SeedDefinition(Guid.NewGuid(), "markant_TestFlag", "d");

        var ex = Assert.Throws<InvalidOperationException>(() => InvokeSet(runner, step, ctx));
        Assert.Contains("Unbekanntes target", ex.Message);
    }

    [Fact]
    public void SetEnvironmentVariable_UnknownDefinition_Throws()
    {
        var fake = new FakeOrgService();
        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "SetEnvironmentVariable",
            SchemaName = "markant_DoesNotExist",
            Value = "x"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => InvokeSet(runner, step, ctx));
        Assert.Contains("nicht gefunden", ex.Message);
    }

    [Fact]
    public void RetrieveEnvironmentVariable_Effective_FallsBackToDefault()
    {
        var fake = new FakeOrgService();
        var defId = Guid.NewGuid();
        fake.SeedDefinition(defId, "markant_TestFlag", "theDefault");

        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "RetrieveEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Source = "effective",
            Alias = "env"
        };

        InvokeRetrieve(runner, step, ctx);

        Assert.True(ctx.FoundRecords.ContainsKey("env"));
        var retrieved = ctx.FoundRecords["env"];
        Assert.Equal("theDefault", retrieved.GetAttributeValue<string>("value"));
        Assert.Equal("defaultValue", retrieved.GetAttributeValue<string>("resolvedsource"));
    }

    [Fact]
    public void RetrieveEnvironmentVariable_Effective_PrefersCurrentValue()
    {
        var fake = new FakeOrgService();
        var defId = Guid.NewGuid();
        var valueId = Guid.NewGuid();
        fake.SeedDefinition(defId, "markant_TestFlag", "theDefault");
        fake.SeedValueRecord(valueId, defId, "markant_TestFlag", "theCurrent");

        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "RetrieveEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Source = "effective",
            Alias = "env"
        };

        InvokeRetrieve(runner, step, ctx);

        var retrieved = ctx.FoundRecords["env"];
        Assert.Equal("theCurrent", retrieved.GetAttributeValue<string>("value"));
        Assert.Equal("currentValue", retrieved.GetAttributeValue<string>("resolvedsource"));
    }

    [Fact]
    public void RetrieveEnvironmentVariable_CurrentValueSource_NullIfNoRecord()
    {
        var fake = new FakeOrgService();
        var defId = Guid.NewGuid();
        fake.SeedDefinition(defId, "markant_TestFlag", "theDefault");

        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "RetrieveEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Source = "currentValue",
            Alias = "env"
        };

        InvokeRetrieve(runner, step, ctx);

        var retrieved = ctx.FoundRecords["env"];
        Assert.Null(retrieved.GetAttributeValue<string>("value"));
        Assert.Equal("currentValue", retrieved.GetAttributeValue<string>("resolvedsource"));
    }

    [Fact]
    public void RetrieveEnvironmentVariable_DefaultValueSource_IgnoresCurrent()
    {
        var fake = new FakeOrgService();
        var defId = Guid.NewGuid();
        var valueId = Guid.NewGuid();
        fake.SeedDefinition(defId, "markant_TestFlag", "theDefault");
        fake.SeedValueRecord(valueId, defId, "markant_TestFlag", "theCurrent");

        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "RetrieveEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Source = "defaultValue",
            Alias = "env"
        };

        InvokeRetrieve(runner, step, ctx);

        var retrieved = ctx.FoundRecords["env"];
        Assert.Equal("theDefault", retrieved.GetAttributeValue<string>("value"));
        Assert.Equal("defaultValue", retrieved.GetAttributeValue<string>("resolvedsource"));
    }

    [Fact]
    public void RetrieveEnvironmentVariable_UnknownSource_Throws()
    {
        var fake = new FakeOrgService();
        var defId = Guid.NewGuid();
        fake.SeedDefinition(defId, "markant_TestFlag", "d");

        var runner = new TestRunner(fake);
        var ctx = new TestContext();

        var step = new TestStep
        {
            Action = "RetrieveEnvironmentVariable",
            SchemaName = "markant_TestFlag",
            Source = "bogus",
            Alias = "env"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => InvokeRetrieve(runner, step, ctx));
        Assert.Contains("Unbekanntes source", ex.Message);
    }

    // ================================================================
    //  Reflection-Helper zum Aufrufen der privaten Handler
    // ================================================================

    private static void InvokeSet(TestRunner runner, TestStep step, TestContext ctx)
        => InvokePrivate(runner, "StepSetEnvironmentVariable", step, ctx);

    private static void InvokeRetrieve(TestRunner runner, TestStep step, TestContext ctx)
        => InvokePrivate(runner, "StepRetrieveEnvironmentVariable", step, ctx);

    private static void InvokePrivate(TestRunner runner, string methodName, TestStep step, TestContext ctx)
    {
        var method = typeof(TestRunner).GetMethod(methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        try
        {
            method!.Invoke(runner, new object[] { step, ctx });
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }
}

// ================================================================
//  Minimaler Fake-IOrganizationService fuer EnvVar-Handler-Tests
// ================================================================

internal sealed class FakeOrgService : IOrganizationService
{
    private readonly Dictionary<Guid, Entity> _definitions = new();
    private readonly Dictionary<Guid, Entity> _values = new();

    public void SeedDefinition(Guid id, string schemaName, string defaultValue)
    {
        var e = new Entity("environmentvariabledefinition", id);
        e["environmentvariabledefinitionid"] = id;
        e["schemaname"] = schemaName;
        e["defaultvalue"] = defaultValue;
        _definitions[id] = e;
    }

    public void SeedValueRecord(Guid id, Guid defId, string schemaName, string value)
    {
        var e = new Entity("environmentvariablevalue", id);
        e["environmentvariablevalueid"] = id;
        e["environmentvariabledefinitionid"] = new EntityReference("environmentvariabledefinition", defId);
        e["schemaname"] = schemaName;
        e["value"] = value;
        _values[id] = e;
    }

    public bool HasValueRecord(Guid defId)
    {
        return _values.Values.Any(v =>
            v.GetAttributeValue<EntityReference>("environmentvariabledefinitionid")?.Id == defId);
    }

    public string? GetValueRecord(Guid valueId)
    {
        return _values.TryGetValue(valueId, out var e)
            ? e.GetAttributeValue<string>("value")
            : null;
    }

    public string? GetDefaultValue(Guid defId)
    {
        return _definitions.TryGetValue(defId, out var e)
            ? e.GetAttributeValue<string>("defaultvalue")
            : null;
    }

    public Guid Create(Entity entity)
    {
        var id = entity.Id == Guid.Empty ? Guid.NewGuid() : entity.Id;
        entity.Id = id;
        var keyField = entity.LogicalName + "id";
        if (!entity.Contains(keyField)) entity[keyField] = id;

        if (entity.LogicalName == "environmentvariablevalue")
            _values[id] = entity;
        else if (entity.LogicalName == "environmentvariabledefinition")
            _definitions[id] = entity;
        else
            throw new NotImplementedException($"FakeOrgService Create: {entity.LogicalName}");

        return id;
    }

    public void Update(Entity entity)
    {
        if (entity.LogicalName == "environmentvariablevalue")
        {
            if (!_values.TryGetValue(entity.Id, out var existing))
                throw new InvalidOperationException($"ValueRecord {entity.Id} not found");
            foreach (var kv in entity.Attributes) existing[kv.Key] = kv.Value;
        }
        else if (entity.LogicalName == "environmentvariabledefinition")
        {
            if (!_definitions.TryGetValue(entity.Id, out var existing))
                throw new InvalidOperationException($"Definition {entity.Id} not found");
            foreach (var kv in entity.Attributes) existing[kv.Key] = kv.Value;
        }
        else
        {
            throw new NotImplementedException($"FakeOrgService Update: {entity.LogicalName}");
        }
    }

    public void Delete(string entityName, Guid id)
    {
        if (entityName == "environmentvariablevalue") _values.Remove(id);
        else if (entityName == "environmentvariabledefinition") _definitions.Remove(id);
        else throw new NotImplementedException($"FakeOrgService Delete: {entityName}");
    }

    public Entity Retrieve(string entityName, Guid id, Microsoft.Xrm.Sdk.Query.ColumnSet columnSet)
    {
        if (entityName == "environmentvariablevalue" && _values.TryGetValue(id, out var v)) return v;
        if (entityName == "environmentvariabledefinition" && _definitions.TryGetValue(id, out var d)) return d;
        throw new InvalidOperationException($"Not found: {entityName} {id}");
    }

    public EntityCollection RetrieveMultiple(Microsoft.Xrm.Sdk.Query.QueryBase query)
    {
        if (query is not QueryExpression qe)
            throw new NotImplementedException("FakeOrgService RetrieveMultiple: only QueryExpression supported");

        IEnumerable<Entity> source = qe.EntityName switch
        {
            "environmentvariabledefinition" => _definitions.Values,
            "environmentvariablevalue" => _values.Values,
            _ => throw new NotImplementedException($"FakeOrgService: {qe.EntityName}")
        };

        foreach (var cond in qe.Criteria.Conditions)
        {
            source = source.Where(e =>
            {
                if (!e.Contains(cond.AttributeName)) return false;
                var actual = e[cond.AttributeName];
                var expected = cond.Values.FirstOrDefault();
                if (actual is EntityReference er) return er.Id.Equals(expected);
                return Equals(actual, expected);
            });
        }

        var list = source.ToList();
        if (qe.TopCount.HasValue) list = list.Take(qe.TopCount.Value).ToList();
        return new EntityCollection(list);
    }

    // ─── Nicht benötigte Methoden ───

    public void Associate(string en, Guid id, Microsoft.Xrm.Sdk.Relationship rel, EntityReferenceCollection rc)
        => throw new NotImplementedException();
    public void Disassociate(string en, Guid id, Microsoft.Xrm.Sdk.Relationship rel, EntityReferenceCollection rc)
        => throw new NotImplementedException();
    public OrganizationResponse Execute(OrganizationRequest request)
        => throw new NotImplementedException();
}
