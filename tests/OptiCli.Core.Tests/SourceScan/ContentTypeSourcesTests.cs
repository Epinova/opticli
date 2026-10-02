using OptiCli.Core.SourceScan;

namespace OptiCli.Core.Tests.SourceScan;

public class ContentTypeSourcesTests : IDisposable
{
    private static readonly Guid ArticleGuid = Guid.Parse("7d0e8f4a-1b2c-4d3e-8f9a-0b1c2d3e4f5a");

    private readonly TempDirectory _root = new();

    public ContentTypeSourcesTests()
    {
        _root.Write("Web/Content/TabNames.cs", """
            namespace Example.Web.Content;

            public static class TabNames
            {
                public const string Teaser = nameof(Teaser);
                public const string Seo = "Search engines";
            }
            """);
        _root.Write("Web/Content/SitePageBase.cs", """
            namespace Example.Web.Content;

            public abstract partial class SitePageBase : PageData
            {
                [Display(Name = "Heading", GroupName = SystemTabNames.Content, Order = 10)]
                [CultureSpecific]
                public virtual string Heading { get; set; }

                [Display(GroupName = TabNames.Teaser, Order = 50)]
                public virtual string TeaserText { get; set; }
            }
            """);
        _root.Write("Web/Content/SitePageBase.Seo.cs", """
            namespace Example.Web.Content;

            public abstract partial class SitePageBase
            {
                [Display(
                    GroupName = TabNames.Seo,
                    Order = 100)]
                [Required]
                public virtual string MetaTitle { get; set; }
            }
            """);
        _root.Write("Web/Features/Article/ArticlePage.cs", """
            using System.Collections.Generic;

            namespace Example.Web.Features.Article;

            /// <summary>A comment that mentions class Foo; it must not become a class.</summary>
            [ContentType(DisplayName = "Article", GUID = "7d0e8f4a-1b2c-4d3e-8f9a-0b1c2d3e4f5a", GroupName = "Content")]
            [AvailableContentTypes(Include = new[] { typeof(ArticlePage) })]
            public class ArticlePage : SitePageBase, IHasTeaser
            {
                [Display(Order = 20, GroupName = "Content")]
                [CultureSpecific(false)]
                [AllowedTypes(typeof(TeaserBlock), typeof(HeroBlock))]
                public virtual ContentArea MainArea { get; set; }

                [AllowedTypes(AllowedTypes = new[]
                {
                    typeof(TeaserBlock),
                })]
                [Display(Order = 30)]
                [UIHint(AreaDescriptor.UiHint)]
                public virtual ContentArea SideArea { get; set; }

                public override string TeaserText
                {
                    get => GetPropertyValue(p => p.TeaserText) ?? Heading;
                    set => SetPropertyValue(p => p.TeaserText, value);
                }

                [Ignore]
                public virtual string Computed => "{" + Heading + "}";

                public class Nested
                {
                    public virtual string NotAProperty { get; set; }
                }
            }
            """);
        _root.Write("Web/Features/Other/ArticlePage.cs", """
            namespace Example.Web.Features.Other;

            public class ArticlePage { }
            """);
        _root.Write("Web/Features/Article/ArticlePage.cshtml", "@model ArticlePage\n<h1>@Model.Heading</h1>");
        _root.Write("Web/Features/Article/ArticleTeaser.cshtml", "@model TeaserViewModel<Example.Web.Features.Article.ArticlePage>\n");
        _root.Write("Web/Views/Shared/_ArticlePage.cshtml", "<p></p>");
        _root.Write("Web/Components/ArticlePage/Default.cshtml", "<p></p>");
        _root.Write("Web/Views/Shared/Unrelated.cshtml", "@model ArticlePageViewModel\n");
        _root.Write("Web/bin/Debug/ArticlePage.cshtml", "@model ArticlePage");
        _root.Write("Web/obj/ArticlePage.cs", "[ContentType(GUID = \"7d0e8f4a-1b2c-4d3e-8f9a-0b1c2d3e4f5a\")] public class ArticlePage { }");
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Indexes_classes_with_guid_bases_and_namespace()
    {
        var index = CSharpSourceIndex.Build(_root.Path);

        var article = Assert.Single(index.Classes, c => c.ContentTypeGuid == ArticleGuid);
        Assert.Equal("ArticlePage", article.Name);
        Assert.Equal("Example.Web.Features.Article", article.Namespace);
        Assert.Equal(["SitePageBase", "IHasTeaser"], article.BaseTypes);
        Assert.Equal(8, article.Line);
        Assert.DoesNotContain(index.Classes, c => c.Name == "Foo");
        Assert.Equal("Teaser", index.Constants["TabNames.Teaser"]);
        Assert.Equal("Search engines", index.Constants["TabNames.Seo"]);
    }

    [Fact]
    public void Indexes_body_less_declarations_with_their_bases_and_guid()
    {
        _root.Write("Web/Blocks/Blocks.cs", """
            namespace Example.Web.Blocks;

            public abstract class SiteBlock : BlockData;

            [ContentType(GUID = "3c1d2e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f")]
            public class AboutBlock : SiteBlock, IHasTeaser;

            public sealed class QuoteBlock(string author) : SiteBlock
            {
                public virtual string Text { get; set; }
            }
            """);
        var index = CSharpSourceIndex.Build(_root.Path);

        var about = Assert.Single(index.Classes, c => c.Name == "AboutBlock");
        Assert.Equal(["SiteBlock", "IHasTeaser"], about.BaseTypes);
        Assert.Equal(Guid.Parse("3c1d2e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f"), about.ContentTypeGuid);
        Assert.Equal(["BlockData"], Assert.Single(index.Classes, c => c.Name == "SiteBlock").BaseTypes);
        Assert.Empty(ContentTypeSources.FindProperties(index, about));

        var quote = Assert.Single(index.Classes, c => c.Name == "QuoteBlock");
        Assert.Null(quote.ContentTypeGuid);
        Assert.Equal(["Text"], ContentTypeSources.FindProperties(index, quote).Keys);
    }

    [Fact]
    public void Finds_the_class_by_guid_before_name()
    {
        var index = CSharpSourceIndex.Build(_root.Path);

        var match = Assert.Single(ContentTypeSources.FindClasses(index, ArticleGuid, "ArticlePage", null));

        Assert.Equal("guid", match.MatchedBy);
        Assert.EndsWith(Path.Combine("Article", "ArticlePage.cs"), match.Class.File, StringComparison.Ordinal);
    }

    [Fact]
    public void Falls_back_to_class_name_narrowed_by_namespace()
    {
        var index = CSharpSourceIndex.Build(_root.Path);

        var match = Assert.Single(ContentTypeSources.FindClasses(index, Guid.NewGuid(), "ArticlePage", "Example.Web.Features.Other"));

        Assert.Equal("className", match.MatchedBy);
        Assert.Equal("Example.Web.Features.Other", match.Class.Namespace);
    }

    [Fact]
    public void Reads_property_attributes_through_base_classes_and_partials()
    {
        var index = CSharpSourceIndex.Build(_root.Path);
        var article = index.Classes.Single(c => c.ContentTypeGuid == ArticleGuid);

        var properties = ContentTypeSources.FindProperties(index, article);

        Assert.Equal(["Heading", "MainArea", "MetaTitle", "SideArea", "TeaserText"], properties.Keys.Order());

        var heading = properties["Heading"];
        Assert.Equal("Information", heading.Tab);
        Assert.Equal(10, heading.Order);
        Assert.Equal("Heading", heading.DisplayName);
        Assert.True(heading.CultureSpecific);
        Assert.Equal("SitePageBase", heading.DeclaredIn);

        var mainArea = properties["MainArea"];
        Assert.Equal("Content", mainArea.Tab);
        Assert.False(mainArea.CultureSpecific);
        Assert.Equal("ArticlePage", mainArea.DeclaredIn);
        Assert.Equal(["TeaserBlock", "HeroBlock"], mainArea.AllowedTypes!.Allowed);
        Assert.Null(heading.AllowedTypes);

        // The array initialiser's braces inside the attribute must not cut the attribute list short.
        var sideArea = properties["SideArea"];
        Assert.Equal(["TeaserBlock"], sideArea.AllowedTypes!.Allowed);
        Assert.Equal(30, sideArea.Order);
        Assert.Equal("AreaDescriptor.UiHint", sideArea.UiHint);

        var metaTitle = properties["MetaTitle"];
        Assert.Equal("Search engines", metaTitle.Tab);
        Assert.Equal(100, metaTitle.Order);
        Assert.True(metaTitle.Required);

        // The override has no attributes of its own, so it inherits those of the overridden property.
        var teaser = properties["TeaserText"];
        Assert.Equal("ArticlePage", teaser.DeclaredIn);
        Assert.Equal("Teaser", teaser.Tab);
        Assert.Equal(50, teaser.Order);
    }

    [Fact]
    public void Finds_views_by_name_partial_component_and_model()
    {
        var views = ContentTypeSources.FindViews(_root.Path, ["ArticlePage"]);

        var found = views.Select(v => (Path.GetRelativePath(_root.Path, v.File).Replace('\\', '/'), v.MatchedBy)).Order().ToList();
        Assert.Equal(
            [
                ("Web/Components/ArticlePage/Default.cshtml", "viewComponent"),
                ("Web/Features/Article/ArticlePage.cshtml", "fileName"),
                ("Web/Features/Article/ArticleTeaser.cshtml", "model"),
                ("Web/Views/Shared/_ArticlePage.cshtml", "partialName"),
            ],
            found);
    }
}
