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
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            // NotSupportedException: keywords SqlClient knows but doesn't support here, e.g. Network Library (on any OS).
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

    /// <summary>The start of the message when SqlClient can't use a string on this machine (<see cref="Unusable"/>).</summary>
    public const string UnusableHere = "This connection string can't be used here:";

    /// <summary>What to do about a server address SqlClient can't use on this OS (a named pipe away from Windows).</summary>
    public const string ProtocolHint = "Give the server as a TCP address in the connection string: Server=<host>,<port> (or tcp:<host>,<port>), e.g. Server=localhost,1433.";

    /// <summary>What to do about a string <see cref="Unusable"/> refuses.</summary>
    public const string UnusableHint =
        "SQL Server Express LocalDB, which the CMS templates' connection string uses, only exists on Windows. Point the site's connection string (`opticli doctor` shows which file it comes from) at a SQL Server database by name instead, e.g. SQL Server in a container: \"Server=localhost,1433;Database=MySite;User Id=sa;Password=...;TrustServerCertificate=True\", or pass --connection.";

    /// <summary>The token SqlClient replaces with the application's data directory in <c>AttachDbFilename</c>.</summary>
    public const string DataDirectory = "|DataDirectory|";

    /// <summary>
    /// Why SqlClient can't use a string on this machine although it names a local server: LocalDB away from Windows, where
    /// it doesn't exist (the CMS templates' development connection string uses it). Null when it can.
    /// </summary>
    public static string? Unusable(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return !OperatingSystem.IsWindows() && IsLocalDb(builder.DataSource)
            ? $"{UnusableHere} SQL Server Express LocalDB ('{builder.DataSource.Trim()}') only runs on Windows."
            : null;
    }

    /// <summary><c>(localdb)\instance</c>, with or without <c>tcp:</c>.</summary>
    public static bool IsLocalDb(string? dataSource)
    {
        var value = dataSource?.Trim() ?? "";
        if (value.StartsWith(TcpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[TcpPrefix.Length..].TrimStart();
        }
        return value.StartsWith(LocalDbPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The string with <c>|DataDirectory|</c> in <c>AttachDbFilename</c> replaced by <paramref name="dataDirectory"/>, as
    /// SqlClient expands it in the site, whose startup sets the data directory to its <c>App_Data</c> (the CMS templates
    /// do). opticli's own process has no such setting: SqlClient would expand it against opticli's folder. The file must
    /// stay inside the folder, as SqlClient requires. Unchanged without the token.
    /// </summary>
    /// <param name="dataDirectory">The site's data directory; null when it isn't known (no project).</param>
    /// <exception cref="RefusedException">The string has the token but there is no data directory, or the file leaves it.</exception>
    public static string ResolveDataDirectory(string connectionString, string? dataDirectory)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var file = builder.AttachDBFilename;
        if (!file.StartsWith(DataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new RefusedException(
                $"{UnusableHere} it attaches '{file}', and without the site's project opticli doesn't know its {DataDirectory}.",
                "Run opticli in the site's repository (or pass --project), or give the database file's full path.");
        }
        var root = Path.GetFullPath(dataDirectory);
        var relative = file[DataDirectory.Length..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new RefusedException($"{UnusableHere} its AttachDbFilename '{file}' leaves the data directory {root}.");
        }
        builder.AttachDBFilename = full;
        return builder.ConnectionString;
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
    /// <param name="dataDirectory">The site's data directory, for <c>|DataDirectory|</c> (<see cref="ResolveDataDirectory"/>).</param>
    internal static VerifiedConnectionString Approve(string? connectionString, string? dataDirectory = null)
    {
        var verdict = Check(connectionString);
        if (!verdict.IsValid || string.IsNullOrWhiteSpace(verdict.Server))
        {
            throw new RefusedException($"Refusing to use this connection string: {verdict.Reason ?? "it names no server."}");
        }
        return Create(connectionString!, verdict, dataDirectory);
    }

    private static VerifiedConnectionString Create(string connectionString, SafetyVerdict verdict, string? dataDirectory = null)
    {
        // Re-serialising through the builder collapses duplicates and synonyms, so the string we
        // later connect with is exactly the one whose server was checked.
        var normalized = new SqlConnectionStringBuilder(connectionString).ConnectionString;
        // The site keeps the string as configured and expands |DataDirectory| itself; opticli connects with it expanded.
        var resolved = Unusable(normalized) is null ? ResolveDataDirectory(normalized, dataDirectory) : normalized;
        return new VerifiedConnectionString(resolved, verdict.Server!, verdict.Database, verdict.IsLocal) { ForSite = normalized };
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
