using OptiCli.Core;
using OptiCli.Core.Configuration;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Discovery;
using OptiCli.Core.Serve;

namespace OptiCli.Integration;

/// <summary>
/// The site the tests run against, found the way the CLI finds it: project discovery, connection string
/// resolution with the local-only check, and the running agent verified to use that same database.
/// </summary>
internal sealed class SiteUnderTest : IAsyncDisposable
{
    private SiteUnderTest(ContentSession session, AgentClient agent)
    {
        Session = session;
        Agent = agent;
    }

    public ContentSession Session { get; }

    public AgentClient Agent { get; }

    public static async Task<SiteUnderTest> ConnectAsync(CancellationToken cancellationToken)
    {
        var environment = OptiCliEnvironment.FromProcess();
        var project = ProjectLocator.Locate(SiteSettings.ProjectDirectory, environment.CurrentDirectory);
        var connection = ConnectionResolver.Resolve(new ConnectionRequest(), project, environment).Require();

        var agent = await AgentProbe.ConnectAsync(StateStore.For(environment, project.Directory), connection, cancellationToken);
        var db = await CmsDatabase.OpenAsync(connection, cancellationToken);
        try
        {
            return new SiteUnderTest(await ContentSession.OpenAsync(db, cancellationToken), agent);
        }
        catch
        {
            await db.DisposeAsync();
            agent.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Agent.Dispose();
        await Session.DisposeAsync();
    }
}
