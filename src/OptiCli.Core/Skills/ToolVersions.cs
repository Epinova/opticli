using System.Globalization;

namespace OptiCli.Core.Skills;

/// <summary>Orders opticli versions (<c>1.2.3</c>, <c>1.2.3-beta.1</c>; build metadata after <c>+</c> is ignored).</summary>
public static class ToolVersions
{
    /// <returns>Negative when <paramref name="a"/> is older than <paramref name="b"/>; null when either isn't a version.</returns>
    public static int? Compare(string? a, string? b)
    {
        if (!TryParse(a, out var left, out var leftPre) || !TryParse(b, out var right, out var rightPre))
        {
            return null;
        }
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var result = left.ElementAtOrDefault(i).CompareTo(right.ElementAtOrDefault(i));
            if (result != 0)
            {
                return result;
            }
        }
        // A pre-release sorts before its release.
        return leftPre == rightPre ? 0 : leftPre is null ? 1 : rightPre is null ? -1 : string.CompareOrdinal(leftPre, rightPre);
    }

    private static bool TryParse(string? text, out int[] numbers, out string? preRelease)
    {
        numbers = [];
        preRelease = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var core = text.Trim().Split('+')[0];
        if (core.IndexOf('-', StringComparison.Ordinal) is var dash and >= 0)
        {
            preRelease = core[(dash + 1)..];
            core = core[..dash];
        }
        var parts = core.Split('.');
        numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }
        return true;
    }
}
