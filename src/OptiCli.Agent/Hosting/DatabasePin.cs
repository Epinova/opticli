using EPiServer.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OptiCli.Agent.Safety;

namespace OptiCli.Agent.Hosting;

internal static class DatabasePin
{
    /// <summary>
    /// Throws (and so stops the site) unless <paramref name="connectionString"/> is local or the approved remote
    /// development database and, when pinned, is the pin.
    /// </summary>
    public static void EnsureAllowed(AgentSettings settings, string name, string? connectionString)
    {
        var verdict = ConnectionGuard.Check(connectionString);
        if (!settings.Allows(verdict))
        {
            throw new InvalidOperationException($"[opticli] Refusing to start: connection string '{name}' is not local{(settings.ApprovedRemote is null ? "" : " nor the approved development database")}. {verdict.Reason}");
        }
        if (settings.PinnedConnection is not null && !string.Equals(connectionString, settings.PinnedConnection, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[opticli] Refusing to start: something in the site overrode the pinned connection string '{name}' (now server '{verdict.Server}', database '{verdict.Database}').");
        }
        Console.Error.WriteLine(verdict.IsLocal
            ? $"[opticli] {name} verified local: server '{verdict.Server}', database '{verdict.Database}'"
            : $"[opticli] {name} verified as the approved remote development database: server '{verdict.Server}', database '{verdict.Database}'");
    }

    /// <summary>The connection the CMS's database layer will use; mirrors its DatabaseConnectionResolver.</summary>
    public static ConnectionStringOptions? Resolve(DataAccessOptions options) =>
        options.ConnectionStrings.FirstOrDefault(c => string.Equals(c.Name, options.DefaultConnectionStringName, StringComparison.OrdinalIgnoreCase))
        ?? options.ConnectionStrings.FirstOrDefault();

    /// <summary>
    /// Other SQL Server strings aren't used by the CMS core, so they don't stop the site, but a
    /// remote one is worth knowing about before site code connects to it.
    /// </summary>
    public static void WarnAboutOtherRemoteStrings(AgentSettings settings, IConfiguration configuration, string pinnedName)
    {
        foreach (var entry in configuration.GetSection("ConnectionStrings").GetChildren())
        {
            if (string.Equals(entry.Key, pinnedName, StringComparison.OrdinalIgnoreCase) || entry.Value is null)
            {
                continue;
            }
            var verdict = ConnectionGuard.Check(entry.Value);
            if (!settings.Allows(verdict) && verdict.Server is not null)
            {
                Console.Error.WriteLine($"[opticli] warning: connection string '{entry.Key}' points at non-local server '{verdict.Server}'.");
            }
        }
    }
}

/// <summary>Runs when the CMS first resolves its <see cref="DataAccessOptions"/>, after every Configure and PostConfigure.</summary>
internal sealed class DataAccessOptionsGuard(AgentSettings settings) : IValidateOptions<DataAccessOptions>
{
    public ValidateOptionsResult Validate(string? name, DataAccessOptions options)
    {
        var failures = new List<string>();
        foreach (var connection in options.ConnectionStrings)
        {
            var verdict = ConnectionGuard.Check(connection.ConnectionString);
            if (!settings.Allows(verdict))
            {
                failures.Add($"CMS connection '{connection.Name}' is not local{(settings.ApprovedRemote is null ? "" : " nor the approved development database")}. {verdict.Reason}");
            }
        }

        var used = DatabasePin.Resolve(options);
        if (settings.PinnedConnection is not null && !string.Equals(used?.ConnectionString, settings.PinnedConnection, StringComparison.Ordinal))
        {
            failures.Add($"The CMS would connect with '{used?.Name ?? "<none>"}', which is not the pinned connection string.");
        }

        if (failures.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }
        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"[opticli] Refusing to start: {failure}");
        }
        return ValidateOptionsResult.Fail(failures.Select(f => $"[opticli] {f}"));
    }
}
