using System.Globalization;
using System.Text.RegularExpressions;
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
/// <param name="agent">
/// The running site's agent, when <c>serve</c> runs: the stored parents are then read through the CMS, as <c>restore</c>
/// reads them; without it, from the database (<see cref="RestoreParents.ReadAsync"/>).
/// </param>
public sealed class TrashReader(ContentSession session, Serve.AgentClient? agent = null)
{
    /// <summary>Where the last <see cref="ListAsync"/> read the stored parents: <c>site</c> or <c>database</c>.</summary>
    public string ParentsFrom { get; private set; } = "database";

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

        var parents = agent is null ? null : await RestoreParents.ThroughSiteAsync(agent, rows.Select(r => r.Id), cancellationToken);
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
/// <c>EPiParentRestoreStore</c>, keyed on the moved item (<c>SourceLink</c>), and deletes the entry when the item is
/// deleted for good. <c>restore</c> reads it through the CMS (<c>IParentRestoreRepository</c>), and so does <c>trash</c>
/// while <c>serve</c> runs (<see cref="ThroughSiteAsync"/>); without it, <see cref="ReadAsync"/> reads the store's rows.
/// </summary>
/// <remarks>
/// <para>The store lives in <c>tblSystemBigTable</c>, which <c>sql</c> treats as personal data: the Dynamic Data Store's
/// tables hold user profiles, form submissions and site secrets too. <see cref="ReadAsync"/> reads only that one store's
/// rows, and of them only two content references (the item and its parent), which are no more personal than
/// <c>tblContent</c>'s <c>fkParentID</c>. It doesn't loosen <c>sql</c>'s guard: <c>sql</c> still refuses those tables
/// without <c>--include-personal-data</c>.</para>
/// <para>Which columns hold the two properties is the store's own mapping. The CMS regenerates the store's view
/// (<c>VW_EPiParentRestoreStore</c>) whenever it maps the store anew, so its definition says what the running CMS uses;
/// the mapping in <c>tblBigTableStoreInfo</c> can lag behind it (seen on a database where entries written by an older CMS
/// sit in <c>Indexed_String01</c> and newer ones, which the CMS reads, in <c>String01</c>, while the mapping still names
/// the first). So the view comes first, and that table only when the view can't be read.</para>
/// </remarks>
public static partial class RestoreParents
{
    public const string StoreName = "EPiParentRestoreStore";

    /// <summary>The store's view, whose definition maps each property to a column (<c>R01.String01 as "ParentLink"</c>).</summary>
    private const string ViewSql = "SELECT OBJECT_DEFINITION(OBJECT_ID(N'dbo.VW_EPiParentRestoreStore')) AS Definition";

    /// <summary>The mapping the store was created with.</summary>
    private const string MappingSql = """
        SELECT i.PropertyName, i.ColumnName
        FROM tblBigTableStoreInfo i
        JOIN tblBigTableStoreConfig s ON s.pkId = i.fkStoreId
        WHERE s.StoreName = @store AND s.TableName = N'tblSystemBigTable' AND i.PropertyName IN (N'SourceLink', N'ParentLink') AND i.Active = 1
        """;

    /// <returns>Content id to the id of its stored parent, for the items that have an entry.</returns>
    public static async Task<IReadOnlyDictionary<int, int>> ReadAsync(CmsDatabase db, IEnumerable<int> contentIds, CancellationToken cancellationToken)
    {
        var ids = contentIds.Distinct().ToList();
        var result = new Dictionary<int, int>();
        if (ids.Count == 0)
        {
            return result;
        }
        var view = (await db.QueryAsync(ViewSql, r => r.GetStringOrNull("Definition"), cancellationToken)).FirstOrDefault();
        var (source, parent) = (Column(view, "SourceLink"), Column(view, "ParentLink"));
        if (source is null || parent is null)
        {
            var mapping = (await db.QueryAsync(MappingSql, r => (Property: r.GetString("PropertyName"), Column: r.GetStringOrNull("ColumnName")), cancellationToken,
                new SqlParameter("@store", StoreName))).ToDictionary(m => m.Property, m => m.Column);
            (source, parent) = (mapping.GetValueOrDefault("SourceLink"), mapping.GetValueOrDefault("ParentLink"));
        }
        // Only the big table's string columns are taken, so nothing else from the database reaches the SQL.
        if (source is null || parent is null || !StringColumn().IsMatch(source) || !StringColumn().IsMatch(parent))
        {
            return result;
        }
        foreach (var list in SqlLists.Ints(ids))
        {
            // A ContentReference is stored as ContentReference.ToString(): "123", "123_456", or "123__provider" for a
            // content provider's (which isn't in tblContent). Only plain ids are matched.
            var sql = $"""
                SELECT b.{source} AS SourceLink, b.{parent} AS ParentLink
                FROM tblSystemBigTable b
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

    /// <summary>The same, through the running site: the CMS's own answer, as <c>restore</c> gets it.</summary>
    /// <returns>Null when the site's agent is older than this opticli and can't answer.</returns>
    public static async Task<IReadOnlyDictionary<int, int>?> ThroughSiteAsync(Serve.AgentClient agent, IEnumerable<int> contentIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, int>();
        foreach (var chunk in contentIds.Distinct().Chunk(Protocol.RestoreParentsResult.MaxIds))
        {
            Protocol.RestoreParentsResult answer;
            try
            {
                answer = await agent.SendAsync<Protocol.RestoreParentsResult>(HttpMethod.Get, Protocol.AgentRoutes.RestoreParents(chunk), null, cancellationToken);
            }
            catch (Errors.NotFoundException ex) when (ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
            {
                return null;
            }
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

    /// <summary>The column a store's view maps <paramref name="property"/> to; null when it doesn't say.</summary>
    public static string? Column(string? viewDefinition, string property)
    {
        if (viewDefinition is null)
        {
            return null;
        }
        var match = Regex.Match(viewDefinition, $@"\bR01\.\[?(?<column>[A-Za-z_0-9]+)\]?\s+as\s+[""\[]{Regex.Escape(property)}[""\]]", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["column"].Value : null;
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

    [GeneratedRegex("^(Indexed_)?String[0-9]{2}$")]
    private static partial Regex StringColumn();
}
