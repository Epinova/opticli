namespace OptiCli.Core.Text;

/// <summary>"Did you mean" candidates for mistyped type, property, profile or site names.</summary>
public static class Suggestions
{
    /// <summary>
    /// Closest candidates first. A candidate qualifies if it contains the input (or vice versa), or
    /// its edit distance is within roughly a third of the input's length, so short inputs only
    /// match near-identical names.
    /// </summary>
    public static IReadOnlyList<string> Closest(string input, IEnumerable<string> candidates, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var needle = input.Trim().ToLowerInvariant();
        var threshold = Math.Max(1, needle.Length / 3);

        return candidates
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c =>
            {
                var candidate = c.ToLowerInvariant();
                var distance = Levenshtein(needle, candidate);
                var contains = candidate.Contains(needle, StringComparison.Ordinal) || needle.Contains(candidate, StringComparison.Ordinal);
                return (Name: c, Distance: distance, Contains: contains);
            })
            .Where(x => x.Distance <= threshold || x.Contains)
            .OrderBy(x => x.Distance <= threshold ? 0 : 1)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>"Did you mean X, Y?" or null when nothing is close.</summary>
    public static string? DidYouMean(string input, IEnumerable<string> candidates)
    {
        var closest = Closest(input, candidates);
        return closest.Count == 0 ? null : $"Did you mean {string.Join(", ", closest)}?";
    }

    /// <summary>
    /// Levenshtein distance that also counts swapping two adjacent characters as one edit
    /// (optimal string alignment), since "tpyes" is a far more likely typo than its plain distance suggests.
    /// </summary>
    public static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }
        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }
}
