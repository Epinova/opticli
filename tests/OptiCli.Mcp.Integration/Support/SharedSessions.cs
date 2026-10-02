namespace OptiCli.Mcp.Integration.Support;

/// <summary>Every test class: one site, one shared set of signed-in editors, run one test at a time.</summary>
[CollectionDefinition(Name)]
public sealed class McpSiteCollection : ICollectionFixture<SharedSessions>
{
    public const string Name = "MCP test site";
}

/// <summary>
/// One signed-in session per test user, for the tests that only need a connected editor: a sign-in per test would be
/// slow, and the module rate-limits registration, authorization and token requests (the test site raises the limits,
/// setup.sh). Tests about signing in and connections make their own sessions. At the end each shared connection is
/// revoked.
/// </summary>
public sealed class SharedSessions : IAsyncLifetime
{
    private readonly Dictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);

    internal async Task<McpSession> ForAsync(string user)
    {
        if (!_sessions.TryGetValue(user, out var session))
        {
            session = await McpSession.ConnectAsync(user);
            _sessions[user] = session;
        }
        return session;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
    }
}
