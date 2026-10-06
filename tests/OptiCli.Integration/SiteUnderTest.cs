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
    private SiteUnderTest(ContentSession session, AgentClient agent, string projectDirectory)
    {
        Session = session;
        Agent = agent;
        ProjectDirectory = projectDirectory;
    }

    public ContentSession Session { get; }

    public string ProjectDirectory { get; }

    public AgentClient Agent { get; }

    /// <summary>
    /// Whether a version's "saved by" changed in SQL reaches the agent, which the tests use to make "someone else's"
    /// draft. On CMS 13 it doesn't: the CMS keeps the content's version list cached after the agent's own save (the
    /// cache is only cleared by the CMS's own changes), so those tests stop where they would change it.
    /// </summary>
    public bool SeesSavedByChangedInSql => Session.Model.Schema.Major < 13;

    public static async Task<SiteUnderTest> ConnectAsync(CancellationToken cancellationToken)
    {
        var environment = OptiCliEnvironment.FromProcess();
        var project = ProjectLocator.Locate(SiteSettings.ProjectDirectory, environment.CurrentDirectory);
        var connection = ConnectionResolver.Resolve(new ConnectionRequest(), project, environment).Require();

        var agent = await AgentProbe.ConnectAsync(StateStore.For(environment, project.Directory), connection, cancellationToken);
        var db = await CmsDatabase.OpenAsync(connection, cancellationToken);
        try
        {
            return new SiteUnderTest(await ContentSession.OpenAsync(db, cancellationToken), agent, project.Directory);
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
