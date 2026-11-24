using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

/// <summary>
/// Catalog content below catalog content, CMS content below CMS content: a variant below a CMS page saved a catalog row in
/// the CMS's tables, and a start page below a node failed inside Commerce's save, both after a dry run that said valid.
/// </summary>
public class CatalogPlacementTests
{
    [Theory]
    [InlineData(CatalogKinds.Catalog, CatalogKinds.Root)]
    [InlineData(CatalogKinds.Node, CatalogKinds.Catalog)]
    [InlineData(CatalogKinds.Node, CatalogKinds.Node)]
    [InlineData(CatalogKinds.Product, CatalogKinds.Node)]
    [InlineData(CatalogKinds.Variation, CatalogKinds.Catalog)]
    [InlineData(CatalogKinds.Bundle, CatalogKinds.Node)]
    [InlineData(null, null)]
    public void Each_kind_goes_where_Commerce_puts_it(string? kind, string? parentKind) =>
        Assert.Null(CatalogPlacement.Problem("SomeType", kind, parentKind, "parent"));

    [Theory]
    [InlineData(CatalogKinds.Variation, null, "not below CMS content (9)")]
    [InlineData(CatalogKinds.Catalog, null, "goes below the catalog root")]
    [InlineData(CatalogKinds.Catalog, CatalogKinds.Catalog, "goes below the catalog root")]
    [InlineData(CatalogKinds.Node, CatalogKinds.Root, "below a catalog or a node")]
    [InlineData(CatalogKinds.Variation, CatalogKinds.Product, "below a catalog or a node")]
    [InlineData(null, CatalogKinds.Node, "CMS content can't go below catalog content")]
    [InlineData(null, CatalogKinds.Root, "CMS content can't go below catalog content")]
    public void Anything_else_is_a_usage_error_before_anything_is_saved(string? kind, string? parentKind, string message)
    {
        var parent = parentKind is null ? "9" : "code:women";

        var ex = Assert.Throws<UsageException>(() => CatalogPlacement.Check("SomeType", kind, parentKind, parent));

        Assert.Contains(message, ex.Message);
        Assert.Contains("Nothing was saved", ex.Hint);
    }
}
