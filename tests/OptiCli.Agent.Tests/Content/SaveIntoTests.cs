using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

public class SaveIntoTests
{
    /// <summary>The agent's saves are attributed to the opticli principal.</summary>
    private const string Agent = AgentProtocol.PrincipalName;

    private static ContentVersion Version(int id, VersionStatus status = VersionStatus.CheckedOut, string? savedBy = Agent) =>
        new(new ContentReference(123, id), "News", status, new DateTime(2025, 1, 27, 9, 0, 0, DateTimeKind.Utc), savedBy, savedBy, 0, "en", true, true);

    private static bool Applies(int? into, ContentVersion latest, string self = Agent, bool startsVariation = false, bool basedElsewhere = false) =>
        SaveInto.Applies(into, latest, self, startsVariation, basedElsewhere);

    [Fact]
    public void The_callers_latest_draft_is_saved_into()
    {
        Assert.True(Applies(12, Version(12)));
        // As the pending-draft rule compares names: trimmed, any case.
        Assert.True(Applies(12, Version(12, savedBy: " OptiCli")));
        // An editor's own draft, for the MCP module.
        Assert.True(Applies(12, Version(12, savedBy: "editor@example.com"), self: "editor@example.com"));
    }

    [Fact]
    public void Without_a_version_or_with_another_than_the_latest_a_new_version_is_saved()
    {
        Assert.False(Applies(null, Version(12)));
        Assert.False(Applies(11, Version(12)));
        Assert.False(Applies(13, Version(12)));
    }

    [Theory]
    [InlineData(VersionStatus.Published)]
    [InlineData(VersionStatus.PreviouslyPublished)]
    [InlineData(VersionStatus.DelayedPublish)]
    [InlineData(VersionStatus.AwaitingApproval)]
    [InlineData(VersionStatus.Rejected)]
    [InlineData(VersionStatus.CheckedIn)]
    public void Only_a_draft_is_saved_into(VersionStatus status) => Assert.False(Applies(12, Version(12, status)));

    [Theory]
    [InlineData("editor@example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Not_a_version_someone_else_saved(string? savedBy)
    {
        Assert.False(Applies(12, Version(12, savedBy: savedBy)));
        // A caller without a name owns nothing, not even a version without one.
        Assert.False(Applies(12, Version(12, savedBy: savedBy), self: ""));
    }

    [Fact]
    public void Not_with_from_a_versioned_ref_or_for_a_variation_s_first_version()
    {
        Assert.False(Applies(12, Version(12), basedElsewhere: true));
        Assert.False(Applies(12, Version(12), startsVariation: true));
    }

    [Fact]
    public void A_draft_save_scheduled_publish_or_review_request_forces_the_current_version_and_a_publish_takes_no_flag()
    {
        Assert.Equal(SaveAction.Save | SaveAction.ForceCurrentVersion, SaveInto.Action(null));
        Assert.Equal(SaveAction.Schedule | SaveAction.ForceCurrentVersion, SaveInto.Action(SaveAction.Schedule));
        Assert.Equal(SaveAction.RequestApproval | SaveAction.ForceCurrentVersion, SaveInto.Action(SaveAction.RequestApproval));
        // Publish | ForceCurrentVersion would leave the primary draft where it was; a new version's publish moves it.
        Assert.Equal(SaveAction.Publish, SaveInto.Action(SaveAction.Publish));
        Assert.All(new SaveAction?[] { null, SaveAction.Schedule, SaveAction.RequestApproval, SaveAction.Publish },
            a => Assert.False(SaveInto.Action(a).HasFlag(SaveAction.ForceNewVersion)));
    }
}
