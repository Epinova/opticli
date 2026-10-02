using EPiServer.Approvals;
using EPiServer.Approvals.ContentApprovals;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

public class ApprovalsTests
{
    private static readonly ContentReference Page = new(123);

    private static ContentApprovalDefinition Sequence(int ownerId) => new()
    {
        ContentLink = new ContentReference(ownerId),
        IsEnabled = true,
        Steps = [new ApprovalDefinitionStep("Legal", [new ApprovalDefinitionReviewer("Lawyers", [], ApprovalDefinitionReviewerType.Role)])],
    };

    private static ContentVersion Version(int id, VersionStatus status) =>
        new(new ContentReference(123, id), "News", status, new DateTime(2025, 1, 27, 9, 0, 0, DateTimeKind.Utc), "editor@example.com", "editor@example.com", 0, "en", true, false);

    [Fact]
    public void Without_a_sequence_a_publish_publishes_and_a_draft_stays_a_draft()
    {
        Assert.Equal(SaveAction.Publish, Approvals.Choose(null, Page, publish: true, requestApproval: false, "123"));
        Assert.Equal(SaveAction.Publish, Approvals.Choose(null, Page, publish: true, requestApproval: true, "123"));
        Assert.Null(Approvals.Choose(Sequence(123), Page, publish: false, requestApproval: false, "123"));
    }

    [Fact]
    public void A_publish_under_a_sequence_is_refused_and_says_where_the_sequence_comes_from()
    {
        var refused = Assert.Throws<AgentException>(() => Approvals.Choose(Sequence(100), Page, publish: true, requestApproval: false, "123 ('News')"));

        Assert.Equal((AgentErrorCodes.Refused, AgentErrorReasons.ApprovalSequence), (refused.Code, refused.Reason));
        Assert.Contains("inherited from 100", refused.Message);
        Assert.Contains("Legal (role Lawyers)", refused.Message);
        Assert.Contains("requestApproval", refused.Hint);
    }

    [Fact]
    public void Request_approval_starts_the_sequence_and_is_an_error_where_there_is_none()
    {
        Assert.Equal(SaveAction.RequestApproval, Approvals.Choose(Sequence(123), Page, publish: false, requestApproval: true, "123"));
        Assert.Equal(SaveAction.RequestApproval, Approvals.Choose(Sequence(123), Page, publish: true, requestApproval: true, "123"));

        var usage = Assert.Throws<AgentException>(() => Approvals.Choose(null, Page, publish: false, requestApproval: true, "123"));
        Assert.Equal((AgentErrorCodes.Usage, AgentErrorReasons.NoApprovalSequence), (usage.Code, usage.Reason));
    }

    [Fact]
    public void Content_whose_newest_version_awaits_approval_is_in_review()
    {
        Approvals.RequireNotInReview([Version(10, VersionStatus.Published), Version(11, VersionStatus.CheckedOut)], "123", "en");
        // A review that ended (rejected) leaves the content editable.
        Approvals.RequireNotInReview([Version(10, VersionStatus.Published), Version(11, VersionStatus.Rejected)], "123", "en");

        var conflict = Assert.Throws<AgentException>(() => Approvals.RequireNotInReview([Version(10, VersionStatus.Published), Version(11, VersionStatus.AwaitingApproval)], "123", "en"));
        Assert.Equal((AgentErrorCodes.Conflict, AgentErrorReasons.InReview), (conflict.Code, conflict.Reason));
        Assert.Contains("123_11", conflict.Message);
    }

    [Fact]
    public void The_reason_reaches_the_error_envelope()
    {
        var error = new AgentException(AgentErrorCodes.Refused, "no") { Reason = AgentErrorReasons.ApprovalSequence }.ToError();

        Assert.Equal(AgentErrorReasons.ApprovalSequence, error.Reason);
    }
}
