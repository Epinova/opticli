using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;

namespace OptiCli.Core.Content;

public static class ContentHeaderReader
{
    /// <summary>
    /// <c>CommonDraftId</c>: the common draft of a <c>tblContentLanguage cl</c> row that isn't published. Such a
    /// branch's values in <c>tblContentProperty</c> are the common draft's, while <c>cl.Version</c> can still point
    /// at the branch's first version. See <see cref="VersionStatuses.PrimaryVersion"/>.
    /// </summary>
    internal static readonly string CommonDraftApply = $"""
        OUTER APPLY (
            SELECT TOP 1 d.pkID AS CommonDraftId FROM tblWorkContent d
            WHERE cl.Status <> {(int)VersionStatus.Published} AND d.fkContentID = cl.fkContentID
              AND d.fkLanguageBranchID = cl.fkLanguageBranchID AND d.CommonDraft = 1
            ORDER BY d.pkID DESC) cd
        """;

    private static readonly string HeadersSql = $$"""
        SELECT c.pkID, c.ContentGUID, c.fkContentTypeID, c.fkParentID, ISNULL(c.ContentPath, '') AS ContentPath,
               ISNULL(c.fkMasterLanguageBranchID, 0) AS MasterLanguageId, c.Deleted, ISNULL(c.ChildOrderRule, 0) AS ChildOrderRule,
               ISNULL(c.PeerOrder, 0) AS PeerOrder,
               cl.fkLanguageBranchID, cl.Name, cl.URLSegment, cl.Status, cl.Version, cd.CommonDraftId, cl.Created, cl.Saved, cl.StartPublish,
               cl.StopPublish, cl.ChangedByName, cl.BlobUri, cl.ThumbnailUri, cl.ExternalURL, cl.AutomaticLink, cl.ContentLinkGUID, cl.LinkURL
        FROM tblContent c
        LEFT JOIN tblContentLanguage cl ON cl.fkContentID = c.pkID
        {{CommonDraftApply}}
        WHERE c.pkID IN ({0})
        """;

    private const string GuidsSql = "SELECT pkID, ContentGUID FROM tblContent WHERE ContentGUID IN ({0})";

    public static async Task<IReadOnlyDictionary<int, ContentHeader>> ByIdsAsync(CmsDatabase db, IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, ContentHeader>();
        foreach (var list in SqlLists.Ints(ids))
        {
            var rows = await db.QueryAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, HeadersSql, list), r => (
                Id: r.GetInt32("pkID"),
                Guid: r.GetGuid("ContentGUID"),
                TypeId: r.GetInt32("fkContentTypeID"),
                ParentId: r.GetInt32OrNull("fkParentID"),
                Path: r.GetString("ContentPath"),
                Master: r.GetInt32("MasterLanguageId"),
                Deleted: r.GetBoolean(r.GetOrdinal("Deleted")),
                OrderRule: r.GetInt32("ChildOrderRule"),
                PeerOrder: r.GetInt32("PeerOrder"),
                Language: r.GetInt32OrNull("fkLanguageBranchID") is { } languageId
                    ? new ContentLanguageRow(
                        languageId,
                        r.GetStringOrNull("Name"),
                        r.GetStringOrNull("URLSegment"),
                        VersionStatuses.From(r.GetInt32OrNull("Status")),
                        VersionStatuses.PrimaryVersion(VersionStatuses.From(r.GetInt32OrNull("Status")), r.GetInt32OrNull("Version"), r.GetInt32OrNull("CommonDraftId")),
                        r.GetDateTimeOrNull("Created"),
                        r.GetDateTimeOrNull("Saved"),
                        r.GetDateTimeOrNull("StartPublish"),
                        r.GetDateTimeOrNull("StopPublish"),
                        r.GetStringOrNull("ChangedByName"),
                        r.GetStringOrNull("BlobUri"),
                        r.GetStringOrNull("ThumbnailUri"),
                        r.GetStringOrNull("ExternalURL"),
                        LinkTarget.From(r.GetBooleanOrNull("AutomaticLink"), r.GetGuidOrNull("ContentLinkGUID"), r.GetStringOrNull("LinkURL")))
                    : null), cancellationToken);

            foreach (var group in rows.GroupBy(r => r.Id))
            {
                var first = group.First();
                result[first.Id] = new ContentHeader(
                    first.Id,
                    first.Guid,
                    first.TypeId,
                    first.ParentId,
                    first.Path,
                    first.Master,
                    first.Deleted,
                    first.OrderRule,
                    first.PeerOrder,
                    group.Where(r => r.Language is not null).ToDictionary(r => r.Language!.LanguageId, r => r.Language!));
            }
        }
        return result;
    }

    public static async Task<ContentHeader?> ByIdAsync(CmsDatabase db, int id, CancellationToken cancellationToken) =>
        (await ByIdsAsync(db, [id], cancellationToken)).GetValueOrDefault(id);

    public static async Task<IReadOnlyDictionary<Guid, int>> IdsByGuidsAsync(CmsDatabase db, IEnumerable<Guid> guids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var list in SqlLists.Guids(guids))
        {
            var rows = await db.QueryAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, GuidsSql, list),
                r => (Id: r.GetInt32("pkID"), Guid: r.GetGuid("ContentGUID")), cancellationToken);
            foreach (var row in rows)
            {
                result[row.Guid] = row.Id;
            }
        }
        return result;
    }

    /// <summary>GUIDs that content providers mapped into the CMS (<c>tblMappedIdentity</c>): id and provider name.</summary>
    public static async Task<IReadOnlyDictionary<Guid, (int Id, string Provider)>> ProviderContentAsync(CmsDatabase db, IEnumerable<Guid> guids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, (int, string)>();
        foreach (var list in SqlLists.Guids(guids))
        {
            var rows = await db.QueryAsync(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "SELECT pkID, Provider, ContentGuid FROM tblMappedIdentity WHERE ContentGuid IN ({0})", list),
                r => (Guid: r.GetGuid("ContentGuid"), Id: r.GetInt32("pkID"), Provider: r.GetString("Provider")), cancellationToken);
            foreach (var row in rows)
            {
                result[row.Guid] = (row.Id, row.Provider);
            }
        }
        return result;
    }

    /// <summary>The newest version of a branch that is not published yet and is newer than the published one.</summary>
    public static async Task<int?> NewerDraftAsync(CmsDatabase db, int contentId, int languageId, int? publishedVersion, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT TOP 1 pkID FROM tblWorkContent
            WHERE fkContentID = @id AND fkLanguageBranchID = @lang AND Status IN ({VersionStatuses.UnpublishedSql})
              AND pkID > @published
            ORDER BY pkID DESC
            """;
        var rows = await db.QueryAsync(sql, r => r.GetInt32("pkID"), cancellationToken,
            new SqlParameter("@id", contentId), new SqlParameter("@lang", languageId), new SqlParameter("@published", publishedVersion ?? 0));
        return rows.Count > 0 ? rows[0] : null;
    }
}
