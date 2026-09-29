namespace OptiCli.Agent.Safety;

/// <param name="Server">The Data Source SqlClient would connect to (never includes credentials).</param>
/// <param name="Database">The Initial Catalog SqlClient would use.</param>
/// <param name="Reason">Why the string was refused; null when local.</param>
/// <param name="Failover">The Failover Partner SqlClient would switch to when the server is down; null when none.</param>
internal sealed record GuardVerdict(bool IsLocal, string? Server, string? Database, string? Reason, string? Failover = null);

/// <summary>
/// The local-only rule, same as the CLI's (<c>OptiCli.Core.Safety.ConnectionSafety</c>): the
/// server must be a loopback name or LocalDB. A name that merely resolves to 127.0.0.1 is still
/// refused, because a false refusal is cheap and a false pass is not.
/// </summary>
internal static class ConnectionGuard
{
    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "[::1]", ".", "(local)"];

    // SqlClient's synonym table (SqlConnectionOptions keyword map).
    private static readonly string[] DataSourceKeys = ["data source", "server", "address", "addr", "network address"];
    private static readonly string[] CatalogKeys = ["initial catalog", "database"];
    private static readonly string[] FailoverKeys = ["failover partner", "failoverpartner"];

    private const string LocalDbPrefix = @"(localdb)\";
    private const string TcpPrefix = "tcp:";

    public static GuardVerdict Check(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new(false, null, null, "The connection string is empty.");
        }

        IReadOnlyList<KeyValuePair<string, string?>> pairs;
        try
        {
            pairs = ConnectionStringParser.Parse(connectionString);
        }
        catch (FormatException ex)
        {
            return new(false, null, null, $"Not a valid SQL Server connection string: {ex.Message}");
        }

        var server = Last(pairs, DataSourceKeys);
        var database = Last(pairs, CatalogKeys);
        if (string.IsNullOrEmpty(database))
        {
            database = null;
        }

        // SqlClient silently switches to the failover partner when the primary is down.
        var failover = Last(pairs, FailoverKeys);
        if (string.IsNullOrWhiteSpace(failover))
        {
            failover = null;
        }

        if (!IsLocalDataSource(server))
        {
            return new(false, server, database, $"Server '{(string.IsNullOrWhiteSpace(server) ? "<none>" : server)}' is not a local SQL Server.", failover);
        }
        if (failover is not null && !IsLocalDataSource(failover))
        {
            return new(false, server, database, $"Failover partner '{failover}' is not a local SQL Server.", failover);
        }

        return new(true, server, database, null, failover);
    }

    /// <summary>
    /// True for <c>[tcp:]host[\instance][,port]</c> where host is a loopback name, and for
    /// <c>(localdb)\instance</c>. Other protocol prefixes (np:, lpc:, admin:) are refused.
    /// </summary>
    public static bool IsLocalDataSource(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return false;
        }

        var value = dataSource.Trim();
        if (value.StartsWith(TcpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[TcpPrefix.Length..].TrimStart();
        }

        if (value.StartsWith(LocalDbPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value.Length > LocalDbPrefix.Length;
        }

        var comma = value.IndexOf(',');
        if (comma >= 0)
        {
            var port = value[(comma + 1)..].Trim();
            if (port.Length == 0 || !port.All(char.IsAsciiDigit))
            {
                return false;
            }
            value = value[..comma];
        }

        var backslash = value.IndexOf('\\');
        if (backslash >= 0)
        {
            if (value[(backslash + 1)..].Trim().Length == 0)
            {
                return false;
            }
            value = value[..backslash];
        }

        return LocalHosts.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>SqlClient folds synonyms into one keyword and the last occurrence wins.</summary>
    private static string? Last(IReadOnlyList<KeyValuePair<string, string?>> pairs, string[] keys)
    {
        string? found = null;
        foreach (var (key, value) in pairs)
        {
            if (keys.Contains(key))
            {
                found = value;
            }
        }
        return found;
    }
}
