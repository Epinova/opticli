using System.Globalization;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Protocol;

namespace OptiCli.Core.Queries;

/// <summary>
/// The effective access rights of an item, from <c>tblContentAccess</c>. That table holds explicit entries only: an item
/// without rows inherits from the nearest ancestor on its <c>ContentPath</c> that has some.
/// </summary>
public static class AccessReader
{
    public static async Task<AccessList> ReadAsync(CmsDatabase db, ContentHeader header, CancellationToken cancellationToken)
    {
        // The item first, then its ancestors from the parent up to the root.
        var chain = header.AncestorIds.Reverse().Prepend(header.Id).ToList();
        var sql = string.Format(CultureInfo.InvariantCulture,
            "SELECT fkContentID, Name, IsRole, AccessMask FROM tblContentAccess WHERE fkContentID IN ({0})",
            string.Join(',', chain.Select(id => id.ToString(CultureInfo.InvariantCulture))));
        var rows = await db.QueryAsync(sql, r => (
            ContentId: r.GetInt32(0),
            Entry: new AccessEntry(r.GetString(1), AccessKinds.From(r.GetInt32(2)), AccessLevels.Describe(r.GetInt32(3)), r.GetInt32(3))), cancellationToken);

        return Effective(header.Id, header.AncestorIds, rows.ToLookup(r => r.ContentId, r => r.Entry));
    }

    /// <param name="ancestorIds">Root first, as <see cref="ContentHeader.AncestorIds"/>.</param>
    /// <param name="explicitEntries">The stored entries of the item and its ancestors, by content id.</param>
    public static AccessList Effective(int id, IReadOnlyList<int> ancestorIds, ILookup<int, AccessEntry> explicitEntries)
    {
        foreach (var candidate in ancestorIds.Reverse().Prepend(id))
        {
            if (explicitEntries[candidate].Any())
            {
                return new AccessList(candidate != id, candidate.ToString(CultureInfo.InvariantCulture), AccessEntry.Sorted(explicitEntries[candidate]));
            }
        }
        return new AccessList(true, null, []);
    }
}
