using System.Net;
using System.Text.RegularExpressions;

namespace OptiCli.Core.Properties;

/// <summary>Attribute parsing for the small, machine-written markup the CMS stores (no HTML parser needed).</summary>
internal static partial class MarkupAttributes
{
    /// <summary>Attributes of one start tag's inner text, names lower-cased, values HTML-decoded.</summary>
    public static Dictionary<string, string> Parse(string attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributePattern().Matches(attributes))
        {
            var value = match.Groups["dq"].Success ? match.Groups["dq"].Value
                : match.Groups["sq"].Success ? match.Groups["sq"].Value
                : match.Groups["bare"].Value;
            result[match.Groups["name"].Value.ToLowerInvariant()] = WebUtility.HtmlDecode(value);
        }
        return result;
    }

    [GeneratedRegex("""(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<bare>[^\s"'>]+))""")]
    private static partial Regex AttributePattern();
}
