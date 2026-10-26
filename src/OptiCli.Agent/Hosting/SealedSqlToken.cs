using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace OptiCli.Agent.Hosting;

/// <summary>
/// A sealed run against a production database that signs in with Microsoft Entra ID: the site's SqlClient gets the access
/// token the run signed in with (<see cref="OptiCli.Protocol.AgentProtocol.SqlTokenVariable"/>, from the stdin line only)
/// instead of fetching its own. So the site writes as exactly the identity the run checked, and a starting site can't stall
/// on fetching a token until Azure SQL drops the login (seen on a real site: "Connection reset by peer" during login).
/// </summary>
internal static class SealedSqlToken
{
    /// <summary>Installs the provider for the pinned connection's authentication method, when the run gave a token.</summary>
    /// <param name="connectionString">The pinned connection, whose <c>Authentication</c> says which method to answer for.</param>
    /// <remarks>
    /// The provider derives from SqlClient's <c>SqlAuthenticationProvider</c> as the agent compiles against it (SqlClient up
    /// to 6, what CMS 12 and 13 bring). SqlClient 7 moved that class to another assembly: on such a site the type doesn't
    /// load, and the site fetches its own token as before (said in its log) rather than not starting.
    /// </remarks>
    public static void Install(string? accessToken, string? expires, string? connectionString)
    {
        if (accessToken is null || connectionString is null)
        {
            return;
        }
        try
        {
            InstallProvider(accessToken, expires, connectionString);
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException or MissingMethodException)
        {
            Console.Error.WriteLine($"[opticli] sealed apply: this site's SqlClient doesn't take opticli's token provider ({ex.GetType().Name}); the site fetches its own Microsoft Entra token.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallProvider(string accessToken, string? expires, string connectionString)
    {
        var method = new SqlConnectionStringBuilder(connectionString).Authentication;
        if (method is SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword)
        {
            return;
        }
        var expiresOn = DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : DateTimeOffset.UtcNow.AddMinutes(30);
        if (!SqlAuthenticationProvider.SetProvider(method, new RunTokenProvider(accessToken, expiresOn, method)))
        {
            Console.Error.WriteLine($"[opticli] sealed apply: SqlClient kept its own provider for {method}; the site fetches its own token.");
            return;
        }
        Console.Error.WriteLine($"[opticli] sealed apply: the site signs in to production with the run's Microsoft Entra token ({method}, valid until {expiresOn:u})");
    }
}

/// <summary>Hands SqlClient the run's token for the one method the pinned connection uses; refuses once it has expired.</summary>
internal sealed class RunTokenProvider(string accessToken, DateTimeOffset expiresOn, SqlAuthenticationMethod method) : SqlAuthenticationProvider
{
    public override bool IsSupported(SqlAuthenticationMethod authenticationMethod) => authenticationMethod == method;

    public override Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters) =>
        expiresOn > DateTimeOffset.UtcNow.AddMinutes(1)
            ? Task.FromResult(new SqlAuthenticationToken(accessToken, expiresOn))
            : throw new InvalidOperationException($"[opticli] The run's Microsoft Entra token for production expired at {expiresOn:u}; nothing more is written. Seal again for what is left.");
}
