using System.CommandLine;
using OptiCli.Core;
using OptiCli.Core.Configuration;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Discovery;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;
using OptiCli.Core.Safety;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;

namespace OptiCli.Cli;

/// <summary>
/// Per-invocation state: the parsed global options plus lazily resolved project and connection,
/// so commands that don't need the database never touch it.
/// </summary>
internal sealed class CliContext(ParseResult parse, GlobalOptions options, OptiCliEnvironment environment)
{
    private (ProjectInfo? Project, OptiCliException? Error)? _project;
    private AgentClient? _agent;
    private ConnectionResolution? _connection;
    private bool _databaseUsed;

    public ParseResult Parse { get; } = parse;

    public OptiCliEnvironment Environment { get; } = environment;

    public ConnectionRequest ConnectionRequest { get; } = new(
        parse.GetValue(options.ConnectionName) ?? ConnectionRequest.DefaultName,
        parse.GetValue(options.Connection),
        parse.GetValue(options.Profile),
        parse.GetValue(options.Database));

    /// <exception cref="OptiCliException">No unique CMS project could be found.</exception>
    public ProjectInfo Project => TryGetProject(out var error) ?? throw error!;

    public ProjectInfo? TryGetProject(out OptiCliException? error)
    {
        _project ??= LocateProject();
        error = _project.Value.Error;
        return _project.Value.Project;
    }

    public ConnectionResolution ResolveConnection() =>
        _connection ??= ConnectionResolver.Resolve(ConnectionRequest, TryGetProject(out _), Environment);

    /// <summary>
    /// The connection this command works with. Marks it used, so the response carries <c>meta.database</c> and
    /// warnings when it is remote.
    /// </summary>
    public VerifiedConnectionString UseConnection()
    {
        var resolution = ResolveConnection();
        if (resolution.Chosen is null && resolution.Failure is NotFoundException && TryGetProject(out var projectError) is null)
        {
            // "No connection string" is a symptom; the missing project is the cause worth reporting.
            throw projectError!;
        }
        var connection = resolution.Require();
        _databaseUsed = true;
        return connection;
    }

    public async Task<CmsDatabase> OpenDatabaseAsync(CancellationToken cancellationToken) =>
        await CmsDatabase.OpenAsync(UseConnection(), cancellationToken);

    /// <summary>Warnings about the database used: a remote one that isn't the development database.</summary>
    public IReadOnlyList<string> DatabaseWarnings => _databaseUsed ? ResolveConnection().Warnings() : [];

    /// <summary><c>meta.database</c>: set when the database used is remote.</summary>
    public DatabaseMeta? DatabaseMeta =>
        _databaseUsed && ResolveConnection() is { Chosen: { IsLocal: false } chosen } resolution
            ? new DatabaseMeta(chosen.Server, chosen.Database, false, resolution.IsDevelopment)
            : null;

    /// <summary>Opens the database and loads the content model; disposing the session closes the connection.</summary>
    public async Task<ContentSession> OpenContentAsync(CancellationToken cancellationToken)
    {
        var db = await OpenDatabaseAsync(cancellationToken);
        try
        {
            return await ContentSession.OpenAsync(db, cancellationToken);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>Where <c>serve</c> keeps this project's state and log.</summary>
    public StateStore StateStore => StateStore.For(Environment, Project.Directory);

    /// <summary>The project's running agent, verified to use the database opticli reads; connected once per invocation.</summary>
    /// <exception cref="UnreachableException">No agent runs (exit 4).</exception>
    public async Task<AgentClient> ConnectAgentAsync(CancellationToken cancellationToken) =>
        _agent ??= await AgentProbe.ConnectAsync(StateStore, UseConnection(), cancellationToken);

    /// <summary>Runs write operations against <paramref name="session"/>'s database and the project's agent.</summary>
    public WriteExecutor Writes(ContentSession session, string? site = null, bool updateExisting = false) =>
        new(session, ConnectAgentAsync, site, TryGetProject(out _)?.Directory, updateExisting);

    private (ProjectInfo?, OptiCliException?) LocateProject()
    {
        try
        {
            return (ProjectLocator.Locate(Parse.GetValue(options.Project), Environment.CurrentDirectory), null);
        }
        catch (OptiCliException ex)
        {
            return (null, ex);
        }
    }
}
