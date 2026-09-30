namespace OptiCli.Core.Serve;

/// <summary>How the site that answers for a project was started.</summary>
public enum ServeMode
{
    /// <summary><c>opticli serve</c>, detached; opticli owns the process.</summary>
    Background,

    /// <summary><c>opticli serve --foreground</c>; the process lives as long as that command.</summary>
    Foreground,

    /// <summary><c>opticli env</c>: the user starts the site (e.g. from an IDE) with the printed variables.</summary>
    External,
}

/// <summary>
/// What <c>serve</c> and <c>env</c> record per project so later commands can find and authenticate to the
/// agent. Stored as JSON, readable by the user only, because <see cref="Token"/> grants write access.
/// </summary>
/// <param name="Pid">Site process id; null in <see cref="ServeMode.External"/> mode.</param>
/// <param name="ProcessStartTime">Start time of <see cref="Pid"/>, so a reused pid is never mistaken for the site.</param>
/// <param name="DbServer">Server of the database the site was pinned to (never the full connection string).</param>
public sealed record ServeState(
    ServeMode Mode,
    string ProjectDirectory,
    int Port,
    string Token,
    DateTimeOffset StartedAt,
    string LogPath,
    string? DbServer,
    string? DbName,
    int? Pid = null,
    DateTimeOffset? ProcessStartTime = null,
    string? OutputDll = null,
    string? AgentDll = null,
    int? HttpsPort = null)
{
    /// <summary>Where the CLI talks to the agent: always plain HTTP on the loopback address.</summary>
    public Uri BaseUrl => new($"http://127.0.0.1:{Port}");

    /// <summary>Where to open the site in a browser: the HTTPS address when it has one.</summary>
    public string BrowseUrl => HttpsPort is { } secure ? SiteEnvironment.HttpsUrl(secure) : BaseUrl.ToString().TrimEnd('/');
}
