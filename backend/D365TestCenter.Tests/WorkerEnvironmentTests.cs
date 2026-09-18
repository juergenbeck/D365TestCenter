using System;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für <see cref="WorkerEnvironment"/>: effektiver Wert = Value-Record vor DefaultValue vor
/// Code-Fallback; fehlende Definition -> Fallback; unparsbarer Int -> Fallback.
/// </summary>
public class WorkerEnvironmentTests
{
    private static Guid SeedDefinition(FakeDataverse fake, string schema, string? defaultValue)
    {
        var def = new Entity("environmentvariabledefinition", Guid.NewGuid());
        def["schemaname"] = schema;
        if (defaultValue != null) def["defaultvalue"] = defaultValue;
        fake.Seed(def);
        return def.Id;
    }

    private static void SeedValue(FakeDataverse fake, Guid defId, string schema, string value)
    {
        var val = new Entity("environmentvariablevalue", Guid.NewGuid());
        val["environmentvariabledefinitionid"] = new EntityReference("environmentvariabledefinition", defId);
        val["schemaname"] = schema;
        val["value"] = value;
        fake.Seed(val);
    }

    [Fact]
    public void ReadBool_ValueRecordWins()
    {
        var fake = new FakeDataverse();
        var defId = SeedDefinition(fake, WorkerSchema.EnvUseWorker, "false");
        SeedValue(fake, defId, WorkerSchema.EnvUseWorker, "true");

        Assert.True(WorkerEnvironment.ReadBool(fake, WorkerSchema.EnvUseWorker, false));
    }

    [Fact]
    public void ReadBool_FallsBackToDefaultValue()
    {
        var fake = new FakeDataverse();
        SeedDefinition(fake, WorkerSchema.EnvUseWorker, "true");

        Assert.True(WorkerEnvironment.ReadBool(fake, WorkerSchema.EnvUseWorker, false));
    }

    [Fact]
    public void ReadBool_NoDefinition_UsesCodeFallback()
    {
        var fake = new FakeDataverse();
        Assert.True(WorkerEnvironment.ReadBool(fake, WorkerSchema.EnvUseWorker, true));
        Assert.False(WorkerEnvironment.ReadBool(fake, WorkerSchema.EnvUseWorker, false));
    }

    [Fact]
    public void ReadInt_ParsesValue_ElseFallback()
    {
        var fake = new FakeDataverse();
        var defId = SeedDefinition(fake, WorkerSchema.EnvChunkSize, "10");
        Assert.Equal(10, WorkerEnvironment.ReadInt(fake, WorkerSchema.EnvChunkSize, 8));

        SeedValue(fake, defId, WorkerSchema.EnvChunkSize, "nichtZahl");
        Assert.Equal(8, WorkerEnvironment.ReadInt(fake, WorkerSchema.EnvChunkSize, 8));
    }
}
