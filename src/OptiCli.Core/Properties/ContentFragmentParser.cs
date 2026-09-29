using System.Globalization;
using System.Text.RegularExpressions;

namespace OptiCli.Core.Properties;

/// <summary>
/// One item of a ContentArea, or one block embedded in rich text, as the CMS serialises it:
/// <c>&lt;div data-contentguid="..." data-contentname="..." data-contentgroup="" ...&gt;{}&lt;/div&gt;</c>.
/// </summary>
/// <param name="Index">Position among the fragments of the value (inline block values are stored by it).</param>
/// <param name="ContentGuid">The referenced content; null for inline blocks, which have no identity.</param>
/// <param name="ContentLink">Older serialisations reference content by id (<c>data-contentlink</c>).</param>
/// <param name="Name">The name the editor saw when saving (<c>data-contentname</c>); the DB name is authoritative.</param>
/// <param name="DisplayOption">The display option tag (<c>data-epi-content-display-option</c>).</param>
/// <param name="ContentGroup">Personalization group: items sharing it are alternatives for different visitor groups.</param>
/// <param name="VisitorGroups">Visitor group ids the item is shown to (<c>data-groups</c>); empty means everyone.</param>
/// <param name="InlineTypeId">For inline blocks: the block's content type id.</param>
/// <param name="InlineName">For inline blocks: the name given in the editor.</param>
/// <param name="RenderSettings">Any other <c>data-*</c> attributes, without the prefix.</param>
public sealed record ContentFragment(
    int Index,
    Guid? ContentGuid,
    int? ContentLink,
    string? Name,
    string? DisplayOption,
    string? ContentGroup,
    IReadOnlyList<string> VisitorGroups,
    int? InlineTypeId,
    string? InlineName,
    IReadOnlyDictionary<string, string> RenderSettings)
{
    public bool IsInline => InlineTypeId is not null || (ContentGuid is null && ContentLink is null);
}

public static partial class ContentFragmentParser
{
    private const string GuidAttribute = "data-contentguid";
    private const string LinkAttribute = "data-contentlink";
    private const string InlineTypeAttribute = "data-inlineblocktypeid";
    private const string DisplayOptionAttribute = "data-epi-content-display-option";

    /// <summary>Attributes with a dedicated field, or that only matter to the CMS itself.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "data-classid", GuidAttribute, LinkAttribute, "data-contentname", "data-contentgroup", "data-groups",
        DisplayOptionAttribute, InlineTypeAttribute, "data-inlineblockname", "data-isinlineblock",
    };

    /// <summary>Fragments in document order. Ordinary markup around them (rich text) is ignored.</summary>
    public static IReadOnlyList<ContentFragment> Parse(string? xhtml)
    {
        if (string.IsNullOrEmpty(xhtml))
        {
            return [];
        }

        var fragments = new List<ContentFragment>();
        foreach (Match tag in StartTagPattern().Matches(xhtml))
        {
            var attributes = MarkupAttributes.Parse(tag.Groups["attributes"].Value);
            if (!attributes.ContainsKey(GuidAttribute) && !attributes.ContainsKey(LinkAttribute) && !attributes.ContainsKey(InlineTypeAttribute))
            {
                continue;
            }

            Guid? guid = Guid.TryParse(attributes.GetValueOrDefault(GuidAttribute), out var parsed) && parsed != Guid.Empty ? parsed : null;
            fragments.Add(new ContentFragment(
                fragments.Count,
                guid,
                LeadingInt(attributes.GetValueOrDefault(LinkAttribute)),
                Blank(attributes.GetValueOrDefault("data-contentname")),
                Blank(attributes.GetValueOrDefault(DisplayOptionAttribute)),
                Blank(attributes.GetValueOrDefault("data-contentgroup")),
                (attributes.GetValueOrDefault("data-groups") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                LeadingInt(attributes.GetValueOrDefault(InlineTypeAttribute)),
                Blank(attributes.GetValueOrDefault("data-inlineblockname")),
                attributes
                    .Where(a => a.Key.StartsWith("data-", StringComparison.OrdinalIgnoreCase) && !Known.Contains(a.Key))
                    .ToDictionary(a => a.Key["data-".Length..], a => a.Value)));
        }
        return fragments;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary><c>123</c>, <c>123_45</c> (version) or <c>123__provider</c> → 123.</summary>
    private static int? LeadingInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var digits = new string(value.Trim().TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    [GeneratedRegex(@"<(?:div|span)\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex StartTagPattern();
}
