using System.Security.Cryptography;
using OptiCli.Core.Configuration;
using OptiCli.Core.Safety;
using OptiCli.Protocol;

namespace OptiCli.Core.Serve;

/// <summary>The environment variables that make a site load the agent, pinned to one database.</summary>
public static class SiteEnvironment
{
    public const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";

    /// <summary>A fresh random token for one run of the site (256 bits, URL-safe).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <param name="connection">Pinned into the site; null leaves the site's own configuration in charge (<c>env</c> without the connection).</param>
    /// <param name="existingStartupHooks">The caller's own <c>DOTNET_STARTUP_HOOKS</c>, kept ahead of the agent.</param>
    /// <param name="includeUrls">False for launch profiles, whose applicationUrl sets the URLs instead.</param>
    /// <param name="approvedRemote">The remote development database the site may use (the agent refuses other remote ones); null for a local one.</param>
    /// <param name="httpsPort">Also listen on <c>https://localhost:&lt;port&gt;</c> (with the development certificate), for browsing a site that redirects to HTTPS.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        string agentDll,
        string token,
        int port,
        string connectionName,
        VerifiedConnectionString? connection,
        string? existingStartupHooks = null,
        bool includeUrls = true,
        VerifiedConnectionString? approvedRemote = null,
        int? httpsPort = null)
    {
        var variables = new List<KeyValuePair<string, string>>
        {
            new("ASPNETCORE_ENVIRONMENT", "Development"),
        };
        if (includeUrls)
        {
            variables.Add(new("ASPNETCORE_URLS", httpsPort is { } secure ? $"{Url(port)};{HttpsUrl(secure)}" : Url(port)));
            if (httpsPort is { } redirectPort)
            {
                // Where UseHttpsRedirection sends browsers; the agent answers on plain HTTP ahead of it.
                variables.Add(new("ASPNETCORE_HTTPS_PORT", redirectPort.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        variables.Add(new(StartupHooksVariable, StartupHooks(existingStartupHooks, agentDll)));
        variables.Add(new(AgentProtocol.TokenVariable, token));
        if (approvedRemote is { IsLocal: false })
        {
            variables.Add(new(AgentProtocol.RemoteDatabaseVariable, AgentProtocol.FormatRemote(approvedRemote.Server, approvedRemote.Database)));
        }
        if (connection is not null)
        {
            variables.Add(new(AgentProtocol.DatabaseVariable, connection.Value));
            if (!string.Equals(connectionName, AgentProtocol.DefaultConnectionName, StringComparison.Ordinal))
            {
                variables.Add(new(AgentProtocol.ConnectionNameVariable, connectionName));
            }
            // For sites that read the connection before hosting startups run (the agent's pin can't reach those), and over
            // one exported in the shell, which the site would otherwise read ahead of user secrets.
            variables.Add(new(ConnectionVariables.Name(connectionName), connection.Value));
        }
        return variables;
    }

    /// <summary>
    /// The inherited variables, other than <c>ConnectionStrings__&lt;Name&gt;</c> itself, that the site would also read as
    /// connection string <paramref name="connectionName"/> (<c>ConnectionStrings:&lt;Name&gt;</c>, another case, an Azure
    /// prefix). ASP.NET Core reads them in no set order, so one could beat the pinned value: <c>serve</c> removes them.
    /// </summary>
    public static IReadOnlyList<string> CompetingConnectionVariables(string connectionName, IEnumerable<string> inherited) =>
        inherited
            .Where(name => ConnectionVariables.Sets(name, connectionName) && !string.Equals(name, ConnectionVariables.Name(connectionName), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The caller's hooks, then the agent. Any other OptiCli.Agent.dll (left over from an <c>opticli env</c> exported in
    /// this shell) is dropped: a second copy from another path can't load, and the site would fail to start.
    /// </summary>
    internal static string StartupHooks(string? existing, string agentDll)
    {
        var hooks = (existing ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(hook => !string.Equals(Path.GetFileName(hook), Path.GetFileName(agentDll), StringComparison.OrdinalIgnoreCase))
            .Append(agentDll);
        return string.Join(Path.PathSeparator, hooks);
    }

    public static string Url(int port) => $"http://127.0.0.1:{port}";

    /// <summary><c>localhost</c>, the name the ASP.NET Core development certificate is issued for.</summary>
    public static string HttpsUrl(int port) => $"https://localhost:{port}";
}
