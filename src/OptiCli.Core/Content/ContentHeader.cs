namespace OptiCli.Core.Content;

/// <summary>
/// What <c>tblContent</c> and <c>tblContentLanguage</c> say about one content item: enough to name it,
/// place it in the tree and build its URL, without reading any property values.
/// </summary>
/// <param name="ContentPath">Ancestor ids, root first, as stored: <c>.1.16.123.</c> (the item itself not included).</param>
/// <param name="Languages">One row per language branch, keyed by <c>tblLanguageBranch.pkID</c>.</param>
public sealed record ContentHeader(
    int Id,
    Guid Guid,
    int TypeId,
    int? ParentId,
    string ContentPath,
    int MasterLanguageId,
    bool Deleted,
    int ChildOrderRule,
    int PeerOrder,
    IReadOnlyDictionary<int, ContentLanguageRow> Languages)
{
    /// <summary>Ancestor ids from the root down to the parent.</summary>
    public IReadOnlyList<int> AncestorIds => ContentPaths.Parse(ContentPath);

    /// <summary>
    /// The branch to show for <paramref name="languageId"/>: that language if the item has it, otherwise
    /// the master branch (shared and invariant content), otherwise whatever exists.
    /// </summary>
    public ContentLanguageRow? LanguageRow(int? languageId) =>
        (languageId is { } id ? Languages.GetValueOrDefault(id) : null)
        ?? Languages.GetValueOrDefault(MasterLanguageId)
        ?? Languages.Values.OrderBy(l => l.LanguageId).FirstOrDefault();

    /// <summary>
    /// Whether the master language branch has been published, even if it is offline now (unpublished or expired): the
    /// CMS publishes no other branch before it. True for content without a master branch row.
    /// </summary>
    public bool MasterPublished => Languages.GetValueOrDefault(MasterLanguageId)?.Status is null or VersionStatus.Published;

    /// <summary>
    /// The branch whose URL segment the CMS uses for <paramref name="languageId"/>: URLs are built from
    /// published branches, so a branch that was never published takes the master branch's segment.
    /// </summary>
    public ContentLanguageRow? RoutingRow(int? languageId)
    {
        var own = languageId is { } id ? Languages.GetValueOrDefault(id) : null;
        if (own is { Status: VersionStatus.Published })
        {
            return own;
        }
        return Languages.GetValueOrDefault(MasterLanguageId) is { Status: VersionStatus.Published } master ? master : LanguageRow(languageId);
    }
}

/// <param name="VersionId">
/// The branch's primary version, whose values <c>tblContentProperty</c> holds: the published one when published,
/// otherwise the common draft (see <see cref="VersionStatuses.PrimaryVersion"/>).
/// </param>
public sealed record ContentLanguageRow(
    int LanguageId,
    string? Name,
    string? UrlSegment,
    VersionStatus Status,
    int? VersionId,
    DateTime? Created,
    DateTime? Saved,
    DateTime? StartPublish,
    DateTime? StopPublish,
    string? ChangedBy,
    string? BlobUri,
    string? ThumbnailUri,
    string? ExternalUrl,
    LinkTarget? Link = null);

/// <summary>
/// Where a page's link goes when its link type isn't "normal": a shortcut to other content, or an "external"
/// link that is a permanent link to content (with an anchor). The CMS gives such a page the target's URL.
/// </summary>
public sealed record LinkTarget(Guid Guid, string? Anchor)
{
    /// <summary>
    /// From <c>tblContentLanguage</c>: <c>ContentLinkGUID</c> is the shortcut target while <c>AutomaticLink</c> is
    /// set; with <c>AutomaticLink</c> off, <c>LinkURL</c> is the link. Links to other sites and inactive links
    /// give null (the page keeps its own URL here), and so do pages that fetch data from the linked page
    /// (<c>FetchData</c>): they only borrow its values for empty properties and keep their own URL in the CMS.
    /// </summary>
    public static LinkTarget? From(bool? automaticLink, bool? fetchData, Guid? contentLinkGuid, string? linkUrl)
    {
        if (automaticLink != false)
        {
            return fetchData != true && contentLinkGuid is { } target && target != Guid.Empty ? new LinkTarget(target, null) : null;
        }
        return Properties.PermanentLinks.Find(linkUrl) is [var link] && linkUrl!.Trim().StartsWith(link.Raw, StringComparison.OrdinalIgnoreCase)
            ? new LinkTarget(link.Guid, link.Anchor)
            : null;
    }
}

public static class ContentPaths
{
    public static IReadOnlyList<int> Parse(string? contentPath) =>
        (contentPath ?? "")
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();
}
