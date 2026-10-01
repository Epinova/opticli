using System.Globalization;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;

namespace OptiCli.Core.Queries;

/// <summary>A reference to content about to be deleted, from content that stays.</summary>
/// <param name="From">The content that has the reference.</param>
/// <param name="To">What it references: the deleted item, or one of its descendants.</param>
/// <param name="Property">Where in <paramref name="From"/>, as <c>where-used</c> shows it.</param>
/// <param name="Kind">As <see cref="Usage.Kind"/>: contentArea, contentReference, richTextLink, ...</param>
public sealed record IncomingReference(string From, string? Name, string? Type, string? Language, string To, string Property, string Kind);

/// <param name="References">The first <see cref="Max"/>, ordered by what they reference.</param>
/// <param name="Count">All of them.</param>
public sealed record IncomingReferences(IReadOnlyList<IncomingReference> References, int Count)
{
    public const int Max = 50;

    /// <summary>The <c>details.reason</c> of a delete stopped by them.</summary>
    public const string Reason = "referenced";

    /// <summary>"3 references from other content (12 MainArea, 40 MainBody, ...)".</summary>
    public string Describe() =>
        $"{Count} reference(s) from other content ({string.Join(", ", References.Take(5).Select(r => $"{r.From} {r.Property} -> {r.To}"))}{(Count > 5 ? ", ..." : "")})";
}

/// <summary>
/// What a delete would leave pointing into the recycle bin: <see cref="WhereUsedReader"/> for the item and every
/// descendant, without references from inside what is deleted (the subtree and its "For this page" folders) or from
/// content already in the recycle bin.
/// </summary>
public static class IncomingReferenceReader
{
    private const string SubtreeSql = """
        SELECT c.pkID FROM tblContent c WHERE c.pkID = @id OR c.ContentPath LIKE @prefix
        UNION
        SELECT a.pkID
        FROM tblContent f
        JOIN tblContent o ON o.ContentGUID = f.ContentOwnerID
        JOIN tblContent a ON a.pkID = f.pkID OR a.ContentPath LIKE f.ContentPath + CAST(f.pkID AS varchar(12)) + '.%'
        WHERE o.pkID = @id OR o.ContentPath LIKE @prefix
        """;

    public static async Task<IncomingReferences> FindAsync(ContentSession session, ContentHeader root, CancellationToken cancellationToken)
    {
        var prefix = $"{(root.ContentPath.Length == 0 ? "." : root.ContentPath)}{root.Id.ToString(CultureInfo.InvariantCulture)}.%";
        var inside = (await session.Db.QueryAsync(SubtreeSql, r => r.GetInt32(0), cancellationToken,
            new SqlParameter("@id", root.Id), new SqlParameter("@prefix", prefix))).ToHashSet();
        var subtree = await ContentHeaderReader.ByIdsAsync(session.Db, inside, cancellationToken);

        // The subtree's own "For this page" content is deleted with it, so only the subtree is looked up.
        var targets = subtree.Values.Where(h => h.Id == root.Id || h.AncestorIds.Contains(root.Id)).OrderBy(h => h.Id).ToList();
        var reader = new WhereUsedReader(session, targets.Count > PageUsageReader.ScansBeforeIndex
            ? await PropertyReferenceIndex.LoadAsync(session.Db, cancellationToken)
            : null);
        var found = new List<IncomingReference>();
        foreach (var target in targets)
        {
            foreach (var usage in await reader.FindAsync(target, cancellationToken))
            {
                var owner = int.TryParse(usage.Ref, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
                if (usage.Deleted == true || inside.Contains(owner))
                {
                    continue;
                }
                found.Add(new IncomingReference(usage.Ref, usage.Name, usage.Type, usage.Language, target.Id.ToString(CultureInfo.InvariantCulture), usage.Property, usage.Kind));
            }
        }
        var distinct = found.Distinct().ToList();
        return new IncomingReferences(distinct.Take(IncomingReferences.Max).ToList(), distinct.Count);
    }
}
