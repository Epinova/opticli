using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OptiCli.Agent.Hosting;

namespace OptiCli.Agent.Tests.Hosting;

/// <summary>
/// The site started for a sealed apply to production serves the agent only: its own pages, edit UI and other endpoints
/// answer 404, so no other process on the machine reaches the site while it runs with the write login.
/// </summary>
[Collection(HostingCollection.Name)]
public class AgentStartupFilterTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/en/")]
    [InlineData("/EPiServer/CMS/")]
    [InlineData("/Util/Login")]
    [InlineData("/_opticlix")]
    public async Task A_sealed_run_answers_404_for_everything_but_the_agent(string path)
    {
        var (status, site, agent) = await RequestAsync(path, sealedRun: true);

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.False(site);
        Assert.False(agent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_agent_answers_either_way(bool sealedRun)
    {
        var (_, site, agent) = await RequestAsync("/_opticli/v1/ping", sealedRun);

        Assert.True(agent);
        Assert.False(site);
    }

    [Fact]
    public async Task Otherwise_the_site_serves_its_own_pages()
    {
        var (status, site, agent) = await RequestAsync("/en/", sealedRun: false);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(site);
        Assert.False(agent);
    }

    private static async Task<(int Status, bool Site, bool Agent)> RequestAsync(string path, bool sealedRun)
    {
        var site = false;
        var agent = false;
        var app = new ApplicationBuilder(new EmptyServices());
        HostingFixture.Stderr(() => AgentStartupFilter.Pipeline(
            siteApp => siteApp.Run(_ => { site = true; return Task.CompletedTask; }),
            sealedRun,
            _ => { agent = true; return Task.CompletedTask; })(app));
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        await app.Build()(context);
        return (context.Response.StatusCode, site, agent);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
