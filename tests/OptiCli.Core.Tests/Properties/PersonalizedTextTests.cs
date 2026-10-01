using OptiCli.Core.Properties;
using OptiCli.Core.Queries;

namespace OptiCli.Core.Tests.Properties;

public class PersonalizedTextTests
{
    private const string Group = "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e10";
    private const string Link = "~/link/9b917b4ce233517b9ddb97af48830517.aspx";

    private static readonly string Text =
        $"""<p>Everyone.</p><div contenteditable="false" class="epi_pc" data-groups="{Group}" data-contentgroup="g1"><div class="epi_pc_h"><div class="epi_vg">Edge visitors</div></div><section contenteditable="true" class="epi_pc_content"><p>Only <a href="{Link}">them</a>.</p></section></div><p>After.</p>""";

    [Fact]
    public void Sections_have_their_visitor_groups_group_and_content_without_the_editor_header()
    {
        var section = Assert.Single(PersonalizedText.Find(Text));

        Assert.Equal([Group], section.VisitorGroups);
        Assert.Equal("g1", section.Group);
        Assert.Equal($"""<p>Only <a href="{Link}">them</a>.</p>""", section.Html);
        Assert.Empty(PersonalizedText.Find("<p>No personalization, not even <span class=\"epi_pc_h\">this</span>.</p>"));
    }

    [Fact]
    public void A_reference_only_inside_sections_is_seen_by_their_groups()
    {
        Assert.Equal([Group], PersonalizedText.GroupsAround(Text, "9b917b4ce233517b9ddb97af48830517"));
        Assert.Null(PersonalizedText.GroupsAround(Text + $"""<a href="{Link}">again</a>""", "9b917b4ce233517b9ddb97af48830517"));
        Assert.Null(PersonalizedText.GroupsAround(Text, "not-there"));
    }

    [Fact]
    public void Visitor_group_names_come_from_the_model_and_roles_have_none()
    {
        var model = new OptiCli.Core.Content.CmsModel([], [], [], [], null, null, visitorGroups: new Dictionary<Guid, string> { [Guid.Parse(Group)] = "Edge visitors" });

        Assert.Equal("Edge visitors", model.VisitorGroupName(Group.ToUpperInvariant()));
        Assert.Null(model.VisitorGroupName("Administrators"));
        Assert.Null(model.VisitorGroupName(Guid.Empty.ToString()));
    }

    [Theory]
    [InlineData(0, "active")]
    [InlineData(4, "delayedPublished")]
    [InlineData(9, "9")]
    public void Project_statuses_have_names(int status, string name) => Assert.Equal(name, ProjectReader.StatusName(status));
}
