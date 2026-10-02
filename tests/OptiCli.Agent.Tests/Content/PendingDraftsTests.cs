using System.Text.Json;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

public class PendingDraftsTests
{
    private static readonly DateTime Monday = new(2025, 1, 27, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The agent's saves are attributed to the opticli principal.</summary>
    private const string Agent = AgentProtocol.PrincipalName;

    private static VersionStamp Version(int id, string? savedBy, bool published = false) => new(id, published, Monday.AddHours(id), savedBy);

    private static PendingDraft Draft(string? savedBy = "editor@example.com") => new("123_12", savedBy, Monday,
        [new PropertyChange("Heading", JsonSerializer.SerializeToElement("Old"), JsonSerializer.SerializeToElement("New"))]);

    [Fact]
    public void A_draft_by_another_user_after_the_published_version_is_pending()
    {
        VersionStamp[] versions = [Version(10, "editor@example.com", published: true), Version(11, "editor@example.com"), Version(12, "opticli")];

        // opticli's own draft on top of it still carries the editor's change.
        Assert.Equal(11, PendingDrafts.NewestByOthers(versions, baseVersion: 12, Agent)?.Id);
        Assert.Equal(11, PendingDrafts.NewestByOthers(versions, baseVersion: 11, Agent)?.Id);
    }

    [Fact]
    public void Drafts_saved_by_opticli_are_not()
    {
        VersionStamp[] versions = [Version(10, "editor@example.com", published: true), Version(11, "opticli"), Version(12, "OptiCli ")];

        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 12, Agent));
    }

    [Fact]
    public void Versions_before_the_published_one_or_after_the_base_do_not_count()
    {
        VersionStamp[] versions = [Version(8, "editor@example.com"), Version(10, "editor@example.com", published: true), Version(11, "opticli"), Version(13, "editor@example.com")];

        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 11, Agent));
        // A change based on the published version itself publishes nothing else.
        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 10, Agent));
        Assert.Equal(13, PendingDrafts.NewestByOthers(versions, baseVersion: 13, Agent)?.Id);
    }

    [Fact]
    public void Every_draft_by_others_up_to_the_base_is_a_candidate_newest_first()
    {
        VersionStamp[] versions = [Version(10, "opticli", published: true), Version(11, "editor@example.com"), Version(12, "opticli"), Version(13, ""), Version(14, "editor@example.com")];

        Assert.Equal([13, 11], PendingDrafts.ByOthers(versions, baseVersion: 13, Agent).Select(v => v.Id));
        Assert.Empty(PendingDrafts.ByOthers(versions, baseVersion: 10, Agent));
    }

    private static PropertyChange Change(string property, string? before, string? after) => new(property,
        before is null ? null : JsonSerializer.SerializeToElement(before), after is null ? null : JsonSerializer.SerializeToElement(after));

    [Fact]
    public void A_base_with_the_drafts_value_for_a_property_it_changed_carries_the_draft()
    {
        // The draft changed Heading and Teaser; the base differs from it only in Teaser, which opticli changed again.
        List<PropertyChange> draft = [Change("Heading", "Old", "Theirs"), Change("Teaser", "Old", "Theirs")];

        Assert.True(PendingDrafts.Carries(draft, [Change("teaser", "Theirs", "Ours")]));
        Assert.True(PendingDrafts.Carries(draft, []));
    }

    [Fact]
    public void A_base_from_the_published_version_does_not_carry_a_later_draft()
    {
        // Based on the published version, the base has none of the draft's values; its own change is elsewhere.
        List<PropertyChange> draft = [Change("Heading", "Old", "Theirs")];

        Assert.False(PendingDrafts.Carries(draft, [Change("Heading", "Theirs", "Old"), Change("MainBody", "Text", "Ours")]));
        // A draft that changed nothing puts nothing live.
        Assert.False(PendingDrafts.Carries([], [Change("MainBody", "Text", "Ours")]));
    }

    [Fact]
    public void Without_a_published_version_every_version_counts()
    {
        Assert.Equal(1, PendingDrafts.NewestByOthers([Version(1, "editor@example.com"), Version(2, "opticli")], baseVersion: 2, Agent)?.Id);
        Assert.Null(PendingDrafts.NewestByOthers([Version(1, "opticli"), Version(2, "opticli")], baseVersion: 2, Agent));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_save_without_a_user_name_is_someone_elses(string? savedBy)
    {
        Assert.Equal(11, PendingDrafts.NewestByOthers([Version(10, "opticli", published: true), Version(11, savedBy)], baseVersion: 11, Agent)?.Id);
    }

    [Fact]
    public void For_an_editor_their_own_drafts_are_theirs_and_opticlis_are_someone_elses()
    {
        VersionStamp[] versions = [Version(10, "admin@example.com", published: true), Version(11, "Editor@Example.com "), Version(12, "opticli")];

        Assert.Equal(12, PendingDrafts.NewestByOthers(versions, baseVersion: 12, "editor@example.com")?.Id);
        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 11, "editor@example.com"));
        Assert.Equal(11, PendingDrafts.NewestByOthers(versions, baseVersion: 11, "colleague@example.com")?.Id);
    }

    [Fact]
    public void A_caller_without_a_name_owns_no_version()
    {
        Assert.Equal(11, PendingDrafts.NewestByOthers([Version(10, "", published: true), Version(11, "")], baseVersion: 11, "")?.Id);
        Assert.False(PendingDrafts.SavedBy(null, ""));
    }

    [Fact]
    public void An_unconfirmed_publish_is_a_conflict_carrying_the_draft()
    {
        var draft = Draft();

        var error = Assert.Throws<AgentException>(() => PendingDrafts.Require(draft, confirmed: false, dryRun: false, "123 ('News')", "en"));

        Assert.Equal((AgentErrorCodes.Conflict, 409), (error.Code, error.Status));
        Assert.Same(draft, error.ToError().PendingDraft);
        Assert.Equal(
            "Publishing 123 ('News') in 'en' would also put live changes saved by editor@example.com in 123_12 (2025-01-27 09:00:00Z) that aren't published yet (Heading).",
            error.Message);
        Assert.Contains("includeDraft", error.Hint);
    }

    [Fact]
    public void Confirming_or_a_dry_run_reports_the_draft_without_failing()
    {
        var draft = Draft(savedBy: "");

        Assert.Same(draft, PendingDrafts.Require(draft, confirmed: true, dryRun: false, "123", null));
        Assert.Same(draft, PendingDrafts.Require(draft, confirmed: false, dryRun: true, "123", null));
        Assert.Null(PendingDrafts.Require(null, confirmed: false, dryRun: false, "123", null));
        Assert.Contains("without a user name", draft.Describe());
    }

    [Fact]
    public void The_draft_round_trips_in_the_error_envelope()
    {
        var error = PendingDrafts.Unconfirmed("123", null, Draft()).ToError();
        var envelope = new AgentResponse<object>(false, null, new AgentMeta("agent", "0.1.0", AgentProtocol.Version), error);

        var json = JsonSerializer.Serialize(envelope, AgentJson.Options);
        var copy = JsonSerializer.Deserialize<AgentResponse<JsonElement>>(json, AgentJson.Options)!;

        Assert.Equal("123_12", copy.Error!.PendingDraft!.Version);
        Assert.Equal(Monday, copy.Error.PendingDraft.Saved);
        Assert.Equal("New", copy.Error.PendingDraft.Changes[0].After!.Value.GetString());
        Assert.Contains("\"pendingDraft\":{\"version\":\"123_12\",\"savedBy\":\"editor@example.com\",\"saved\":\"2025-01-27T09:00:00Z\"", json);
    }
}
