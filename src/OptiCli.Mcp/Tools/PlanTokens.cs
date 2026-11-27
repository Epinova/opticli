using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// The tokens a dry run gives and its real run takes, for write tools' plans (<see cref="PlanRunner"/>) and action
/// tools' previews (<see cref="ActionRunner"/>): a hash of what the dry run showed and when the token expires, protected
/// with the site's Data Protection keys under a purpose of the tool's own.
/// </summary>
internal static class PlanTokens
{
    /// <summary>How long a dry run's token works.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <returns>The token for <paramref name="hash"/>, and when it expires, in whole seconds as the token carries it.</returns>
    public static (string Token, DateTimeOffset Expires) Issue(IDataProtector protector, TimeProvider time, string hash)
    {
        var expires = DateTimeOffset.FromUnixTimeSeconds(time.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds());
        return (protector.Protect(FormattableString.Invariant($"{expires.ToUnixTimeSeconds()}.{hash}")), expires);
    }

    /// <returns>The hash and expiry <paramref name="token"/> carries; null for a token this protector didn't give.</returns>
    public static (string Hash, DateTimeOffset Expires)? Read(IDataProtector protector, string token)
    {
        string payload;
        try
        {
            payload = protector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null;
        }
        var dot = payload.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && long.TryParse(payload.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? (payload[(dot + 1)..], DateTimeOffset.FromUnixTimeSeconds(seconds))
            : null;
    }
}
