namespace OptiCli.Protocol;

/// <summary>
/// What a site started from the IDE with <c>opticli ide on</c> writes, once it listens, to the file named by
/// <see cref="AgentProtocol.IdeFileProperty"/>: how the CLI reaches its agent. User-only, because <see cref="Token"/>
/// grants write access; removed again when the site shuts down cleanly.
/// </summary>
/// <param name="ProcessId">The site's process.</param>
/// <param name="ProcessStartTime">When it started, so a reused pid is never mistaken for the site.</param>
/// <param name="Token">The token the agent made for this run.</param>
/// <param name="Addresses">The addresses the site listens on, as Kestrel reports them (<c>https://localhost:5001</c>, <c>http://[::]:80</c>).</param>
/// <param name="DatabaseServer">The server of the database the CMS uses, as the agent checked it.</param>
/// <param name="DatabaseName">That database's name.</param>
public sealed record IdeAnnouncement(
    int Protocol,
    string AgentVersion,
    int ProcessId,
    DateTimeOffset ProcessStartTime,
    string Token,
    IReadOnlyList<string> Addresses,
    DateTimeOffset StartedAt,
    string? DatabaseServer,
    string? DatabaseName);
