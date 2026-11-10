using System.Text;
using System.Text.RegularExpressions;

namespace OptiCli.Cms.Content;

/// <summary>
/// Whether two spellings of rich text (XhtmlString) are what the CMS stores alike. On save the CMS writes every tag
/// its own way: the element and attribute names in lower case, attribute values in double quotes, and elements without
/// content (<c>&lt;br&gt;</c>, <c>&lt;img&gt;</c>) closed with <c> /&gt;</c>. Text, entities and attribute values stay as
/// they are. So <c>&lt;img alt='x'&gt;</c> written again over a stored <c>&lt;img alt="x" /&gt;</c> is no change.
/// </summary>
internal static partial class RichTextMarkup
{
    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> differ only in how their tags are written. Anything this
    /// doesn't read as a tag (a comment, a stray <c>&lt;</c>) must match exactly, so the answer errs towards "different".
    /// </summary>
    public static bool Same(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.Ordinal)
        || string.Equals(Normalize(a ?? ""), Normalize(b ?? ""), StringComparison.Ordinal);

    /// <summary>The markup with each tag written as the CMS writes it; comments as they are.</summary>
    internal static string Normalize(string markup) => Tag().Replace(markup, tag =>
    {
        if (tag.Groups["comment"].Success)
        {
            return tag.Value;
        }
        var name = tag.Groups["name"].Value.ToLowerInvariant();
        if (tag.Groups["end"].Success)
        {
            return $"</{name}>";
        }
        var written = new StringBuilder("<").Append(name);
        foreach (Match attribute in Attribute().Matches(tag.Groups["attributes"].Value))
        {
            written.Append(' ').Append(attribute.Groups["name"].Value.ToLowerInvariant());
            if (attribute.Groups["value"].Success)
            {
                var value = attribute.Groups["value"].Value;
                if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
                {
                    value = value[1..^1];
                }
                written.Append("=\"").Append(value.Replace("\"", "&quot;", StringComparison.Ordinal)).Append('"');
            }
        }
        return written.Append(tag.Groups["closed"].Success || Void.Contains(name) ? " />" : ">").ToString();
    });

    /// <summary>The HTML elements that have no content, which the CMS always closes.</summary>
    private static readonly HashSet<string> Void =
        ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"];

    // A comment is matched only to be left as it is: what looks like a tag inside it isn't one.
    [GeneratedRegex("""(?<comment><!--[\s\S]*?-->)|<(?:(?<end>/)(?<name>[A-Za-z][\w:-]*)\s*|(?<name>[A-Za-z][\w:-]*)(?<attributes>(?:\s+[^\s"'=/>]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'=<>`]+))?)*)\s*(?<closed>/)?)>""", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex("""(?<name>[^\s"'=/>]+)(?:\s*=\s*(?<value>"[^"]*"|'[^']*'|[^\s"'=<>`]+))?""", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();
}
