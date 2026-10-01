using OptiCli.Core.Writes;
using OptiCli.Protocol;

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
        Assert.StartsWith("opticli delete 123", UndoHints.For(new UploadOperation("/tmp/report.pdf", Parent: "10"), Output()));
    }

    [Fact]
    public void A_published_edit_is_undone_by_republishing_the_previously_published_version()
    {
        // Based on a draft (455) that was never live: going back means the version that was published before (450).
        var published = Output(published: true) with { PreviouslyPublished = "123_450" };

        Assert.Equal(
            "123_456 is now published; to go back, publish the previously published version: opticli publish 123 --version 450",
            UndoHints.For(new SetOperation("123", Publish: true), published));
        Assert.Equal(
            "123_456 is now published; to go back, publish the previously published version: opticli publish 123 --version 450",
            UndoHints.For(new AreaEdit("123", "MainArea", "add", "5", Publish: true), published));
    }

    [Fact]
    public void The_agents_previously_published_version_id_becomes_a_version_ref()
    {
        var result = new WriteResult
        {
            Content = new ContentSummary { Ref = "123_456", Id = 123, Version = 456, Guid = Guid.Empty, Name = "Name", Language = "en", Status = "published" },
            Saved = true,
            Published = true,
            BaseVersion = 455,
            PreviouslyPublished = 450,
        };

        var output = WriteOutput.From(result);

        Assert.Equal(("123_455", "123_450"), (output.BaseVersion, output.PreviouslyPublished));
        Assert.EndsWith("opticli publish 123 --version 450", UndoHints.For(new SetOperation("123", Publish: true), output));
        Assert.Null(WriteOutput.From(result with { PreviouslyPublished = null }).PreviouslyPublished);
    }

    [Fact]
    public void A_first_publish_is_undone_by_unpublish()
    {
        var hint = UndoHints.For(new SetOperation("123", Publish: true), Output(published: true));

        Assert.Equal("123_456 is now published, and it is the first published version in 'en'; to take it offline again: opticli unpublish 123 --lang en", hint);
        Assert.DoesNotContain("--version", hint);
        Assert.DoesNotContain("in '", UndoHints.For(new PublishOperation("123"), Output(published: true, language: null)));
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
        Assert.Equal(
            "123_456 is now published; to go back, publish the previously published version: opticli publish 123 --version 450",
            UndoHints.For(new PublishOperation("123"), Output(published: true) with { PreviouslyPublished = "123_450" }));
    }

    [Fact]
    public void Nothing_saved_means_nothing_to_undo()
    {
        Assert.Null(UndoHints.For(new SetOperation("123"), Output(saved: false)));
        Assert.Null(UndoHints.For(new MoveOperation("123", "20"), Moved(moved: false)));
    }

    private static AccessEntry Role(string name, int mask) => new(name, AccessKinds.Role, AccessLevels.Describe(mask), mask);

    private static AccessOutput Access(AccessList before, AccessList after, bool saved = true) =>
        new("123", Guid.Empty, "ArticlePage", "Name", saved, DryRun: false, before, after);

    private static readonly AccessList Parent = new(true, "10", [Role("Administrators", 63), Role("Everyone", 1)]);

    [Fact]
    public void Breaking_inheritance_is_undone_by_inheriting_again()
    {
        var after = new AccessList(false, "123", [Role("Administrators", 63), Role("Authenticated", 1)]);

        Assert.Equal("opticli access 123 --inherit (it inherited from 10 before)",
            UndoHints.For(new AccessOperation("123", BreakInheritance: true), Access(Parent, after)));
    }

    [Fact]
    public void An_explicit_change_is_undone_by_the_inverse_grants_and_revokes()
    {
        var before = new AccessList(false, "123", [Role("Administrators", 63), Role("Authenticated", 1), Role("Web Editors", 63)]);
        var after = new AccessList(false, "123", [Role("Administrators", 63), Role("Everyone", 3), Role("Web Editors", 7)]);

        Assert.Equal("opticli access 123 --revoke Everyone --grant Authenticated=Read --grant 'Web Editors=FullAccess'",
            UndoHints.For(new AccessOperation("123"), Access(before, after)));
    }

    [Fact]
    public void Going_back_to_inheriting_is_undone_by_breaking_inheritance_and_restoring_the_entries()
    {
        var before = new AccessList(false, "123", [Role("Administrators", 63), Role("Authenticated", 1)]);

        Assert.Equal("opticli access 123 --break-inheritance --revoke Everyone --grant Authenticated=Read",
            UndoHints.For(new AccessOperation("123", Inherit: true), Access(before, Parent)));
    }

    [Fact]
    public void An_unchanged_acl_needs_no_undo()
    {
        Assert.Null(UndoHints.For(new AccessOperation("123", Grant: new Dictionary<string, string> { ["Everyone"] = "Read" }), Access(Parent, Parent, saved: false)));
    }

    [Fact]
    public void Updating_existing_content_is_undone_like_a_draft_not_by_deleting_it()
    {
        var updated = Output() with { Existing = true };

        Assert.Contains("unpublished draft", UndoHints.For(new CreateOperation("10", "ArticlePage", "Name"), updated));
        Assert.Contains("unpublished draft", UndoHints.For(new TranslateOperation("123", "en"), updated));
        Assert.Null(UndoHints.For(new UploadOperation("/tmp/a.pdf", Parent: "10"), Output(saved: false) with { Existing = true }));
    }

    [Fact]
    public void Restored_content_names_how_to_put_it_back_in_the_recycle_bin()
    {
        Assert.Equal("123 was moved back out of the recycle bin (opticli delete 123 returns it there).",
            UndoHints.For(new CreateOperation("10", "ArticlePage", "Name"), Output(saved: false) with { Existing = true, Restored = true }));
        Assert.StartsWith("123 was moved back out of the recycle bin (opticli delete 123 returns it there); 123_456 is an unpublished draft",
            UndoHints.For(new CreateOperation("10", "ArticlePage", "Name"), Output() with { Existing = true, Restored = true }));
    }
}
