using OptiCli.Core.Discovery;

namespace OptiCli.Core.Tests;

/// <summary>
/// A fake repository with a CMS web project, plus a fake home directory for user secrets and the
/// opticli user config, so configuration precedence can be tested without touching the real ones.
/// </summary>
internal sealed class SiteFixture : IDisposable
{
    public const string SecretsId = "00000000-1111-2222-3333-444444444444";

    public const string ProjectDirectory = "repo/src/Web";

    private readonly TempDirectory _root = new();

    public SiteFixture(string? userSecretsId = SecretsId)
    {
        _root.Write("repo/Example.sln", "");
        _root.Write($"{ProjectDirectory}/Web.csproj", Csproj(userSecretsId));
        Directory.CreateDirectory(_root.Combine("home"));
    }

    public string Root => _root.Path;

    public string ProjectPath => _root.Combine(ProjectDirectory);

    public string Home => _root.Combine("home");

    public static string Csproj(string? userSecretsId = null, string sdk = "Microsoft.NET.Sdk.Web", string package = "EPiServer.CMS.AspNetCore") => $"""
        <Project Sdk="{sdk}">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            {(userSecretsId is null ? "" : $"<UserSecretsId>{userSecretsId}</UserSecretsId>")}
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="{package}" Version="12.0.0" />
          </ItemGroup>
        </Project>
        """;

    public string Write(string relativePath, string content, bool withBom = false) => _root.Write(relativePath, content, withBom);

    public void WriteProjectFile(string relativePath, string content, bool withBom = false) =>
        _root.Write($"{ProjectDirectory}/{relativePath}", content, withBom);

    public void WriteSecrets(string content, bool withBom = true) =>
        _root.Write($"home/.microsoft/usersecrets/{SecretsId}/secrets.json", content, withBom);

    public void WriteUserConfig(string content) => _root.Write("home/.config/opticli/config.json", content);

    public OptiCliEnvironment Environment(IDictionary<string, string>? variables = null)
    {
        var all = new Dictionary<string, string>(variables ?? new Dictionary<string, string>()) { ["HOME"] = Home };
        return new OptiCliEnvironment(ProjectPath, all);
    }

    public ProjectInfo Project() => ProjectLocator.Locate(null, ProjectPath);

    public void Dispose() => _root.Dispose();
}
