using OptiCli.Core.Cms;
using OptiCli.Core.Content;

namespace OptiCli.Core.Tests.Content;

/// <summary>
/// A small hand-written CMS model: an article page with a hero block, a fact list (whose items have a list
/// of page references), a ContentArea and a Category property, a teaser block type with a custom option list and a date, two languages (en master, sv)
/// plus the invariant branch, one site and two categories.
/// </summary>
internal static class ModelFixture
{
    public const int ArticlePage = 10;
    public const int TeaserBlock = 20;
    public const int HeroBlock = 30;
    public const int FactBlock = 40;
    public const int ImageFile = 50;
    public const int StartPage = 60;

    // Property definition ids.
    public const int Heading = 101;
    public const int MainBody = 102;
    public const int MainArea = 103;
    public const int Hero = 104;
    public const int Facts = 105;
    public const int RelatedPage = 106;
    public const int Priority = 107;
    public const int ShowDate = 108;
    public const int Tags = 109;
    public const int Links = 110;
    public const int MoreLink = 111;
    public const int RelatedItems = 112;
    public const int Topics = 113;
    public const int TeaserText = 201;
    public const int TeaserOptions = 202;
    public const int TeaserStart = 203;
    public const int HeroHeading = 301;
    public const int HeroSubHeading = 302;
    public const int FactLabel = 401;
    public const int FactSources = 402;

    public const int English = 1;
    public const int Swedish = 2;
    public const int Invariant = 3;

    public static readonly LanguageBranch En = new(English, "en", "English", null, true);
    public static readonly LanguageBranch Sv = new(Swedish, "sv", "Svenska", "se", true);
    public static readonly LanguageBranch None = new(Invariant, "", null, null, false);

    public static CmsModel Create(IReadOnlyList<SiteInfo>? sites = null) => new(
        [
            Type(ArticlePage, "ArticlePage", "Page"),
            Type(TeaserBlock, "TeaserBlock", "Block"),
            Type(HeroBlock, "HeroBlock", "Block"),
            Type(FactBlock, "FactBlock", "Block"),
            Type(ImageFile, "ImageFile", "Image"),
            Type(StartPage, "StartPage", "Page"),
        ],
        [
            new(Heading, ArticlePage, "Heading", "String", PropertyBaseType.String, null, true, false),
            new(MainBody, ArticlePage, "MainBody", "XhtmlString", PropertyBaseType.LongString, null, true, false),
            new(MainArea, ArticlePage, "MainArea", "ContentArea", PropertyBaseType.LongString, null, false, false),
            new(Hero, ArticlePage, "Hero", "HeroBlock", PropertyBaseType.Block, HeroBlock, false, false),
            new(Facts, ArticlePage, "Facts", "FactBlock", PropertyBaseType.Block, FactBlock, false, true),
            new(RelatedPage, ArticlePage, "RelatedPage", "ContentReference", PropertyBaseType.ContentReference, null, false, false),
            new(Priority, ArticlePage, "Priority", "Number", PropertyBaseType.Number, null, false, false),
            new(ShowDate, ArticlePage, "ShowDate", "Boolean", PropertyBaseType.Boolean, null, false, false),
            new(Tags, ArticlePage, "Tags", "StringList", PropertyBaseType.Json, null, false, false),
            new(Links, ArticlePage, "Links", "LinkCollection", PropertyBaseType.LinkCollection, null, false, false),
            new(MoreLink, ArticlePage, "MoreLink", "LinkItem", PropertyBaseType.LongString, null, false, false),
            new(RelatedItems, ArticlePage, "RelatedItems", "ContentReferenceList", PropertyBaseType.Json, null, false, false),
            new(Topics, ArticlePage, "Topics", "Category", PropertyBaseType.Category, null, false, false),
            new(TeaserText, TeaserBlock, "Text", "XhtmlString", PropertyBaseType.LongString, null, true, false),
            new(TeaserOptions, TeaserBlock, "Options", "OptionList", PropertyBaseType.LongString, null, false, false),
            new(TeaserStart, TeaserBlock, "StartDate", "Date", PropertyBaseType.Date, null, false, false),
            new(HeroHeading, HeroBlock, "Heading", "String", PropertyBaseType.String, null, true, false),
            new(HeroSubHeading, HeroBlock, "SubHeading", "String", PropertyBaseType.String, null, false, false),
            new(FactLabel, FactBlock, "Label", "String", PropertyBaseType.String, null, false, false),
            new(FactSources, FactBlock, "Sources", "PageReference", PropertyBaseType.PageReference, null, false, true),
        ],
        [En, Sv, None],
        sites ?? [Site()],
        globalAssetsRoot: 3,
        contentAssetsRoot: 4,
        categories: new Dictionary<int, string> { [2] = "News", [3] = "Events" });

    /// <summary>A site whose start page is 5, assets root 6, with hosts for en (primary) and sv.</summary>
    public static SiteInfo Site(params HostInfo[] hosts) => new(
        1,
        Guid.Parse("00000000-0000-0000-0000-00000000000a"),
        "Example",
        "https://www.example.com/",
        "5",
        "Home",
        "en",
        "6",
        hosts.Length > 0
            ? hosts
            : [new HostInfo("www.example.com", HostType.Primary, "en", true), new HostInfo("www.example.se", HostType.Undefined, "sv", true)]);

    private static ContentTypeInfo Type(int id, string name, string typeBase) =>
        new(id, Guid.NewGuid(), name, null, null, ContentKinds.From(typeBase == "Page" ? 0 : 1, typeBase), typeBase, null, 0);
}
