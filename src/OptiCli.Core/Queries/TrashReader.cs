using System.Globalization;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <param name="Path">The names from below the root down to the parent itself, joined with <c> / </c>.</param>
/// <param name="Deleted">True when the parent is in the recycle bin too: restore that first, or restore elsewhere.</param>
public sealed record TrashParent(string Ref, string? Name, string? Type, string Path, string? Url)
{
    public bool? Deleted { get; init; }

    /// <summary>The CMS stored a parent that no longer exists (it was deleted for good).</summary>
    public bool? Missing { get; init; }
}

/// <param name="DeletedBy">Who moved it to the recycle bin, as the CMS recorded it.</param>
/// <param name="Deleted">When, UTC.</param>
/// <param name="OriginalParent">
/// Where <c>restore</c> puts it back: the parent the CMS stored when it was moved to the recycle bin. Null when the CMS
/// has no record of it (a store emptied, a database copied without it): <c>restore --to</c> then.
/// </param>
/// <param name="Descendants">Content below it, in the recycle bin with it; it comes back with it.</param>
public sealed record TrashItem(
    string Ref, Guid Guid, string Type, string? Name, string? Language, string Status,
    string? DeletedBy, DateTime? Deleted, TrashParent? OriginalParent, int Descendants);

/// <summary>
/// <c>trash</c>: what is directly in the recycle bin (what was deleted; what was below it is counted, not listed), newest
/// first, with the parent each item goes back to.
/// </summary>
/// <param name="connect">
/// Connects to the running site's agent: the stored parents are then read through the CMS, as <c>restore</c> reads them.
/// Without it, or when the site can't answer, from the database (<see cref="RestoreParents.ReadAsync"/>).
/// </param>
public sealed class TrashReader(ContentSession session, Func<CancellationToken, Task<Serve.AgentClient>>? connect = null)
{
    /// <summary>Where the last <see cref="ListAsync"/> read the stored parents: <c>site</c> or <c>database</c>.</summary>
    public string ParentsFrom { get; private set; } = "database";

    /// <summary>Why the last <see cref="ListAsync"/> didn't ask the site, when it was given one to ask (<see cref="Serve.SiteFallback"/>).</summary>
    public string? NotFromSite { get; private set; }

    /// <summary>Content type of the recycle bin (<c>ContentReference.WasteBasket</c>).</summary>
    public const string RecycleBinType = "SysRecycleBin";

    // The type filter is an inlined id list (see SqlLists), empty when every type is wanted.
    private static string Sql(string typeFilter) => $"""
        SELECT c.pkID, c.DeletedBy, c.DeletedDate,
               (SELECT COUNT(*) FROM tblContent d WHERE d.ContentPath LIKE ISNULL(c.ContentPath, '') + CAST(c.pkID AS varchar(12)) + '.%') AS Descendants
        FROM tblContent c
        JOIN tblContent bin ON bin.pkID = c.fkParentID
        JOIN tblContentType bt ON bt.pkID = bin.fkContentTypeID AND bt.Name = N'{RecycleBinType}'
        WHERE (@since IS NULL OR c.DeletedDate >= @since) AND (@by IS NULL OR c.DeletedBy LIKE @by ESCAPE '\'){typeFilter}
        ORDER BY c.DeletedDate DESC, c.pkID DESC
        OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY
        """;

    /// <param name="by">Matches anywhere in the name of who deleted it.</param>
    /// <param name="typeIds">Only content of these types; null for every type.</param>
    /// <returns>Up to <paramref name="limit"/> + 1 items.</returns>
    public async Task<IReadOnlyList<TrashItem>> ListAsync(DateTime? since, string? by, IReadOnlyCollection<int>? typeIds, int offset, int limit, CancellationToken cancellationToken)
    {
        if (typeIds is { Count: 0 })
        {
            return [];
        }
        var typeFilter = typeIds is null ? "" : $" AND c.fkContentTypeID IN ({string.Join(",", SqlLists.Ints(typeIds))})";
        var rows = await session.Db.QueryAsync(Sql(typeFilter), r => (
                Id: r.GetInt32("pkID"),
                DeletedBy: r.GetStringOrNull("DeletedBy"),
                Deleted: r.GetDateTimeOrNull("DeletedDate"),
                Descendants: r.GetInt32("Descendants")),
            cancellationToken,
            new SqlParameter("@since", System.Data.SqlDbType.DateTime) { Value = (object?)since ?? DBNull.Value },
            new SqlParameter("@by", System.Data.SqlDbType.NVarChar, 255) { Value = by is null ? DBNull.Value : WhereClause.ContainsPattern(by) },
            new SqlParameter("@offset", offset),
            new SqlParameter("@take", limit + 1));

        IReadOnlyDictionary<int, int>? parents = null;
        NotFromSite = null;
        if (connect is not null)
        {
            var answer = await Serve.SiteFallback.AskAsync(connect, agent => RestoreParents.ThroughSiteAsync(agent, rows.Select(r => r.Id), cancellationToken), cancellationToken);
            parents = answer.Value;
            NotFromSite = answer.WhyNot;
        }
        ParentsFrom = parents is null ? "database" : "site";
        parents ??= await RestoreParents.ReadAsync(session.Db, rows.Select(r => r.Id), cancellationToken);
        // Read as they are now, not from the session's cache: whether a parent is deleted may have changed in this session.
        var headers = await ContentHeaderReader.ByIdsAsync(session.Db, rows.Select(r => r.Id).Concat(parents.Values), cancellationToken);
        await session.Identities.LoadAsync(headers.Keys.Concat(headers.Values.SelectMany(h => h.AncestorIds)), [], cancellationToken);
        return rows.Where(r => headers.ContainsKey(r.Id)).Select(r =>
        {
            var identity = session.Identities.Describe(headers[r.Id], null);
            return new TrashItem(identity.Ref!, identity.Guid, identity.Type!, identity.Name, identity.Language, identity.Status!,
                r.DeletedBy, r.Deleted, parents.TryGetValue(r.Id, out var parent) ? Parent(parent, headers.GetValueOrDefault(parent)) : null, r.Descendants);
        }).ToList();
    }

    private TrashParent Parent(int id, ContentHeader? header)
    {
        if (header is null)
        {
            return new TrashParent(ContentIdentity.RefFor(id), null, null, "", null) { Missing = true };
        }
        var identity = session.Identities.Describe(header, null);
        // Below the root: the root's name says nothing, and the recycle bin's is its type's.
        var names = header.AncestorIds.Skip(1).Append(id)
            .Select(a => session.Identities.Header(a) is { } h ? session.Identities.Describe(h, null).Name : null)
            .Select(name => name ?? "?");
        return new TrashParent(identity.Ref!, identity.Name, identity.Type, string.Join(" / ", names), identity.Url)
        {
            Deleted = header.Deleted ? true : null,
        };
    }
}

/// <summary>
/// The parent each item had before its last move, as the CMS stores it for the edit UI's Restore: its
/// <c>ParentRestoreService</c> saves every move's previous parent in the Dynamic Data Store, store
/// <c>EPiParentRestoreStore</c>, keyed on the moved item (<c>SourceLink</c>; CMS 13 renamed the two properties
/// <c>Source</c> and <c>Parent</c> when it upgrades the store), and deletes the entry when the item is
/// deleted for good. <c>restore</c> reads it through the CMS (<c>IParentRestoreRepository</c>), and so does <c>trash</c>
/// while <c>serve</c> runs (<see cref="ThroughSiteAsync"/>); without it, <see cref="ReadAsync"/> reads the store's rows.
/// </summary>
/// <remarks>
/// The store lives in <c>tblSystemBigTable</c>, which <c>sql</c> treats as personal data: the Dynamic Data Store's tables
/// hold user profiles, form submissions and site secrets too. <see cref="ReadAsync"/> reads only that one store's rows, and
/// of them only two content references (the item and its parent), which are no more personal than <c>tblContent</c>'s
/// <c>fkParentID</c>. It doesn't loosen <c>sql</c>'s guard: <c>sql</c> still refuses those tables without
/// <c>--include-personal-data</c>. Which columns hold the two: <see cref="DynamicDataStore"/>.
/// </remarks>
public static class RestoreParents
{
    public const string StoreName = "EPiParentRestoreStore";

    /// <returns>Content id to the id of its stored parent, for the items that have an entry.</returns>
    public static async Task<IReadOnlyDictionary<int, int>> ReadAsync(CmsDatabase db, IEnumerable<int> contentIds, CancellationToken cancellationToken)
    {
        var ids = contentIds.Distinct().ToList();
        var result = new Dictionary<int, int>();
        if (ids.Count == 0
            || await DynamicDataStore.FindAsync(db, StoreName, [.. Cms13Properties, .. Cms12Properties], cancellationToken) is not { } store
            || Columns(store) is not { } columns)
        {
            return result;
        }
        var (source, parent) = columns;
        foreach (var list in SqlLists.Ints(ids))
        {
            // A ContentReference is stored as ContentReference.ToString(): "123", "123_456", or "123__provider" for a
            // content provider's (which isn't in tblContent). Only plain ids are matched.
            var sql = $"""
                SELECT b.{source} AS SourceLink, b.{parent} AS ParentLink
                FROM {store.Table} b
                WHERE b.StoreName = @store AND b.{source} IN ({string.Join(",", list.Split(',').Select(id => $"N'{id}'"))})
                """;
            foreach (var (item, stored) in await db.QueryAsync(sql, r => (r.GetStringOrNull("SourceLink"), r.GetStringOrNull("ParentLink")), cancellationToken,
                new SqlParameter("@store", StoreName)))
            {
                if (Id(item) is { } itemId && Id(stored) is { } parentId)
                {
                    result[itemId] = parentId;
                }
            }
        }
        return result;
    }

    /// <summary>The item's and the parent's property names: CMS 13's, which its <c>ParentRestoreRemapService</c> renames the store to.</summary>
    private static readonly string[] Cms13Properties = ["Source", "Parent"];

    private static readonly string[] Cms12Properties = ["SourceLink", "ParentLink"];

    /// <summary>The columns of the item and its parent: CMS 13's names when the store maps them, else CMS 12's; null for neither.</summary>
    internal static (string Source, string Parent)? Columns(DynamicDataStore store) =>
        store.Column(Cms13Properties[0]) is { } source && store.Column(Cms13Properties[1]) is { } parent ? (source, parent)
        : store.Column(Cms12Properties[0]) is { } oldSource && store.Column(Cms12Properties[1]) is { } oldParent ? (oldSource, oldParent)
        : null;

    /// <summary>The same, through the running site: the CMS's own answer, as <c>restore</c> gets it.</summary>
    /// <exception cref="Errors.OptiCliException">The site couldn't answer (an agent older than this opticli: <c>not_found</c>).</exception>
    public static async Task<IReadOnlyDictionary<int, int>> ThroughSiteAsync(Serve.AgentClient agent, IEnumerable<int> contentIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, int>();
        foreach (var chunk in contentIds.Distinct().Chunk(Protocol.RestoreParentsResult.MaxIds))
        {
            var answer = await agent.SendAsync<Protocol.RestoreParentsResult>(HttpMethod.Get, Protocol.AgentRoutes.RestoreParents(chunk), null, cancellationToken);
            foreach (var (item, parent) in answer.Parents)
            {
                if (Id(item) is { } itemId && Id(parent) is { } parentId)
                {
                    result[itemId] = parentId;
                }
            }
        }
        return result;
    }

    /// <summary>The content id of a stored <c>ContentReference</c>: <c>123</c> or <c>123_456</c>; null for anything else.</summary>
    public static int? Id(string? stored)
    {
        var text = stored?.Trim() ?? "";
        var id = text.Split('_')[0];
        return text.Contains("__", StringComparison.Ordinal) || !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0
            ? null
            : value;
    }
}
