using System.Text.RegularExpressions;

namespace OptiCli.Core.Properties;

/// <summary>A part of rich text shown only to some visitor groups.</summary>
/// <param name="VisitorGroups">Visitor group ids (<c>data-groups</c>).</param>
/// <param name="Group">Personalization group (<c>data-contentgroup</c>): sections sharing it are alternatives.</param>
/// <param name="Html">The section's content.</param>
/// <param name="Start">Where the section starts in the text, and <paramref name="End"/> where it ends.</param>
public sealed record PersonalizedSection(IReadOnlyList<string> VisitorGroups, string? Group, string Html, int Start, int End);

/// <summary>
/// Personalized sections in an XhtmlString, as the CMS stores them:
/// <c>&lt;div class="epi_pc" data-groups="id,id"&gt;&lt;div class="epi_pc_h"&gt;...&lt;/div&gt;&lt;section class="epi_pc_content"&gt;...&lt;/section&gt;&lt;/div&gt;</c>.
/// The header (<c>epi_pc_h</c>) is only what the editor shows.
/// </summary>
public static partial class PersonalizedText
{
    public static IReadOnlyList<PersonalizedSection> Find(string? xhtml)
    {
        if (string.IsNullOrEmpty(xhtml) || !xhtml.Contains("epi_pc", StringComparison.Ordinal))
        {
            return [];
        }
        var sections = new List<PersonalizedSection>();
        foreach (Match open in Container().Matches(xhtml))
        {
            var attributes = MarkupAttributes.Parse(open.Groups["attributes"].Value);
            if (!(attributes.GetValueOrDefault("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("epi_pc"))
            {
                continue;
            }
            var content = Content().Match(xhtml, open.Index + open.Length);
            if (!content.Success)
            {
                continue;
            }
            var start = content.Index + content.Length;
            var end = xhtml.IndexOf("</section>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                continue;
            }
            var groups = (attributes.GetValueOrDefault("data-groups") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var group = attributes.GetValueOrDefault("data-contentgroup") is { Length: > 0 } named ? named : null;
            sections.Add(new PersonalizedSection(groups, group, xhtml[start..end].Trim(), open.Index, end));
        }
        return sections;
    }

    /// <summary>
    /// The visitor groups that see <paramref name="needle"/> (a GUID, say): those of the sections it is in. Null when it
    /// is also outside them, so everyone sees it, or isn't there at all.
    /// </summary>
    public static IReadOnlyList<string>? GroupsAround(string? xhtml, string needle)
    {
        if (xhtml is null)
        {
            return null;
        }
        var sections = Find(xhtml);
        var groups = new List<string>();
        var found = false;
        for (var at = xhtml.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0; at = xhtml.IndexOf(needle, at + needle.Length, StringComparison.OrdinalIgnoreCase))
        {
            found = true;
            if (sections.FirstOrDefault(s => at >= s.Start && at < s.End) is not { } section)
            {
                return null;
            }
            groups.AddRange(section.VisitorGroups);
        }
        return found ? groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null;
    }

    [GeneratedRegex("""<div\b(?<attributes>[^>]*\bepi_pc\b[^>]*)>""", RegexOptions.IgnoreCase)]
    private static partial Regex Container();

    [GeneratedRegex("""<section\b[^>]*\bepi_pc_content\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex Content();
}
