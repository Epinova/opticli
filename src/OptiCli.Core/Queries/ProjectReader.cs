using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <summary>A project (edit mode's Projects): versions of several items that are published together.</summary>
/// <param name="Status"><c>active</c>, <c>publishing</c>, <c>publishFailed</c>, <c>published</c>, <c>delayedPublished</c> or <c>reactivating</c>.</param>
/// <param name="PublishAt">When it is scheduled to be published, UTC.</param>
public sealed record ProjectInfo(int Id, string Name, string Status, DateTime? Created, string? CreatedBy, DateTime? PublishAt, int Items);

/// <summary>One version in a project.</summary>
/// <param name="Version">The version (<c>id_version</c>); null when the item names the content without one.</param>
public sealed record ProjectItemInfo(string Ref, string? Version, string? Type, string? Name, string? Language, string? Status, string? Category);

/// <summary>A project an item is in, as <c>get</c> shows it.</summary>
public sealed record ItemProject(int Id, string Name, string Status, string? Version, string? Language);

/// <summary>Projects from <c>tblProject</c> and <c>tblProjectItem</c>; read-only.</summary>
public static class ProjectReader
{
    private static readonly string[] Statuses = ["active", "publishing", "publishFailed", "published", "delayedPublished", "reactivating"];

    public static string StatusName(int status) => status >= 0 && status < Statuses.Length ? Statuses[status] : status.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static Task<IReadOnlyList<ProjectInfo>> ListAsync(CmsDatabase db, CancellationToken cancellationToken) =>
        db.QueryAsync("""
            SELECT p.pkID, p.Name, p.Status, p.Created, p.CreatedBy, p.DelayPublishUntil,
                   (SELECT COUNT(*) FROM tblProjectItem i WHERE i.fkProjectID = p.pkID) AS Items
            FROM tblProject p
            ORDER BY p.Created DESC, p.pkID DESC
            """, r => new ProjectInfo(
                r.GetInt32("pkID"), r.GetStringOrNull("Name") ?? "", StatusName(r.GetInt32("Status")), r.GetDateTimeOrNull("Created"),
                r.GetStringOrNull("CreatedBy") is { Length: > 0 } by ? by : null, r.GetDateTimeOrNull("DelayPublishUntil"), r.GetInt32("Items")), cancellationToken);

    /// <summary>The project's items, with the content they name described.</summary>
    public static async Task<IReadOnlyList<ProjectItemInfo>> ItemsAsync(ContentSession session, int projectId, CancellationToken cancellationToken)
    {
        var rows = await session.Db.QueryAsync("""
            SELECT i.ContentLinkID, i.ContentLinkWorkID, i.Language, i.Category, w.Status
            FROM tblProjectItem i
            LEFT JOIN tblWorkContent w ON w.pkID = i.ContentLinkWorkID
            WHERE i.fkProjectID = @id AND (i.ContentLinkProvider IS NULL OR i.ContentLinkProvider = '')
            ORDER BY i.pkID
            """, r => (Id: r.GetInt32("ContentLinkID"), Version: r.GetInt32OrNull("ContentLinkWorkID"), Language: r.GetStringOrNull("Language"),
                Category: r.GetStringOrNull("Category"), Status: r.GetInt32OrNull("Status")), cancellationToken, new SqlParameter("@id", projectId));
        await session.Identities.LoadAsync(rows.Select(r => r.Id), [], cancellationToken);
        return rows.Select(r =>
        {
            var header = session.Identities.Header(r.Id);
            var language = r.Language?.Trim() is { Length: > 0 } code ? session.Model.LanguageByCode(code) : null;
            var identity = header is null ? ContentIdentity.MissingId(r.Id) : session.Identities.Describe(header, language);
            return new ProjectItemInfo(identity.Ref!, r.Version is > 0 ? ContentIdentity.RefFor(r.Id, r.Version) : null, identity.Type, identity.Name,
                language?.Code ?? identity.Language, r.Status is { } status ? VersionStatuses.Name(VersionStatuses.From(status)) : identity.Status,
                r.Category is { Length: > 0 } category ? category : null);
        }).ToList();
    }

    /// <summary>The projects that hold a version of content <paramref name="contentId"/>.</summary>
    public static Task<IReadOnlyList<ItemProject>> ForContentAsync(CmsDatabase db, int contentId, CancellationToken cancellationToken) =>
        db.QueryAsync("""
            SELECT p.pkID, p.Name, p.Status, i.ContentLinkWorkID, i.Language
            FROM tblProjectItem i
            JOIN tblProject p ON p.pkID = i.fkProjectID
            WHERE i.ContentLinkID = @id AND (i.ContentLinkProvider IS NULL OR i.ContentLinkProvider = '')
            ORDER BY p.pkID
            """, r => new ItemProject(r.GetInt32("pkID"), r.GetStringOrNull("Name") ?? "", StatusName(r.GetInt32("Status")),
                r.GetInt32OrNull("ContentLinkWorkID") is > 0 and var version ? ContentIdentity.RefFor(contentId, version) : null,
                r.GetStringOrNull("Language")?.Trim() is { Length: > 0 } language ? language : null), cancellationToken,
            new SqlParameter("@id", contentId));
}
