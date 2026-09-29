using System.Net;
using System.Text.RegularExpressions;

namespace OptiCli.Core.Properties;

/// <param name="Attributes">Other attributes of the element (e.g. <c>class</c>, <c>rel</c>).</param>
public sealed record LinkElement(string? Href, string? Text, string? Title, string? Target, IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// Parses <c>LinkItem</c> (<c>&lt;a href title target&gt;text&lt;/a&gt;</c>) and <c>LinkCollection</c>
/// (<c>&lt;links&gt;&lt;a .../&gt;...&lt;/links&gt;</c>) values.
/// </summary>
public static partial class LinkMarkup
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase) { "href", "title", "target" };

    public static IReadOnlyList<LinkElement> Parse(string? markup)
    {
        if (string.IsNullOrWhiteSpace(markup))
        {
            return [];
        }

        return AnchorPattern().Matches(markup)
            .Select(match =>
            {
                var attributes = MarkupAttributes.Parse(match.Groups["attributes"].Value);
                var text = WebUtility.HtmlDecode(TagPattern().Replace(match.Groups["text"].Value, "")).Trim();
                return new LinkElement(
                    attributes.GetValueOrDefault("href"),
                    text.Length == 0 ? null : text,
                    NullIfEmpty(attributes.GetValueOrDefault("title")),
                    NullIfEmpty(attributes.GetValueOrDefault("target")),
                    attributes.Where(a => !Known.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value));
            })
            .ToList();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    [GeneratedRegex(@"<a\b(?<attributes>[^>]*?)(?:/>|>(?<text>.*?)</a\s*>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();
}
