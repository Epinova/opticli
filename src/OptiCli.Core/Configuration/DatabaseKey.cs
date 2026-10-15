using OptiCli.Core.Safety;

namespace OptiCli.Core.Configuration;

/// <summary>
/// A server and database as roles are kept: the values SqlClient connects to (<see cref="ConnectionSafety.Check"/>),
/// normalised so that the spellings of one target are one key. Roles belong to the target, not to the entry or the
/// setting that names it, so a second entry for a production database is production too.
/// </summary>
/// <remarks>
/// Case, a protocol prefix (<c>tcp:</c>), the default port 1433, a trailing dot and the loopback names (<c>localhost</c>,
/// <c>127.0.0.1</c>, <c>::1</c>, <c>.</c>, <c>(local)</c>) don't make another key. A server reached by another name
/// (an IP address, an alias, a DNS name that points at it) does: that is the same limit the local check has.
/// </remarks>
public sealed record DatabaseKey(string Server, string Database)
{
    private static readonly string[] Protocols = ["tcp:", "np:", "lpc:", "admin:"];

    private static readonly string[] LoopbackNames = ["localhost", "127.0.0.1", "::1", "[::1]", ".", "(local)"];

    private const string DefaultPort = "1433";

    /// <returns>Null without a server.</returns>
    public static DatabaseKey? Of(string? server, string? database) =>
        string.IsNullOrWhiteSpace(server) ? null : new(NormalizeServer(server), database?.Trim().ToLowerInvariant() ?? "");

    public static string NormalizeServer(string server)
    {
        var value = server.Trim().ToLowerInvariant();
        foreach (var protocol in Protocols.Where(p => value.StartsWith(p, StringComparison.Ordinal)))
        {
            value = value[protocol.Length..].TrimStart();
            break;
        }
        if (value.StartsWith(@"(localdb)\", StringComparison.Ordinal))
        {
            return value;
        }

        string? port = null;
        var comma = value.IndexOf(',');
        if (comma >= 0)
        {
            port = value[(comma + 1)..].Trim();
            value = value[..comma].Trim();
        }
        string? instance = null;
        var backslash = value.IndexOf('\\');
        if (backslash >= 0)
        {
            instance = value[(backslash + 1)..].Trim();
            value = value[..backslash].Trim();
        }

        value = LoopbackNames.Contains(value) ? "localhost" : value.TrimEnd('.');
        if (instance == "mssqlserver")
        {
            // The default instance's name.
            instance = null;
        }
        if (port == DefaultPort && instance is null)
        {
            port = null;
        }
        return value + (instance is null ? "" : $"\\{instance}") + (string.IsNullOrEmpty(port) ? "" : $",{port}");
    }

    public override string ToString() => $"{Database} on {Server}";
}
