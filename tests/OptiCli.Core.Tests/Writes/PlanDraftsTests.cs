using OptiCli.Core.Content;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

public class PlanDraftsTests
{
    private static WriteOutput Output(int version, string? language = "en", string status = "checkedOut", bool saved = true, bool published = false, bool into = false, int content = 123) =>
        new(WriteOutput.Id(content), WriteOutput.VersionRef(content, version), Guid.Empty, "ArticlePage", "Name", language, status, "10",
            saved, published, DryRun: false, Valid: true, WriteOutput.VersionRef(content, version - 1), [], null)
        { IntoVersion = into ? true : null };

    private static PlanDrafts.Key Key(string? language = "en", string? variation = null, int content = 123) => PlanDrafts.Key.Of(content, language, variation);

    private static readonly SetOperation Set = new("123", Name: "x");

    private static readonly (SavedInto?, string?) Nothing = (null, null);

    [Fact]
    public void A_saved_draft_is_found_for_its_content_and_language_and_later_saves_into_it_keep_who_made_it()
    {
        var drafts = new PlanDrafts();
        Assert.Equal(Nothing, drafts.After(new AreaEdit("123", "MainContentArea", "add", Item: "5"), 1, Output(456), null));

        var draft = drafts.Find(Key("EN"));
        Assert.Equal(new PlanDrafts.Draft(123, 456, 1), draft);
        Assert.Null(drafts.Find(Key("sv")));
        Assert.Null(drafts.Find(Key(content: 124)));

        // Operation 3 saved into it: it says so, and the draft is still operation 1's.
        var (into, note) = drafts.After(PlanDrafts.Into(Set, 456), 3, Output(456, into: true), draft);
        Assert.Equal(new SavedInto("123_456", 1), into);
        Assert.Null(note);
        Assert.Equal(new PlanDrafts.Draft(123, 456, 1), drafts.Find(Key()));
    }

    [Fact]
    public void A_new_version_where_a_draft_was_offered_says_so_and_becomes_the_draft()
    {
        var drafts = new PlanDrafts();
        drafts.After(Set, 0, Output(456), null);

        var (into, note) = drafts.After(Set, 2, Output(460), drafts.Find(Key()));

        Assert.Null(into);
        Assert.StartsWith("Saved as a new version (123_460), not into 123_456, the draft operation 0 made", note, StringComparison.Ordinal);
        Assert.Equal(new PlanDrafts.Draft(123, 460, 2), drafts.Find(Key()));
    }

    [Fact]
    public void A_step_that_changed_nothing_leaves_the_draft_as_it_was()
    {
        var drafts = new PlanDrafts();
        drafts.After(Set, 0, Output(456), null);

        Assert.Equal(Nothing, drafts.After(Set, 1, Output(456, saved: false), drafts.Find(Key())));
        Assert.Equal(new PlanDrafts.Draft(123, 456, 0), drafts.Find(Key()));
    }

    [Fact]
    public void Created_translated_and_uploaded_content_saved_as_a_draft_counts_too()
    {
        var drafts = new PlanDrafts();
        drafts.After(new CreateOperation("10", "ArticlePage", "News"), 0, Output(456), null);
        drafts.After(new TranslateOperation("123", "sv"), 1, Output(457, language: "sv"), null);
        drafts.After(new UploadOperation("/tmp/report.pdf", Parent: "10"), 2, Output(900, language: null, content: 200), null);

        Assert.Equal(0, drafts.Find(Key())!.Operation);
        Assert.Equal(1, drafts.Find(Key("sv"))!.Operation);
        // Content that isn't localizable has one branch, which the site reports without a language.
        Assert.Equal(new PlanDrafts.Draft(200, 900, 2), drafts.Find(Key("en", content: 200)));
    }

    [Fact]
    public void A_content_variation_is_a_draft_of_its_own()
    {
        var drafts = new PlanDrafts();
        drafts.After(Set with { Variation = "Summer" }, 0, Output(456), null);

        Assert.Null(drafts.Find(Key()));
        Assert.Equal(456, drafts.Find(Key(variation: " summer "))!.Version);
        Assert.Null(drafts.Find(Key(variation: "winter")));
    }

    [Theory]
    [InlineData("published", true, false, false)]
    [InlineData("delayedPublish", false, true, false)]
    [InlineData("awaitingApproval", false, false, true)]
    public void A_step_that_publishes_schedules_or_sends_for_review_leaves_no_draft_to_save_into(string status, bool published, bool scheduled, bool review)
    {
        var drafts = new PlanDrafts();
        drafts.After(Set, 0, Output(456), null);
        var output = Output(456, status: status, published: published, into: true) with
        {
            ScheduledFor = scheduled ? DateTime.UtcNow.AddDays(1) : null,
            ApprovalRequested = review ? true : null,
        };

        var (into, _) = drafts.After(Set with { Publish = published }, 1, output, drafts.Find(Key()));

        Assert.Equal(new SavedInto("123_456", 0), into);
        Assert.Null(drafts.Find(Key()));
    }

    [Fact]
    public void Publish_unpublish_and_discard_forget_the_branch_and_delete_forgets_the_content()
    {
        var drafts = new PlanDrafts();
        void Both()
        {
            drafts.After(Set, 0, Output(456), null);
            drafts.After(Set with { Lang = "sv" }, 1, Output(457, language: "sv"), null);
        }

        foreach (var operation in new WriteOperation[] { new PublishOperation("123"), new UnpublishOperation("123"), new DiscardOperation("123") })
        {
            Both();
            drafts.After(operation, 2, Output(456, status: "published", published: true), null);
            Assert.Null(drafts.Find(Key()));
            Assert.NotNull(drafts.Find(Key("sv")));
        }

        Both();
        drafts.After(new TranslateOperation("123", "sv") { Remove = true }, 2, new RemoveLanguageOutput("123", Guid.Empty, "ArticlePage", "Name", "sv", 2, false, Removed: true, DryRun: false), null);
        Assert.Null(drafts.Find(Key("sv")));
        Assert.NotNull(drafts.Find(Key()));

        Both();
        drafts.After(new DeleteOperation("123"), 2, new MoveOutput("123", Guid.Empty, "ArticlePage", "Name", "en", "published", "1", "10", Moved: true, DryRun: false, Descendants: 0), null);
        Assert.Null(drafts.Find(Key()));
        Assert.Null(drafts.Find(Key("sv")));
    }

    [Fact]
    public void A_move_keeps_the_draft()
    {
        var drafts = new PlanDrafts();
        drafts.After(Set, 0, Output(456), null);

        drafts.After(new MoveOperation("123", "20"), 1, new MoveOutput("123", Guid.Empty, "ArticlePage", "Name", "en", "published", "20", "10", Moved: true, DryRun: false, Descendants: 0), null);

        Assert.NotNull(drafts.Find(Key()));
    }

    [Fact]
    public void Only_set_area_and_composition_that_leave_the_base_to_opticli_save_into_a_draft()
    {
        Assert.True(PlanDrafts.MaySaveInto(Set));
        Assert.True(PlanDrafts.MaySaveInto(new AreaEdit("123", "MainContentArea", "add", Item: "5")));
        Assert.True(PlanDrafts.MaySaveInto(new CompositionEdit("123", "remove", Node: "hero")));

        Assert.False(PlanDrafts.MaySaveInto(Set with { From = FromVersion.Published }));
        Assert.False(PlanDrafts.MaySaveInto(Set with { BaseVersion = 455 }));
        Assert.False(PlanDrafts.MaySaveInto(Set with { Force = true }));
        Assert.False(PlanDrafts.MaySaveInto(new AreaEdit("123", "MainContentArea", "add", Item: "5", Force: true)));
        Assert.False(PlanDrafts.MaySaveInto(new CompositionEdit("123", "remove", Node: "hero") { From = new FromVersion(450) }));
        Assert.False(PlanDrafts.MaySaveInto(new CreateOperation("10", "ArticlePage", "News")));
        Assert.False(PlanDrafts.MaySaveInto(new PublishOperation("123")));

        Assert.Equal(456, ((SetOperation)PlanDrafts.Into(Set, 456)).IntoVersion);
        Assert.Equal(456, ((AreaEdit)PlanDrafts.Into(new AreaEdit("123", "MainContentArea", "add", Item: "5"), 456)).IntoVersion);
        Assert.Equal(456, ((CompositionEdit)PlanDrafts.Into(new CompositionEdit("123", "remove", Node: "hero"), 456)).IntoVersion);
    }

    [Fact]
    public void The_database_must_still_have_it_as_an_unpublished_draft_of_opticlis_in_the_branch()
    {
        static VersionInfo Version(VersionStatus status = VersionStatus.CheckedOut, string? by = "opticli", int content = 123, int language = 1, string? variation = null) =>
            new($"{content}_456", "en", "checkedOut", "Name", DateTime.UtcNow, by, null, null, true, variation)
            {
                Id = 456, ContentId = content, LanguageId = language, StatusValue = status,
            };

        Assert.True(PlanDrafts.StillOurs(Version(), 123, 1, null));
        Assert.True(PlanDrafts.StillOurs(Version(by: " OptiCli "), 123, 1, null));
        Assert.True(PlanDrafts.StillOurs(Version(variation: "Summer"), 123, 1, "summer"));

        Assert.False(PlanDrafts.StillOurs(null, 123, 1, null));
        Assert.False(PlanDrafts.StillOurs(Version(VersionStatus.Published), 123, 1, null));
        Assert.False(PlanDrafts.StillOurs(Version(VersionStatus.DelayedPublish), 123, 1, null));
        Assert.False(PlanDrafts.StillOurs(Version(VersionStatus.AwaitingApproval), 123, 1, null));
        Assert.False(PlanDrafts.StillOurs(Version(by: "someone@example.com"), 123, 1, null));
        Assert.False(PlanDrafts.StillOurs(Version(by: null), 123, 1, null));
        Assert.False(PlanDrafts.StillOurs(Version(content: 124), 123, 1, null));
        Assert.False(PlanDrafts.StillOurs(Version(language: 2), 123, 1, null));
        Assert.False(PlanDrafts.StillOurs(Version(variation: "summer"), 123, 1, null));
        Assert.False(PlanDrafts.StillOurs(Version(), 123, 1, "summer"));
    }

    [Fact]
    public void A_step_that_saved_into_an_earlier_draft_points_to_that_steps_undo()
    {
        var into = new SavedInto("123_456", 1);

        var hint = UndoHints.For(Set, Output(456, into: true), into);
        Assert.StartsWith("Saved into 123_456, the draft operation 1 made, so nothing live changed; operation 1's undo covers this change too (", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("opticli discard", hint, StringComparison.Ordinal);

        // A publish of the draft keeps its own undo, which reverses the earlier steps' changes as well.
        var published = UndoHints.For(Set with { Publish = true }, Output(456, status: "published", published: true, into: true) with { PreviouslyPublished = "123_450" }, into);
        Assert.StartsWith("Saved into 123_456, the draft operation 1 made", published, StringComparison.Ordinal);
        Assert.EndsWith("opticli publish 123 --version 450", published, StringComparison.Ordinal);

        // A scheduled publish of the draft: discarding it to cancel drops the earlier steps' changes too, which the hint says first.
        var at = new DateTime(2030, 1, 2, 8, 0, 0, DateTimeKind.Utc);
        var scheduled = UndoHints.For(Set with { PublishAt = at }, Output(456, status: "delayedPublish", into: true) with { ScheduledFor = at }, into);
        Assert.StartsWith("Saved into 123_456, the draft operation 1 made, so what follows covers the changes saved into it before this one too: ", scheduled, StringComparison.Ordinal);
        Assert.EndsWith("opticli discard 123 --version 456", scheduled, StringComparison.Ordinal);

        // Nothing saved: no undo, whatever it was offered.
        Assert.Null(UndoHints.For(Set, Output(456, saved: false), into));

        // Without one, as before.
        Assert.Equal(UndoHints.For(Set, Output(456)), UndoHints.For(Set, Output(456), null));
    }

    [Fact]
    public void A_declined_offer_that_published_says_so_and_leaves_no_draft()
    {
        var drafts = new PlanDrafts();
        drafts.After(Set, 0, Output(456), null);

        var (into, note) = drafts.After(Set with { Publish = true }, 1, Output(460, status: "published", published: true), drafts.Find(Key()));

        Assert.Null(into);
        Assert.StartsWith("Saved as a new version (123_460), not into 123_456", note, StringComparison.Ordinal);
        Assert.Null(drafts.Find(Key()));
    }

    [Fact]
    public void Publishing_content_that_isnt_localizable_forgets_its_draft()
    {
        var drafts = new PlanDrafts();
        drafts.After(new UploadOperation("/tmp/report.pdf", Parent: "10"), 0, Output(900, language: null, content: 200), null);

        drafts.After(new PublishOperation("200"), 1, Output(900, language: null, status: "published", published: true, content: 200), null);

        Assert.Null(drafts.Find(Key("en", content: 200)));
    }

    [Fact]
    public void A_later_step_that_publishes_the_draft_makes_the_earlier_undos_point_to_it()
    {
        var drafts = new PlanDrafts();
        var area = new AreaEdit("123", "MainContentArea", "add", Item: "5");
        PlanStepResult Saved(int index, WriteOperation operation, WriteOutput output, SavedInto? into) =>
            new(index, operation.Kind, null, PlanStepStatus.Saved, output, Undo: UndoHints.For(operation, output, into)) { SavedInto = into };
        var results = new List<PlanStepResult>();

        drafts.After(area, 0, Output(456), null);
        results.Add(Saved(0, area, Output(456), null));
        Assert.Null(drafts.Ended);
        var (into, _) = drafts.After(Set, 1, Output(456, into: true), drafts.Find(Key()));
        results.Add(Saved(1, Set, Output(456, into: true), into));
        results.Add(new PlanStepResult(2, "move", "x", PlanStepStatus.Saved, Undo: "opticli move 123 --to 10"));

        drafts.After(new PublishOperation("123"), 3, Output(456, status: "published", published: true), null);
        Assert.Equal((new PlanDrafts.Draft(123, 456, 0), "published it"), drafts.Ended);
        PlanDrafts.Revise(results, drafts.Ended!.Value.Draft, 3, drafts.Ended.Value.How);

        Assert.Equal("123_456 was saved as a draft; operation 3 then published it with the changes saved into it since: see operation 3's undo.", results[0].Undo);
        Assert.Equal("Saved into 123_456, the draft operation 0 made; operation 3 then published it with this change: see operation 3's undo.", results[1].Undo);
        Assert.Equal("opticli move 123 --to 10", results[2].Undo);
        Assert.All(results, r => Assert.DoesNotContain("nothing live changed", r.Undo, StringComparison.Ordinal));
    }

    [Fact]
    public void A_step_that_schedules_its_save_into_the_draft_ends_it_and_a_create_keeps_its_undo()
    {
        var drafts = new PlanDrafts();
        var create = new CreateOperation("10", "ArticlePage", "News");
        drafts.After(create, 0, Output(456), null);
        var results = new List<PlanStepResult> { new(0, "create", "page", PlanStepStatus.Saved, Output(456), Undo: "opticli delete 123 (moves it to the recycle bin)") };

        var scheduled = Output(456, status: "delayedPublish", into: true) with { ScheduledFor = DateTime.UtcNow.AddDays(1) };
        drafts.After(Set with { PublishAt = DateTimeOffset.UtcNow.AddDays(1) }, 1, scheduled, drafts.Find(Key()));

        Assert.Equal((new PlanDrafts.Draft(123, 456, 0), "scheduled it to be published"), drafts.Ended);
        PlanDrafts.Revise(results, drafts.Ended!.Value.Draft, 1, drafts.Ended.Value.How);
        Assert.Equal("opticli delete 123 (moves it to the recycle bin)", results[0].Undo);

        // A discard of another version, or a publish of nothing the run saved, ends nothing.
        drafts.After(Set, 2, Output(460), null);
        drafts.After(new DiscardOperation("123", 300), 3, Output(300) with { Discarded = true, Saved = false }, null);
        Assert.Null(drafts.Ended);
    }

    [Fact]
    public void The_agents_into_version_reaches_the_output_only_when_true()
    {
        var result = new WriteResult
        {
            Content = new ContentSummary { Ref = "123_456", Id = 123, Version = 456, Guid = Guid.Empty, Name = "Name", Language = "en", Status = "checkedOut" },
            Saved = true,
            IntoVersion = true,
        };

        Assert.True(WriteOutput.From(result).IntoVersion);
        Assert.Null(WriteOutput.From(result with { IntoVersion = false }).IntoVersion);
    }
}
