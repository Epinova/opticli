using System.Text.Json.Nodes;

namespace OptiCli.Core.Content;

/// <summary>The result of <c>get</c>: identity, version facts and decoded properties.</summary>
/// <param name="Version">The version shown, as a ref (<c>123_456</c>).</param>
/// <param name="Variation">CMS 13: the content variation the version shown belongs to; null for the content's own versions.</param>
/// <param name="Languages">All language branches the item has.</param>
/// <param name="LatestDraft">A newer unpublished version of this branch, when one exists.</param>
/// <param name="ChildSortOrder">Pages only: how its children are sorted (<c>FilterSortOrder</c> name).</param>
/// <param name="SortIndex">Pages only: its place among its siblings when the parent sorts by index.</param>
/// <param name="SimpleAddress">Pages only: the simple address (<c>/campaign</c>), when it has one.</param>
/// <param name="Shortcut">Pages only: where its link goes instead of the page itself, when it isn't a normal page.</param>
/// <param name="Category">Pages, shared blocks and media: the built-in category's names, when it has any.</param>
/// <param name="RequestedLanguage">Set when the item has no branch in the requested language and another is shown.</param>
/// <param name="LanguageRule">
/// <c>fallback</c> or <c>replacement</c> when language settings (<c>tblContentLanguageSetting</c>) chose the branch shown
/// for the requested language.
/// </param>
/// <param name="Projects">The projects that hold a version of the item.</param>
/// <param name="Approval">The content approval sequence publishing goes through, when one applies (its own or inherited).</param>
public sealed record ContentDocument(
    string Ref,
    Guid Guid,
    string Type,
    string? Name,
    string? Language,
    string Status,
    string? Url,
    string Kind,
    string? Version,
    string? Variation,
    string? MasterLanguage,
    IReadOnlyList<string> Languages,
    string? Parent,
    DateTime? Saved,
    string? ChangedBy,
    DateTime? StartPublish,
    DateTime? StopPublish,
    string? ChildSortOrder,
    int? SortIndex,
    string? SimpleAddress,
    ShortcutInfo? Shortcut,
    IReadOnlyList<string>? Category,
    string? LatestDraft,
    bool? Deleted,
    string? RequestedLanguage,
    string? LanguageRule,
    Queries.ApprovalSequence? Approval,
    IReadOnlyList<Queries.ItemProject>? Projects,
    IReadOnlyList<string>? Notes,
    JsonObject Properties)
{
    /// <summary>CMS 13: true for a Visual Builder blueprint, a template for new content rather than content itself.</summary>
    public bool? Blueprint { get; init; }

    /// <summary>
    /// CMS 13: a Visual Builder experience's (or section's) composition: sections → rows → columns → elements, each with
    /// its type, display template and settings, and an inline element's properties (<see cref="Properties.Compositions"/>).
    /// It replaces the properties it is stored in (<c>Layout</c>, <c>UnstructuredData</c>) in <see cref="Properties"/>.
    /// </summary>
    public JsonObject? Composition { get; init; }
}

/// <summary>A page's shortcut, in the shape <c>set Shortcut</c> takes (with <see cref="To"/> as an identity).</summary>
/// <param name="Type"><c>shortcut</c>, <c>external</c>, <c>fetchData</c> or <c>inactive</c>.</param>
/// <param name="To">The page a shortcut or fetch-data page points at, or the page an external permanent link goes to.</param>
/// <param name="Url">An external link's URL, as stored.</param>
/// <param name="Anchor">The anchor of an external link to a page.</param>
/// <param name="Target">The window it opens in (<c>_blank</c>), when not the same one.</param>
public sealed record ShortcutInfo(string Type, ContentIdentity? To, string? Url, string? Anchor, string? Target)
{
    private static readonly string[] Types = ["normal", "shortcut", "external", "inactive", "fetchData"];

    /// <summary>The type's name, or null for a normal page (and unknown values).</summary>
    public static string? TypeName(int? linkType) => linkType is > 0 and < 5 ? Types[linkType.Value] : null;

    /// <summary><c>_blank</c> from <c>target="_blank"</c>.</summary>
    public static string? FrameTarget(string? frameName)
    {
        var value = frameName?.Trim();
        return string.IsNullOrEmpty(value) ? null
            : value.StartsWith("target=", StringComparison.OrdinalIgnoreCase) ? value[7..].Trim('"', '\'', ' ')
            : value;
    }

    /// <summary><c>/campaign</c> from the stored <c>~/campaign</c>.</summary>
    public static string? SimpleAddressPath(string? stored)
    {
        var path = stored?.Trim().TrimStart('~').Trim('/');
        return string.IsNullOrEmpty(path) ? null : "/" + path;
    }
}
