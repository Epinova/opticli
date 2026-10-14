using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;

namespace OptiCli.Core.Data;

/// <summary>The identity a refused login used, as far as opticli knows it; the token itself is never kept in it.</summary>
/// <param name="Name">The UPN (or user name) of a user, the login name for SQL authentication.</param>
/// <param name="ObjectId">Microsoft Entra object id.</param>
/// <param name="AppId">Microsoft Entra application (client) id, for a service principal or managed identity.</param>
/// <param name="TenantId">Microsoft Entra tenant id.</param>
public sealed record LoginIdentity(string? Name, string? ObjectId = null, string? AppId = null, string? TenantId = null)
{
    /// <summary>The claims of a Microsoft Entra access token; null when it can't be read.</summary>
    internal static LoginIdentity? FromToken(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            string? Claim(string name) =>
                claims.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                    ? text
                    : null;
            var isApp = Claim("idtyp") == "app";
            var identity = new LoginIdentity(
                isApp ? null : Claim("upn") ?? Claim("preferred_username") ?? Claim("unique_name") ?? Claim("email"),
                Claim("oid"),
                isApp || Claim("upn") is null ? Claim("appid") ?? Claim("azp") : null,
                Claim("tid"));
            return identity is { Name: null, ObjectId: null, AppId: null } ? null : identity;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public override string ToString()
    {
        var ids = new[] { ObjectId is null ? null : $"object id {ObjectId}", AppId is null ? null : $"application id {AppId}" }.OfType<string>().ToList();
        return (Name, ids.Count) switch
        {
            (not null, 0) => Name,
            (not null, _) => $"{Name} ({string.Join(", ", ids)})",
            _ => string.Join(", ", ids),
        };
    }
}

/// <summary>
/// Tells a login the server refused (it was reached) from a server that can't be reached, and says who was refused:
/// for Microsoft Entra authentication the server reports only "&lt;token-identified principal&gt;".
/// </summary>
internal static class LoginFailure
{
    /// <summary>18456 = login failed; 4060 = the login can't open the database (it doesn't exist, or no access).</summary>
    public static bool Matches(SqlException ex) => ex.Number is 18456 or 4060;

    public static LoginFailedException Create(SqlException ex, VerifiedConnectionString connection, SqlConnectionStringBuilder builder, string? accessToken) =>
        Create(ex.Number, ex.Message, connection.Server, connection.Database, connection.IsLocal, builder.Authentication, builder.UserID, accessToken, ex);

    internal static LoginFailedException Create(
        int number,
        string serverMessage,
        string server,
        string? database,
        bool isLocal,
        SqlAuthenticationMethod authentication,
        string? userId,
        string? accessToken,
        Exception? inner = null)
    {
        var entra = IsEntra(authentication);
        // A token means the identity signed in to Microsoft Entra and the server took it; without one, the server
        // turned the login down before asking for one (a SQL Server that doesn't take Microsoft Entra logins).
        var signedIn = entra && accessToken is not null;
        var identity = (signedIn ? LoginIdentity.FromToken(accessToken!) : null) ?? (string.IsNullOrEmpty(userId) ? null : new LoginIdentity(userId));
        var who = (entra, identity) switch
        {
            (true, not null) => $"the Microsoft Entra identity {identity}",
            (true, null) => signedIn ? "the Microsoft Entra identity opticli signed in with" : "the Microsoft Entra login",
            (false, not null) => $"the login '{identity.Name}'",
            _ => isLocal ? "the Windows/integrated login" : "the integrated login",
        };
        var db = database is null ? "the default database" : $"database '{database}'";
        var other = authentication == SqlAuthenticationMethod.ActiveDirectoryDefault
            ? "Active Directory Default takes the first credential it finds (environment variables, managed identity, Visual Studio, `az login`)."
            : "change the identity in the connection string.";

        var message = $"'{server}' was reached but refused {who} for {db}: {serverMessage}";
        var hint = (number, entra, signedIn) switch
        {
            (4060, _, _) => $"Check that {db} exists on '{server}' (`opticli doctor` shows which connection string was used); if it does, {who} has no access to it.",
            (_, true, true) => $"The identity signed in, but has no user in {db}. A database admin can add one (`CREATE USER [{identity?.Name ?? "<name>"}] FROM EXTERNAL PROVIDER`, with the rights the site's own login has), or connect as an identity that has one: {other}",
            (_, true, false) => $"opticli got no Microsoft Entra token for this login, so the server may not take Microsoft Entra logins (a SQL Server outside Azure needs SQL or Windows authentication). Otherwise the identity has no user in {db}: {other}",
            _ => $"Check the user name and password in the connection string, and that the login exists, is enabled and has a user in {db} (`opticli doctor` shows which connection string was used).",
        };
        return new LoginFailedException(message, hint, inner) { Details = identity is null ? null : new { identity } };
    }

    private static bool IsEntra(SqlAuthenticationMethod authentication) =>
        authentication is not (SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword);
}

/// <summary>
/// Wraps SqlClient's Microsoft Entra provider to keep the last access token it handed out, so a refused login can name
/// the identity. Process-wide, which is fine in the CLI: it opens one connection at a time.
/// </summary>
internal sealed class EntraTokenCapture : SqlAuthenticationProvider
{
    private static readonly object Gate = new();
    private static string? s_lastToken;
    private readonly SqlAuthenticationProvider _inner;

    private EntraTokenCapture(SqlAuthenticationProvider inner) => _inner = inner;

    /// <summary>Makes sure the provider for <paramref name="authentication"/> is wrapped.</summary>
    /// <remarks>
    /// The last token isn't forgotten here: SqlClient caches a token per connection pool and reuses it for a later
    /// connection without asking the provider again, and the CLI uses one connection string per run.
    /// </remarks>
    public static void Prepare(SqlAuthenticationMethod authentication)
    {
        if (authentication is SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword)
        {
            return;
        }
        lock (Gate)
        {
            if (GetProvider(authentication) is { } current and not EntraTokenCapture)
            {
                SetProvider(authentication, new EntraTokenCapture(current));
            }
        }
    }

    /// <summary>The last token handed out in this process, if any.</summary>
    public static string? LastToken => Volatile.Read(ref s_lastToken);

    public override bool IsSupported(SqlAuthenticationMethod authenticationMethod) => _inner.IsSupported(authenticationMethod);

    public override async Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters)
    {
        var token = await _inner.AcquireTokenAsync(parameters);
        Volatile.Write(ref s_lastToken, token.AccessToken);
        return token;
    }
}
