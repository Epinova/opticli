using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

public class ApprovalRulesTests
{
    private static readonly ApprovalSequence Sequence = new("100", true, [new ApprovalStep("Legal", [new ApprovalReviewer("Lawyers", "role", ["en"])])]);

    [Fact]
    public void A_publish_under_a_sequence_is_refused_with_the_option_that_requests_approval()
    {
        var refused = Assert.Throws<RefusedException>(() => ApprovalRules.Check(Sequence, new PublishOperation("123"), "123"));

        Assert.Contains("--request-approval", refused.Hint);
        Assert.Equal(AgentErrorReasons.ApprovalSequence, Assert.IsType<AgentErrorDetails>(refused.Details).Reason);
        Assert.False(ApprovalRules.Check(Sequence, new SetOperation("123", Name: "x"), "123"));
    }

    [Fact]
    public void Request_approval_needs_a_sequence_unless_it_also_publishes()
    {
        Assert.True(ApprovalRules.Check(Sequence, new SetOperation("123", Name: "x") { RequestApproval = true }, "123"));
        Assert.False(ApprovalRules.Check(null, new SetOperation("123", Name: "x", Publish: true) { RequestApproval = true }, "123"));
        Assert.Throws<UsageException>(() => ApprovalRules.Check(null, new SetOperation("123", Name: "x") { RequestApproval = true }, "123"));
    }

    [Fact]
    public void Apply_request_approval_only_marks_the_steps_that_publish()
    {
        Assert.True(new SetOperation("123", Publish: true).WithRequestApproval().RequestApproval);
        Assert.True(new PublishOperation("123").WithRequestApproval().RequestApproval);
        Assert.False(new SetOperation("123").WithRequestApproval().RequestApproval);
        Assert.False(new MoveOperation("123", "5").WithRequestApproval().RequestApproval);
    }

    [Fact]
    public void Plans_take_request_approval_on_the_steps_that_can_publish()
    {
        var plan = WritePlan.Parse("""{"operations": [{"op": "publish", "ref": "123", "requestApproval": true}, {"op": "set", "ref": "124", "name": "x", "requestApproval": true}]}""");

        Assert.All(plan.Steps, s => Assert.True(s.Operation.RequestApproval));
        var invalid = Assert.Throws<UsageException>(() => WritePlan.Parse("""{"operations": [{"op": "delete", "ref": "123", "requestApproval": true}]}"""));
        Assert.Contains("unknown field \"requestApproval\"", invalid.Message);
    }

    [Fact]
    public void The_undo_hint_of_a_review_request_says_nothing_went_live()
    {
        var output = new WriteOutput("123", "123_12", null, "ArticlePage", "News", "en", "awaitingApproval", "5",
            Saved: true, Published: false, DryRun: false, Valid: true, BaseVersion: "123_11", Changes: [], Validation: null)
        { ApprovalRequested = true };

        Assert.Contains("sent for review", UndoHints.For(new SetOperation("123") { RequestApproval = true }, output));
    }
}
