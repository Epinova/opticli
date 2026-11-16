using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Queries;

/// <summary>A project (edit mode's Projects): versions of several items that are published together.</summary>
/// <param name="Status"><c>active</c>, <c>publishing</c>, <c>publishFailed</c>, <c>published</c>, <c>delayedPublished</c> or <c>reactivating</c>.</param>
/// <param name="PublishAt">When it is scheduled to be published, UTC.</param>
/// <param name="Ready">
/// Of <paramref name="Items"/>, the ones marked Ready to publish (their version <c>checkedIn</c>, the content not in the
/// recycle bin): what <c>projects publish</c> puts live, as the edit UI's "N of M ready" counts.
/// </param>
public sealed record ProjectInfo(int Id, string Name, string Status, DateTime? Created, string? CreatedBy, DateTime? PublishAt, int Items, int Ready)
{
    /// <summary>Not published: a project whose name <c>projects create</c> won't give another.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Unpublished => Status != "published";
}

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
                   (SELECT COUNT(*) FROM tblProjectItem i WHERE i.fkProjectID = p.pkID) AS Items,
                   (SELECT COUNT(*) FROM tblProjectItem i
                      JOIN tblWorkContent w ON w.pkID = i.ContentLinkWorkID
                      JOIN tblContent c ON c.pkID = i.ContentLinkID
                    WHERE i.fkProjectID = p.pkID AND w.Status = @checkedIn AND c.Deleted = 0
                      AND (i.ContentLinkProvider IS NULL OR i.ContentLinkProvider = '')) AS Ready
            FROM tblProject p
            ORDER BY p.Created DESC, p.pkID DESC
            """, r => new ProjectInfo(
                r.GetInt32("pkID"), r.GetStringOrNull("Name") ?? "", StatusName(r.GetInt32("Status")), r.GetDateTimeOrNull("Created"),
                r.GetStringOrNull("CreatedBy") is { Length: > 0 } by ? by : null, r.GetDateTimeOrNull("DelayPublishUntil"), r.GetInt32("Items"), r.GetInt32("Ready")), cancellationToken,
            new SqlParameter("@checkedIn", (int)VersionStatus.CheckedIn));

    /// <summary>
    /// The project <paramref name="reference"/> names, as the site agent finds it: by id, else by its exact name (trimmed,
    /// case-insensitive), of which a project that isn't published wins over published ones.
    /// </summary>
    /// <returns>Null when no project has that id or name.</returns>
    /// <exception cref="UsageException">An empty name, or one that more than one candidate has (listing their ids).</exception>
    public static ProjectInfo? Find(IReadOnlyList<ProjectInfo> projects, string reference)
    {
        var name = reference.Trim();
        if (name.Length == 0)
        {
            throw new UsageException("Name the project: its id or its name.");
        }
        if (int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && projects.FirstOrDefault(p => p.Id == id) is { } byId)
        {
            return byId;
        }
        var named = projects.Where(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = named.Count > 1 && named.Any(p => p.Unpublished) ? named.Where(p => p.Unpublished).ToList() : named;
        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new UsageException(
                $"{candidates.Count} projects are named '{name}': {string.Join(", ", candidates.OrderBy(p => p.Id).Select(Describe))}.",
                "Name the project by its id."),
        };
    }

    /// <summary>As <see cref="Find"/>, for a project that must exist.</summary>
    /// <exception cref="NotFoundException">No project has that id or name (with close names).</exception>
    public static ProjectInfo Require(IReadOnlyList<ProjectInfo> projects, string reference) =>
        Find(projects, reference) ?? throw new NotFoundException(
            $"No project {(int.TryParse(reference.Trim(), out _) ? reference.Trim() : $"named '{reference.Trim()}'")}.",
            Text.Suggestions.Closest(reference, projects.Where(p => p.Unpublished).Select(p => p.Name)) is { Count: > 0 } close
                ? $"Did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}? `opticli projects` lists them."
                : projects.Count == 0 ? "The site has no projects; `opticli projects create <name>` makes one." : "List them with `opticli projects`.");

    /// <summary>"12 ('Spring campaign', active)", as the site agent names a project.</summary>
    public static string Describe(ProjectInfo project) => $"{project.Id} ('{project.Name}', {project.Status})";

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
