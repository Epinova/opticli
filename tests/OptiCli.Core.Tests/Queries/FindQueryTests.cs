using OptiCli.Core.Queries;
using OptiCli.Core.Tests.Content;

namespace OptiCli.Core.Tests.Queries;

public class FindQueryTests
{
    [Theory]
    [InlineData(ModelFixture.Heading, "p.fkLanguageBranchID = cl.fkLanguageBranchID")]
    [InlineData(ModelFixture.Priority, "p.fkLanguageBranchID = c.fkMasterLanguageBranchID")]
    public void Top_level_values_are_read_from_the_branch_their_property_says(int definition, string expected)
    {
        Assert.Equal(expected, FindQuery.LanguageCondition(Definition(definition), inBlock: false));
    }

    [Theory]
    // The hero block is shared: its culture-specific Heading is stored on the master branch with BranchSpecificScope = 0,
    // and on the item's own branch with 1 when the block is culture-specific. Only rows without the flag fall back to
    // the inner property's setting, as get does.
    [InlineData(ModelFixture.HeroHeading, "1")]
    [InlineData(ModelFixture.HeroSubHeading, "0")]
    public void Values_inside_a_block_are_read_from_the_branch_BranchSpecificScope_says(int definition, string fallback)
    {
        Assert.Equal(
            $"p.fkLanguageBranchID = CASE WHEN ISNULL(p.BranchSpecificScope, {fallback}) = 1 THEN cl.fkLanguageBranchID ELSE c.fkMasterLanguageBranchID END",
            FindQuery.LanguageCondition(Definition(definition), inBlock: true));
    }

    private static Core.Content.PropertyDefinition Definition(int id) => ModelFixture.Create().Properties[id];
}
