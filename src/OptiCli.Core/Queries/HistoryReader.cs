using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <param name="Ref">The content id.</param>
public sealed record HistoryParent(string Ref, string? Name);

/// <summary>One entry of the CMS's change log (<c>tblActivityLog</c>) for a content item.</summary>
/// <param name="When">UTC.</param>
/// <param name="By">Who, as the CMS recorded it; empty for changes without a user (a scheduled job).</param>
/// <param name="Action">See <see cref="HistoryActions"/>.</param>
/// <param name="Version">The version it was about (<c>123_456</c>), when it names one.</param>
/// <param name="Name">The item's name as it was then.</param>
/// <param name="From">For a move (and a delete or restore, which are moves): where it came from.</param>
/// <param name="To">Where it went.</param>
/// <param name="PreviousStatus">For a publish: what the version was before (<c>checkedIn</c>, <c>delayedPublish</c>, ...).</param>
public sealed record HistoryEntry(
    DateTime When, string By, string Action, string? Version, string? Language, string? Name,
    HistoryParent? From = null, HistoryParent? To = null, string? PreviousStatus = null);

/// <summary>
/// The CMS's <c>ContentActionType</c> (<c>tblActivityLog.Action</c> for <c>Type = 'Content'</c>), by value, as
/// <c>history</c> names them. Two moves get names of their own: into the recycle bin is <see cref="Delete"/>, as the edit
/// UI and <c>opticli delete</c> call it, and out of it is <see cref="Restore"/>; the CMS's own Delete (value 3) is
/// <see cref="DeletePermanently"/>.
/// </summary>
public static class HistoryActions
{
    public const string Delete = "delete";
    public const string Restore = "restore";
    public const string DeletePermanently = "deletePermanently";
    public const string Move = "move";

    private static readonly string[] ByValue =
    [
        "unknown", "checkIn", "publish", DeletePermanently, "save", Move, "create", "deleteLanguage", "deleteChildren",
        "deletedItems", "rejected", "delayedPublish", "requestApproval", "deleteVersion",
    ];

    public static string Name(int value) => value >= 0 && value < ByValue.Length ? ByValue[value] : "unknown";
}

/// <summary>
/// <c>history &lt;ref&gt;</c>: what the CMS's change log says happened to one item, newest first. It is the only record of
/// moves and deletes; drafts saved aren't in it (<c>versions</c> lists them), and the Change Log Auto Truncate job removes
/// old entries.
/// </summary>
public sealed class HistoryReader(ContentSession session)
{
    /// <summary>The entries for the item itself (<c>content://default/123</c>) and its versions (<c>.../123/456/en</c>).</summary>
    private const string Sql = """
        SELECT pkID, LogData, ChangeDate, Action, ChangedBy
        FROM tblActivityLog
        WHERE Type = N'Content' AND (RelatedItem = @item OR RelatedItem LIKE @versions)
          AND (@since IS NULL OR ChangeDate >= @since) AND (@by IS NULL OR ChangedBy LIKE @by ESCAPE '\')
        ORDER BY ChangeDate DESC, pkID DESC
        OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY
        """;

    private const string RecycleBinSql = """
        SELECT c.pkID FROM tblContent c JOIN tblContentType t ON t.pkID = c.fkContentTypeID WHERE t.Name = N'SysRecycleBin'
        """;

    /// <summary>The change log's name for content: <c>content://&lt;provider&gt;/&lt;id&gt;</c>, <c>default</c> for the CMS's own.</summary>
    public static string RelatedItem(int id, string? provider = null) =>
        $"content://{provider ?? "default"}/{id.ToString(CultureInfo.InvariantCulture)}";

    /// <param name="by">Matches anywhere in the name of who made the change.</param>
    /// <returns>Up to <paramref name="limit"/> + 1 entries, newest first.</returns>
    public async Task<IReadOnlyList<HistoryEntry>> ListAsync(int id, string? provider, DateTime? since, string? by, int offset, int limit, CancellationToken cancellationToken)
    {
        var item = RelatedItem(id, provider);
        var rows = await session.Db.QueryAsync(Sql, r => (
                Data: r.GetStringOrNull("LogData"),
                When: r.GetDateTimeOrNull("ChangeDate") ?? default,
                Action: r.GetInt32("Action"),
                By: r.GetStringOrNull("ChangedBy") ?? ""),
            cancellationToken,
            new SqlParameter("@item", item),
            new SqlParameter("@versions", item + "/%"),
            new SqlParameter("@since", System.Data.SqlDbType.DateTime) { Value = (object?)since ?? DBNull.Value },
            new SqlParameter("@by", System.Data.SqlDbType.NVarChar, 255) { Value = by is null ? DBNull.Value : WhereClause.ContainsPattern(by) },
            new SqlParameter("@offset", offset),
            new SqlParameter("@take", limit + 1));

        var entries = rows.Select(r => (Row: r, Data: Attributes(r.Data))).ToList();
        var parents = entries.SelectMany(e => new[] { e.Data.GetValueOrDefault("OldParent"), e.Data.GetValueOrDefault("NewParent") })
            .Select(RestoreParents.Id).OfType<int>().Distinct().ToList();
        await session.Identities.LoadAsync(parents, [], cancellationToken);
        var bin = parents.Count == 0 ? null : (await session.Db.QueryAsync(RecycleBinSql, r => (int?)r.GetInt32("pkID"), cancellationToken)).FirstOrDefault();
        return entries.Select(e => Entry(e.Row.When, e.Row.By, e.Row.Action, e.Data, bin)).ToList();
    }

    private HistoryEntry Entry(DateTime when, string by, int action, IReadOnlyDictionary<string, string> data, int? bin)
    {
        var from = Parent(data.GetValueOrDefault("OldParent"));
        var to = Parent(data.GetValueOrDefault("NewParent"));
        var name = HistoryActions.Name(action);
        if (name == HistoryActions.Move && bin is { } recycleBin)
        {
            var binRef = ContentIdentity.RefFor(recycleBin);
            name = to?.Ref == binRef ? HistoryActions.Delete : from?.Ref == binRef ? HistoryActions.Restore : name;
        }
        var version = data.GetValueOrDefault("ContentLink") is { } link && link.Contains('_') && !link.Contains("__", StringComparison.Ordinal) ? link : null;
        return new HistoryEntry(
            when, by, name, version,
            data.GetValueOrDefault("Language") is { Length: > 0 } language ? language : null,
            data.GetValueOrDefault("Name"),
            from, to,
            data.GetValueOrDefault("PreviousState") is { Length: > 0 } previous ? char.ToLowerInvariant(previous[0]) + previous[1..] : null);
    }

    private HistoryParent? Parent(string? stored) =>
        RestoreParents.Id(stored) is { } id
            ? new HistoryParent(ContentIdentity.RefFor(id), session.Identities.Header(id) is { } header ? session.Identities.Describe(header, null).Name : null)
            : null;

    /// <summary>The attributes of the entry's <c>&lt;properties .../&gt;</c> element; none when it isn't that.</summary>
    public static IReadOnlyDictionary<string, string> Attributes(string? logData)
    {
        if (string.IsNullOrWhiteSpace(logData))
        {
            return new Dictionary<string, string>();
        }
        try
        {
            return XElement.Parse(logData).Attributes().GroupBy(a => a.Name.LocalName).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        }
        catch (XmlException)
        {
            return new Dictionary<string, string>();
        }
    }
}
