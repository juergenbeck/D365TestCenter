using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core;

/// <summary>
/// Parses JSON without Newtonsoft's date detection, so every string stays exactly as written.
///
/// <c>JObject.Parse</c>/<c>JToken.Parse</c> use <c>DateParseHandling.DateTime</c> by default: an
/// ISO string such as <c>"2026-06-01T08:00:00.0000000Z"</c> becomes a date token and is written
/// back as <c>"2026-06-01T08:00:00Z"</c> (trailing fraction dropped, offsets converted to the
/// machine's local time, <c>ToString()</c> on the token is culture-formatted). The pack pipeline
/// (build-pack, import-pack, pack reader, definition export) must transport a test definition
/// unchanged, so it parses through this helper. The engine's own deserialisation of
/// <c>jbe_definitionjson</c> deliberately keeps the default (date-typed filter and field values).
/// </summary>
public static class VerbatimJson
{
    /// <summary>Parses a JSON object; throws <see cref="JsonReaderException"/> like <c>JObject.Parse</c>.</summary>
    public static JObject ParseObject(string json) => (JObject)Parse(json, root => JObject.Load(root));

    /// <summary>Parses any JSON token; throws <see cref="JsonReaderException"/> like <c>JToken.Parse</c>.</summary>
    public static JToken ParseToken(string json) => Parse(json, root => JToken.ReadFrom(root));

    private static JToken Parse(string json, System.Func<JsonReader, JToken> load)
    {
        using var reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None
        };
        var token = load(reader);

        // Same strictness as JObject.Parse/JToken.Parse: only comments may follow the root.
        while (reader.Read())
        {
            if (reader.TokenType != JsonToken.Comment)
                throw new JsonReaderException("Additional text encountered after finished reading JSON content.");
        }
        return token;
    }
}
