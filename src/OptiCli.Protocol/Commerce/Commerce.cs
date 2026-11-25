namespace OptiCli.Protocol;

/// <summary>Response of <see cref="AgentRoutes.Commerce"/>: the site's Commerce as the Commerce agent sees it.</summary>
/// <param name="Version">The site's EPiServer.Commerce.Core version.</param>
/// <param name="CatalogRoot">The catalog root, which catalogs are made below: <c>-1073741823__CatalogContent</c>.</param>
/// <param name="CatalogRootGuid">The catalog root's GUID.</param>
/// <param name="Catalogs">The catalogs, by name.</param>
public sealed record CommerceInfo(string Version, string CatalogRoot, Guid CatalogRootGuid, IReadOnlyList<CatalogSummary> Catalogs);

/// <param name="Ref">The catalog's ref (<c>-2147483646__CatalogContent</c>): its id is local to the database.</param>
public sealed record CatalogSummary(string Ref, Guid Guid, string Name);

/// <summary>Body of <see cref="AgentRoutes.CommerceRefs"/>: refs to resolve to catalog content.</summary>
/// <param name="Refs">
/// <c>code:&lt;code&gt;</c> (a node or entry by its code), <c>catalog:&lt;name&gt;</c> (a catalog by its name),
/// <c>catalog-root</c>, a GUID, or <c>id__CatalogContent</c> (negative for catalogs and the catalog root).
/// </param>
public sealed record CatalogRefsRequest(IReadOnlyList<string> Refs);

/// <summary>Response of <see cref="AgentRoutes.CommerceRefs"/>: one item per ref asked for, in the same order.</summary>
public sealed record CatalogRefsResponse(IReadOnlyList<CatalogRef> Refs);

/// <summary>A ref resolved to catalog content, or not found.</summary>
/// <param name="Input">The ref as it was asked for.</param>
/// <param name="Found">Whether it names catalog content (a GUID or ref of CMS content is not found here).</param>
/// <param name="Ref">Its ref, <c>id__CatalogContent</c>: what the CMS agent's content routes take.</param>
/// <param name="Type">Its content type's name.</param>
/// <param name="Kind">One of <see cref="CatalogKinds"/>.</param>
/// <param name="Code">A node's or entry's code; null for catalogs and the root.</param>
/// <param name="Parent">Its parent's ref; null for the root.</param>
/// <param name="MasterLanguage">Its master language; null for the root.</param>
/// <param name="Languages">The languages it exists in.</param>
/// <param name="Version">Its published version (or the latest, when it was never published).</param>
/// <param name="CatalogLanguages">
/// The languages of its catalog (a catalog's own): the default language and <c>CatalogLanguages</c>, the only ones Commerce
/// keeps catalog content in. Null for the catalog root.
/// </param>
public sealed record CatalogRef(
    string Input,
    bool Found,
    string? Ref = null,
    Guid? Guid = null,
    string? Name = null,
    string? Type = null,
    [property: OutputValues(typeof(CatalogKinds))] string? Kind = null,
    string? Code = null,
    string? Parent = null,
    string? MasterLanguage = null,
    IReadOnlyList<string>? Languages = null,
    int? Version = null,
    IReadOnlyList<string>? CatalogLanguages = null);

/// <summary>What catalog content is, as Commerce's content types say (and <c>tblContentType.Base</c> records).</summary>
[ValueSetName("CatalogKind")]
public static class CatalogKinds
{
    public const string Root = "root";
    public const string Catalog = "catalog";
    public const string Node = "node";
    public const string Product = "product";
    public const string Variation = "variation";
    public const string Bundle = "bundle";
    public const string Package = "package";
    public const string DynamicPackage = "dynamicPackage";

    /// <summary>The kind of a content type whose <c>tblContentType.Base</c> is <paramref name="typeBase"/>; null for anything that isn't catalog content.</summary>
    public static string? FromBase(string? typeBase) => typeBase switch
    {
        "Catalog" => Catalog,
        "Node" => Node,
        "Product" => Product,
        "Variation" => Variation,
        "Bundle" => Bundle,
        "Package" => Package,
        "DynamicPackage" => DynamicPackage,
        _ => null,
    };

    /// <summary>Kinds that are entries (have prices, stock and SKUs): everything below a node but the node itself.</summary>
    public static bool IsEntry(string? kind) => kind is Product or Variation or Bundle or Package or DynamicPackage;
}

/// <summary>The refs that name catalog content, as both the CLI and the Commerce agent parse them.</summary>
public static class CatalogRefSyntax
{
    /// <summary>The content provider Commerce serves catalog content from.</summary>
    public const string Provider = "CatalogContent";

    public const string CodePrefix = "code:";

    public const string CatalogPrefix = "catalog:";

    /// <summary>The catalog root, which catalogs are made below (<c>-1073741823__CatalogContent</c> in every database).</summary>
    public const string Root = "catalog-root";

    /// <summary>For messages: the forms a catalog ref takes.</summary>
    public const string Description = "code:<code> (a node or entry), catalog:<name> (a catalog), catalog-root, a GUID, or id__CatalogContent";

    /// <summary>The code in <c>code:&lt;code&gt;</c>; null for any other ref.</summary>
    public static string? Code(string? reference) => After(reference, CodePrefix);

    /// <summary>The name in <c>catalog:&lt;name&gt;</c>; null for any other ref.</summary>
    public static string? CatalogName(string? reference) => After(reference, CatalogPrefix);

    /// <summary>Whether the ref is <see cref="Root"/>.</summary>
    public static bool IsRoot(string? reference) => string.Equals(reference?.Trim(), Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the ref is a <c>code:</c>, <c>catalog:</c> or <c>catalog-root</c> ref, which only the Commerce agent resolves.</summary>
    public static bool IsCommerceOnly(string? reference) => Code(reference) is not null || CatalogName(reference) is not null || IsRoot(reference);

    /// <summary>The id of <c>id__CatalogContent</c> (negative for catalogs and the catalog root); null for any other ref.</summary>
    public static int? ProviderId(string? reference)
    {
        var value = reference?.Trim();
        var suffix = "__" + Provider;
        if (value is null || !value.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }
        return int.TryParse(value[..^suffix.Length], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var id) && id != 0
            ? id
            : null;
    }

    /// <summary><c>id__CatalogContent</c>.</summary>
    public static string Format(int id) => $"{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}__{Provider}";

    private static string? After(string? reference, string prefix)
    {
        var value = reference?.Trim();
        return value is not null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && value.Length > prefix.Length
            ? value[prefix.Length..].Trim()
            : null;
    }
}
