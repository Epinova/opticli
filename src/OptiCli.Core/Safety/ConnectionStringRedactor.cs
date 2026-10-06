using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace OptiCli.Core.Safety;

/// <summary>Masks passwords so a connection string can be shown to a person or a log.</summary>
public static partial class ConnectionStringRedactor
{
    private const string Mask = "***";

    public static string Redact(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return "";
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = Mask;
            }
            return builder.ConnectionString;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            // Unparseable: fall back to masking anything that looks like a password value.
            return PasswordPattern().Replace(connectionString, m => $"{m.Groups["key"].Value}={Mask}");
        }
    }

    [GeneratedRegex("""(?<key>password|pwd)\s*=\s*("[^"]*"|'[^']*'|[^;]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPattern();
}
