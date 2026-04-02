using System;

namespace D365TestCenter.Core;

/// <summary>
/// Extension methods for netstandard2.0 compatibility.
/// string.Contains(string, StringComparison) is only available in netstandard2.1+.
/// </summary>
internal static class NetStandardPolyfills
{
    /// <summary>
    /// Polyfill for string.Contains(string, StringComparison) which is not available in netstandard2.0.
    /// </summary>
    public static bool Contains(this string source, string value, StringComparison comparison)
    {
        return source.IndexOf(value, comparison) >= 0;
    }
}
