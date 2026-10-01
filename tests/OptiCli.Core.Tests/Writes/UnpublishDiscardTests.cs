using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class UnpublishDiscardTests
{
    private static WriteOutput Output(bool saved = true, bool published = false, string? previous = null) =>
        new("123", "123_13", null, "ArticlePage", "News", "en", "published", "5", saved, published, DryRun: false, Valid: true, BaseVersion: "123_12", Changes: [], Validation: null)
        { PreviouslyPublished = previous };

    [Fact]
    public void Plans_take_unpublish_and_discard()
    {
        var plan = WritePlan.Parse("""{"operations": [{"op": "unpublish", "ref": "123", "lang": "sv"}, {"op": "discard", "ref": "124", "version": 7, "includeDraft": true}]}""");

        Assert.Equal(new UnpublishOperation("123", "sv"), plan.Steps[0].Operation);
        var discard = Assert.IsType<DiscardOperation>(plan.Steps[1].Operation);
        Assert.Equal((7, true), (discard.Version, discard.IncludeDraft));
    }

    [Fact]
    public void Unpublish_is_undone_by_publishing_the_version_that_was_live()
    {
        var hint = UndoHints.For(new UnpublishOperation("123"), Output(previous: "123_12") with { Unpublished = true });

        Assert.Equal("123 is offline in 'en'; to put it back, publish the version that was live: opticli publish 123 --version 12", hint);
    }

    [Fact]
    public void A_discard_can_not_be_undone()
    {
        Assert.StartsWith("None: 123_13 was deleted for good", UndoHints.For(new DiscardOperation("123"), Output(saved: false) with { Discarded = true }));
        Assert.Null(UndoHints.For(new DiscardOperation("123"), Output(saved: false)));
    }
}
