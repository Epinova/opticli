using System.Globalization;
using System.Text.RegularExpressions;

namespace OptiCli.Core.Properties;

/// <summary>One level of nesting in a <see cref="ScopePath"/>.</summary>
/// <param name="PropertyId">The property definition that holds the nested block(s).</param>
/// <param name="InlineTypeId">For a block created inline in a ContentArea: the block's content type id.</param>
/// <param name="Index">Position in a block list or ContentArea; null for a single local block.</param>
public sealed record ScopeStep(int PropertyId, int? InlineTypeId, int? Index);

/// <summary>
/// Decodes <c>tblContentProperty.ScopeName</c> / <c>tblWorkContentProperty.ScopeName</c>, which is how the
/// CMS stores values that live inside blocks rather than on the content item itself. Each dot-separated
/// segment is a property definition id; the last one is the row's own property:
/// <list type="bullet">
/// <item><c>.10.20.</c>: property 20 of the local block in property 10</item>
/// <item><c>.10(2).20.</c>: property 20 of item 2 of the block list in property 10</item>
/// <item><c>.10:7(1).20.</c>: property 20 of the inline block (type 7) at position 1 of ContentArea 10</item>
/// <item><c>.10(2).20(0)</c>: item 0 of the list property 20 (<c>IList&lt;ContentReference&gt;</c>, ...) in item 2 of
/// block list 10; list values get an index on their own segment and no trailing dot</item>
/// </list>
/// Segments nest, so <c>.10:7(0).30:8(1).40.</c> is a value two blocks deep. Top-level values have no scope.
/// </summary>
/// <param name="LeafIndex">For an item of a list of plain values: its position in the list.</param>
public sealed partial record ScopePath(IReadOnlyList<ScopeStep> Steps, int LeafPropertyId, int? LeafIndex = null)
{
    /// <returns>Null for top-level values (no scope) and for scopes this parser does not recognise.</returns>
    public static ScopePath? Parse(string? scopeName)
    {
        if (string.IsNullOrWhiteSpace(scopeName))
        {
            return null;
        }

        var segments = scopeName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var leaf = segments.Length == 0 ? null : LeafPattern().Match(segments[^1]);
        if (leaf is not { Success: true } || (segments.Length < 2 && !leaf.Groups["index"].Success))
        {
            return null;
        }

        var steps = new List<ScopeStep>();
        foreach (var segment in segments[..^1])
        {
            var match = SegmentPattern().Match(segment);
            if (!match.Success)
            {
                return null;
            }
            steps.Add(new ScopeStep(
                Int(match.Groups["property"].Value),
                match.Groups["type"].Success ? Int(match.Groups["type"].Value) : null,
                match.Groups["index"].Success ? Int(match.Groups["index"].Value) : null));
        }

        return new ScopePath(
            steps,
            Int(leaf.Groups["property"].Value),
            leaf.Groups["index"].Success ? Int(leaf.Groups["index"].Value) : null);
    }

    private static int Int(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<property>[0-9]+)(?::(?<type>[0-9]+))?(?:\((?<index>[0-9]+)\))?$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"^(?<property>[0-9]+)(?:\((?<index>[0-9]+)\))?$")]
    private static partial Regex LeafPattern();
}
