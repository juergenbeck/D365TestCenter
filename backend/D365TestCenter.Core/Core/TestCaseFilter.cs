using System;
using System.Collections.Generic;
using System.Linq;

namespace D365TestCenter.Core;

/// <summary>
/// Zentrale Testfall-Filter-Logik (ADR 2026-06-30 1432). Konsolidiert die zuvor
/// in <see cref="TestCenterOrchestrator"/> (CLI-Pfad) und <c>TestCaseLoader</c>
/// (Plugin-/Coordinator-Pfad) duplizierte ApplyFilter-Logik an einer Stelle und
/// ergänzt die <b>Negation</b> (Exclude per <c>!</c>-Präfix).
///
/// Unterstützte Filter-Formate (Terme komma-getrennt, jeder Term optional mit
/// führendem <c>!</c> = Exclude):
///   "*" oder leer/null   -> alle aktivierten Tests
///   "TC01"               -> exakt diese ID
///   "TC*" / "*01" / "*BC*" -> Wildcard (Prefix/Suffix/Contains)
///   "TC01,TC02"          -> Liste
///   "tag:merge"          -> alle mit Tag
///   "category:Bridge"    -> alle in Kategorie
///   "*,!LST-STATUS-01"   -> alle AUSSER LST-STATUS-01
///   "!tag:known-flaky"   -> alle AUSSER mit Tag known-flaky
///   "LSP*,!LSP-STATUS-01"-> alle LSP* ausser LSP-STATUS-01
///
/// Semantik: Include-Menge = Vereinigung aller positiven Term-Treffer; ist KEIN
/// positiver Term vorhanden (nur Excludes), gilt als Include-Menge "alle
/// aktivierten". Exclude entfernt aus der Include-Menge alles, was ein negierter
/// Term trifft. Ergebnis = Include minus Exclude.
/// </summary>
public static class TestCaseFilter
{
    public static List<TestCase> Apply(IEnumerable<TestCase> testCases, string? filter)
    {
        var enabled = testCases.Where(tc => tc.Enabled).ToList();

        if (string.IsNullOrWhiteSpace(filter) || filter!.Trim() == "*")
            return enabled;

        var includes = new List<string>();
        var excludes = new List<string>();
        foreach (var raw in filter.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var term = raw.Trim();
            if (term.Length == 0) continue;
            if (term.StartsWith("!", StringComparison.Ordinal))
            {
                var body = term.Substring(1).Trim();
                if (body.Length > 0) excludes.Add(body);
            }
            else
            {
                includes.Add(term);
            }
        }

        // Grundmenge: alle positiven Terme vereint; ohne positiven Term -> alle aktivierten.
        IEnumerable<TestCase> result = includes.Count == 0
            ? enabled
            : enabled.Where(tc => includes.Any(sel => Matches(tc, sel)));

        if (excludes.Count > 0)
            result = result.Where(tc => !excludes.Any(sel => Matches(tc, sel)));

        return result.ToList();
    }

    /// <summary>Prüft, ob ein Testfall einen einzelnen (nicht-negierten) Selektor erfüllt.</summary>
    private static bool Matches(TestCase tc, string selector)
    {
        if (selector == "*")
            return true;

        if (selector.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = selector.Substring("tag:".Length).Trim();
            return tc.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        }

        if (selector.StartsWith("category:", StringComparison.OrdinalIgnoreCase))
        {
            var cat = selector.Substring("category:".Length).Trim();
            return (tc.Category ?? "").Equals(cat, StringComparison.OrdinalIgnoreCase);
        }

        return MatchesIdPattern(tc.Id, selector);
    }

    /// <summary>ID-Pattern-Match mit Wildcard-Support: Prefix (TC*), Suffix (*01), Contains (*BC*), exakt.</summary>
    private static bool MatchesIdPattern(string testId, string pattern)
    {
        if (pattern.Contains("*"))
        {
            if (pattern.StartsWith("*") && pattern.EndsWith("*"))
            {
                var mid = pattern.Trim('*');
                return mid.Length == 0
                    || testId.IndexOf(mid, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (pattern.StartsWith("*"))
            {
                var suffix = pattern.TrimStart('*');
                return testId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            }
            if (pattern.EndsWith("*"))
            {
                var prefix = pattern.TrimEnd('*');
                return testId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }

            // "mitte": genau ein Stern in der Mitte (a*b)
            var parts = pattern.Split('*');
            return parts.Length == 2
                && testId.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
                && testId.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase);
        }

        return testId.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
