using OptiCli.Commands;
using OptiCli.Core;
using OptiCli.Core.Configuration;
using OptiCli.Core.Discovery;

namespace OptiCli.Tests;

/// <summary>The hint when <c>serve</c> refuses a remote database that isn't the development database.</summary>
public sealed class ServeRefusalTests : IDisposable
{
    private const string RemoteDb = "Server=tcp:sql.example.com,1433;Database=Shared;User Id=app;Password=hunter2";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("opticli-serve-");

    public void Dispose() => _root.Delete(recursive: true);

    private string ProjectDirectory => Path.Combine(_root.FullName, "Web");

    [Theory]
    [InlineData(ConnectionSource.Flag, "--connection")]
    [InlineData(ConnectionSource.OptiCliDb, "OPTICLI_DB")]
    public void A_string_for_this_run_says_how_to_make_it_choosable(ConnectionSource source, string location)
    {
        var resolution = source == ConnectionSource.Flag
            ? Resolve(new ConnectionRequest(Explicit: RemoteDb), project: false)
            : Resolve(new ConnectionRequest(), project: false, new() { ["OPTICLI_DB"] = RemoteDb });

        Assert.Equal(source, resolution.Chosen!.Source);
        Assert.False(resolution.IsDevelopment);
        var hint = ServeCommand.NotDevelopmentHint(resolution);
        Assert.Contains($"A connection string from {location} can't be chosen as the development database", hint, StringComparison.Ordinal);
        Assert.Contains("Properties/launchSettings.json", hint, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__EPiServerDB", hint, StringComparison.Ordinal);
        Assert.Contains("`opticli db list`", hint, StringComparison.Ordinal);
        Assert.Contains("`opticli db use <id>`", hint, StringComparison.Ordinal);
        Assert.Contains($"without {location}", hint, StringComparison.Ordinal);
        Assert.Contains($"Reads with {location} work", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hint_names_the_connection_string_in_use()
    {
        var resolution = Resolve(new ConnectionRequest(Name: "CmsDb", Explicit: RemoteDb), project: false);

        Assert.Contains("ConnectionStrings__CmsDb", ServeCommand.NotDevelopmentHint(resolution), StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_profile_not_chosen_yet_gets_its_db_use_command()
    {
        WriteProfile(RemoteDb);

        var resolution = Resolve(new ConnectionRequest(Profile: "Remote"));

        Assert.Equal(ConnectionSource.LaunchProfile, resolution.Chosen!.Source);
        Assert.False(resolution.IsDevelopment);
        var hint = ServeCommand.NotDevelopmentHint(resolution);
        Assert.Contains($"`opticli db use {resolution.Chosen.Id}`", hint, StringComparison.Ordinal);
        Assert.Contains("Reads with --db work", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("launchSettings", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Once_the_launch_profile_is_chosen_the_site_may_run_against_it()
    {
        WriteProfile(RemoteDb);
        var candidate = Assert.Single(Resolve(new ConnectionRequest()).Selectable);
        Assert.Equal(ConnectionSource.LaunchProfile, candidate.Source);
        UserConfig.SaveDatabase(Environment().UserConfigFile, ProjectDirectory, SavedDatabase.From(candidate, SavedDatabase.ViaCommand, DateTimeOffset.UtcNow));

        // `opticli serve`, `serve --profile Remote`, and the same string still passed with --connection.
        Assert.True(Resolve(new ConnectionRequest()).IsDevelopment);
        Assert.True(Resolve(new ConnectionRequest(Profile: "Remote")).IsDevelopment);
        Assert.True(Resolve(new ConnectionRequest(Explicit: RemoteDb)).IsDevelopment);
    }

    private void WriteProfile(string connectionString)
    {
        Directory.CreateDirectory(Path.Combine(ProjectDirectory, "Properties"));
        File.WriteAllText(Path.Combine(ProjectDirectory, "Web.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="EPiServer.CMS.AspNetCore" Version="12.0.0" />
              </ItemGroup>
            </Project>
            """);
        var escaped = connectionString.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(ProjectDirectory, "Properties", "launchSettings.json"), $$"""
            {
              "profiles": {
                "Remote": {
                  "commandName": "Project",
                  "environmentVariables": { "ConnectionStrings__EPiServerDB": "{{escaped}}" }
                }
              }
            }
            """);
    }

    private ConnectionResolution Resolve(ConnectionRequest request, bool project = true, Dictionary<string, string>? variables = null) =>
        ConnectionResolver.Resolve(request, project ? ProjectLocator.Locate(null, ProjectDirectory) : null, Environment(variables));

    private OptiCliEnvironment Environment(Dictionary<string, string>? variables = null)
    {
        var home = Path.Combine(_root.FullName, "home");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(ProjectDirectory);
        var all = new Dictionary<string, string>(variables ?? []) { ["HOME"] = home };
        return new OptiCliEnvironment(ProjectDirectory, all);
    }
}
