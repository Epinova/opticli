using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class UndoHintsTests
{
    private static WriteOutput Output(bool saved = true, bool published = false, string? baseVersion = "123_455", string? language = "en") =>
        new("123", "123_456", Guid.Empty, "ArticlePage", "Name", language, published ? "published" : "checkedOut", "10",
            saved, published, DryRun: false, Valid: true, baseVersion, [], null);

    private static MoveOutput Moved(bool moved = true) =>
        new("123", Guid.Empty, "ArticlePage", "Name", "en", "published", "20", "10", moved, DryRun: !moved, Descendants: 0);

    [Fact]
    public void Create_is_undone_by_a_delete()
    {
        Assert.Equal("opticli delete 123 (moves it to the recycle bin)", UndoHints.For(new CreateOperation("10", "ArticlePage", "Name"), Output()));
        Assert.StartsWith("opticli delete 123", UndoHints.For(new BlockCreateOperation("TeaserBlock", "Name", For: "10"), Output()));
    }

    [Fact]
    public void A_published_edit_is_undone_by_republishing_its_base_version()
    {
        Assert.Equal(
            "123_456 was published; to go back, re-publish the version it was based on: opticli publish 123 --version 455",
            UndoHints.For(new SetOperation("123", Publish: true), Output(published: true)));
    }

    [Fact]
    public void A_draft_needs_no_undo()
    {
        Assert.Contains("unpublished draft", UndoHints.For(new AreaEdit("123", "MainArea", "add", "5"), Output()));
    }

    [Fact]
    public void Move_and_delete_are_undone_by_moving_back()
    {
        Assert.Equal("opticli move 123 --to 10", UndoHints.For(new MoveOperation("123", "20"), Moved()));
        Assert.Equal("opticli move 123 --to 10 (restores it from the recycle bin)", UndoHints.For(new DeleteOperation("123"), Moved()));
    }

    [Fact]
    public void Translate_and_publish_explain_what_to_do()
    {
        Assert.Contains("Language branch 'en'", UndoHints.For(new TranslateOperation("123", "en"), Output()));
        Assert.Contains("opticli versions 123", UndoHints.For(new PublishOperation("123"), Output(published: true)));
    }

    [Fact]
    public void Nothing_saved_means_nothing_to_undo()
    {
        Assert.Null(UndoHints.For(new SetOperation("123"), Output(saved: false)));
        Assert.Null(UndoHints.For(new MoveOperation("123", "20"), Moved(moved: false)));
    }
}
