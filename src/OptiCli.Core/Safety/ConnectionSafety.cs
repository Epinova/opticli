using Microsoft.Data.SqlClient;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Safety;

/// <summary>
/// Tells local SQL Servers (this machine) from remote ones. Local databases are always usable; a remote one only
/// when the connection resolver approves it (the user chose it as the project's development database, or picked
/// it explicitly).
/// </summary>
/// <remarks>
/// The string is parsed with <see cref="SqlConnectionStringBuilder"/> rather than by hand, so key
/// synonyms (<c>Server</c>, <c>Data Source</c>, <c>Address</c>, ...), quoting and duplicate keys
/// (last one wins) resolve exactly as they will when SqlClient connects. The check is on the literal
/// host name: a name that merely resolves to 127.0.0.1 counts as remote, because treating a local
/// server as shared is cheap and the reverse is not.
/// </remarks>
public static class ConnectionSafety
{
    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "[::1]", ".", "(local)"];

    private const string LocalDbPrefix = @"(localdb)\";
    private const string TcpPrefix = "tcp:";

    public static SafetyVerdict Check(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new SafetyVerdict(false, null, null, "The connection string is empty.", IsValid: false);
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return new SafetyVerdict(false, null, null, $"Not a valid SQL Server connection string: {ex.Message}", IsValid: false);
        }

        var server = builder.DataSource;
        var database = string.IsNullOrEmpty(builder.InitialCatalog) ? null : builder.InitialCatalog;

        if (!IsLocalDataSource(server))
        {
            var shown = string.IsNullOrWhiteSpace(server) ? "<none>" : server;
            return new SafetyVerdict(false, server, database, $"Server '{shown}' is not a local SQL Server.");
        }

        // SqlClient silently switches to the failover partner when the primary is down.
        if (!string.IsNullOrEmpty(builder.FailoverPartner) && !IsLocalDataSource(builder.FailoverPartner))
        {
            return new SafetyVerdict(false, server, database, $"Failover partner '{builder.FailoverPartner}' is not a local SQL Server.");
        }

        return new SafetyVerdict(true, server, database, null);
    }

    /// <summary>
    /// Checks that the string is local and returns the only handle <see cref="Data.CmsDatabase"/> accepts.
    /// </summary>
    /// <exception cref="RefusedException">The string is invalid or not local.</exception>
    public static VerifiedConnectionString Verify(string? connectionString)
    {
        var verdict = Check(connectionString);
        if (!verdict.IsLocal)
        {
            throw new RefusedException(
                $"Refusing to use this connection string: {verdict.Reason}",
                "Only a local SQL Server (localhost, 127.0.0.1, ::1, '.', '(local)' or '(localdb)\\<instance>') is accepted here.");
        }
        return Create(connectionString!, verdict);
    }

    /// <summary>
    /// Returns the handle for a string the connection resolver chose, local or not. Only the resolver calls this:
    /// it decides whether a remote database may be used.
    /// </summary>
    /// <exception cref="RefusedException">The string is not a valid SQL Server connection string.</exception>
    internal static VerifiedConnectionString Approve(string? connectionString)
    {
        var verdict = Check(connectionString);
        if (!verdict.IsValid || string.IsNullOrWhiteSpace(verdict.Server))
        {
            throw new RefusedException($"Refusing to use this connection string: {verdict.Reason ?? "it names no server."}");
        }
        return Create(connectionString!, verdict);
    }

    private static VerifiedConnectionString Create(string connectionString, SafetyVerdict verdict)
    {
        // Re-serialising through the builder collapses duplicates and synonyms, so the string we
        // later connect with is exactly the one whose server was checked.
        var normalized = new SqlConnectionStringBuilder(connectionString).ConnectionString;
        return new VerifiedConnectionString(normalized, verdict.Server!, verdict.Database, verdict.IsLocal);
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
}
