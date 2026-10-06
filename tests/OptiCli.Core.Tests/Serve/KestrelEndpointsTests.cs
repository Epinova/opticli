using OptiCli.Core.Safety;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class KestrelEndpointsTests : IDisposable
{
    private readonly SiteFixture _site = new();

    public void Dispose() => _site.Dispose();

    [Fact]
    public void Endpoints_come_from_appsettings_user_secrets_and_the_environment()
    {
        _site.Write($"{SiteFixture.ProjectDirectory}/appsettings.json", """{"Kestrel": {"Endpoints": {"Http": {"Url": "http://localhost:5000"}}}}""");
        _site.Write($"{SiteFixture.ProjectDirectory}/appsettings.Development.json", """
            // comments are allowed
            {"Kestrel": {"Endpoints": {"https": {"Url": "https://localhost:5001"}}, "Limits": {"MaxRequestBodySize": 1}}}
            """);
        _site.WriteSecrets("""{"Kestrel:Endpoints:FromSecrets:Url": "http://localhost:5002"}""");
        var environment = _site.Environment(new Dictionary<string, string>
        {
            ["Kestrel__Endpoints__FromEnvironment__Url"] = "http://localhost:5003",
            ["ASPNETCORE_Kestrel__Endpoints__Prefixed__Url"] = "http://localhost:5004",
            // Left over from an exported `opticli env`: opticli's own.
            ["Kestrel__Endpoints__OptiCli__Url"] = "http://127.0.0.1:5199",
            ["Kestrel__Limits__MaxRequestBodySize"] = "1",
        });

        Assert.Equal(["FromEnvironment", "FromSecrets", "Http", "https", "Prefixed"], KestrelEndpoints.Configured(_site.Project(), environment));
    }

    [Fact]
    public void A_site_without_endpoints_or_with_unreadable_settings_has_none()
    {
        Assert.Empty(KestrelEndpoints.Configured(_site.Project(), _site.Environment()));

        _site.Write($"{SiteFixture.ProjectDirectory}/appsettings.json", "{ broken");
        Assert.Empty(KestrelEndpoints.Configured(_site.Project(), _site.Environment()));
    }

    [Fact]
    public void Opticli_adds_its_addresses_as_endpoints_when_the_site_has_its_own()
    {
        var plain = SiteEnvironment.Build("/a/OptiCli.Agent.dll", "token", 5199, "EPiServerDB", null).ToDictionary(v => v.Key, v => v.Value);
        var withEndpoints = SiteEnvironment.Build("/a/OptiCli.Agent.dll", "token", 5199, "EPiServerDB", null, httpsPort: 5200, kestrelEndpoints: true).ToDictionary(v => v.Key, v => v.Value);
        var profile = SiteEnvironment.Build("/a/OptiCli.Agent.dll", "token", 5199, "EPiServerDB", null, includeUrls: false, kestrelEndpoints: true).ToDictionary(v => v.Key, v => v.Value);

        Assert.DoesNotContain(plain.Keys, k => k.StartsWith("Kestrel__", StringComparison.Ordinal));
        Assert.Equal("http://127.0.0.1:5199", withEndpoints["Kestrel__Endpoints__OptiCli__Url"]);
        Assert.Equal("https://localhost:5200", withEndpoints["Kestrel__Endpoints__OptiCliHttps__Url"]);
        Assert.Equal("http://127.0.0.1:5199", withEndpoints["ASPNETCORE_URLS"].Split(';')[0]);
        // Kestrel ignores a launch profile's applicationUrl just the same.
        Assert.Equal("http://127.0.0.1:5199", profile["Kestrel__Endpoints__OptiCli__Url"]);
    }

    [Fact]
    public void Variables_opticli_owns_but_does_not_set_are_listed_for_blanking()
    {
        var local = SiteEnvironment.Build("/a/OptiCli.Agent.dll", "token", 5199, "EPiServerDB", null);
        var pinned = SiteEnvironment.Build("/a/OptiCli.Agent.dll", "token", 5199, "CmsDb", ConnectionSafety.Verify("Server=localhost;Database=ExampleDb;Integrated Security=True"));

        Assert.Equal(["OPTICLI_DB", "OPTICLI_CONNECTION_NAME", "OPTICLI_REMOTE_DB", "OPTICLI_DRIFT_FILE", "OPTICLI_SCHEDULER"], SiteEnvironment.NotSet(local));
        Assert.Equal(["OPTICLI_REMOTE_DB", "OPTICLI_DRIFT_FILE", "OPTICLI_SCHEDULER"], SiteEnvironment.NotSet(pinned));
    }
}
