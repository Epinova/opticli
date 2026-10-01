using System.Globalization;
using System.Text.RegularExpressions;

namespace OptiCli.Core.Writes;

/// <summary>
/// <c>$id</c>s inside rich text in a plan: a link <c>href="$id"</c> (or <c>"$id#anchor"</c>), and a block's
/// <c>data-contentguid="$id"</c> or <c>data-contentlink="$id"</c>. They become what the CMS stores: a permanent link
/// (<c>~/link/&lt;guid&gt;.aspx</c>), the GUID, the content id.
/// </summary>
public static partial class TextRefs
{
    public const string Href = "href";
    public const string ContentGuid = "data-contentguid";
    public const string ContentLink = "data-contentlink";

    /// <param name="Attribute"><see cref="Href"/>, <see cref="ContentGuid"/> or <see cref="ContentLink"/> (lower case).</param>
    /// <param name="Id">The plan id, without <c>$</c>.</param>
    public sealed record Found(string Attribute, string Id);

    /// <summary>Whether the attribute is written with the GUID alone, which can be known before the content exists.</summary>
    public static bool NeedsOnlyGuid(string attribute) => attribute != ContentLink;

    public static IEnumerable<Found> Find(string? text) => text is null || !text.Contains('$')
        ? []
        : Pattern().Matches(text).Select(m => new Found(m.Groups["attribute"].Value.ToLowerInvariant(), m.Groups["id"].Value));

    /// <param name="replace">The attribute's new value for a reference (without the anchor), or null to leave it as it is.</param>
    public static string Map(string text, Func<Found, string?> replace) => !text.Contains('$')
        ? text
        : Pattern().Replace(text, m =>
        {
            var found = new Found(m.Groups["attribute"].Value.ToLowerInvariant(), m.Groups["id"].Value);
            return replace(found) is { } value
                ? $"{m.Groups["attribute"].Value}{m.Groups["equals"].Value}{m.Groups["quote"].Value}{value}{m.Groups["anchor"].Value}{m.Groups["quote"].Value}"
                : m.Value;
        });

    /// <summary>What a reference becomes once the content's id and GUID are known; null when the one it needs isn't.</summary>
    public static string? Value(string attribute, int? id, Guid? guid) => attribute switch
    {
        Href => guid is { } link ? PermanentLink(link) : null,
        ContentGuid => guid?.ToString("D"),
        _ => id?.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>A link to content as the CMS stores it in rich text.</summary>
    public static string PermanentLink(Guid guid) => $"~/link/{guid:N}.aspx";

    [GeneratedRegex("""(?<attribute>\b(?:href|data-contentguid|data-contentlink))(?<equals>\s*=\s*)(?<quote>["'])\$(?<id>[A-Za-z][A-Za-z0-9_-]*)(?<anchor>#[^"']*)?\k<quote>""", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
