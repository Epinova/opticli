using System.Globalization;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <summary>A content item in the tree, with its languages and how many children it has.</summary>
/// <param name="Children">Nested children (tree only), in the order the CMS lists them.</param>
/// <param name="SortIndex">Set when the parent sorts its children by sort index (<c>children</c> and <c>tree</c>).</param>
/// <param name="More">How many further children exist beyond those listed.</param>
public sealed record TreeNode(
    string Ref,
    Guid Guid,
    string Type,
    string? Name,
    string? Language,
    string Status,
    string? Url,
    IReadOnlyList<string> Languages,
    int ChildCount,
    bool? Deleted,
    int? SortIndex = null,
    IReadOnlyList<TreeNode>? Children = null,
    int? More = null);

/// <summary><c>tree</c>, <c>children</c> and <c>ancestors</c>, all from <c>tblTree</c>.</summary>
public sealed class TreeReader(ContentSession session)
{
    /// <summary>Hard cap on nodes one <c>tree</c> call loads, so a deep tree of a big site stays fast.</summary>
    public const int MaxNodes = 5000;

    private const string DescendantsSql = """
        SELECT TOP (@cap) fkChildID, NestingLevel FROM tblTree
        WHERE fkParentID = @root AND NestingLevel <= @depth
        ORDER BY NestingLevel, fkChildID
        """;

    private const string ChildCountsSql = """
        SELECT fkParentID, COUNT(*) AS Children FROM tblTree
        WHERE NestingLevel = 1 AND fkParentID IN ({0})
        GROUP BY fkParentID
        """;

    /// <returns>The root with nested children, and whether <see cref="MaxNodes"/> cut the tree short.</returns>
    public async Task<(TreeNode Root, bool Capped)> TreeAsync(int rootId, int depth, int perNode, LanguageBranch? language, CancellationToken cancellationToken)
    {
        var descendants = await session.Db.QueryAsync(DescendantsSql, r => (Id: r.GetInt32("fkChildID"), Level: (int)r.GetInt16(r.GetOrdinal("NestingLevel"))),
            cancellationToken, new SqlParameter("@root", rootId), new SqlParameter("@depth", depth), new SqlParameter("@cap", MaxNodes + 1));
        var capped = descendants.Count > MaxNodes;
        var ids = descendants.Take(MaxNodes).Select(d => d.Id).Append(rootId).ToList();

        await session.Identities.LoadAsync(ids, [], cancellationToken);
        var counts = await ChildCountsAsync(ids, cancellationToken);
        var byParent = ids.Select(session.Identities.Header).OfType<ContentHeader>()
            .Where(h => h.Id != rootId)
            .ToLookup(h => h.ParentId ?? 0);

        TreeNode Build(ContentHeader header, int level)
        {
            var node = Node(header, language, counts.GetValueOrDefault(header.Id), SortIndex(header));
            if (level >= depth)
            {
                return node;
            }
            var children = ChildOrder.Sort(byParent[header.Id], header.ChildOrderRule, language?.Id);
            var shown = children.Take(perNode).Select(c => Build(c, level + 1)).ToList();
            var more = node.ChildCount - shown.Count;
            return node with { Children = shown, More = more > 0 ? more : null };
        }

        var root = await session.HeaderAsync(rootId, cancellationToken);
        return (Build(root, 0), capped);
    }

    /// <summary>All direct children, sorted by the parent's rule.</summary>
    public async Task<IReadOnlyList<ContentHeader>> ChildrenAsync(int parentId, LanguageBranch? language, CancellationToken cancellationToken)
    {
        var parent = await session.HeaderAsync(parentId, cancellationToken);
        var ids = await session.Db.QueryAsync("SELECT fkChildID FROM tblTree WHERE fkParentID = @id AND NestingLevel = 1",
            r => r.GetInt32("fkChildID"), cancellationToken, new SqlParameter("@id", parentId));
        await session.Identities.LoadAsync(ids, [], cancellationToken);
        return ChildOrder.Sort(ids.Select(session.Identities.Header).OfType<ContentHeader>(), parent.ChildOrderRule, language?.Id);
    }

    /// <summary>Tree nodes (without nested children) for a page of headers.</summary>
    /// <param name="sortIndex">Show each node's sort index when its parent sorts by it (for children).</param>
    public async Task<IReadOnlyList<TreeNode>> NodesAsync(
        IReadOnlyList<ContentHeader> headers, LanguageBranch? language, CancellationToken cancellationToken, bool sortIndex = false)
    {
        var counts = await ChildCountsAsync(headers.Select(h => h.Id), cancellationToken);
        return headers.Select(h => Node(h, language, counts.GetValueOrDefault(h.Id), sortIndex ? SortIndex(h) : null)).ToList();
    }

    /// <summary>The item's sort index when its (loaded) parent lists children by it; otherwise it doesn't show.</summary>
    private int? SortIndex(ContentHeader header) =>
        header.ParentId is { } parentId && session.Identities.Header(parentId) is { } parent && ChildOrder.ByIndex(parent.ChildOrderRule)
            ? header.PeerOrder
            : null;

    private TreeNode Node(ContentHeader header, LanguageBranch? language, int childCount, int? sortIndex = null)
    {
        var identity = session.Identities.Describe(header, language);
        return new TreeNode(
            identity.Ref!,
            header.Guid,
            identity.Type!,
            identity.Name,
            identity.Language,
            identity.Status!,
            identity.Url,
            header.Languages.Keys.Select(id => session.Model.Language(id)?.DisplayCode).OfType<string>().Order().ToList(),
            childCount,
            identity.Deleted,
            sortIndex);
    }

    private async Task<Dictionary<int, int>> ChildCountsAsync(IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<int, int>();
        foreach (var list in SqlLists.Ints(ids))
        {
            var rows = await session.Db.QueryAsync(string.Format(CultureInfo.InvariantCulture, ChildCountsSql, list),
                r => (Id: r.GetInt32("fkParentID"), Count: r.GetInt32("Children")), cancellationToken);
            foreach (var (id, count) in rows)
            {
                counts[id] = count;
            }
        }
        return counts;
    }
}
