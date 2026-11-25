using EPiServer;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using Mediachase.Commerce.Catalog;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce.Catalog;

/// <summary>
/// Finds catalog content by the refs the CLI takes for it (<see cref="CatalogRefSyntax"/>): by code through Commerce's
/// <see cref="ReferenceConverter"/>, a catalog by its name, and GUIDs and <c>id__CatalogContent</c> refs through the CMS.
/// </summary>
internal sealed class CatalogContentFinder(IServiceProvider services)
{
    private readonly ReferenceConverter _references = services.GetRequiredService<ReferenceConverter>();

    private readonly IContentLoader _loader = services.GetRequiredService<IContentLoader>();

    private readonly IContentTypeRepository _types = services.GetRequiredService<IContentTypeRepository>();

    public ContentReference RootLink => _references.GetRootLink();

    /// <summary>The catalog content a ref names; null when it names none (also for CMS content's GUIDs and ids).</summary>
    public CatalogContentBase? Find(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }
        ContentReference? link = null;
        if (CatalogRefSyntax.IsRoot(reference))
        {
            link = RootLink;
        }
        else if (CatalogRefSyntax.Code(reference) is { } code)
        {
            link = _references.GetContentLink(code);
        }
        else if (CatalogRefSyntax.CatalogName(reference) is { } name)
        {
            link = Catalogs().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.ContentLink;
        }
        else if (CatalogRefSyntax.ProviderId(reference) is { } id)
        {
            link = Canonical(id) ? new ContentReference(id, 0, CatalogRefSyntax.Provider) : null;
        }
        else if (Guid.TryParse(reference.Trim(), out var guid) && guid != Guid.Empty)
        {
            link = _loader.TryGet<IContent>(guid, out var byGuid) ? byGuid.ContentLink : null;
        }
        if (link is null || ContentReference.IsNullOrEmpty(link) || !string.Equals(link.ProviderName, CatalogRefSyntax.Provider, StringComparison.Ordinal))
        {
            return null;
        }
        return _loader.TryGet<CatalogContentBase>(link.ToReferenceWithoutVersion(), AnyLanguage(), out var content) ? content : null;
    }

    /// <summary>
    /// Whether an id is one Commerce gives content: it decodes any id by its top two bits, so an id that names nothing still
    /// loads something (-5 is the catalog root, and keeps -5 as its id). Only an id that encodes back to itself is real.
    /// </summary>
    private bool Canonical(int id)
    {
        var link = new ContentReference(id, 0, CatalogRefSyntax.Provider);
        try
        {
            var type = _references.GetContentType(link);
            var encoded = type == CatalogContentType.Root ? RootLink : _references.GetContentLink(_references.GetObjectId(link), type, 0);
            return encoded is not null && encoded.ID == id;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The catalog content a ref names.</summary>
    /// <exception cref="AgentException"><c>not_found</c>, saying which forms a catalog ref takes.</exception>
    public CatalogContentBase Require(string? reference, string what = "ref") =>
        Find(reference) ?? throw AgentException.NotFound(
            $"No catalog content '{reference?.Trim()}' ({what}).",
            $"A catalog ref is {CatalogRefSyntax.Description}; `opticli commerce children` lists the catalogs, `opticli commerce tree` what is below them.");

    /// <summary>The catalogs below the catalog root.</summary>
    public IReadOnlyList<CatalogContent> Catalogs() => _loader.GetChildren<CatalogContent>(RootLink, AnyLanguage()).ToList();

    /// <summary>The content as <see cref="CatalogRef"/> describes it.</summary>
    public CatalogRef Describe(string input, CatalogContentBase content)
    {
        var localizable = content as ILocalizable;
        return new CatalogRef(
            input,
            true,
            CatalogRefSyntax.Format(content.ContentLink.ID),
            content.ContentGuid,
            content.Name,
            _types.Load(content.ContentTypeID)?.Name,
            KindOf(content),
            CodeOf(content),
            ContentReference.IsNullOrEmpty(content.ParentLink) ? null : CatalogRefSyntax.Format(content.ParentLink.ID),
            localizable?.MasterLanguage?.Name is { Length: > 0 } master ? master : null,
            localizable?.ExistingLanguages?.Select(l => l.Name).Where(l => l.Length > 0).ToList(),
            content.ContentLink.WorkID > 0 ? content.ContentLink.WorkID : null,
            CatalogLanguagesOf(content));
    }

    /// <summary>
    /// The languages of the catalog the content is in (a catalog's own): its default language and its CatalogLanguages,
    /// the languages Commerce keeps catalog content in. Null for the catalog root.
    /// </summary>
    private IReadOnlyList<string>? CatalogLanguagesOf(CatalogContentBase content)
    {
        var catalog = content as CatalogContent;
        if (catalog is null && content is not RootContent && content.CatalogId > 0
            && _loader.TryGet<CatalogContent>(_references.GetContentLink(content.CatalogId, CatalogContentType.Catalog, 0), AnyLanguage(), out var loaded))
        {
            catalog = loaded;
        }
        return catalog is null
            ? null
            : new[] { catalog.DefaultLanguage }.Concat(catalog.CatalogLanguages ?? Enumerable.Empty<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A node's or entry's code; null for catalogs and the root.</summary>
    public static string? CodeOf(IContent content) => content switch
    {
        EntryContentBase entry => entry.Code,
        NodeContent node => node.Code,
        _ => null,
    };

    /// <summary>
    /// One of <see cref="CatalogKinds"/>, from the content's class or the Commerce class it derives from. By name, since
    /// <c>DynamicPackageContent</c> came after Commerce 14.0, which this agent compiles against.
    /// </summary>
    public static string? KindOf(IContent content)
    {
        for (var type = content.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Namespace == "EPiServer.Commerce.Catalog.ContentTypes" && KindByClass(type.Name) is { } kind)
            {
                return kind;
            }
        }
        return null;
    }

    private static string? KindByClass(string name) => name switch
    {
        "RootContent" => CatalogKinds.Root,
        "CatalogContent" => CatalogKinds.Catalog,
        "NodeContent" => CatalogKinds.Node,
        "ProductContent" => CatalogKinds.Product,
        "VariationContent" => CatalogKinds.Variation,
        "BundleContent" => CatalogKinds.Bundle,
        "DynamicPackageContent" => CatalogKinds.DynamicPackage,
        "PackageContent" => CatalogKinds.Package,
        _ => null,
    };

    public static LoaderOptions AnyLanguage() => new() { LanguageLoaderOption.FallbackWithMaster() };
}
