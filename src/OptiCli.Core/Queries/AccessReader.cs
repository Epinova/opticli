using System.Globalization;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Protocol;

namespace OptiCli.Core.Queries;

/// <summary>
/// The effective access rights of an item, from <c>tblContentAccess</c>. That table holds explicit entries only: an item
/// without rows inherits from the nearest ancestor on its <c>ContentPath</c> that has some. A "For this page" folder is
/// the exception: the CMS gives it, and so everything in it that inherits, the access rights of the content that owns it.
/// </summary>
public static class AccessReader
{
    public static async Task<AccessList> ReadAsync(CmsDatabase db, ContentHeader header, CancellationToken cancellationToken)
    {
        var chain = await ChainAsync(header.Id, header.AncestorIds, ids => AssetFoldersAsync(db, ids, cancellationToken));
        var sql = string.Format(CultureInfo.InvariantCulture,
            "SELECT fkContentID, Name, IsRole, AccessMask FROM tblContentAccess WHERE fkContentID IN ({0})", IdList(chain.Candidates.DefaultIfEmpty(header.Id)));
        var rows = await db.QueryAsync(sql, r => (
            ContentId: r.GetInt32(0),
            Entry: new AccessEntry(r.GetString(1), AccessKinds.From(r.GetInt32(2)), AccessLevels.Describe(r.GetInt32(3)), r.GetInt32(3))), cancellationToken);

        return Effective(header.Id, chain, rows.ToLookup(r => r.ContentId, r => r.Entry));
    }

    /// <summary>The items whose stored entries may apply, nearest first, as the CMS looks for them.</summary>
    /// <param name="AssetsOf">The content whose "For this page" folder holds the item (or is it).</param>
    public sealed record AccessChain(IReadOnlyList<int> Candidates, int? AssetsOf);

    /// <summary>A "For this page" folder, a <c>SysContentAssetFolder</c> with a <c>ContentOwnerID</c>.</summary>
    /// <param name="OwnerId">The content with that GUID; null when there is none, and the CMS falls back to the folder's parent.</param>
    /// <param name="OwnerAncestorIds">Root first, as <see cref="ContentHeader.AncestorIds"/>.</param>
    public sealed record AssetFolder(int Id, int? OwnerId, IReadOnlyList<int> OwnerAncestorIds);

    /// <summary>
    /// The item, then its ancestors from the parent up; at a "For this page" folder, the folder's owner and the owner's
    /// ancestors instead (the folder's own entries never apply).
    /// </summary>
    /// <param name="ancestorIds">Root first, as <see cref="ContentHeader.AncestorIds"/>.</param>
    /// <param name="assetFolders">The "For this page" folders among the given ids.</param>
    public static async Task<AccessChain> ChainAsync(int id, IReadOnlyList<int> ancestorIds, Func<IReadOnlyList<int>, Task<IReadOnlyDictionary<int, AssetFolder>>> assetFolders)
    {
        var candidates = new List<int>();
        int? assetsOf = null;
        var owners = new HashSet<int>();
        var segment = ancestorIds.Reverse().Prepend(id).ToList();
        while (segment.Count > 0)
        {
            var folders = await assetFolders(segment);
            var at = segment.FindIndex(folders.ContainsKey);
            if (at < 0)
            {
                candidates.AddRange(segment);
                break;
            }
            candidates.AddRange(segment.Take(at));
            var folder = folders[segment[at]];
            if (folder.OwnerId is { } owner && owners.Add(owner))
            {
                assetsOf ??= owner;
                segment = folder.OwnerAncestorIds.Reverse().Prepend(owner).ToList();
            }
            else
            {
                segment = segment.Skip(at + 1).ToList();
            }
        }
        return new AccessChain(candidates, assetsOf);
    }

    /// <param name="explicitEntries">The stored entries of the chain's candidates, by content id.</param>
    public static AccessList Effective(int id, AccessChain chain, ILookup<int, AccessEntry> explicitEntries)
    {
        var assetsOf = chain.AssetsOf?.ToString(CultureInfo.InvariantCulture);
        foreach (var candidate in chain.Candidates)
        {
            if (explicitEntries[candidate].Any())
            {
                return new AccessList(candidate != id, candidate.ToString(CultureInfo.InvariantCulture), AccessEntry.Sorted(explicitEntries[candidate]), assetsOf);
            }
        }
        return new AccessList(true, null, [], assetsOf);
    }

    private static async Task<IReadOnlyDictionary<int, AssetFolder>> AssetFoldersAsync(CmsDatabase db, IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var sql = string.Format(CultureInfo.InvariantCulture, """
            SELECT f.pkID, o.pkID, ISNULL(o.ContentPath, '')
            FROM tblContent f
            LEFT JOIN tblContent o ON o.ContentGUID = f.ContentOwnerID
            WHERE f.pkID IN ({0}) AND f.ContentOwnerID IS NOT NULL AND f.ContentOwnerID <> '00000000-0000-0000-0000-000000000000'
            """, IdList(ids));
        var rows = await db.QueryAsync(sql, r => new AssetFolder(
            r.GetInt32(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.IsDBNull(1) ? [] : ContentPaths.Parse(r.GetString(2))), cancellationToken);
        return rows.ToDictionary(f => f.Id);
    }

    private static string IdList(IEnumerable<int> ids) => string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
}
