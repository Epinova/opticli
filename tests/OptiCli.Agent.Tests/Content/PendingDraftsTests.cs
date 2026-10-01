using System.Text.Json;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

public class PendingDraftsTests
{
    private static readonly DateTime Monday = new(2025, 1, 27, 9, 0, 0, DateTimeKind.Utc);

    private static VersionStamp Version(int id, string? savedBy, bool published = false) => new(id, published, Monday.AddHours(id), savedBy);

    private static PendingDraft Draft(string? savedBy = "editor@example.com") => new("123_12", savedBy, Monday,
        [new PropertyChange("Heading", JsonSerializer.SerializeToElement("Old"), JsonSerializer.SerializeToElement("New"))]);

    [Fact]
    public void A_draft_by_another_user_after_the_published_version_is_pending()
    {
        VersionStamp[] versions = [Version(10, "editor@example.com", published: true), Version(11, "editor@example.com"), Version(12, "opticli")];

        // opticli's own draft on top of it still carries the editor's change.
        Assert.Equal(11, PendingDrafts.NewestByOthers(versions, baseVersion: 12)?.Id);
        Assert.Equal(11, PendingDrafts.NewestByOthers(versions, baseVersion: 11)?.Id);
    }

    [Fact]
    public void Drafts_saved_by_opticli_are_not()
    {
        VersionStamp[] versions = [Version(10, "editor@example.com", published: true), Version(11, "opticli"), Version(12, "OptiCli ")];

        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 12));
    }

    [Fact]
    public void Versions_before_the_published_one_or_after_the_base_do_not_count()
    {
        VersionStamp[] versions = [Version(8, "editor@example.com"), Version(10, "editor@example.com", published: true), Version(11, "opticli"), Version(13, "editor@example.com")];

        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 11));
        // A change based on the published version itself publishes nothing else.
        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 10));
        Assert.Equal(13, PendingDrafts.NewestByOthers(versions, baseVersion: 13)?.Id);
    }

    [Fact]
    public void Without_a_published_version_every_version_counts()
    {
        Assert.Equal(1, PendingDrafts.NewestByOthers([Version(1, "editor@example.com"), Version(2, "opticli")], baseVersion: 2)?.Id);
        Assert.Null(PendingDrafts.NewestByOthers([Version(1, "opticli"), Version(2, "opticli")], baseVersion: 2));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_save_without_a_user_name_is_someone_elses(string? savedBy)
    {
        Assert.Equal(11, PendingDrafts.NewestByOthers([Version(10, "opticli", published: true), Version(11, savedBy)], baseVersion: 11)?.Id);
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
