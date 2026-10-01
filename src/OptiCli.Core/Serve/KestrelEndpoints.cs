using System.Text.Json;
using System.Text.RegularExpressions;
using OptiCli.Core.Configuration;
using OptiCli.Core.Discovery;

namespace OptiCli.Core.Serve;

/// <summary>
/// Endpoints a site configures under <c>Kestrel:Endpoints</c>. Kestrel then ignores <c>ASPNETCORE_URLS</c>, so the site
/// would never listen where opticli waits for it: <c>serve</c> and <c>env</c> add their address as one more endpoint.
/// </summary>
public static partial class KestrelEndpoints
{
    /// <summary>The endpoint opticli adds for its HTTP address.</summary>
    public const string Http = "OptiCli";

    /// <summary>The endpoint opticli adds for <c>--https</c>.</summary>
    public const string Https = "OptiCliHttps";

    private const string Section = "Kestrel:Endpoints:";

    /// <summary>
    /// The configured endpoint names, from <c>appsettings.json</c>, <c>appsettings.Development.json</c>, user secrets
    /// and environment variables, as the site would read them in Development. Opticli's own are left out.
    /// </summary>
    public static IReadOnlyList<string> Configured(ProjectInfo project, OptiCliEnvironment environment)
    {
        var keys = new List<string>();
        var files = new List<string>
        {
            Path.Combine(project.Directory, "appsettings.json"),
            Path.Combine(project.Directory, "appsettings.Development.json"),
        };
        if (project.Project.UserSecretsId is { } id && !id.Contains("$(", StringComparison.Ordinal))
        {
            files.Add(environment.UserSecretsFile(id));
        }
        foreach (var file in files.Where(File.Exists))
        {
            try
            {
                using var document = JsonConfigFile.Parse(file);
                keys.AddRange(JsonConfigFile.Flatten(document.RootElement).Keys);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // The site fails on it too, and says why.
            }
        }
        // Environment variables use "__" for ':', with or without the prefixes ASP.NET Core also reads.
        keys.AddRange(environment.Variables.Select(v => VariablePrefix().Replace(v.Key, "").Replace("__", ":", StringComparison.Ordinal)));
        return Names(keys);
    }

    /// <summary>The endpoint names among configuration keys (<c>Kestrel:Endpoints:&lt;Name&gt;:Url</c>).</summary>
    internal static IReadOnlyList<string> Names(IEnumerable<string> keys) =>
        keys
            .Where(key => key.StartsWith(Section, StringComparison.OrdinalIgnoreCase))
            .Select(key => key[Section.Length..].Split(':')[0])
            .Where(name => name.Length > 0 && !name.Equals(Http, StringComparison.OrdinalIgnoreCase) && !name.Equals(Https, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [GeneratedRegex("^(ASPNETCORE_|DOTNET_)", RegexOptions.IgnoreCase)]
    private static partial Regex VariablePrefix();

    /// <summary>The variable that sets the URL of endpoint <paramref name="name"/>.</summary>
    public static string UrlVariable(string name) => $"Kestrel__Endpoints__{name}__Url";

    public static string Warning(IReadOnlyList<string> configured) =>
        $"The site configures Kestrel endpoints ({string.Join(", ", configured)}), which replace ASPNETCORE_URLS: opticli adds its "
        + $"address as one more endpoint ({UrlVariable(Http)}), and the site also listens on its own.";
}
