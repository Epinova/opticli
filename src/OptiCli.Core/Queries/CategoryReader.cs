using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <param name="Name">What <c>set Category=...</c> takes (case doesn't matter), and what <c>get</c> shows.</param>
/// <param name="Description">The name editors see in edit mode (<c>set</c> takes it too).</param>
/// <param name="Parent">The parent category's id; null at the top.</param>
/// <param name="Path">The names from the top down to it, joined with <c>/</c>.</param>
/// <param name="Selectable">Edit mode offers it; <c>set</c> only takes selectable categories.</param>
/// <param name="Visible">Shown in edit mode's category tree (<c>Available</c>).</param>
/// <param name="Items">Content items (any language, version or property) that have it.</param>
public sealed record CategoryInfo(int Id, string Name, string? Description, int? Parent, string Path, int Depth, bool Selectable, bool Visible, int Items, Guid Guid);

/// <summary><c>categories</c>: the category tree (<c>tblCategory</c>), depth first in the order admin mode sorts it.</summary>
public sealed class CategoryReader(CmsDatabase db)
{
    // tblContentCategory holds the primary values of every branch (built-in category and Category properties alike).
    private const string Sql = """
        SELECT c.pkID, c.fkParentID, c.CategoryGUID, c.SortOrder, c.Available, c.Selectable, c.CategoryName, c.CategoryDescription,
               (SELECT COUNT(DISTINCT cc.fkContentID) FROM tblContentCategory cc WHERE cc.fkCategoryID = c.pkID) AS Items
        FROM tblCategory c
        """;

    public async Task<IReadOnlyList<CategoryInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync(Sql, r => (
                Id: r.GetInt32("pkID"),
                Parent: r.GetInt32OrNull("fkParentID"),
                Guid: r.GetGuid("CategoryGUID"),
                Sort: r.GetInt32OrNull("SortOrder") ?? 0,
                Available: r.GetBooleanOrNull("Available") ?? true,
                Selectable: r.GetBooleanOrNull("Selectable") ?? true,
                Name: r.GetStringOrNull("CategoryName") ?? "",
                Description: r.GetStringOrNull("CategoryDescription"),
                Items: r.GetInt32("Items")),
            cancellationToken);
        return Tree(rows.Select(r => new CategoryRow(r.Id, r.Parent, r.Guid, r.Sort, r.Available, r.Selectable, r.Name, r.Description, r.Items)).ToList());
    }

    /// <param name="Sort">Admin mode's order among siblings.</param>
    internal sealed record CategoryRow(int Id, int? Parent, Guid Guid, int Sort, bool Available, bool Selectable, string Name, string? Description, int Items);

    /// <summary>
    /// Depth first, siblings in their sort order. The CMS's own root category (the one without a parent) isn't listed:
    /// the categories editors see are its children, at the top (<c>parent</c> null).
    /// </summary>
    internal static IReadOnlyList<CategoryInfo> Tree(IReadOnlyList<CategoryRow> rows)
    {
        var children = rows.ToLookup(r => r.Parent);
        var result = new List<CategoryInfo>();
        var visited = new HashSet<int>();
        void Walk(int? parent, int? shownParent, string path, int depth)
        {
            foreach (var row in children[parent].OrderBy(r => r.Sort).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!visited.Add(row.Id))
                {
                    continue;
                }
                var own = path.Length == 0 ? row.Name : $"{path}/{row.Name}";
                result.Add(new CategoryInfo(row.Id, row.Name, row.Description, shownParent, own, depth, row.Selectable, row.Available, row.Items, row.Guid));
                Walk(row.Id, row.Id, own, depth + 1);
            }
        }
        foreach (var root in children[null])
        {
            visited.Add(root.Id);
            Walk(root.Id, null, "", 0);
        }
        return result;
    }
}
