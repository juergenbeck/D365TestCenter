using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core.Validation;

/// <summary>
/// Reads the test cases out of a pack file, whatever shape the pack has. Shared by the
/// CLI <c>validate</c> command so every pack form reaches the <see cref="PackValidator"/>
/// as a plain <see cref="TestCase"/>.
///
/// Supported shapes:
///   1. bare array            <c>[ { "testId": "...", "steps": [...] }, ... ]</c>
///   2. suite wrapper         <c>{ "testCases": [ ... ] }</c>  (build-pack / workspace packs)
///   3. bare test case        <c>{ "testId": "...", "steps": [...] }</c>
///   4. record pack           entries carrying the Dataverse columns of <c>jbe_testcase</c>,
///                            with the executable definition inside <c>jbe_definitionjson</c>
///                            (the demo packs shipped in <c>webresource/packs</c>)
///
/// Shape 4 is the reason this type exists. A record-pack entry carries no TestCase
/// properties at all, so deserializing it directly yields an empty test case: no steps to
/// check and, worse, an obsolete pre-ADR-0004 <c>preconditions</c>/<c>assertions</c> inside
/// the definition that rule R10 can no longer see. Unpacking here mirrors what
/// <c>TestCenterOrchestrator.LoadTestCases</c> does when it reads the same records from
/// Dataverse: deserialize the definition, then fill in the id, title, tags and category from
/// the columns when the definition does not carry them itself.
/// </summary>
public static class PackFileReader
{
    // Dataverse columns of jbe_testcase, as a record pack spells them.
    private const string ColDefinition = "jbe_definitionjson";
    private const string ColTestId = "jbe_testid";
    private const string ColTitle = "jbe_title";
    private const string ColTags = "jbe_tags";
    private const string ColCategory = "jbe_category";
    private const string ColEnabled = "jbe_enabled";

    /// <summary>
    /// Same reader settings the engine uses (<c>TestCenterOrchestrator.JsonReadSettings</c>):
    /// with <see cref="MetadataPropertyHandling.Ignore"/> a <c>$type</c> property inside a
    /// step stays an ordinary JSON property instead of being swallowed as Newtonsoft type
    /// metadata, which is what the ExecuteRequest rules expect to see.
    /// </summary>
    private static readonly JsonSerializer Serializer = JsonSerializer.Create(
        new JsonSerializerSettings { MetadataPropertyHandling = MetadataPropertyHandling.Ignore });

    /// <summary>Reads a pack file from disk. See <see cref="Read"/> for the supported shapes.</summary>
    public static List<TestCase> ReadFile(string path) => Read(File.ReadAllText(path));

    /// <summary>Reads pack JSON and returns its test cases. Never returns null.</summary>
    public static List<TestCase> Read(string json)
    {
        var root = JToken.Parse(json);

        if (root is JArray topArray) return ReadEntries(topArray);

        if (root is JObject obj)
        {
            if (obj["testCases"] is JArray tcArr) return ReadEntries(tcArr);

            var single = ReadEntry(obj);
            return single != null ? new List<TestCase> { single } : new List<TestCase>();
        }

        return new List<TestCase>();
    }

    private static List<TestCase> ReadEntries(JArray entries)
        => entries.OfType<JObject>()
                  .Select(ReadEntry)
                  .Where(tc => tc != null)
                  .Select(tc => tc!)
                  .ToList();

    private static TestCase? ReadEntry(JObject entry)
        => entry[ColDefinition] != null ? ReadRecordEntry(entry) : ReadTestCaseEntry(entry);

    /// <summary>
    /// Shapes 1 to 3: the entry already is a test case. Maps the workspace-pack field
    /// <c>testId</c> onto the model's <c>id</c> so the validator reports the correct test ID.
    /// </summary>
    private static TestCase? ReadTestCaseEntry(JObject entry)
    {
        if (entry["id"] == null && entry["testId"] is JToken testId)
        {
            entry = (JObject)entry.DeepClone();
            entry["id"] = testId;
        }
        return entry.ToObject<TestCase>(Serializer);
    }

    /// <summary>
    /// Shape 4: a jbe_testcase record. The executable definition sits in
    /// <c>jbe_definitionjson</c>, either as an embedded object (demo packs) or as a JSON
    /// string (a plain Dataverse export). Columns fill the gaps the definition leaves.
    /// </summary>
    private static TestCase? ReadRecordEntry(JObject entry)
    {
        var definition = ParseDefinition(entry[ColDefinition]);
        if (definition == null) return null;

        var tc = definition.ToObject<TestCase>(Serializer);
        if (tc == null) return null;

        if (string.IsNullOrWhiteSpace(tc.Id))
            tc.Id = ValueAsString(entry[ColTestId]) ?? "";
        if (string.IsNullOrWhiteSpace(tc.Title))
            tc.Title = ValueAsString(entry[ColTitle]) ?? "";
        if (string.IsNullOrWhiteSpace(tc.Category))
            tc.Category = ValueAsString(entry[ColCategory]);
        if (tc.Tags.Count == 0)
        {
            var tags = ValueAsString(entry[ColTags]);
            if (!string.IsNullOrWhiteSpace(tags))
                tc.Tags = tags!.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        }
        if (entry[ColEnabled] is JValue { Type: JTokenType.Boolean } enabled)
            tc.Enabled = enabled.Value<bool>();

        return tc;
    }

    /// <summary>Accepts the definition as an embedded object or as an embedded JSON string.</summary>
    private static JObject? ParseDefinition(JToken? token)
    {
        switch (token)
        {
            case JObject o:
                return o;
            case JValue { Type: JTokenType.String } v:
                var raw = v.Value<string>();
                if (string.IsNullOrWhiteSpace(raw) || !raw!.TrimStart().StartsWith("{")) return null;
                try { return JObject.Parse(raw); }
                catch (JsonException) { return null; }
            default:
                return null;
        }
    }

    private static string? ValueAsString(JToken? token)
        => token == null || token.Type == JTokenType.Null ? null : token.ToString();
}
