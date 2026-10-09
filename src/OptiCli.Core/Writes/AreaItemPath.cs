using System.Globalization;
using System.Text.RegularExpressions;

namespace OptiCli.Core.Writes;

/// <summary>
/// One item of a ContentArea named by position, <c>MainArea[2]</c> (zero-based), as <c>where-used</c> and
/// <c>find --where</c> show an inline block's place: <c>set 123 MainArea[2].Heading=Hi</c> changes that inline block's values.
/// </summary>
public static partial class AreaItemPath
{
    /// <returns>The property and the position; null for a plain property name.</returns>
    public static (string Property, int Index)? Parse(string name)
    {
        var match = Pattern().Match(name);
        return match.Success && int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? (match.Groups["property"].Value, index)
            : null;
    }

    [GeneratedRegex(@"^(?<property>[A-Za-z_][A-Za-z0-9_]*)\[(?<index>\d+)\]$")]
    private static partial Regex Pattern();
}
