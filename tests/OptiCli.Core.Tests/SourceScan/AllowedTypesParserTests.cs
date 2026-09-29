using OptiCli.Core.SourceScan;

namespace OptiCli.Core.Tests.SourceScan;

public class AllowedTypesParserTests
{
    [Theory]
    [InlineData("[AllowedTypes(typeof(TeaserBlock))]", "TeaserBlock", "")]
    [InlineData("[Display(Order = 1)]\n[AllowedTypes(typeof(TeaserBlock), typeof(Blocks.HeroBlock))]", "TeaserBlock,HeroBlock", "")]
    [InlineData("[AllowedTypes(new[] { typeof(BlockData) }, new[] { typeof(HeroBlock) })]", "BlockData", "HeroBlock")]
    [InlineData("[AllowedTypes([typeof(TeaserBlock)], [typeof(HeroBlock)])]", "TeaserBlock", "HeroBlock")]
    [InlineData("[AllowedTypes(AllowedTypes = new[] { typeof(TeaserBlock), typeof(IImage) }, RestrictedTypes = new[] { typeof(HeroBlock) })]", "TeaserBlock,IImage", "HeroBlock")]
    [InlineData("[Required, EPiServer.DataAnnotations.AllowedTypesAttribute(RestrictedTypes = new Type[] { typeof(HeroBlock) })]", "", "HeroBlock")]
    [InlineData("[AllowedTypes(\n    typeof(TeaserBlock), // teasers\n    typeof(HeroBlock))]", "TeaserBlock,HeroBlock", "")]
    public void Reads_allowed_and_restricted_types_from_every_attribute_form(string attributes, string allowed, string restricted)
    {
        var parsed = AllowedTypesParser.Parse(attributes)!;

        Assert.Equal(allowed, string.Join(",", parsed.Allowed));
        Assert.Equal(restricted, string.Join(",", parsed.Restricted));
    }

    [Fact]
    public void No_attribute_means_no_declaration()
    {
        Assert.Null(AllowedTypesParser.Parse("[Display(Name = \"AllowedTypes\")]\n[CultureSpecific]"));
    }
}
