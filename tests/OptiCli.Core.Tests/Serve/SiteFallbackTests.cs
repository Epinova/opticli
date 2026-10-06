using OptiCli.Core.Errors;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

/// <summary>The one rule for reads that ask the site when they can: its answer, or the real reason it gave none.</summary>
public class SiteFallbackTests
{
    private static readonly AgentClient Agent = new(new Uri("http://127.0.0.1:1/"), "token");

    private static Task<SiteFallback.Answer<string>> Ask(Func<CancellationToken, Task<AgentClient>> connect, Func<AgentClient, Task<string>> ask) =>
        SiteFallback.AskAsync(connect, ask, CancellationToken.None);

    [Fact]
    public async Task The_sites_answer_wins()
    {
        Assert.Equal(new SiteFallback.Answer<string>("answer", null), await Ask(_ => Task.FromResult(Agent), _ => Task.FromResult("answer")));
    }

    [Fact]
    public async Task Without_a_running_site_the_reason_says_so()
    {
        var answer = await Ask(_ => throw new UnreachableException("No opticli agent is running for this project; writes go through the running site."), _ => Task.FromResult("x"));

        Assert.Equal((null, SiteFallback.NotRunning), (answer.Value, answer.WhyNot));
    }

    [Fact]
    public async Task A_site_on_another_database_is_named_as_it_is()
    {
        var answer = await Ask(_ => throw new RefusedException("The running site uses another database."), _ => Task.FromResult("x"));

        Assert.Equal("the site couldn't be asked: The running site uses another database", answer.WhyNot);
    }

    [Fact]
    public async Task An_older_agent_and_an_error_from_the_site_are_told_apart()
    {
        var old = await Ask(_ => Task.FromResult(Agent), _ => throw new NotFoundException("No agent route for GET /_opticli/v1/x."));
        var failed = await Ask(_ => Task.FromResult(Agent), _ => throw new InternalException("Object reference not set."));

        Assert.Equal(SiteFallback.OutOfDate, old.WhyNot);
        Assert.Equal("the site answered with an error: Object reference not set", failed.WhyNot);
    }
}
