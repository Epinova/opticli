using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// Where catalog content may be made, which neither the CMS nor Commerce checks before saving: a catalog type below CMS
/// content saved a catalog type's row in the CMS's own tables, and a CMS type below catalog content failed deep in
/// Commerce's save. A catalog goes below the catalog root, a node or an entry below a catalog or a node, and CMS content
/// never below catalog content.
/// </summary>
public static class CatalogPlacement
{
    /// <summary>What is wrong with making a type of <paramref name="kind"/> below a parent of <paramref name="parentKind"/>; null when nothing is.</summary>
    /// <param name="type">The new content's type, for the message.</param>
    /// <param name="kind">The type's catalog kind (<see cref="CatalogKinds"/>); null for a CMS type.</param>
    /// <param name="parentKind">The parent's catalog kind; null for CMS content.</param>
    /// <param name="parent">The parent's ref, for the message.</param>
    public static string? Problem(string type, string? kind, string? parentKind, string parent) => (kind, parentKind) switch
    {
        (null, null) => null,
        (null, _) => $"{type} isn't a catalog type, and {parent} is catalog content ({parentKind}): CMS content can't go below catalog content.",
        (CatalogKinds.Catalog, CatalogKinds.Root) => null,
        (CatalogKinds.Catalog, _) => $"{type} is a catalog: it goes below the catalog root (catalog-root), not below {Below(parent, parentKind)}.",
        (_, CatalogKinds.Catalog or CatalogKinds.Node) => null,
        _ => $"{type} is a catalog {kind}: it goes below a catalog or a node (catalog:<name>, code:<node>), not below {Below(parent, parentKind)}.",
    };

    /// <exception cref="UsageException">The placement is wrong; nothing was saved.</exception>
    public static void Check(string type, string? kind, string? parentKind, string parent)
    {
        if (Problem(type, kind, parentKind, parent) is { } problem)
        {
            throw new UsageException(problem, "Catalogs go below catalog-root, nodes and entries below a catalog or node, CMS content below CMS content. Nothing was saved.");
        }
    }

    private static string Below(string parent, string? parentKind) =>
        parentKind is null ? $"CMS content ({parent})" : $"{parent} (a {parentKind})";
}
