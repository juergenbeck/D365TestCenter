using System;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// ADR-0009 Phase 4: the Dataverse source for jbe_BuildInventory. MapEntry maps a jbe_testcase
/// record (incl. picklist labels via FormattedValues) to an InventoryEntry; MatchesFilter pins the
/// filter vocabulary. InventoryBuilder.Render itself is unchanged (covered by InventoryBuilderTests).
/// </summary>
public class DataverseInventorySourceTests
{
    static Entity TestCase(string id, string title, string? domain = null,
        string? statusLabel = null, string? tags = null, string? tickets = null,
        int? level = null, string? owner = null, int? estMin = null)
    {
        var e = new Entity("jbe_testcase") { Id = Guid.NewGuid() };
        e["jbe_testid"] = id;
        e["jbe_title"] = title;
        if (tags != null) e["jbe_tags"] = tags;
        if (tickets != null) e["jbe_tickets"] = tickets;
        if (level.HasValue) e["jbe_testlevel"] = level.Value;
        if (owner != null) e["jbe_owner"] = owner;
        if (estMin.HasValue) e["jbe_estimatedminutes"] = estMin.Value;
        // jbe_domain is a free-text field; jbe_lifecyclestatus is a picklist whose label
        // comes back in FormattedValues (as Dataverse returns it).
        if (domain != null) e["jbe_domain"] = domain;
        if (statusLabel != null) { e["jbe_lifecyclestatus"] = new OptionSetValue(105710001); e.FormattedValues["jbe_lifecyclestatus"] = statusLabel; }
        return e;
    }

    [Fact]
    public void MapEntry_MapsFieldsAndPicklistLabels()
    {
        var e = TestCase("DYN-TC8", "Achter", domain: "DSGVO", statusLabel: "Aktiv",
            tags: "smoke, tier-1", tickets: "DYN-1, DYN-2", level: 2, owner: "Jane", estMin: 5);

        var entry = DataverseInventorySource.MapEntry(e);

        Assert.NotNull(entry);
        Assert.Equal("DYN-TC8", entry!.Id);
        Assert.Equal("Achter", entry.Titel);
        Assert.Equal("DSGVO", entry.Domaene);                 // free-text field
        Assert.Equal("Aktiv", entry.Status);                  // picklist label from FormattedValues
        Assert.Equal(new[] { "smoke", "tier-1" }, entry.SuiteTags);
        Assert.Equal("DYN-1, DYN-2", entry.Ticket);
        Assert.Equal("2", entry.Stufe);
        Assert.Equal("Jane", entry.Verantwortlich);
        Assert.Equal("5", entry.GeschaetztMin);
    }

    [Fact]
    public void MapEntry_NoTestId_ReturnsNull()
    {
        var e = new Entity("jbe_testcase") { Id = Guid.NewGuid() };
        e["jbe_title"] = "kein id";
        Assert.Null(DataverseInventorySource.MapEntry(e));
    }

    [Fact]
    public void MapEntry_MissingOptionalFields_EmptyStrings()
    {
        var entry = DataverseInventorySource.MapEntry(TestCase("ID-1", "Titel"));
        Assert.NotNull(entry);
        Assert.Equal("", entry!.Domaene);
        Assert.Equal("", entry.Status);
        Assert.Empty(entry.SuiteTags);
        Assert.Equal("", entry.Stufe);
        Assert.Equal("", entry.Verantwortlich);
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("DYN-TC8", true)]
    [InlineData("DYN-*", true)]
    [InlineData("OTHER-*", false)]
    [InlineData("tag:smoke", true)]
    [InlineData("tag:nope", false)]
    [InlineData("domain:DSGVO", true)]
    [InlineData("domaene:DSGVO", true)]
    [InlineData("domain:other", false)]
    public void MatchesFilter_Vocabulary(string? filter, bool expected)
    {
        var entry = DataverseInventorySource.MapEntry(
            TestCase("DYN-TC8", "Achter", domain: "DSGVO", tags: "smoke, tier-1"))!;
        Assert.Equal(expected, DataverseInventorySource.MatchesFilter(entry, filter));
    }

    [Fact]
    public void Load_FiltersAndMaps_OverFake()
    {
        var fake = new FakeDataverse();
        fake.Seed(TestCase("DYN-TC8", "Achter", tags: "smoke"));
        fake.Seed(TestCase("DYN-TC1", "Erster", tags: "regression"));
        fake.Seed(TestCase("ZP-TC1", "Zett", tags: "smoke"));

        Assert.Equal(3, DataverseInventorySource.Load(fake, "*").Entries.Count);

        var dyn = DataverseInventorySource.Load(fake, "DYN-*");
        Assert.Equal(2, dyn.Entries.Count);
        Assert.All(dyn.Entries, x => Assert.StartsWith("DYN-", x.Id));

        Assert.Equal(2, DataverseInventorySource.Load(fake, "tag:smoke").Entries.Count);
    }
}
