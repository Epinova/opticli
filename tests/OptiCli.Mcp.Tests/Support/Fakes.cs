using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tests.Support;

/// <summary>The site's role store, faked: editors have the roles set here, whatever their login says.</summary>
internal sealed class FakeEditorRoles : IEditorRoles
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["editor"] = ["WebEditors"],
        ["admin"] = ["WebAdmins"],
        ["visitor"] = [],
    };

    private readonly ConcurrentDictionary<string, bool> _disabled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, the store "can't say", and the module falls back to the login's roles.</summary>
    public bool Unavailable { get; set; }

    public void Set(string user, params string[] roles) => _roles[user] = roles;

    /// <summary>Disables (or enables again) a user's account, keeping their roles.</summary>
    public void Disable(string user, bool disabled = true)
    {
        if (disabled)
        {
            _disabled[user] = true;
        }
        else
        {
            _disabled.TryRemove(user, out _);
        }
    }

    /// <summary>Deletes a user: no account, no roles.</summary>
    public void Delete(string user) => _roles.TryRemove(user, out _);

    public Task<IReadOnlyList<string>?> GetRolesAsync(string userName, CancellationToken cancellationToken) =>
        Task.FromResult(Unavailable ? null : _roles.TryGetValue(userName, out var roles) ? roles : (IReadOnlyList<string>)[]);

    /// <summary>A user the fake knows is active unless disabled; an unknown one doesn't exist.</summary>
    public Task<bool?> IsActiveAsync(string userName, CancellationToken cancellationToken) =>
        Task.FromResult(Unavailable ? null : (bool?)(_roles.ContainsKey(userName) && !_disabled.ContainsKey(userName)));

    /// <summary>One virtual role, as the CMS maps it: CmsAdmins is WebAdmins or Administrators.</summary>
    public bool IsInRole(ClaimsPrincipal user, string role) =>
        user.IsInRole(role) || (role == "CmsAdmins" && (user.IsInRole("WebAdmins") || user.IsInRole("Administrators")));
}

/// <summary>A clock the test moves.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>Captures every log entry, to check what the audit trail says (and what it never says).</summary>
internal sealed class AuditLog : ILoggerProvider
{
    public ConcurrentQueue<(string Category, string Message)> Entries { get; } = new();

    public IEnumerable<string> Audit => Entries.Where(e => e.Category == McpAudit.Category).Select(e => e.Message);

    public string All => string.Join("\n", Entries.Select(e => e.Message));

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(AuditLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            log.Entries.Enqueue((category, formatter(state, exception)));
    }
}
