using System.Security.Cryptography;
using System.Text;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Random secrets and their hashes. Codes, refresh tokens and client secrets are stored only as SHA-256 hashes: a copy
/// of the database gives nobody a working credential. They have 256 bits of entropy, so a plain hash is enough (no
/// salt or slow hash: there is nothing to brute-force).
/// </summary>
internal static class Secrets
{
    /// <summary>32 random bytes, base64url: 43 characters, safe in URLs and form fields as they are.</summary>
    public static string New() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes.</summary>
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>Compares in constant time, so how long a comparison takes says nothing about how much matched.</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Proof Key for Code Exchange (RFC 7636), S256 only: plain would give away the verifier in the redirect.</summary>
internal static class Pkce
{
    /// <summary>A challenge is base64url SHA-256: exactly 43 characters.</summary>
    public static bool IsValidChallenge(string challenge) => challenge.Length == 43 && challenge.All(IsBase64UrlChar);

    /// <summary>
    /// Whether <paramref name="verifier"/> is 43 to 128 unreserved characters (RFC 7636 4.1) and hashes to
    /// <paramref name="challenge"/>; compared in constant time.
    /// </summary>
    public static bool Matches(string verifier, string challenge) =>
        verifier.Length is >= 43 and <= 128
        && verifier.All(c => IsBase64UrlChar(c) || c is '.' or '~')
        && Secrets.FixedTimeEquals(Challenge(verifier), challenge);

    public static string Challenge(string verifier) => Secrets.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static bool IsBase64UrlChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
}
