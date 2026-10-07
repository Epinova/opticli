using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Data;

namespace OptiCli.Core.Content;

/// <summary>One row of <c>tblWorkContent</c>: a saved version of one language branch.</summary>
/// <param name="Ref"><c>content_version</c>, usable as a ref.</param>
/// <param name="Primary">True for the branch's primary version (the published one, when published).</param>
/// <param name="Variation">
/// CMS 13: the content variation the version belongs to (<c>tblContentVariation.Key</c>); null for a version of the
/// content itself. A variation's version holds only the properties it changes.
/// </param>
public sealed record VersionInfo(
    string Ref,
    string? Language,
    string Status,
    string? Name,
    DateTime? Saved,
    string? ChangedBy,
    DateTime? StartPublish,
    DateTime? DelayPublishUntil,
    bool? Primary,
    string? Variation = null)
{
    [JsonIgnore] public int Id { get; init; }

    [JsonIgnore] public int ContentId { get; init; }

    [JsonIgnore] public int LanguageId { get; init; }

    [JsonIgnore] public VersionStatus StatusValue { get; init; }

    /// <summary>When this version stops being published, as saved in it (a draft can change it).</summary>
    [JsonIgnore] public DateTime? StopPublish { get; init; }

    /// <summary>The page's child order rule and sort index as saved in this version (they are versioned).</summary>
    [JsonIgnore] public int? ChildOrderRule { get; init; }

    [JsonIgnore] public int? PeerOrder { get; init; }

    /// <summary>The page's link type (<c>EPiServer.Core.PageShortcutType</c>): 0 normal, 1 shortcut, 2 external, 3 inactive, 4 fetch data.</summary>
    [JsonIgnore] public int? LinkType { get; init; }

    /// <summary>The page's link: its own permanent link, a shortcut target's, or an external link's URL.</summary>
    [JsonIgnore] public string? LinkUrl { get; init; }

    /// <summary>The page a shortcut or fetch-data page points at.</summary>
    [JsonIgnore] public Guid? ShortcutGuid { get; init; }

    /// <summary>The window its link opens in, as stored: <c>target="_blank"</c>.</summary>
    [JsonIgnore] public string? FrameName { get; init; }

    /// <summary>The simple address, as stored: <c>~/campaign</c>.</summary>
    [JsonIgnore] public string? ExternalUrl { get; init; }
}

public static class VersionReader
{
    private static string Columns(CmsSchema schema) => $"""
        wc.pkID, wc.fkContentID, wc.fkLanguageBranchID, wc.Status, wc.Name, wc.Saved, wc.ChangedByName, wc.StartPublish,
        wc.StopPublish, wc.DelayPublishUntil, wc.ChildOrderRule, wc.PeerOrder, wc.LinkType, wc.LinkURL, wc.ContentLinkGUID, wc.ExternalURL,
        f.FrameName, cl.Status AS BranchStatus, cl.Version AS BranchVersion, cd.CommonDraftId, {(schema.Variations ? "v.[Key]" : "NULL")} AS Variation
        """;

    private static string From(CmsSchema schema) => $"""
        FROM tblWorkContent wc
        LEFT JOIN tblContentLanguage cl ON cl.fkContentID = wc.fkContentID AND cl.fkLanguageBranchID = wc.fkLanguageBranchID
        LEFT JOIN tblFrame f ON f.pkID = wc.fkFrameID
        {(schema.Variations ? "LEFT JOIN tblContentVariation v ON v.pkID = wc.fkVariationID" : "")}
        {ContentHeaderReader.CommonDraftApply(schema)}
        """;

    private static string Select(CmsModel model, string top = "") => $"SELECT {top}{Columns(model.Schema)} {From(model.Schema)}";

    public static async Task<VersionInfo?> ByIdAsync(CmsDatabase db, CmsModel model, int versionId, CancellationToken cancellationToken) =>
        (await db.QueryAsync($"{Select(model)} WHERE wc.pkID = @version", r => Map(r, model), cancellationToken,
            new SqlParameter("@version", versionId))).FirstOrDefault();

    /// <summary>The branch's newest version, not counting a content variation's (CMS 13).</summary>
    public static async Task<VersionInfo?> LatestAsync(CmsDatabase db, CmsModel model, int contentId, int languageId, CancellationToken cancellationToken) =>
        (await db.QueryAsync($"{Select(model, "TOP 1 ")} WHERE wc.fkContentID = @id AND wc.fkLanguageBranchID = @lang{model.Schema.DefaultVariationOnly("wc")} ORDER BY wc.pkID DESC",
            r => Map(r, model), cancellationToken, new SqlParameter("@id", contentId), new SqlParameter("@lang", languageId))).FirstOrDefault();

    /// <summary>The branch's published version, not a content variation's (CMS 13); null when it isn't published.</summary>
    public static async Task<VersionInfo?> PublishedAsync(CmsDatabase db, CmsModel model, int contentId, int languageId, CancellationToken cancellationToken) =>
        (await db.QueryAsync($"{Select(model, "TOP 1 ")} WHERE wc.fkContentID = @id AND wc.fkLanguageBranchID = @lang AND wc.Status = {(int)VersionStatus.Published}{model.Schema.DefaultVariationOnly("wc")} ORDER BY wc.pkID DESC",
            r => Map(r, model), cancellationToken, new SqlParameter("@id", contentId), new SqlParameter("@lang", languageId))).FirstOrDefault();

    /// <summary>
    /// CMS 13: a content variation's version in the branch: its published one, or with <paramref name="latest"/> its newest.
    /// The key is matched as the CMS does (<c>LoweredKey</c>). Null when there is none (or on a schema without variations).
    /// </summary>
    public static async Task<VersionInfo?> VariationAsync(CmsDatabase db, CmsModel model, int contentId, int languageId, string key, bool latest, CancellationToken cancellationToken) =>
        !model.Schema.Variations ? null : (await db.QueryAsync(
            $"{Select(model, "TOP 1 ")} WHERE wc.fkContentID = @id AND wc.fkLanguageBranchID = @lang AND v.LoweredKey = LOWER(@key){(latest ? "" : $" AND wc.Status = {(int)VersionStatus.Published}")} ORDER BY wc.pkID DESC",
            r => Map(r, model), cancellationToken, new SqlParameter("@id", contentId), new SqlParameter("@lang", languageId), new SqlParameter("@key", key))).FirstOrDefault();

    /// <summary>CMS 13: the keys of the content variations the content has versions of, in any language.</summary>
    public static async Task<IReadOnlyList<string>> VariationKeysAsync(CmsDatabase db, CmsModel model, int contentId, CancellationToken cancellationToken) =>
        !model.Schema.Variations ? [] : await db.QueryAsync("""
            SELECT DISTINCT v.[Key] FROM tblWorkContent wc JOIN tblContentVariation v ON v.pkID = wc.fkVariationID
            WHERE wc.fkContentID = @id ORDER BY v.[Key]
            """, r => r.GetString(0), cancellationToken, new SqlParameter("@id", contentId));

    /// <summary>
    /// The versions of the branch after its published version (any version, when it was never published) up to
    /// <paramref name="upTo"/>, saved by someone other than <paramref name="except"/>, newest first: what the site agent
    /// asks to confirm before publishing <paramref name="upTo"/>, if it carries their changes.
    /// </summary>
    public static Task<IReadOnlyList<VersionInfo>> SavedByOthersAsync(
        CmsDatabase db, CmsModel model, int contentId, int languageId, int upTo, string except, CancellationToken cancellationToken)
    {
        var sql = $"""
            {Select(model)}
            WHERE wc.fkContentID = @id AND wc.fkLanguageBranchID = @lang AND wc.pkID <= @upTo{model.Schema.DefaultVariationOnly("wc")}
              AND wc.pkID > ISNULL((SELECT MAX(p.pkID) FROM tblWorkContent p
                                    WHERE p.fkContentID = @id AND p.fkLanguageBranchID = @lang AND p.Status = {(int)VersionStatus.Published}{model.Schema.DefaultVariationOnly("p")}), 0)
              AND ISNULL(LTRIM(RTRIM(wc.ChangedByName)), '') <> @except
            ORDER BY wc.pkID DESC
            """;
        return db.QueryAsync(sql, r => Map(r, model), cancellationToken,
            new SqlParameter("@id", contentId), new SqlParameter("@lang", languageId), new SqlParameter("@upTo", upTo),
            new SqlParameter("@except", except));
    }

    /// <summary>
    /// Newest first, a content variation's versions (CMS 13) included, marked as such; fetches one row more than
    /// <paramref name="limit"/> so callers can tell whether more exist.
    /// </summary>
    public static Task<IReadOnlyList<VersionInfo>> ListAsync(
        CmsDatabase db, CmsModel model, int contentId, int? languageId, int offset, int limit, CancellationToken cancellationToken)
    {
        var sql = $"""
            {Select(model)}
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
        var variation = r.GetStringOrNull("Variation");
        return new VersionInfo(
            ContentIdentity.RefFor(contentId, id),
            model.Language(languageId)?.DisplayCode,
            VersionStatuses.Name(status),
            r.GetStringOrNull("Name"),
            r.GetDateTimeOrNull("Saved"),
            r.GetStringOrNull("ChangedByName"),
            r.GetDateTimeOrNull("StartPublish"),
            r.GetDateTimeOrNull("DelayPublishUntil"),
            variation is null && VersionStatuses.PrimaryVersion(VersionStatuses.From(r.GetInt32OrNull("BranchStatus")), r.GetInt32OrNull("BranchVersion"), r.GetInt32OrNull("CommonDraftId")) == id
                ? true
                : null,
            variation)
        {
            Id = id,
            ContentId = contentId,
            LanguageId = languageId,
            StatusValue = status,
            StopPublish = r.GetDateTimeOrNull("StopPublish"),
            ChildOrderRule = r.GetInt32OrNull("ChildOrderRule"),
            PeerOrder = r.GetInt32OrNull("PeerOrder"),
            LinkType = r.GetInt32OrNull("LinkType"),
            LinkUrl = r.GetStringOrNull("LinkURL"),
            ShortcutGuid = r.GetGuidOrNull("ContentLinkGUID"),
            FrameName = r.GetStringOrNull("FrameName"),
            ExternalUrl = r.GetStringOrNull("ExternalURL"),
        };
    }
}
