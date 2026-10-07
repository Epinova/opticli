using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms.Compat;
#if CMS13
using EPiServer.VisualBuilder;
#endif

namespace OptiCli.Agent.Tests.Cms;

/// <summary>CMS 13's Visual Builder reads in the agent: kinds and the composition's storage properties; nothing on CMS 12.</summary>
public class CmsCompositionsTests
{
    public class PlainBlock : BlockData;

    [Fact]
    public void A_block_without_a_composition_behaviour_has_no_visual_builder_kind()
    {
        var block = new PlainBlock();

        Assert.Null(CmsCompositions.Kind(block, new ContentType()));
        Assert.False(CmsCompositions.IsStorage(block, new PropertyLongString { Name = "Layout" }));
    }

#if CMS13
    public class Experience : ExperienceData;

    public class Section : SectionData;

    [Fact]
    public void Experiences_sections_and_element_enabled_blocks_have_their_kinds_and_their_layout_is_storage()
    {
        var element = new ContentType { CompositionBehaviors = [CompositionBehavior.ElementEnabled] };

        Assert.Equal("experience", CmsCompositions.Kind(new Experience(), null));
        Assert.Equal("section", CmsCompositions.Kind(new Section(), null));
        Assert.Equal("element", CmsCompositions.Kind(new PlainBlock(), element));
        Assert.True(CmsCompositions.IsStorage(new Experience(), new PropertyLongString { Name = "UnstructuredData" }));
        Assert.True(CmsCompositions.IsStorage(new Section(), new PropertyLongString { Name = "Layout" }));
        Assert.False(CmsCompositions.IsStorage(new Experience(), new PropertyLongString { Name = "Summary" }));
    }
#endif
}
