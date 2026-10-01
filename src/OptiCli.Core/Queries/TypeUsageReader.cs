using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;

namespace OptiCli.Core.Queries;

/// <summary>One instance of a type and where it is used.</summary>
/// <param name="Count">How many usages it has (0: unused).</param>
public sealed record InstanceUsage(string Ref, Guid Guid, string? Name, string? Language, string? Status, int Count, IReadOnlyList<Usage> Usages);

/// <summary>
/// <c>where-used --type</c>: <see cref="WhereUsedReader"/> for every instance of a content type (outside the recycle bin),
/// with the stored values read once (<see cref="PropertyReferenceIndex"/>) instead of scanned per instance.
/// </summary>
public sealed class TypeUsageReader(ContentSession session)
{
    /// <returns>Every instance, the most used first; unused ones last, by id.</returns>
    public async Task<IReadOnlyList<InstanceUsage>> FindAsync(int typeId, CancellationToken cancellationToken)
    {
        var ids = await session.Db.QueryAsync("SELECT pkID FROM tblContent WHERE fkContentTypeID = @type AND Deleted = 0 ORDER BY pkID",
            r => r.GetInt32(0), cancellationToken, new SqlParameter("@type", typeId));
        if (ids.Count == 0)
        {
            return [];
        }
        var headers = await ContentHeaderReader.ByIdsAsync(session.Db, ids, cancellationToken);
        var reader = new WhereUsedReader(session, ids.Count > PageUsageReader.ScansBeforeIndex ? await PropertyReferenceIndex.LoadAsync(session.Db, cancellationToken) : null);
        var result = new List<InstanceUsage>();
        foreach (var id in ids)
        {
            if (!headers.TryGetValue(id, out var header))
            {
                continue;
            }
            var usages = await reader.FindAsync(header, cancellationToken);
            var identity = session.Identities.Describe(header, null);
            result.Add(new InstanceUsage(identity.Ref!, header.Guid, identity.Name, identity.Language, identity.Status, usages.Count, usages));
        }
        return result.OrderByDescending(r => r.Count).ThenBy(r => int.Parse(r.Ref, System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }
}
