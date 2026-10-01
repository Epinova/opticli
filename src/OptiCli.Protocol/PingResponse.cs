namespace OptiCli.Protocol;

/// <summary>Response of <see cref="AgentRoutes.Ping"/>.</summary>
public sealed record PingResponse
{
    public required int Protocol { get; init; }

    public required string AgentVersion { get; init; }

    /// <summary>Version of the site's EPiServer.CMS.Core, e.g. <c>12.23.1</c>.</summary>
    public required string CmsVersion { get; init; }

    /// <summary>.NET runtime the site runs on.</summary>
    public required string Runtime { get; init; }

    public required string Environment { get; init; }

    /// <summary>Principal every write is saved as (<see cref="AgentProtocol.PrincipalName"/>).</summary>
    public required string Principal { get; init; }

    /// <summary>The database the CMS actually uses. The CLI stops the site if this is not the one it pinned.</summary>
    public required DatabaseTarget Database { get; init; }

    /// <summary>
    /// The site's own process id. On Windows <c>serve</c> starts the site through <c>cmd.exe</c>, so this is the process
    /// to stop. Null from agents older than this field.
    /// </summary>
    public int? ProcessId { get; init; }
}

/// <summary>Response of <see cref="AgentRoutes.Shutdown"/>.</summary>
/// <param name="Stopping">The site has been asked to stop; it exits once running requests finish.</param>
public sealed record ShutdownResponse(bool Stopping);

/// <summary>Server and database only; credentials never leave the site.</summary>
/// <param name="ConnectionName">Connection string name the CMS resolves, normally <c>EPiServerDB</c>.</param>
/// <param name="Local">Passed the agent's local-only check.</param>
/// <param name="Pinned">Equal to <see cref="AgentProtocol.DatabaseVariable"/>, i.e. nothing in the site overrode the pin.</param>
public sealed record DatabaseTarget(string ConnectionName, string? Server, string? Name, bool Local, bool Pinned);
