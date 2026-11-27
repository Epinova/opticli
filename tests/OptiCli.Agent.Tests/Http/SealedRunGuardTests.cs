using OptiCli.Agent.Extensions;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Http;

/// <summary>
/// The site of a sealed apply runs reads and the writes a sealed plan has, without someone else's drafts; everything else
/// is refused where it would run, whatever the CLI sent.
/// </summary>
public class SealedRunGuardTests
{
    private static void Check(AgentEndpoint endpoint) =>
        SealedRunGuard.Check(new RouteMatch(endpoint), AgentMiddleware.Handlers[endpoint]);

    [Fact]
    public void Reads_and_the_writes_of_a_sealed_plan_pass()
    {
        foreach (var endpoint in AgentMiddleware.Handlers.Keys.Where(e => !AgentMiddleware.Handlers[e].Gated || SealedRunGuard.Writes.Contains(e)))
        {
            Check(endpoint);
        }
        Assert.All(SealedRunGuard.Writes, e => Assert.True(AgentMiddleware.Handlers[e].Gated, $"{e} is listed as a write but isn't one."));
    }

    [Theory]
    [InlineData(nameof(AgentEndpoint.Delete))]
    [InlineData(nameof(AgentEndpoint.Unpublish))]
    [InlineData(nameof(AgentEndpoint.Discard))]
    [InlineData(nameof(AgentEndpoint.Access))]
    [InlineData(nameof(AgentEndpoint.RemoveLanguage))]
    [InlineData(nameof(AgentEndpoint.SiteAdd))]
    [InlineData(nameof(AgentEndpoint.SiteHosts))]
    [InlineData(nameof(AgentEndpoint.LanguageBranches))]
    [InlineData(nameof(AgentEndpoint.JobRun))]
    [InlineData(nameof(AgentEndpoint.UserAdd))]
    [InlineData(nameof(AgentEndpoint.TypesRemove))]
    [InlineData(nameof(AgentEndpoint.ProjectCreate))]
    [InlineData(nameof(AgentEndpoint.ProjectPublish))]
    public void Every_other_write_is_refused(string name)
    {
        var refused = Assert.Throws<AgentException>(() => Check(Enum.Parse<AgentEndpoint>(name)));
        Assert.Equal((AgentErrorCodes.Refused, SealedRunGuard.Reason), (refused.Code, refused.Reason));
    }

    [Fact]
    public void An_extensions_writes_are_refused_and_its_reads_pass()
    {
        var write = new AgentMiddleware.EndpointHandler(true, _ => Task.FromResult((200, (object)"")));
        var read = AgentMiddleware.Read(_ => "");
        var refused = Assert.Throws<AgentException>(() =>
            SealedRunGuard.Check(new RouteMatch(AgentEndpoint.Extension, null, new ExtensionRoute(write)), write));
        Assert.Contains("Commerce", refused.Message);
        SealedRunGuard.Check(new RouteMatch(AgentEndpoint.Extension, null, new ExtensionRoute(read)), read);
    }

    [Fact]
    public void Someone_elses_draft_is_never_confirmed()
    {
        object[] confirmed =
        [
            new DraftRequest { IncludeDraft = true },
            new PublishRequest { IncludeDraft = true },
        ];
        foreach (var body in confirmed)
        {
            Assert.Equal(SealedRunGuard.Reason, Assert.Throws<AgentException>(() => SealedRunGuard.CheckBody(body)).Reason);
        }
        SealedRunGuard.CheckBody(new DraftRequest());
        SealedRunGuard.CheckBody(new PublishRequest());
    }
}
