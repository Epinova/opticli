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
    private SiteUnderTest(ContentSession session, AgentClient agent, string projectDirectory, Uri? siteUrl)
    {
        Session = session;
        Agent = agent;
        ProjectDirectory = projectDirectory;
        SiteUrl = siteUrl;
    }

    public ContentSession Session { get; }

    public string ProjectDirectory { get; }

    public AgentClient Agent { get; }

    /// <summary>Where `opticli serve` runs the site (http on loopback), to request pages as a visitor; null if unknown.</summary>
    public Uri? SiteUrl { get; }

    /// <summary>The user <see cref="HandOverAsync"/> makes a draft's: not opticli, whose drafts the agent counts as its own.</summary>
    public const string SomeoneElse = "someone-else@example.com";

    /// <summary>
    /// Makes a version <see cref="SomeoneElse"/>'s draft, as if that editor had saved it (the agent only saves as opticli):
    /// through the edge-case fixture's <c>POST /opticli-fixture/hand-over</c>, which saves it again as that user, so the
    /// CMS records them and clears its cached version list. On a site without the fixture, CMS 12 gets the change in SQL
    /// (its version list isn't cached after the agent's save); CMS 13 keeps the list cached after the agent's own save
    /// until the CMS changes the content again, so a change in SQL wouldn't reach the agent there.
    /// </summary>
    /// <returns>False on a CMS 13 site without the fixture: the test then stops where it needs the other user's draft.</returns>
    public async Task<bool> HandOverAsync(string versionRef, CancellationToken cancellationToken)
    {
        if (SiteUrl is { } url)
        {
            using var http = new HttpClient { BaseAddress = url };
            using var response = await http.PostAsync($"opticli-fixture/hand-over?version={Uri.EscapeDataString(versionRef)}&user={Uri.EscapeDataString(SomeoneElse)}", null, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                return true;
            }
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException($"POST /opticli-fixture/hand-over answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
            }
        }
        if (Session.Model.Schema.Major >= 13)
        {
            return false;
        }
        var changed = await Session.Db.QueryAsync("UPDATE tblWorkContent SET ChangedByName = @user WHERE pkID = @version; SELECT @@ROWCOUNT",
            r => r.GetInt32(0), cancellationToken,
            new Microsoft.Data.SqlClient.SqlParameter("@user", SomeoneElse),
            new Microsoft.Data.SqlClient.SqlParameter("@version", int.Parse(versionRef.Split('_')[1], System.Globalization.CultureInfo.InvariantCulture)));
        return changed.Single() == 1;
    }

    public static async Task<SiteUnderTest> ConnectAsync(CancellationToken cancellationToken)
    {
        var environment = OptiCliEnvironment.FromProcess();
        var project = ProjectLocator.Locate(SiteSettings.ProjectDirectory, environment.CurrentDirectory);
        var connection = ConnectionResolver.Resolve(new ConnectionRequest(), project, environment).Require();

        var state = StateStore.For(environment, project.Directory);
        var agent = await AgentProbe.ConnectAsync(state, connection, cancellationToken);
        var db = await CmsDatabase.OpenAsync(connection, cancellationToken);
        try
        {
            return new SiteUnderTest(await ContentSession.OpenAsync(db, cancellationToken), agent, project.Directory, state.Read()?.BaseUrl);
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
