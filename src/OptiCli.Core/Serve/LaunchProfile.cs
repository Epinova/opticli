using System.Text.Json;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Text;

namespace OptiCli.Core.Serve;

/// <summary>
/// A launch profile's <c>environmentVariables</c> (<c>Properties/launchSettings.json</c>), which <c>serve --profile</c> gives
/// the site as <c>dotnet run --launch-profile</c> would, except those <c>serve</c> sets itself: the address, the
/// environment, the startup hook, the pinned connection string and opticli's own.
/// </summary>
/// <param name="Applied">The variables the site gets, by name.</param>
/// <param name="Skipped">The names of the profile's variables that <c>serve</c> sets itself, which it leaves out.</param>
public sealed record LaunchProfile(string Name, IReadOnlyList<KeyValuePair<string, string>> Applied, IReadOnlyList<string> Skipped)
{
    public const string File = "Properties/launchSettings.json";

    /// <summary>
    /// Variables <c>serve</c> sets for every run: where the site listens (<c>ASPNETCORE_URLS</c> and the like, Kestrel
    /// endpoints), the environment (always Development), the startup hook and the agent's own.
    /// </summary>
    public static bool OwnedByServe(string name, string connectionName) =>
        name.Equals("ASPNETCORE_URLS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DOTNET_URLS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ASPNETCORE_HTTP_PORTS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ASPNETCORE_HTTPS_PORTS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
        || name.Equals(SiteEnvironment.StartupHooksVariable, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Kestrel__Endpoints__", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Kestrel:Endpoints:", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("OPTICLI_", StringComparison.OrdinalIgnoreCase)
        || name.Equals($"ConnectionStrings__{connectionName}", StringComparison.OrdinalIgnoreCase)
        || name.Equals($"ConnectionStrings:{connectionName}", StringComparison.OrdinalIgnoreCase);

    /// <exception cref="NotFoundException">No launch settings, or no such profile (the hint names close ones).</exception>
    /// <exception cref="UsageException">The file can't be read.</exception>
    public static LaunchProfile Read(string projectDirectory, string name, string connectionName)
    {
        var file = Path.Combine(projectDirectory, File);
        if (!System.IO.File.Exists(file))
        {
            throw new NotFoundException($"--profile '{name}' given, but {File} does not exist.");
        }
        try
        {
            using var document = JsonConfigFile.Parse(file);
            var profiles = document.RootElement.TryGetProperty("profiles", out var all) && all.ValueKind == JsonValueKind.Object
                ? all.EnumerateObject().ToList()
                : [];
            var profile = profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (profile.Value.ValueKind != JsonValueKind.Object)
            {
                throw new NotFoundException(
                    $"Launch profile '{name}' not found in {File}.",
                    Suggestions.DidYouMean(name, profiles.Select(p => p.Name)) ?? $"Profiles: {string.Join(", ", profiles.Select(p => p.Name))}.");
            }
            var applied = new List<KeyValuePair<string, string>>();
            var skipped = new List<string>();
            if (profile.Value.TryGetProperty("environmentVariables", out var variables) && variables.ValueKind == JsonValueKind.Object)
            {
                foreach (var variable in variables.EnumerateObject())
                {
                    var value = variable.Value.ValueKind switch
                    {
                        JsonValueKind.String => variable.Value.GetString() ?? "",
                        JsonValueKind.Null => "",
                        _ => variable.Value.GetRawText(),
                    };
                    if (OwnedByServe(variable.Name, connectionName))
                    {
                        skipped.Add(variable.Name);
                    }
                    else
                    {
                        applied.Add(KeyValuePair.Create(variable.Name, value));
                    }
                }
            }
            return new LaunchProfile(profile.Name, applied, skipped);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"Could not read {File}: {ex.Message}");
        }
    }

    /// <summary>What <c>serve</c> says about it: which variables the site got, by name only (values may be secrets), and which it left out.</summary>
    public string Describe() =>
        $"Applied launch profile '{Name}': {(Applied.Count == 0 ? "it sets no other variables" : string.Join(", ", Applied.Select(v => v.Key)))}"
        + (Skipped.Count == 0 ? "." : $"; left out {string.Join(", ", Skipped)}, which serve sets itself (the connection string is the database serve pins).");
}
