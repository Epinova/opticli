using System.Text.Json;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <param name="Lang">Only this language branch; every branch when left out.</param>
/// <param name="Cursor">Where to start: 0, or the previous page's <see cref="VersionList.Next"/>.</param>
/// <param name="Limit">The page size (default 20, at most 100).</param>
internal sealed record VersionsRequest(string? Lang = null, int? Cursor = null, int? Limit = null);

/// <summary>One saved version, as the edit UI's version list shows it.</summary>
/// <param name="Ref">The version's ref (<c>123_456</c>), for reading, publishing or discarding it.</param>
/// <param name="Status">camelCase CMS status: <c>checkedOut</c> (a draft), <c>published</c>, <c>previouslyPublished</c>, <c>awaitingApproval</c>, ...</param>
/// <param name="CommonDraft">The draft the edit UI opens for this branch.</param>
/// <param name="SavedBy">Who saved it, as the CMS recorded it; empty for a scheduled job or an import.</param>
/// <param name="ScheduledFor">For a version scheduled to be published: when, UTC.</param>
internal sealed record VersionInfo(string Ref, int Version, string Name, string? Language, string Status, bool CommonDraft, DateTime Saved, string? SavedBy, DateTime? ScheduledFor);

/// <param name="Content">The content, in its master language.</param>
/// <param name="Versions">Newest first.</param>
/// <param name="Next">The cursor for the next page; null when there is nothing more.</param>
internal sealed record VersionList(ContentSummary Content, IReadOnlyList<VersionInfo> Versions, int? Next);

/// <summary>
/// The saved versions of content, newest first, for choosing a <c>baseVersion</c> or a version to publish or discard.
/// The CLI reads them from the database; the MCP module has only the CMS. Read access to the content is enough, as in
/// the edit UI.
/// </summary>
internal static class VersionsOperation
{
    public const int DefaultLimit = 20;

    public const int MaxLimit = 100;

    /// <exception cref="AgentException"><c>not_found</c> for content that doesn't exist or the caller can't read, or a branch it doesn't have.</exception>
    public static VersionList Run(CmsCall call, string reference, VersionsRequest body)
    {
        var locator = new ContentLocator(call);
        var link = locator.ResolveContent(reference);
        var content = locator.LoadAnyLanguage(link);
        var limit = Paging.Limit(body.Limit, DefaultLimit, MaxLimit);
        var cursor = Paging.Cursor(body.Cursor);
        var language = string.IsNullOrWhiteSpace(body.Lang) ? null : locator.ContentLanguage(content, body.Lang.Trim());

        var all = locator.Versions(link, language);
        var page = all.Skip(cursor).Take(limit).Select(Describe).ToList();
        var next = cursor + page.Count < all.Count ? cursor + page.Count : (int?)null;
        return new VersionList(ContentSummaries.Describe(content, call.Service<IContentTypeRepository>()), page, next);
    }

    private static VersionInfo Describe(ContentVersion version) => new(
        version.ContentLink.ToString(),
        version.ContentLink.WorkID,
        version.Name,
        string.IsNullOrEmpty(version.LanguageBranch) ? null : version.LanguageBranch,
        JsonNamingPolicy.CamelCase.ConvertName(version.Status.ToString()),
        version.IsCommonDraft,
        version.Saved.ToUniversalTime(),
        version.SavedBy,
        version.Status == VersionStatus.DelayedPublish && version.DelayPublishUntil is { } at ? at.ToUniversalTime() : null);
}
