using EPiServer.Core;
using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Content;

/// <summary>Which content the edit UI lets a property hold, by the types its metadata names (lowercase full names).</summary>
public class EditUiTypeRulesTests
{
    private static string Name<T>() => typeof(T).FullName!.ToLowerInvariant();

    [Fact]
    public void Content_matches_by_its_class_a_base_class_or_an_interface()
    {
        var teasers = new EditUiTypes([Name<TeaserBlock>()], []);
        Assert.True(EditUiTypeRules.Allows(teasers, typeof(TeaserBlock)));
        Assert.True(EditUiTypeRules.Allows(teasers, typeof(WideTeaserBlock)));
        Assert.False(EditUiTypeRules.Allows(teasers, typeof(OtherBlock)));

        Assert.True(EditUiTypeRules.Allows(new EditUiTypes([Name<BlockData>()], []), typeof(OtherBlock)));
        Assert.True(EditUiTypeRules.Allows(new EditUiTypes([Name<IContentData>()], []), typeof(PageData)));
        Assert.False(EditUiTypeRules.Allows(new EditUiTypes([Name<BlockData>()], []), typeof(PageData)));
    }

    [Fact]
    public void A_restricted_type_is_refused_also_where_another_allows_it()
    {
        Assert.False(EditUiTypeRules.Allows(new EditUiTypes([Name<BlockData>()], [Name<TeaserBlock>()]), typeof(WideTeaserBlock)));
        Assert.True(EditUiTypeRules.Allows(new EditUiTypes([Name<BlockData>()], [Name<TeaserBlock>()]), typeof(OtherBlock)));
        // Restrictions alone: anything else.
        Assert.True(EditUiTypeRules.Allows(new EditUiTypes([], [Name<TeaserBlock>()]), typeof(PageData)));
        Assert.False(EditUiTypeRules.Allows(new EditUiTypes([], [Name<TeaserBlock>()]), typeof(TeaserBlock)));
    }

    public class TeaserBlock : BlockData
    {
    }

    public class WideTeaserBlock : TeaserBlock
    {
    }

    public class OtherBlock : BlockData
    {
    }
}
