using System.Net;
using System.Text.RegularExpressions;

namespace OptiCli.Core.Properties;

/// <param name="Raw">The link as written, e.g. <c>~/link/0b6f...a6b.aspx?epslanguage=en#contact</c>.</param>
/// <param name="Language">The <c>epslanguage</c> query value, when the link pins a language.</param>
/// <param name="Anchor">The fragment after <c>#</c>, without it.</param>
public sealed record PermanentLink(Guid Guid, string Raw, string? Language, string? Anchor);

/// <summary>
/// Finds CMS permanent links (<c>~/link/{guid}.aspx</c>), the form rich text, link items and URL
/// properties use to point at content so links survive moves and renames.
/// </summary>
public static partial class PermanentLinks
{
    public static IReadOnlyList<PermanentLink> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var links = new List<PermanentLink>();
        foreach (Match match in LinkPattern().Matches(text))
        {
            if (!Guid.TryParse(match.Groups["guid"].Value, out var guid))
            {
                continue;
            }
            var query = WebUtility.HtmlDecode(match.Groups["query"].Value);
            var language = LanguagePattern().Match(query) is { Success: true } lang ? lang.Groups["code"].Value : null;
            var anchor = match.Groups["anchor"].Success ? match.Groups["anchor"].Value.TrimStart('#') : null;
            links.Add(new PermanentLink(guid, match.Value, language, string.IsNullOrEmpty(anchor) ? null : anchor));
        }
        return links;
    }

    /// <summary>The GUID when the whole value is one permanent link (URL and link properties).</summary>
    public static Guid? Single(string? value) =>
        Find(value) is [var only] && value!.Trim().Length == only.Raw.Length ? only.Guid : null;

    [GeneratedRegex(@"~?/link/(?<guid>[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.aspx(?<query>\?[^""'#\s<>]*)?(?<anchor>#[^""'\s<>]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"(?:^\?|&)epslanguage=(?<code>[A-Za-z-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LanguagePattern();
}
