using System.Text.Json.Nodes;
using OptiCli.Core.Sync;

namespace OptiCli.Core.Tests.Sync;

/// <summary>
/// What a sync does with an item: from what the source and the target have and what the last sync left (the decision
/// table), and after comparing the values (<see cref="SyncDiff.Same"/>).
/// </summary>
public class SyncDiffTests
{
    private static readonly Guid Item = Guid.Parse("e351ed2f-34a5-5da4-be93-cb2fb61413ca");
    private const string Parent = "f7a3aca5-85c9-5b56-9fec-4a82895877da";
    private const string Elsewhere = "e9cefa25-6a0a-47d0-b12c-74c3f6a51c9a";

    private static SourceItem Source(int version = 10, bool published = true, string place = Parent) =>
        new(place, new Dictionary<string, (int, bool)> { ["en"] = (version, published) });

    private static TargetItem Target(int primary = 20, int latest = 20, bool published = true, bool deleted = false, string? place = Parent) =>
        new(5, Item, 7, place, deleted, new Dictionary<string, TargetBranch> { ["en"] = new(primary, published, latest) });

    private static SyncedItem Synced(int source = 10, int target = 20, bool trashed = false, string place = Parent) =>
        new(place, place, new Dictionary<string, SyncedBranch> { ["en"] = new(source, true, target) }, trashed);

    [Fact]
    public void What_the_target_lacks_is_created_and_what_is_in_its_recycle_bin_restored()
    {
        Assert.Equal(SyncStep.Create, SyncDiff.Present(Source(), null, null, false).Step);
        Assert.Equal(SyncStep.Restore, SyncDiff.Present(Source(), Target(deleted: true), null, false).Step);
        // The sync moved it there itself (the source had deleted it, and has it back now): restored too.
        Assert.Equal(SyncStep.Restore, SyncDiff.Present(Source(), Target(deleted: true), Synced(trashed: true), false).Step);
    }

    [Fact]
    public void Synced_content_trashed_or_deleted_locally_is_a_conflict_unless_overwritten()
    {
        var trashed = SyncDiff.Present(Source(), Target(deleted: true), Synced(), overwriteLocal: false);
        Assert.Equal(SyncStep.Conflict, trashed.Step);
        Assert.Contains("recycle bin", trashed.Reason, StringComparison.Ordinal);
        Assert.Equal((SyncStep.Restore, true), (SyncDiff.Present(Source(), Target(deleted: true), Synced(), overwriteLocal: true) is var restored ? (restored.Step, restored.Overwrite) : default));
        Assert.Equal(SyncStep.Conflict, SyncDiff.Present(Source(), null, Synced(), false).Step);
        Assert.Equal(SyncStep.Create, SyncDiff.Present(Source(), null, Synced(), true).Step);
    }

    [Fact]
    public void Without_a_state_everything_is_compared_and_a_waiting_draft_makes_a_difference_a_conflict()
    {
        Assert.Equal(new SyncDecision(SyncStep.Compare), SyncDiff.Present(Source(), Target(), null, false));
        var draft = SyncDiff.Present(Source(), Target(primary: 20, latest: 21), null, false);
        Assert.Equal((SyncStep.Compare, true), (draft.Step, draft.ConflictIfDifferent));
        Assert.Contains("newer draft", draft.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, 20, SyncStep.Unchanged)]
    [InlineData(10, 21, SyncStep.Kept)]
    [InlineData(11, 20, SyncStep.Compare)]
    public void With_a_state_what_changed_on_which_side_decides(int sourceVersion, int targetLatest, SyncStep step)
    {
        var decision = SyncDiff.Present(Source(sourceVersion), Target(latest: targetLatest), Synced(), false);

        Assert.Equal(step, decision.Step);
        Assert.False(decision.ConflictIfDifferent);
    }

    [Fact]
    public void Changed_on_both_sides_is_compared_and_a_conflict_only_when_the_values_differ()
    {
        var both = SyncDiff.Present(Source(11), Target(latest: 21), Synced(), false);

        Assert.Equal((SyncStep.Compare, true, false), (both.Step, both.ConflictIfDifferent, both.Overwrite));
        Assert.Equal(SyncStep.Unchanged, SyncDiff.Compared(both, different: false, moved: false).Step);
        var conflict = SyncDiff.Compared(both, different: true, moved: false);
        Assert.Equal(SyncStep.Conflict, conflict.Step);
        Assert.Contains("changed in the source, and changed in the target (en)", conflict.Reason, StringComparison.Ordinal);
        var overwritten = SyncDiff.Compared(SyncDiff.Present(Source(11), Target(latest: 21), Synced(), overwriteLocal: true), different: true, moved: false);
        Assert.Equal((SyncStep.Compare, true), (overwritten.Step, overwritten.Overwrite));
    }

    [Fact]
    public void A_move_publish_or_new_branch_in_the_source_is_a_change_too_and_a_local_move_a_local_one()
    {
        var synced = Synced();

        Assert.True(SyncDiff.SourceChanged(Source(place: Elsewhere), synced));
        Assert.True(SyncDiff.SourceChanged(Source(published: false), synced));
        Assert.True(SyncDiff.SourceChanged(new SourceItem(Parent, new Dictionary<string, (int, bool)> { ["en"] = (10, true), ["sv"] = (12, true) }), synced));
        Assert.False(SyncDiff.SourceChanged(Source(), synced));

        Assert.Equal("moved in the target", SyncDiff.TargetChanged(Target(place: Elsewhere), synced));
        Assert.Contains("restored in the target", SyncDiff.TargetChanged(Target(), Synced(trashed: true)), StringComparison.Ordinal);
        Assert.Contains("branch was removed", SyncDiff.TargetChanged(new TargetItem(5, Item, 7, Parent, false, new Dictionary<string, TargetBranch>()), synced), StringComparison.Ordinal);
        // A branch only the target has isn't the sync's.
        Assert.Null(SyncDiff.TargetChanged(new TargetItem(5, Item, 7, Parent, false, new Dictionary<string, TargetBranch> { ["en"] = new(20, true, 20), ["de"] = new(30, true, 31) }), synced));
    }

    [Fact]
    public void A_branch_made_in_the_target_that_the_source_now_writes_too_is_a_local_change()
    {
        // Synced with en only; then sv was translated in the target, and in the source as well.
        var source = new SourceItem(Parent, new Dictionary<string, (int, bool)> { ["en"] = (10, true), ["sv"] = (12, true) });
        var target = new TargetItem(5, Item, 7, Parent, false, new Dictionary<string, TargetBranch> { ["en"] = new(20, true, 20), ["sv"] = new(30, true, 30) });

        var decision = SyncDiff.Present(source, target, Synced(), false);

        Assert.Equal((SyncStep.Compare, true), (decision.Step, decision.ConflictIfDifferent));
        Assert.Contains("sv branch was made in the target", decision.Reason, StringComparison.Ordinal);
        Assert.Equal(SyncStep.Conflict, SyncDiff.Compared(decision, different: true, moved: false).Step);
        // The source's new branch that the target doesn't have is only the source's change.
        Assert.False(SyncDiff.Present(source, Target(), Synced(), false).ConflictIfDifferent);
    }

    [Theory]
    [InlineData(20, true, 20, 20)]
    [InlineData(20, true, 21, 20)]
    [InlineData(20, false, 21, 21)]
    public void A_branch_found_the_same_is_recorded_without_a_newer_local_draft(int primary, bool published, int latest, int recorded)
    {
        // The draft wasn't compared: recorded as the published version, it stays a local change for the next sync.
        Assert.Equal(recorded, SyncDiff.InStep(10, true, new TargetBranch(primary, published, latest)).Target);
        var next = SyncDiff.Present(Source(), Target(primary, latest, published), Synced(target: recorded), false);
        Assert.Equal(latest != recorded ? SyncStep.Kept : SyncStep.Unchanged, next.Step);
    }

    [Fact]
    public void A_version_with_the_synced_id_saved_at_another_time_is_a_local_change()
    {
        // The target restored from a backup taken before the sync, then edited: the edit gets the id the sync's version had.
        var saved = new DateTime(2026, 10, 16, 8, 0, 0);
        var synced = new SyncedItem(Parent, Parent, new Dictionary<string, SyncedBranch> { ["en"] = new(10, true, 20, saved) });
        TargetItem At(DateTime? when) => new(5, Item, 7, Parent, false, new Dictionary<string, TargetBranch> { ["en"] = new(20, true, 20, when) });

        Assert.Null(SyncDiff.TargetChanged(At(saved), synced));
        Assert.Equal("changed in the target (en)", SyncDiff.TargetChanged(At(saved.AddMinutes(5)), synced));
        // A state without the time (or a version without one) compares the id only.
        Assert.Null(SyncDiff.TargetChanged(At(saved.AddMinutes(5)), Synced()));
        var both = SyncDiff.Present(Source(11), At(saved.AddMinutes(5)), synced, false);
        Assert.Equal((SyncStep.Compare, true), (both.Step, both.ConflictIfDifferent));
        // Recorded with the time when found the same, without it when a newer local draft stays a local change.
        Assert.Equal(saved, SyncDiff.InStep(10, true, new TargetBranch(20, true, 20, saved)).TargetSaved);
        Assert.Null(SyncDiff.InStep(10, true, new TargetBranch(20, true, 21, saved)).TargetSaved);
    }

    [Fact]
    public void Gone_from_the_source_is_trashed_only_with_delete_and_a_local_change_makes_it_a_conflict()
    {
        Assert.Equal(SyncStep.Gone, SyncDiff.Absent(Target(), Synced(), delete: false, false).Step);
        Assert.Equal(SyncStep.Trash, SyncDiff.Absent(Target(), Synced(), delete: true, false).Step);
        Assert.Equal(SyncStep.Conflict, SyncDiff.Absent(Target(latest: 21), Synced(), delete: true, false).Step);
        Assert.Equal(SyncStep.Trash, SyncDiff.Absent(Target(latest: 21), Synced(), delete: true, overwriteLocal: true).Step);
        Assert.Equal(SyncStep.Gone, SyncDiff.Absent(Target(deleted: true), Synced(), delete: true, false).Step);
        Assert.Equal(SyncStep.Forget, SyncDiff.Absent(null, Synced(), delete: true, false).Step);
        // In the source's recycle bin, never synced: the target's content came from the source all the same.
        Assert.Equal(SyncStep.Trash, SyncDiff.Absent(Target(), null, delete: true, false).Step);
        Assert.Equal(SyncStep.Conflict, SyncDiff.Absent(Target(latest: 21), null, delete: true, false).Step);
    }

    [Fact]
    public void Places_name_a_parent_or_the_owner_of_a_for_this_page_folder()
    {
        var owner = Guid.Parse(Parent);

        Assert.Equal(Parent, SyncDiff.Place(owner, null));
        Assert.Equal($"for:{Parent}", SyncDiff.Place(Guid.Parse(Elsewhere), owner));
        Assert.Equal(owner, SyncDiff.Owner($"for:{Parent}"));
        Assert.Null(SyncDiff.Owner(Parent));
    }

    private static Dictionary<string, JsonObject> Branches(string json) =>
        JsonNode.Parse(json)!.AsObject().ToDictionary(b => b.Key, b => b.Value!.AsObject());

    [Fact]
    public void The_same_values_are_what_the_source_writes_as_the_target_has_them()
    {
        var source = Branches("""{"en": {"name": "A", "published": true, "values": {"Heading": "Hi", "Image": "<img src=\"/contentassets/0123456789abcdef0123456789abcdef/a.png\">", "Area": [{"ref": "x"}]}}}""");

        // Values only the target has (a property only its model has) and branches only it has don't count; the GUID of a
        // "For this page" folder in a URL is each database's own.
        Assert.Null(SyncDiff.Same(source, Branches("""
            {"en": {"name": "A", "published": true, "values": {"Heading": "Hi", "Image": "<img src=\"/contentassets/fedcba9876543210fedcba9876543210/a.png\">", "Area": [{"ref": "x"}], "Extra": 1}},
             "de": {"name": "B", "published": false, "values": {}}}
            """)));
        Assert.Equal("en", SyncDiff.Same(source, Branches("""{"en": {"name": "A", "published": false, "values": {"Heading": "Hi", "Image": "<img src=\"/contentassets/0123456789abcdef0123456789abcdef/a.png\">", "Area": [{"ref": "x"}]}}}""")));
        Assert.Equal("en", SyncDiff.Same(source, Branches("""{"en": {"name": "B", "published": true, "values": {"Heading": "Hi", "Image": "<img src=\"/contentassets/0123456789abcdef0123456789abcdef/a.png\">", "Area": [{"ref": "x"}]}}}""")));
        Assert.Equal("en", SyncDiff.Same(source, Branches("""{"en": {"name": "A", "published": true, "values": {"Heading": "Hi", "Area": [{"ref": "x"}]}}}""")));
        Assert.Equal("en", SyncDiff.Same(source, Branches("""{"en": {"name": "A", "published": true, "values": {"Heading": "Hi", "Image": "<img src=\"/contentassets/0123456789abcdef0123456789abcdef/a.png\">", "Area": [{"ref": "y"}]}}}""")));
        Assert.Equal("en", SyncDiff.Same(source, Branches("""{"sv": {"name": "A", "published": true, "values": {}}}""")));
    }
}
