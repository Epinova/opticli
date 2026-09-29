using OptiCli.Core.Queries;
using OptiCli.Core.SourceScan;
using OptiCli.Core.Tests.Content;

namespace OptiCli.Core.Tests.Queries;

public class AllowedInQueryTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public AllowedInQueryTests()
    {
        _root.Write("Web/ArticlePage.cs", """
            namespace Example.Web;

            public class ArticlePage : PageData
            {
                [AllowedTypes(new[] { typeof(SiteBlock) }, new[] { typeof(FactBlock) })]
                public virtual ContentArea MainArea { get; set; }

                public virtual IList<ContentReference> RelatedItems { get; set; }

                public virtual ContentReference RelatedPage { get; set; }
            }
            """);
        _root.Write("Web/Blocks.cs", """
            namespace Example.Web;

            public abstract class SiteBlock : BlockData { }

            public class TeaserBlock : SiteBlock { }

            public class FactBlock : SiteBlock { }

            public class HeroBlock : BlockData { }
            """);
    }

    [Fact]
    public void Matches_through_base_classes_and_lists_unrestricted_lists_after_explicit_matches()
    {
        var rows = Find("TeaserBlock", explicitOnly: false);

        Assert.Equal([("MainArea", "explicit", "SiteBlock"), ("RelatedItems", "any", null)], rows.Select(r => (r.Property, r.Allowed, r.MatchedBy)));
        Assert.Equal("Web/ArticlePage.cs:6", rows[0].Source!.Replace('\\', '/'));
    }

    [Fact]
    public void Leaves_out_restricted_and_unrelated_types_and_with_explicit_the_unrestricted_ones()
    {
        Assert.Equal(["RelatedItems"], Find("FactBlock", explicitOnly: false).Select(r => r.Property));
        Assert.Equal(["RelatedItems"], Find("HeroBlock", explicitOnly: false).Select(r => r.Property));
        Assert.Empty(Find("HeroBlock", explicitOnly: true));
    }

    public void Dispose() => _root.Dispose();

    private List<AllowedIn> Find(string target, bool explicitOnly)
    {
        var model = ModelFixture.Create();
        return AllowedInQuery.Find(model, CSharpSourceIndex.Build(_root.Path), _root.Path, model.RequireType(target), explicitOnly).ToList();
    }
}
