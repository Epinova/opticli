using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class LanguageBranchTests
{
    [Fact]
    public void Plans_translate_with_blocks_and_remove_branches()
    {
        var plan = WritePlan.Parse("""{"operations": [{"op": "translate", "ref": "123", "lang": "sv", "withBlocks": true}, {"op": "translate", "ref": "124", "lang": "de", "remove": true, "confirm": true}]}""");

        var translate = Assert.IsType<TranslateOperation>(plan.Steps[0].Operation);
        var remove = Assert.IsType<TranslateOperation>(plan.Steps[1].Operation);
        Assert.Equal((true, false), (translate.WithBlocks, translate.Remove));
        Assert.Equal((true, true), (remove.Remove, remove.Confirm));
    }

    [Fact]
    public void A_new_branch_is_undone_by_removing_it_with_the_blocks_it_translated()
    {
        var output = new WriteOutput("123", "123_20", null, "ArticlePage", "Nyheter", "sv", "checkedOut", "5", Saved: true, Published: false, DryRun: false, Valid: true,
            BaseVersion: null, Changes: [], Validation: null)
        {
            Blocks = [new BlockTranslation("200", "Teaser", "TeaserBlock", "200_21", "translated"), new BlockTranslation("201", "Hero", "HeroBlock", null, "exists")],
        };

        Assert.Equal("Language branch 'sv' was created (123_20); to remove it again: opticli translate 123 --lang sv --remove --confirm (and the same for the blocks it translated: 200)",
            UndoHints.For(new TranslateOperation("123", "sv") { WithBlocks = true }, output));
    }

    [Fact]
    public void A_removed_branch_can_not_be_brought_back()
    {
        var removed = new RemoveLanguageOutput("123", Guid.Empty, "ArticlePage", "News", "sv", 4, Published: true, Removed: true, DryRun: false);

        Assert.StartsWith("None: the 'sv' branch of 123 and its 4 version(s)", UndoHints.For(new TranslateOperation("123", "sv") { Remove = true }, removed));
        Assert.Null(UndoHints.For(new TranslateOperation("123", "sv") { Remove = true }, removed with { Removed = false, DryRun = true }));
    }
}
