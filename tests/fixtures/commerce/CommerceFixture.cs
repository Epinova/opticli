// Copied into the Commerce test sites (CMS 12 + Commerce 14, CMS 13 + Commerce 15) by setup.sh: a small content model
// of the site's own, as a real Commerce site has one. On the CMS side a start page that points at a catalog and at
// content (a ContentArea and a reference list, which may hold catalog content), a teaser block and an image media
// type; on the catalog side a fashion-like node, product, variant and bundle with a few properties each, and a media
// reference on the product. Pages for the start page, products and variants (plain HTML, no views; a product's and variant's show the
// images of its CommerceMediaCollection), and Commerce's hierarchical catalog routing below the start page, so catalog
// content has URLs that answer. The content comes from setup.sh's fixture.plan.json (applied with opticli) and the tests.
// POST /opticli-fixture/hand-over (loopback only) saves a version of catalog content again as another user, for the tests
// of someone else's draft (opticli's agent only saves as itself).
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Principal;
using System.Text;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Commerce.Catalog.DataAnnotations;
using EPiServer.Commerce.Routing;
using EPiServer.Data.Entity;
using EPiServer.DataAccess;
using EPiServer.Framework;
using EPiServer.Framework.DataAnnotations;
using EPiServer.Framework.Initialization;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using EPiServer.Web;
using EPiServer.Web.Mvc;
using EPiServer.Web.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace OptiCliCommerce;

[ContentType(GUID = "0c2b6f1e-6a51-4c1a-9a0e-3f1d2c7b5a01", DisplayName = "Start page")]
public class StartPage : PageData
{
    [Display(Name = "Heading", Order = 10)]
    public virtual string? Heading { get; set; }

    [Display(Name = "Main body", Order = 20)]
    [CultureSpecific]
    public virtual XhtmlString? MainBody { get; set; }

    /// <summary>The catalog the site sells from, as a site points at its catalog in its own code.</summary>
    [Display(Name = "Catalog", Order = 30)]
    [AllowedTypes(typeof(CatalogContent))]
    public virtual ContentReference? Catalog { get; set; }

    /// <summary>A product the start page shows.</summary>
    [Display(Name = "Featured product", Order = 40)]
    public virtual ContentReference? FeaturedProduct { get; set; }

    /// <summary>Content of either kind: CMS content and catalog content.</summary>
    [Display(Name = "Main content area", Order = 50)]
    public virtual ContentArea? MainContentArea { get; set; }

    /// <summary>A list of references, of either kind.</summary>
    [Display(Name = "Related content", Order = 60)]
    public virtual IList<ContentReference>? RelatedContent { get; set; }
}

[ContentType(GUID = "0c2b6f1e-6a51-4c1a-9a0e-3f1d2c7b5a02", DisplayName = "Image file")]
[MediaDescriptor(ExtensionString = "jpg,jpeg,jpe,gif,png,webp")]
public class ImageFile : ImageData
{
    [Display(Name = "Alternative text", Order = 10)]
    [CultureSpecific]
    public virtual string? AltText { get; set; }
}

[ContentType(GUID = "0c2b6f1e-6a51-4c1a-9a0e-3f1d2c7b5a03", DisplayName = "Teaser block")]
public class TeaserBlock : BlockData
{
    [Display(Name = "Heading", Order = 10)]
    [CultureSpecific]
    public virtual string? Heading { get; set; }
}

[CatalogContentType(GUID = "0c2b6f1e-6a51-4c1a-9a0e-3f1d2c7b5a10", MetaClassName = "FashionNode", DisplayName = "Fashion node")]
public class FashionNode : NodeContent
{
    [Display(Name = "Teaser", Order = 10)]
    [CultureSpecific]
    public virtual string? Teaser { get; set; }
}

[CatalogContentType(GUID = "0c2b6f1e-6a51-4c1a-9a0e-3f1d2c7b5a11", MetaClassName = "FashionProduct", DisplayName = "Fashion product")]
public class FashionProduct : ProductContent
{
    [Display(Name = "Brand", Order = 10)]
    public virtual string? Brand { get; set; }

    [Display(Name = "Description", Order = 20)]
    [CultureSpecific]
    public virtual XhtmlString? Description { get; set; }

    [Display(Name = "Main image", Order = 30)]
    [UIHint(UIHint.Image)]
    public virtual ContentReference? MainImage { get; set; }
}

[CatalogContentType(GUID = "0c2b6f1e-6a51-4c1a-9a0e-3f1d2c7b5a12", MetaClassName = "FashionVariant", DisplayName = "Fashion variant")]
public class FashionVariant : VariationContent
{
    [Display(Name = "Size", Order = 10)]
    public virtual string? Size { get; set; }

    [Display(Name = "Color", Order = 20)]
    public virtual string? Color { get; set; }
}

[CatalogContentType(GUID = "0c2b6f1e-6a51-4c1a-9a0e-3f1d2c7b5a13", MetaClassName = "FashionBundle", DisplayName = "Fashion bundle")]
public class FashionBundle : BundleContent
{
    [Display(Name = "Teaser", Order = 10)]
    [CultureSpecific]
    public virtual string? Teaser { get; set; }
}

/// <summary>Commerce's hierarchical catalog routing below the start page: the standard registration, on both majors.</summary>
[InitializableModule]
[ModuleDependency(typeof(EPiServer.Commerce.Initialization.InitializationModule))]
public class CatalogRoutingFixture : IInitializableModule
{
    public void Initialize(InitializationEngine context) => CatalogRouteHelper.MapDefaultHierarchialRouter(false);

    public void Uninitialize(InitializationEngine context)
    {
    }
}

/// <summary>
/// <c>POST /opticli-fixture/hand-over?version=&lt;id_version_CatalogContent&gt;&amp;user=&lt;name&gt;</c> from the machine
/// itself: saves that version of catalog content again, unchanged and as the same version, as <c>user</c>, so it becomes
/// that user's draft the way an editor's save makes it (Commerce records the user and clears its cached version list).
/// The tests of someone else's draft use it, as the edge-case site's fixture of the same name; opticli's agent only saves
/// as itself.
/// </summary>
[InitializableModule]
public class HandOverFixture : IConfigurableModule
{
    public const string Path = "/opticli-fixture/hand-over";

    public void ConfigureContainer(ServiceConfigurationContext context) =>
        context.Services.AddTransient<IStartupFilter, Endpoint>();

    public void Initialize(InitializationEngine context)
    {
    }

    public void Uninitialize(InitializationEngine context)
    {
    }

    private sealed class Endpoint : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (!context.Request.Path.Equals(Path, StringComparison.OrdinalIgnoreCase))
                {
                    await nextMiddleware();
                    return;
                }
                if (!HttpMethods.IsPost(context.Request.Method) || context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)
                    || !ContentReference.TryParse(context.Request.Query["version"], out var version) || version.WorkID <= 0
                    || context.Request.Query["user"].ToString() is not { Length: > 0 } user)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                var repository = context.RequestServices.GetRequiredService<IContentRepository>();
                var accessor = context.RequestServices.GetRequiredService<IPrincipalAccessor>();
                var principal = accessor.Principal;
                accessor.Principal = new GenericPrincipal(new GenericIdentity(user), ["WebEditors"]);
                try
                {
                    var content = (IContent)((IReadOnly)repository.Get<IContent>(version)).CreateWritableClone();
                    ((IChangeTrackable)content).ChangedBy = user;
                    repository.Save(content, SaveAction.CheckOut | SaveAction.ForceCurrentVersion, AccessLevel.NoAccess);
                }
                finally
                {
                    accessor.Principal = principal;
                }
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            });
            next(app);
        };
    }
}

/// <summary>Pages as plain HTML: enough for a test to see that a URL answers, and what it shows.</summary>
internal static class FixtureHtml
{
    public static ContentResult Page(string title, string body) => new()
    {
        ContentType = "text/html; charset=utf-8",
        Content = $"<!doctype html><html><head><title>{WebUtility.HtmlEncode(title)}</title></head><body><h1>{WebUtility.HtmlEncode(title)}</h1>{body}</body></html>",
    };

    /// <summary>The item's images, from its CommerceMediaCollection (the Assets tab), as a product listing shows them.</summary>
    public static string Images(IAssetContainer item, IUrlResolver urls)
    {
        var html = new StringBuilder();
        foreach (var media in (item.CommerceMediaCollection ?? []).OrderBy(m => m.SortOrder))
        {
            html.Append($"<img class=\"asset\" data-group=\"{WebUtility.HtmlEncode(media.GroupName)}\" src=\"{WebUtility.HtmlEncode(urls.GetUrl(media.AssetLink))}\" alt=\"\">");
        }
        return html.ToString();
    }

    public static string Link(IUrlResolver urls, ContentReference? link, string css) =>
        ContentReference.IsNullOrEmpty(link) ? "" : $"<p class=\"{css}\"><a href=\"{WebUtility.HtmlEncode(urls.GetUrl(link))}\">{css}</a></p>";
}

public class StartPageController(IUrlResolver urls) : PageController<StartPage>
{
    public IActionResult Index(StartPage currentPage) => FixtureHtml.Page(currentPage.Heading ?? currentPage.Name,
        FixtureHtml.Link(urls, currentPage.Catalog, "catalog") + FixtureHtml.Link(urls, currentPage.FeaturedProduct, "featured"));
}

public class FashionProductController(IUrlResolver urls) : ContentController<FashionProduct>
{
    public IActionResult Index(FashionProduct currentContent) => FixtureHtml.Page(currentContent.DisplayName ?? currentContent.Name,
        $"<p class=\"code\">{WebUtility.HtmlEncode(currentContent.Code)}</p><p class=\"brand\">{WebUtility.HtmlEncode(currentContent.Brand)}</p>{FixtureHtml.Images(currentContent, urls)}");
}

public class FashionVariantController(IUrlResolver urls) : ContentController<FashionVariant>
{
    public IActionResult Index(FashionVariant currentContent) => FixtureHtml.Page(currentContent.DisplayName ?? currentContent.Name,
        $"<p class=\"code\">{WebUtility.HtmlEncode(currentContent.Code)}</p><p class=\"size\">{WebUtility.HtmlEncode(currentContent.Size)}</p>{FixtureHtml.Images(currentContent, urls)}");
}
