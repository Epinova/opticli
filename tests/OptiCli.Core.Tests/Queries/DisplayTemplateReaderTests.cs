using OptiCli.Core.Cms;
using OptiCli.Core.Queries;

namespace OptiCli.Core.Tests.Queries;

public class DisplayTemplateReaderTests
{
    [Fact]
    public void A_select_settings_choices_are_read_from_their_json_in_their_order()
    {
        var choices = DisplayTemplateReader.Choices("""[{"key":"dark","name":"Dark","sortOrder":1},{"key":"light","name":"Light","sortOrder":0}]""");

        Assert.Equal([new DisplaySettingChoiceInfo("light", "Light"), new DisplaySettingChoiceInfo("dark", "Dark")], choices);
        Assert.Null(DisplayTemplateReader.Choices("[]"));
        Assert.Null(DisplayTemplateReader.Choices("not json"));
    }

    private static ContentTypeInfo Type(string name, ContentKind kind, string typeBase, params string[] behaviors) =>
        new(1, Guid.NewGuid(), name, null, null, kind, typeBase, null, 0) { CompositionBehaviors = behaviors };

    [Fact]
    public void A_template_applies_by_node_type_content_type_and_base_each_when_it_names_one()
    {
        var element = Type("TextElement", ContentKind.Element, "Block", "ElementEnabled");
        var any = new DisplayTemplateInfo("any", null, null, null, null, false, []);
        var components = new DisplayTemplateInfo("components", null, "component", null, null, true, []);
        var forOther = new DisplayTemplateInfo("other", null, "component", null, "OtherElement", false, []);
        var forSections = new DisplayTemplateInfo("sections", null, null, "Section", null, false, []);

        Assert.Equal("component", DisplayTemplateInfo.NodeTypeOf(element));
        Assert.Equal(["any", "components"], new[] { any, components, forOther, forSections }.Where(t => t.AppliesTo(element, "component")).Select(t => t.Key));
        Assert.Equal("component", DisplayTemplateInfo.NodeTypeOf(Type("Banner", ContentKind.Block, "Block", "SectionEnabled")));
        Assert.Equal("section", DisplayTemplateInfo.NodeTypeOf(Type("Section", ContentKind.Section, "Section")));
        Assert.Null(DisplayTemplateInfo.NodeTypeOf(Type("Teaser", ContentKind.Block, "Block")));
    }
}
