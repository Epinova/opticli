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
    /// The provider derives from SqlClient's <c>SqlAuthenticationProvider</c> as the agent compiles against it. SqlClient 7
    /// moved that class to another assembly and forwards it at runtime (the agent's tests run on 7.1). Should a site's
    /// SqlClient not take it anyway, the site fetches its own token as before (said in its log) rather than not starting.
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
        catch (Exception ex)
        {
            // Never stops the site: without the provider it fetches its own token, as before.
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
        var fallback = SqlAuthenticationProvider.GetProvider(method);
        if (!SqlAuthenticationProvider.SetProvider(method, new RunTokenProvider(accessToken, expiresOn, method, fallback)))
        {
            Console.Error.WriteLine($"[opticli] sealed apply: SqlClient kept its own provider for {method}; the site fetches its own token.");
            return;
        }
        Console.Error.WriteLine($"[opticli] sealed apply: the site signs in to production with the run's Microsoft Entra token ({method}, valid until {expiresOn:u})");
    }
}

/// <summary>
/// Hands SqlClient the run's token for the one method the pinned connection uses. Once it is about to expire (a long run),
/// SqlClient's own provider fetches one, as the site would without opticli; without one, the writes stop.
/// </summary>
internal sealed class RunTokenProvider(string accessToken, DateTimeOffset expiresOn, SqlAuthenticationMethod method, SqlAuthenticationProvider? fallback) : SqlAuthenticationProvider
{
    private int _fellBack;

    public override bool IsSupported(SqlAuthenticationMethod authenticationMethod) => authenticationMethod == method;

    public override Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters)
    {
        if (expiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return Task.FromResult(new SqlAuthenticationToken(accessToken, expiresOn));
        }
        if (fallback is null)
        {
            throw new InvalidOperationException($"[opticli] The run's Microsoft Entra token for production expired at {expiresOn:u}, and SqlClient has no provider of its own to fetch another; nothing more is written. Seal again for what is left.");
        }
        if (Interlocked.Exchange(ref _fellBack, 1) == 0)
        {
            Console.Error.WriteLine($"[opticli] sealed apply: the run's Microsoft Entra token expired at {expiresOn:u}; the site fetches its own from now on.");
        }
        return fallback.AcquireTokenAsync(parameters);
    }
}
