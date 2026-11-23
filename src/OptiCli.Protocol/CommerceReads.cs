namespace OptiCli.Protocol;

/// <summary>Response of <c>GET</c> <see cref="AgentRoutes.CommercePrices"/>: every price row of an entry.</summary>
/// <param name="Entry">The entry's ref.</param>
/// <param name="Code">Its code, which Commerce keys prices by.</param>
public sealed record PriceList(string Entry, string Code, IReadOnlyList<CommercePrice> Prices);

/// <summary>Response of <c>GET</c> <see cref="AgentRoutes.CommerceInventory"/>: an entry's stock, per warehouse.</summary>
public sealed record InventoryList(string Entry, string Code, IReadOnlyList<CommerceStock> Stock);

/// <summary>Response of <c>GET</c> <see cref="AgentRoutes.CommerceWarehouses"/>.</summary>
public sealed record WarehouseList(IReadOnlyList<CommerceWarehouse> Warehouses);

/// <summary>Response of <c>GET</c> <see cref="AgentRoutes.CommerceMarkets"/>.</summary>
public sealed record MarketList(IReadOnlyList<CommerceMarket> Markets);

/// <summary>Catalog content at the other end of a relation.</summary>
/// <param name="Kind">One of <see cref="RelationKinds"/>.</param>
/// <param name="Quantity">For bundle and package entries.</param>
public sealed record RelatedContent(string Ref, string? Code, string? Name, [property: OutputValues(typeof(RelationKinds))] string Kind, decimal? Quantity, int SortOrder);

/// <summary>Response of <c>GET</c> <see cref="AgentRoutes.CommerceRelations"/>.</summary>
/// <param name="Children">A product's variants, a bundle's or package's entries, a node's extra entries.</param>
/// <param name="Parents">The products, bundles and packages it is in, and the nodes it is an extra entry of.</param>
public sealed record RelationList(string Ref, IReadOnlyList<RelatedContent> Children, IReadOnlyList<RelatedContent> Parents);

/// <summary>Response of <see cref="AgentRoutes.CommerceChildren"/>: the catalog content below catalog content, in catalog order.</summary>
public sealed record CatalogChildren(CatalogRef Parent, IReadOnlyList<CatalogRef> Children, bool More);
