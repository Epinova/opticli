namespace OptiCli.Tests;

/// <summary>
/// A CMS project in a temporary folder, with the opticli user config and state there too (<c>APPDATA</c> and
/// <c>LOCALAPPDATA</c>, which opticli prefers on every OS), and no connection string inherited from the shell.
/// </summary>
internal sealed class RolesSite : IDisposable
{
    /// <summary>The development database the project's appsettings.json names (never connected to).</summary>
    public const string DevDatabase = "alloy-edge-roles-dev";

    private static readonly string[] Isolated = ["APPDATA", "LOCALAPPDATA", "OPTICLI_DB", "ConnectionStrings__EPiServerDB", "ConnectionStrings:EPiServerDB"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "opticli-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previous = [];

    public RolesSite()
    {
        foreach (var name in Isolated)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(_root, "appdata"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(_root, "localappdata"));
        WriteProjectFile("Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="EPiServer.CMS.AspNetCore" Version="12.0.0" /></ItemGroup>
            </Project>
            """);
        WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "Server=localhost,1433;Database={{DevDatabase}};User Id=sa;Password=hunter2-dev" } }""");
    }

    public string ProjectDirectory => Path.Combine(_root, "site");

    public string ProjectFile => Path.Combine(_root, "site", "Web.csproj");

    public string ConfigFile => Path.Combine(_root, "appdata", "opticli", "config.json");

    public void WriteProjectFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, "site", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public Task<Run> RunAsync(params string[] args) => Opticli.RunAsync([.. args, "--project", ProjectFile, "--json"]);

    /// <summary>Sets a variable until disposed.</summary>
    public IDisposable Variable(string name, string value)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new Restore(() => Environment.SetEnvironmentVariable(name, previous));
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
