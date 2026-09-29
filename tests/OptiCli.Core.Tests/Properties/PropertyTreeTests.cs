using OptiCli.Core.Properties;
using static OptiCli.Core.Tests.Content.ModelFixture;

namespace OptiCli.Core.Tests.Properties;

public class PropertyTreeTests
{
    [Fact]
    public void Nests_rows_by_scope()
    {
        var rows = new[]
        {
            new PropertyRow(123, Heading, English, String: "Title"),
            new PropertyRow(123, HeroHeading, English, ".104.301.", String: "Hero title"),
            new PropertyRow(123, FactLabel, English, ".105(1).401.", String: "Second"),
            new PropertyRow(123, FactLabel, English, ".105(0).401.", String: "First"),
            new PropertyRow(123, TeaserText, English, ".103:20(2).201.", LongString: "<p>Inline</p>"),
            new PropertyRow(123, HeroHeading, English, ".103:20(2).150:30(0).301.", String: "Deep"),
        };

        var tree = PropertyTree.Build(rows);

        Assert.Equal("Title", tree[Heading].Row!.String);
        Assert.Equal("Hero title", tree[Hero].Properties[HeroHeading].Row!.String);
        Assert.Equal(["First", "Second"], tree[Facts].Items.Values.Select(i => i.Properties[FactLabel].Row!.String));
        var inline = tree[MainArea].Items[2];
        Assert.Equal(TeaserBlock, inline.TypeId);
        Assert.Equal("<p>Inline</p>", inline.Properties[TeaserText].Row!.LongString);
        Assert.Equal("Deep", inline.Properties[150].Items[0].Properties[HeroHeading].Row!.String);
        Assert.Equal(HeroBlock, inline.Properties[150].Items[0].TypeId);
    }

    [Fact]
    public void Skips_rows_with_unreadable_scopes()
    {
        var tree = PropertyTree.Build([new PropertyRow(123, Heading, English, ".x.101.", String: "?")]);

        Assert.Empty(tree);
    }

    [Fact]
    public void Effective_rows_take_culture_specific_values_from_the_branch_and_shared_ones_from_master()
    {
        bool CultureSpecific(int id) => id is Heading or HeroHeading;
        var rows = new[]
        {
            new PropertyRow(123, Heading, English, String: "English heading"),
            new PropertyRow(123, Heading, Swedish, String: "Svensk rubrik"),
            new PropertyRow(123, Priority, English, Number: 5),
            new PropertyRow(123, HeroSubHeading, English, ".104.302.", String: "Shared sub"),
            new PropertyRow(123, HeroSubHeading, Swedish, ".104.302.", BranchSpecific: true, String: "Branch-specific sub"),
            new PropertyRow(123, HeroSubHeading, English, ".104.302.", BranchSpecific: true, String: "English branch sub"),
        };

        var swedish = PropertyRows.Effective(rows, Swedish, English, CultureSpecific).ToList();

        Assert.Contains(swedish, r => r.String == "Svensk rubrik");
        Assert.Contains(swedish, r => r.Number == 5);
        Assert.Contains(swedish, r => r.String == "Shared sub");
        Assert.Contains(swedish, r => r.String == "Branch-specific sub");
        Assert.DoesNotContain(swedish, r => r.String is "English heading" or "English branch sub");

        var english = PropertyRows.Effective(rows, English, English, CultureSpecific).ToList();
        Assert.Equal(4, english.Count);
        Assert.All(english, r => Assert.Equal(English, r.LanguageId));
    }

    [Fact]
    public void Values_inside_a_shared_block_come_from_master_even_when_the_inner_property_is_culture_specific()
    {
        // A shared block list whose items have a culture-specific Heading: the CMS stores every item value once, on
        // the master branch, with BranchSpecificScope = 0, and shows those values in every branch.
        bool CultureSpecific(int id) => id is Heading or HeroHeading;
        var rows = new[]
        {
            new PropertyRow(123, HeroHeading, English, ".105(0).301.", BranchSpecific: false, String: "Stored once"),
            new PropertyRow(123, HeroHeading, Swedish, ".105(1).301.", BranchSpecific: false, String: "Stale copy"),
            // Older CMS versions left the flag empty; then the inner property's own setting decides.
            new PropertyRow(123, HeroHeading, Swedish, ".104.301.", String: "Legacy branch value"),
        };

        var swedish = PropertyRows.Effective(rows, Swedish, English, CultureSpecific).Select(r => r.String).ToList();

        Assert.Equal(["Stored once", "Legacy branch value"], swedish);
    }
}
