using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;

namespace OptiCli.Core.Content;

/// <summary>One row of <c>tblWorkContent</c>: a saved version of one language branch.</summary>
/// <param name="Ref"><c>content_version</c>, usable as a ref.</param>
/// <param name="Primary">True for the branch's primary version (the published one, when published).</param>
public sealed record VersionInfo(
    string Ref,
    string? Language,
    string Status,
    string? Name,
    DateTime? Saved,
    string? ChangedBy,
    DateTime? StartPublish,
    DateTime? DelayPublishUntil,
    bool? Primary)
{
    [JsonIgnore] public int Id { get; init; }

    [JsonIgnore] public int ContentId { get; init; }

    [JsonIgnore] public int LanguageId { get; init; }

    [JsonIgnore] public VersionStatus StatusValue { get; init; }

    /// <summary>The page's child order rule and sort index as saved in this version (they are versioned).</summary>
    [JsonIgnore] public int? ChildOrderRule { get; init; }

    [JsonIgnore] public int? PeerOrder { get; init; }
}

public static class VersionReader
{
    private const string Columns = """
        wc.pkID, wc.fkContentID, wc.fkLanguageBranchID, wc.Status, wc.Name, wc.Saved, wc.ChangedByName, wc.StartPublish,
        wc.DelayPublishUntil, wc.ChildOrderRule, wc.PeerOrder, cl.Status AS BranchStatus, cl.Version AS BranchVersion, cd.CommonDraftId
        """;

    private static readonly string From = $"""
        FROM tblWorkContent wc
        LEFT JOIN tblContentLanguage cl ON cl.fkContentID = wc.fkContentID AND cl.fkLanguageBranchID = wc.fkLanguageBranchID
        {ContentHeaderReader.CommonDraftApply}
        """;

    public static async Task<VersionInfo?> ByIdAsync(CmsDatabase db, CmsModel model, int versionId, CancellationToken cancellationToken) =>
        (await db.QueryAsync($"SELECT {Columns} {From} WHERE wc.pkID = @version", r => Map(r, model), cancellationToken,
            new SqlParameter("@version", versionId))).FirstOrDefault();

    public static async Task<VersionInfo?> LatestAsync(CmsDatabase db, CmsModel model, int contentId, int languageId, CancellationToken cancellationToken) =>
        (await db.QueryAsync($"SELECT TOP 1 {Columns} {From} WHERE wc.fkContentID = @id AND wc.fkLanguageBranchID = @lang ORDER BY wc.pkID DESC",
            r => Map(r, model), cancellationToken, new SqlParameter("@id", contentId), new SqlParameter("@lang", languageId))).FirstOrDefault();

    /// <summary>Newest first; fetches one row more than <paramref name="limit"/> so callers can tell whether more exist.</summary>
    public static Task<IReadOnlyList<VersionInfo>> ListAsync(
        CmsDatabase db, CmsModel model, int contentId, int? languageId, int offset, int limit, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT {Columns} {From}
            WHERE wc.fkContentID = @id AND (@lang IS NULL OR wc.fkLanguageBranchID = @lang)
            ORDER BY wc.pkID DESC
            OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY
            """;
        return db.QueryAsync(sql, r => Map(r, model), cancellationToken,
            new SqlParameter("@id", contentId),
            new SqlParameter("@lang", System.Data.SqlDbType.Int) { Value = (object?)languageId ?? DBNull.Value },
            new SqlParameter("@offset", offset),
            new SqlParameter("@take", limit + 1));
    }

    private static VersionInfo Map(SqlDataReader r, CmsModel model)
    {
        var id = r.GetInt32("pkID");
        var contentId = r.GetInt32("fkContentID");
        var languageId = r.GetInt32("fkLanguageBranchID");
        var status = VersionStatuses.From(r.GetInt32OrNull("Status"));
        return new VersionInfo(
            ContentIdentity.RefFor(contentId, id),
            model.Language(languageId)?.DisplayCode,
            VersionStatuses.Name(status),
            r.GetStringOrNull("Name"),
            r.GetDateTimeOrNull("Saved"),
            r.GetStringOrNull("ChangedByName"),
            r.GetDateTimeOrNull("StartPublish"),
            r.GetDateTimeOrNull("DelayPublishUntil"),
            VersionStatuses.PrimaryVersion(VersionStatuses.From(r.GetInt32OrNull("BranchStatus")), r.GetInt32OrNull("BranchVersion"), r.GetInt32OrNull("CommonDraftId")) == id
                ? true
                : null)
        {
            Id = id,
            ContentId = contentId,
            LanguageId = languageId,
            StatusValue = status,
            ChildOrderRule = r.GetInt32OrNull("ChildOrderRule"),
            PeerOrder = r.GetInt32OrNull("PeerOrder"),
        };
    }
}
