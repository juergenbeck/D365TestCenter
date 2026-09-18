using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core;

/// <summary>
/// Testdaten-Aufbereitung: Löst Platzhalter in Dictionaries auf.
/// Nutzt die zentrale PlaceholderEngine für alle Ersetzungen.
/// </summary>
public sealed class TestDataFactory
{
    private readonly PlaceholderEngine _engine = new();

    /// <summary>
    /// Ersetzt Platzhalter in allen String-Werten eines Dictionaries.
    /// JToken-Strings werden dabei zu regulären Strings aufgelöst.
    /// </summary>
    public Dictionary<string, object?> ResolveTemplateData(
        Dictionary<string, object?> data, TestContext ctx)
    {
        return _engine.ResolveAll(data, ctx);
    }
}
