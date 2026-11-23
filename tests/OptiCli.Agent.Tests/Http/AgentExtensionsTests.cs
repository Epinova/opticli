using System.Text.Json;
using OptiCli.Agent.Extensions;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Http;

/// <summary>The Commerce agent is loaded only into a Commerce site, and its routes reach it only there.</summary>
public class AgentExtensionsTests
{
    private const string CommerceDeps = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" },
          "libraries": {
            "Alloy/1.0.0": { "type": "project" },
            "EPiServer.CMS.Core/12.24.3": { "type": "package" },
            "EPiServer.Commerce.Core/14.48.0": { "type": "package" }
          }
        }
        """;

    private const string CmsDeps = """
        {
          "libraries": {
            "Alloy/1.0.0": { "type": "project" },
            "EPiServer.CMS.Core/12.24.3": { "type": "package" },
            "EPiServer.Commerce.Core.Fake/1.0.0": { "type": "package" }
          }
        }
        """;

    [Fact]
    public void A_Commerce_site_is_one_whose_deps_list_the_Commerce_core_package()
    {
        Assert.Equal("14.48.0", SiteDependencies.PackageVersion(JsonDocument.Parse(CommerceDeps), AgentExtensions.CommercePackage));
        Assert.Null(SiteDependencies.PackageVersion(JsonDocument.Parse(CmsDeps), AgentExtensions.CommercePackage));
        Assert.Null(SiteDependencies.PackageVersion("/no/such/site.deps.json", AgentExtensions.CommercePackage));
    }

    [Fact]
    public void A_project_of_the_same_name_is_not_the_package()
    {
        var deps = JsonDocument.Parse("""{"libraries": {"EPiServer.Commerce.Core/1.0.0": {"type": "project"}}}""");

        Assert.Null(SiteDependencies.PackageVersion(deps, AgentExtensions.CommercePackage));
    }

    [Fact]
    public void The_test_host_is_no_Commerce_site_so_nothing_is_loaded()
    {
        AgentExtensions.Load();

        Assert.Null(AgentExtensions.Commerce);
        Assert.Null(AgentExtensions.Find("commerce"));
    }

    [Fact]
    public void The_switch_turns_the_Commerce_agent_off()
    {
        var (extension, status) = AgentExtensions.LoadCommerce("15.5.0", AppContext.BaseDirectory, "OFF");

        Assert.Null(extension);
        Assert.Equal(new CommerceStatus("15.5.0", false, "OPTICLI_COMMERCE=off turned it off."), status);
    }

    [Fact]
    public void A_missing_Commerce_agent_is_reported_and_the_site_runs_on()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"opticli-no-commerce-{Guid.NewGuid():N}");

        var (extension, status) = AgentExtensions.LoadCommerce("14.48.0", folder, null);

        Assert.Null(extension);
        Assert.False(status.Agent);
        Assert.Contains("OptiCli.Agent.Commerce.dll isn't next to the agent", status.Error);
    }

    [Fact]
    public void A_Commerce_agent_that_fails_to_load_is_reported_not_thrown()
    {
        var folder = Directory.CreateTempSubdirectory("opticli-bad-commerce-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "OptiCli.Agent.Commerce.dll"), "not an assembly");

            var (extension, status) = AgentExtensions.LoadCommerce("14.48.0", folder, null);

            Assert.Null(extension);
            Assert.False(status.Agent);
            Assert.StartsWith("BadImageFormatException", status.Error);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_loaded_extension_gets_its_routes_and_others_dont_exist()
    {
        Assert.Throws<AgentException>(() => AgentRouter.Match("GET", "/v1/test-extension"));

        using (AgentExtensions.Use(new TestExtension()))
        {
            var match = AgentRouter.Match("GET", "/v1/test-extension/things/42");
            Assert.Equal(AgentEndpoint.Extension, match.Endpoint);
            Assert.Equal("42", match.Argument);
            Assert.NotNull(match.Extension);

            var missing = Assert.Throws<AgentException>(() => AgentRouter.Match("GET", "/v1/test-extension/nope"));
            Assert.Equal(AgentErrorCodes.NotFound, missing.Code);
            Assert.Contains("GET /v1/test-extension/things/{id}", missing.Hint);
            // The CMS agent's own routes are as they were.
            Assert.Equal(AgentEndpoint.Ping, AgentRouter.Match("GET", "/v1/ping").Endpoint);
        }

        Assert.Throws<AgentException>(() => AgentRouter.Match("GET", "/v1/test-extension/things/42"));
    }

    private sealed class TestExtension : AgentExtension
    {
        public override string Segment => "test-extension";

        public override string RouteList => "GET /v1/test-extension/things/{id}";

        public override ExtensionRoute? Route(string method, IReadOnlyList<string> segments) => segments switch
        {
            ["things", var id] => new ExtensionRoute(AgentMiddleware.Read(_ => new { }), id),
            _ => null,
        };
    }
}
