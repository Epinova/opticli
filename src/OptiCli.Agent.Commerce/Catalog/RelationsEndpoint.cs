using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Commerce.Catalog.Linking;
using EPiServer.Core;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Catalog;

/// <summary>
/// <see cref="AgentRoutes.CommerceRelations"/>: a product's variants, a bundle's or package's entries, and entries' extra
/// categories, through Commerce's <see cref="IRelationRepository"/>. Which relation it is follows from the two kinds.
/// </summary>
internal static class RelationsEndpoint
{
    public static RelationResult Write(AgentRequest request, RelationRequest body)
    {
        var finder = new CatalogContentFinder(request.Context.RequestServices);
        var parent = finder.Require(body.Parent, "parent");
        var child = finder.Require(body.Child, "child");
        var kind = KindOf(CatalogContentFinder.KindOf(parent), CatalogContentFinder.KindOf(child))
            ?? throw AgentException.Usage(
                $"{Describe(parent)} and {Describe(child)} can't be related: a product takes variants, a bundle or package takes entries (with a quantity), and a node takes entries as an extra category.",
                "An entry's own node is its parent already; to move it, use Commerce's catalog UI.");
        if (body.Quantity is not null && kind is not (RelationKinds.BundleEntry or RelationKinds.PackageEntry))
        {
            throw AgentException.Usage($"quantity is for a bundle's or package's entries, not a {kind}.");
        }
        if (body.Quantity is <= 0)
        {
            throw AgentException.Usage("quantity must be more than 0.");
        }
        if (kind == RelationKinds.Category && parent.ContentLink.CompareToIgnoreWorkID(child.ParentLink))
        {
            throw AgentException.Usage($"{Describe(parent)} is already {Describe(child)}'s own node (its parent): there is no extra category to add or remove.");
        }

        var relations = request.Context.RequestServices.GetRequiredService<IRelationRepository>();
        var existing = relations.GetChildren<Relation>(parent.ContentLink.ToReferenceWithoutVersion())
            .FirstOrDefault(r => r.Child.CompareToIgnoreWorkID(child.ContentLink) && KindOf(r) == kind);
        var parentRef = CatalogRefSyntax.Format(parent.ContentLink.ID);
        var childRef = CatalogRefSyntax.Format(child.ContentLink.ID);

        if (body.Remove)
        {
            if (existing is null)
            {
                return new RelationResult(parentRef, childRef, kind, CommerceWriteStatus.Absent, null, 0, body.DryRun);
            }
            if (!body.DryRun)
            {
                relations.RemoveRelations([existing]);
            }
            return new RelationResult(parentRef, childRef, kind, CommerceWriteStatus.Removed, QuantityOf(existing), existing.SortOrder, body.DryRun);
        }

        var quantity = kind is RelationKinds.BundleEntry or RelationKinds.PackageEntry ? body.Quantity ?? QuantityOf(existing) ?? 1m : (decimal?)null;
        var sortOrder = body.SortOrder ?? existing?.SortOrder ?? 0;
        var status = existing is null ? CommerceWriteStatus.Created
            : existing.SortOrder == sortOrder && QuantityOf(existing) == quantity ? CommerceWriteStatus.Unchanged
            : CommerceWriteStatus.Updated;
        if (!body.DryRun && status != CommerceWriteStatus.Unchanged)
        {
            relations.UpdateRelations([New(kind, parent.ContentLink.ToReferenceWithoutVersion(), child.ContentLink.ToReferenceWithoutVersion(), sortOrder, quantity, (existing as EntryRelation)?.GroupName)]);
        }
        return new RelationResult(parentRef, childRef, kind, status, quantity, sortOrder, body.DryRun);
    }

    public static RelationList List(AgentRequest request)
    {
        var finder = new CatalogContentFinder(request.Context.RequestServices);
        var content = finder.Require(request.Context.Request.Query["ref"].ToString());
        var relations = request.Context.RequestServices.GetRequiredService<IRelationRepository>();
        var link = content.ContentLink.ToReferenceWithoutVersion();
        RelatedContent Related(ContentReference other, Relation relation)
        {
            var found = finder.Find(CatalogRefSyntax.Format(other.ID));
            return new RelatedContent(CatalogRefSyntax.Format(other.ID), found is null ? null : CatalogContentFinder.CodeOf(found), found?.Name, KindOf(relation) ?? "unknown", QuantityOf(relation), relation.SortOrder);
        }
        // An entry's own node is its parent, not an extra category (Commerce keeps it as the primary node relation).
        var children = relations.GetChildren<Relation>(link).Where(r => KindOf(r) is not null && r is not NodeEntryRelation { IsPrimary: true }).Select(r => Related(r.Child, r)).ToList();
        var parents = relations.GetParents<Relation>(link)
            .Where(r => KindOf(r) is not null && r is not NodeEntryRelation { IsPrimary: true } && !(r is NodeEntryRelation && r.Parent.CompareToIgnoreWorkID(content.ParentLink)))
            .Select(r => Related(r.Parent, r)).ToList();
        return new RelationList(CatalogRefSyntax.Format(link.ID), children, parents);
    }

    /// <summary>The relation two kinds of catalog content can have; null when none.</summary>
    internal static string? KindOf(string? parent, string? child) => (parent, child) switch
    {
        (CatalogKinds.Product, CatalogKinds.Variation) => RelationKinds.Variant,
        (CatalogKinds.Bundle, _) when CatalogKinds.IsEntry(child) => RelationKinds.BundleEntry,
        (CatalogKinds.Package or CatalogKinds.DynamicPackage, _) when CatalogKinds.IsEntry(child) => RelationKinds.PackageEntry,
        (CatalogKinds.Node, _) when CatalogKinds.IsEntry(child) => RelationKinds.Category,
        _ => null,
    };

    private static string? KindOf(Relation relation) => relation switch
    {
        ProductVariation => RelationKinds.Variant,
        BundleEntry => RelationKinds.BundleEntry,
        PackageEntry => RelationKinds.PackageEntry,
        NodeEntryRelation => RelationKinds.Category,
        _ => null,
    };

    private static decimal? QuantityOf(Relation? relation) => relation is BundleEntry or PackageEntry ? Pricing.Decimals.Plain(((EntryRelation)relation).Quantity ?? 1m) : null;

    private static Relation New(string kind, ContentReference parent, ContentReference child, int sortOrder, decimal? quantity, string? group) => kind switch
    {
        RelationKinds.Variant => new ProductVariation { Parent = parent, Child = child, SortOrder = sortOrder, GroupName = group },
        RelationKinds.BundleEntry => new BundleEntry { Parent = parent, Child = child, SortOrder = sortOrder, Quantity = quantity, GroupName = group },
        RelationKinds.PackageEntry => new PackageEntry { Parent = parent, Child = child, SortOrder = sortOrder, Quantity = quantity, GroupName = group },
        _ => new NodeEntryRelation { Parent = parent, Child = child, SortOrder = sortOrder },
    };

    private static string Describe(CatalogContentBase content) =>
        $"'{content.Name}' ({CatalogContentFinder.KindOf(content) ?? "catalog content"}, {CatalogRefSyntax.Format(content.ContentLink.ID)})";
}
