using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

public class MasterLanguageRuleTests
{
    [Fact]
    public void A_publish_before_the_master_is_published_is_refused_before_anything_is_saved()
    {
        var refused = Assert.Throws<ContentValidationException>(() =>
            MasterLanguageRule.Check(123, "123 ('News')", "en", "no", new SetOperation("123", Lang: "en", Publish: true), deferred: false, dryRun: true));

        Assert.Equal("Dry run: the 'en' branch of 123 ('News') can't be published before its master language ('no') is published; the CMS refuses that. Nothing was saved.", refused.Message);
        Assert.Equal("Publish the master branch first (opticli publish 123), or save this branch as a draft (leave out --publish).", refused.Hint);
        Assert.Equal(5, ExitCodes.For(refused.Code));
        Assert.Equal(AgentErrorReasons.MasterNotPublished, Assert.IsType<AgentErrorDetails>(refused.Details).Reason);

        var publish = Assert.Throws<ContentValidationException>(() =>
            MasterLanguageRule.Check(123, "123 ('News')", "en", "no", new PublishOperation("123", Lang: "en"), deferred: false, dryRun: false));
        Assert.StartsWith("The 'en' branch of 123", publish.Message);
        Assert.Equal("Publish the master branch first (opticli publish 123), then this branch.", publish.Hint);
    }

    [Fact]
    public void A_scheduled_publish_or_review_request_only_warns_since_the_cms_saves_it()
    {
        var at = new DateTimeOffset(2030, 1, 1, 8, 0, 0, TimeSpan.Zero);

        Assert.Contains("this scheduled publish fails when it comes due",
            MasterLanguageRule.Check(123, "123 ('News')", "en", "no", new PublishOperation("123", Lang: "en") { PublishAt = at }, deferred: true, dryRun: true));
        Assert.Contains("can't be published once it is approved, unless the master branch is published first (opticli publish 123)",
            MasterLanguageRule.Check(123, "123 ('News')", "en", "no", new SetOperation("123", Lang: "en") { RequestApproval = true }, deferred: true, dryRun: false));
    }

    [Fact]
    public void The_cms_refusing_it_itself_gets_the_same_advice_instead_of_dry_run()
    {
        var error = new AgentError(AgentErrorCodes.Validation, "Validation failed: PageLanguageBranch: ...")
        {
            Validation = [new ValidationIssue("PageLanguageBranch", "...")],
            Reason = AgentErrorReasons.MasterNotPublished,
        };

        var refused = AgentErrors.ToException(error);

        Assert.IsType<ContentValidationException>(refused);
        Assert.Equal(MasterLanguageRule.Hint, refused.Hint);
        Assert.Equal(AgentErrorReasons.MasterNotPublished, Assert.IsType<AgentErrorDetails>(refused.Details).Reason);
        Assert.Contains("--dry-run", AgentErrors.ToException(error with { Reason = null }).Hint);
    }
}
