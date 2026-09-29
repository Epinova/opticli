using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <param name="Ref">The content item.</param>
/// <param name="Version">The newest unpublished version, as a ref.</param>
/// <param name="Drafts">How many unpublished versions of this branch are newer than the published one.</param>
public sealed record DraftInfo(
    string Ref, Guid Guid, string Type, string? Name, string? Language, string Status, string? Url,
    string Version, DateTime? Saved, string? ChangedBy, int Drafts);

/// <summary>
/// <c>drafts</c>: branches with changes nobody has published: never published, or with versions newer
/// than the published one. One row per content item and language, newest change first.
/// </summary>
public sealed class DraftReader(ContentSession session)
{
    // Newest draft per branch via CROSS APPLY TOP 1 (index seeks per branch) rather than a window over
    // every unpublished version: sites with import jobs can have millions of never-published versions.
    // The count per branch is only computed for the rows of the page returned. RECOMPILE lets the
    // optimizer drop the unused optional filters; a cached generic plan is ten times slower.
    // The type filter is an inlined id list (see SqlLists), empty when every type is wanted.
    private static string Sql(string typeFilter) => $"""
        WITH newest AS (
            SELECT cl.fkContentID, cl.fkLanguageBranchID, cl.Status AS BranchStatus, cl.Version AS BranchVersion,
                   d.pkID, d.Status, d.Name, d.Saved, d.ChangedByName
            FROM tblContentLanguage cl
            JOIN tblContent c ON c.pkID = cl.fkContentID AND c.Deleted = 0{typeFilter}
            CROSS APPLY (
                SELECT TOP 1 wc.pkID, wc.Status, wc.Name, wc.Saved, wc.ChangedByName
                FROM tblWorkContent wc
                WHERE wc.fkContentID = cl.fkContentID AND wc.fkLanguageBranchID = cl.fkLanguageBranchID
                  AND wc.Status IN ({VersionStatuses.UnpublishedSql})
                  AND (cl.Status <> {(int)VersionStatus.Published} OR wc.pkID > ISNULL(cl.Version, 0))
                ORDER BY wc.pkID DESC) d
            WHERE (@lang IS NULL OR cl.fkLanguageBranchID = @lang)
        )
        SELECT n.pkID, n.fkContentID, n.fkLanguageBranchID, n.Status, n.Name, n.Saved, n.ChangedByName,
               (SELECT COUNT(*) FROM tblWorkContent w
                WHERE w.fkContentID = n.fkContentID AND w.fkLanguageBranchID = n.fkLanguageBranchID
                  AND w.Status IN ({VersionStatuses.UnpublishedSql})
                  AND (n.BranchStatus <> {(int)VersionStatus.Published} OR w.pkID > ISNULL(n.BranchVersion, 0))) AS Drafts
        FROM newest n
        WHERE (@since IS NULL OR n.Saved >= @since) AND (@by IS NULL OR n.ChangedByName LIKE @by ESCAPE '\')
        ORDER BY n.Saved DESC, n.pkID DESC
        OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY
        OPTION (RECOMPILE)
        """;

    /// <param name="by">Matches anywhere in the saving user's name.</param>
    /// <param name="typeIds">Only content of these types; null for every type.</param>
    /// <returns>Up to <paramref name="limit"/> + 1 drafts.</returns>
    public async Task<IReadOnlyList<DraftInfo>> ListAsync(
        DateTime? since, string? by, LanguageBranch? language, IReadOnlyCollection<int>? typeIds, int offset, int limit, CancellationToken cancellationToken)
    {
        if (typeIds is { Count: 0 })
        {
            return [];
        }
        var typeFilter = typeIds is null ? "" : $" AND c.fkContentTypeID IN ({string.Join(",", SqlLists.Ints(typeIds))})";
        var rows = await session.Db.QueryAsync(Sql(typeFilter), r => (
                Version: r.GetInt32("pkID"),
                ContentId: r.GetInt32("fkContentID"),
                LanguageId: r.GetInt32("fkLanguageBranchID"),
                Status: VersionStatuses.From(r.GetInt32OrNull("Status")),
                Name: r.GetStringOrNull("Name"),
                Saved: r.GetDateTimeOrNull("Saved"),
                ChangedBy: r.GetStringOrNull("ChangedByName"),
                Drafts: r.GetInt32("Drafts")),
            cancellationToken,
            new SqlParameter("@lang", System.Data.SqlDbType.Int) { Value = (object?)language?.Id ?? DBNull.Value },
            new SqlParameter("@since", System.Data.SqlDbType.DateTime) { Value = (object?)since ?? DBNull.Value },
            new SqlParameter("@by", System.Data.SqlDbType.NVarChar, 255) { Value = by is null ? DBNull.Value : WhereClause.ContainsPattern(by) },
            new SqlParameter("@offset", offset),
            new SqlParameter("@take", limit + 1));

        await session.Identities.LoadAsync(rows.Select(r => r.ContentId), [], cancellationToken);
        return rows.Select(r =>
        {
            var header = session.Identities.Header(r.ContentId)!;
            var identity = session.Identities.Describe(header, session.Model.Language(r.LanguageId), status: r.Status, name: r.Name);
            return new DraftInfo(identity.Ref!, identity.Guid, identity.Type!, identity.Name, identity.Language, identity.Status!, identity.Url,
                ContentIdentity.RefFor(r.ContentId, r.Version), r.Saved, r.ChangedBy, r.Drafts);
        }).ToList();
    }
}
