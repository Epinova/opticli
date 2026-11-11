using OptiCli.Core.Errors;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class LaunchProfileTests
{
    private const string Settings = """
        {
          // Comments, as Visual Studio writes them.
          "profiles": {
            "Alloy.E2E": {
              "commandName": "Project",
              "applicationUrl": "https://localhost:5001",
              "environmentVariables": {
                "ASPNETCORE_ENVIRONMENT": "Development",
                "ASPNETCORE_URLS": "https://localhost:5001",
                "ConnectionStrings__EPiServerDB": "Server=localhost;Database=alloy-e2e",
                "Search__Index": "alloy-e2e",
                "Features__Enabled": true,
                "Kestrel__Endpoints__Https__Url": "https://localhost:5001",
                "OPTICLI_DB": "x",
                "DOTNET_STARTUP_HOOKS": "/tmp/hook.dll"
              }
            },
            "Bare": { "commandName": "Project" }
          }
        }
        """;

    [Fact]
    public void Keeps_the_profiles_variables_but_those_serve_sets_itself()
    {
        using var dir = new TempDirectory();
        dir.Write(LaunchProfile.File, Settings);

        var profile = LaunchProfile.Read(dir.Path, "alloy.e2e", "EPiServerDB");

        Assert.Equal("Alloy.E2E", profile.Name);
        Assert.Equal(
            [KeyValuePair.Create("Search__Index", "alloy-e2e"), KeyValuePair.Create("Features__Enabled", "true")],
            profile.Applied);
        Assert.Equal(
            ["ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "ConnectionStrings__EPiServerDB", "Kestrel__Endpoints__Https__Url", "OPTICLI_DB", "DOTNET_STARTUP_HOOKS"],
            profile.Skipped);
        // Names only: values may be secrets.
        Assert.DoesNotContain("alloy-e2e", profile.Describe());
        Assert.Contains("Search__Index, Features__Enabled", profile.Describe());
    }

    [Fact]
    public void A_profile_without_variables_applies_none()
    {
        using var dir = new TempDirectory();
        dir.Write(LaunchProfile.File, Settings);

        var profile = LaunchProfile.Read(dir.Path, "Bare", "EPiServerDB");

        Assert.Empty(profile.Applied);
        Assert.Empty(profile.Skipped);
    }

    [Fact]
    public void An_unknown_profile_names_close_ones()
    {
        using var dir = new TempDirectory();
        dir.Write(LaunchProfile.File, Settings);

        var error = Assert.Throws<NotFoundException>(() => LaunchProfile.Read(dir.Path, "Alloy.E2", "EPiServerDB"));
        Assert.Contains("Alloy.E2E", error.Hint);
    }

    [Fact]
    public void Another_connection_name_is_not_pinned_so_it_is_applied()
    {
        using var dir = new TempDirectory();
        dir.Write(LaunchProfile.File, Settings);

        var profile = LaunchProfile.Read(dir.Path, "Alloy.E2E", "CommerceDB");

        Assert.Contains(profile.Applied, v => v.Key == "ConnectionStrings__EPiServerDB");
    }
}
