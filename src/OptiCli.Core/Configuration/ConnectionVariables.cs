namespace OptiCli.Core.Configuration;

/// <summary>
/// The environment variables ASP.NET Core's environment configuration provider reads as <c>ConnectionStrings:&lt;Name&gt;</c>:
/// <c>ConnectionStrings__&lt;Name&gt;</c> or <c>ConnectionStrings:&lt;Name&gt;</c> in any case, and the Azure App Service
/// prefixes (<c>SQLCONNSTR_&lt;Name&gt;</c> and the like).
/// </summary>
public static class ConnectionVariables
{
    private static readonly string[] AzurePrefixes = ["SQLCONNSTR_", "SQLAZURECONNSTR_", "MYSQLCONNSTR_", "CUSTOMCONNSTR_"];

    /// <summary>The form opticli sets.</summary>
    public static string Name(string connectionName) => $"ConnectionStrings__{connectionName}";

    /// <summary>True when the site would read <paramref name="variable"/> as connection string <paramref name="connectionName"/>.</summary>
    public static bool Sets(string variable, string connectionName)
    {
        if (string.Equals(variable.Replace("__", ":", StringComparison.Ordinal), $"ConnectionStrings:{connectionName}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return AzurePrefixes.Any(prefix => variable.Length == prefix.Length + connectionName.Length
            && variable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && variable.EndsWith(connectionName, StringComparison.OrdinalIgnoreCase));
    }
}
