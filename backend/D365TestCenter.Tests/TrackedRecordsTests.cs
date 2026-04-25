using System;
using D365TestCenter.Core;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer B5 — TrackedRecords-Persistierung in jbe_trackedrecords.
/// Siehe ZastrPay-Feedback Bug B5.
/// </summary>
public class TrackedRecordsTests
{
    [Fact]
    public void TrackedRecord_DefaultsAreSensible()
    {
        var tr = new TrackedRecord();
        Assert.Equal("", tr.Entity);
        Assert.Equal(Guid.Empty, tr.Id);
        Assert.Null(tr.Alias);
    }

    [Fact]
    public void TestCaseResult_TrackedRecords_DefaultsToEmptyList()
    {
        var tcr = new TestCaseResult();
        Assert.NotNull(tcr.TrackedRecords);
        Assert.Empty(tcr.TrackedRecords);
    }

    [Fact]
    public void TrackedRecord_SerializesAsExpected()
    {
        var tr = new TrackedRecord
        {
            Entity = "contact",
            Id = Guid.Parse("12345678-1234-1234-1234-123456789012"),
            Alias = "myContact"
        };
        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        };
        var json = JsonConvert.SerializeObject(tr, settings);

        Assert.Contains("\"entity\":\"contact\"", json);
        Assert.Contains("\"id\":\"12345678-1234-1234-1234-123456789012\"", json);
        Assert.Contains("\"alias\":\"myContact\"", json);
    }

    [Fact]
    public void TrackedRecord_DeserializesFromJson()
    {
        const string json = """
        { "entity": "account", "id": "abcdefab-1234-5678-9012-345678901234", "alias": "acc1" }
        """;

        var tr = JsonConvert.DeserializeObject<TrackedRecord>(json);

        Assert.NotNull(tr);
        Assert.Equal("account", tr!.Entity);
        Assert.Equal(Guid.Parse("abcdefab-1234-5678-9012-345678901234"), tr.Id);
        Assert.Equal("acc1", tr.Alias);
    }

    [Fact]
    public void TestCaseResult_RoundTripWithTrackedRecords()
    {
        var tcr = new TestCaseResult
        {
            TestId = "TC-01",
            Title = "Test",
            TrackedRecords =
            {
                new TrackedRecord { Entity = "account", Id = Guid.NewGuid(), Alias = "acc1" },
                new TrackedRecord { Entity = "contact", Id = Guid.NewGuid(), Alias = "con1" }
            }
        };

        var json = JsonConvert.SerializeObject(tcr);
        var roundtrip = JsonConvert.DeserializeObject<TestCaseResult>(json);

        Assert.NotNull(roundtrip);
        Assert.Equal(2, roundtrip!.TrackedRecords.Count);
        Assert.Equal("account", roundtrip.TrackedRecords[0].Entity);
        Assert.Equal("acc1", roundtrip.TrackedRecords[0].Alias);
    }
}
