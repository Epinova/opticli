using System.Security.Cryptography;
using System.Text;

namespace OptiCli.Agent.Safety;

internal static class TokenComparer
{
    /// <summary>
    /// Constant-time comparison. Both sides are hashed first so neither the content nor the length
    /// of the expected token leaks through timing.
    /// </summary>
    public static bool Matches(string? given, string expected)
    {
        if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(expected))
        {
            return false;
        }
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(given));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
