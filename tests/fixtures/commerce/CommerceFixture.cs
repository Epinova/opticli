// Copied into the Commerce test sites (CMS 12 + Commerce 14, CMS 13 + Commerce 15) by setup.sh: a small content model
// of the site's own, as a real Commerce site has one. On the CMS side a start page that points at a catalog and an image
// media type; on the catalog side a fashion-like node, product, variant and bundle with a few properties each, and a
// media reference on the product. No content: opticli makes it (the tests, the guide's plan).
using System.ComponentModel.DataAnnotations;
using EPiServer.Commerce.Catalog.ContentTypes;
using EPiServer.Commerce.Catalog.DataAnnotations;
using EPiServer.Framework.DataAnnotations;
using EPiServer.Web;

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
}

[ContentType(GUID = "0c2b6f1e-6a51-4c1a-9a0e-3f1d2c7b5a02", DisplayName = "Image file")]
[MediaDescriptor(ExtensionString = "jpg,jpeg,jpe,gif,png,webp")]
public class ImageFile : ImageData
{
    [Display(Name = "Alternative text", Order = 10)]
    [CultureSpecific]
    public virtual string? AltText { get; set; }
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
