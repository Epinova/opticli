using System.Text.Json;
using OptiCli.Core.Safety;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Serve;

public class EnvironmentTests
{
    private const string Connection = "Server=localhost,1433;Database=ExampleDb;User Id=sa;Password=secret;TrustServerCertificate=True";

    private static Dictionary<string, string> Build(VerifiedConnectionString? connection, string name = "EPiServerDB", string? hooks = null, bool urls = true) =>
        SiteEnvironment.Build("/tools/agent/OptiCli.Agent.dll", "token", 5199, name, connection, hooks, urls).ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void Pins_the_verified_connection_and_injects_the_agent()
    {
        var variables = Build(ConnectionSafety.Verify(Connection));

        Assert.Equal("Development", variables["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("http://127.0.0.1:5199", variables["ASPNETCORE_URLS"]);
        Assert.Equal("/tools/agent/OptiCli.Agent.dll", variables[SiteEnvironment.StartupHooksVariable]);
        Assert.Equal("token", variables[AgentProtocol.TokenVariable]);
        Assert.Equal("ExampleDb", ConnectionSafety.Check(variables[AgentProtocol.DatabaseVariable]).Database);
        Assert.Equal(variables[AgentProtocol.DatabaseVariable], variables["ConnectionStrings__EPiServerDB"]);
        Assert.False(variables.ContainsKey(AgentProtocol.ConnectionNameVariable));
    }

    [Fact]
    public void A_custom_connection_name_is_passed_on_and_existing_hooks_are_kept()
    {
        var variables = Build(ConnectionSafety.Verify(Connection), name: "CmsDb", hooks: "/other/Hook.dll");

        Assert.Equal("CmsDb", variables[AgentProtocol.ConnectionNameVariable]);
        Assert.True(variables.ContainsKey("ConnectionStrings__CmsDb"));
        Assert.Equal($"/other/Hook.dll{Path.PathSeparator}/tools/agent/OptiCli.Agent.dll", variables[SiteEnvironment.StartupHooksVariable]);
    }

    [Fact]
    public void Other_spellings_of_the_pinned_connection_string_are_removed_from_the_inherited_environment()
    {
        string[] inherited =
        [
            "ConnectionStrings__EPiServerDB", "ConnectionStrings:EPiServerDB", "connectionstrings__episerverdb",
            "SQLCONNSTR_EPiServerDB", "ConnectionStrings__Other", "OPTICLI_DB", "PATH",
        ];

        Assert.Equal(
            ["ConnectionStrings:EPiServerDB", "SQLCONNSTR_EPiServerDB", "connectionstrings__episerverdb"],
            SiteEnvironment.CompetingConnectionVariables("EPiServerDB", inherited));
        Assert.Empty(SiteEnvironment.CompetingConnectionVariables("CmsDb", ["ConnectionStrings__EPiServerDB", "ConnectionStrings__CmsDb"]));
    }

    [Fact]
    public void A_stale_agent_hook_from_an_exported_env_is_replaced()
    {
        var stale = string.Join(Path.PathSeparator, "/old/tool/agent/OptiCli.Agent.dll", "/other/Hook.dll");

        Assert.Equal($"/other/Hook.dll{Path.PathSeparator}/tools/agent/OptiCli.Agent.dll", SiteEnvironment.StartupHooks(stale, "/tools/agent/OptiCli.Agent.dll"));
    }

    [Fact]
    public void Without_a_connection_nothing_secret_but_the_token_is_included()
    {
        var variables = Build(null, urls: false);

        Assert.False(variables.ContainsKey(AgentProtocol.DatabaseVariable));
        Assert.DoesNotContain(variables.Keys, k => k.StartsWith("ConnectionStrings", StringComparison.Ordinal));
        Assert.False(variables.ContainsKey("ASPNETCORE_URLS"));
    }

    [Fact]
    public void A_remote_development_database_is_approved_for_the_agent_by_server_and_name()
    {
        var remote = ConnectionSafety.Approve("Server=tcp:dev.example.net,1433;Database=ExampleDb;User Id=app;Password=secret");

        var variables = SiteEnvironment.Build("/agent.dll", "token", 5199, "EPiServerDB", remote, approvedRemote: remote).ToDictionary(v => v.Key, v => v.Value);
        var local = Build(ConnectionSafety.Verify(Connection));

        Assert.Equal("tcp:dev.example.net,1433|ExampleDb", variables[AgentProtocol.RemoteDatabaseVariable]);
        Assert.Equal(("tcp:dev.example.net,1433", "ExampleDb"), AgentProtocol.ParseRemote(variables[AgentProtocol.RemoteDatabaseVariable]));
        Assert.False(local.ContainsKey(AgentProtocol.RemoteDatabaseVariable));
    }

    [Fact]
    public void Tokens_are_random_and_url_safe()
    {
        var a = SiteEnvironment.NewToken();
        var b = SiteEnvironment.NewToken();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    private static readonly KeyValuePair<string, string>[] Variables = [new("A", "it's \"quoted\" \\ $HOME"), new("B", "plain")];

    [Fact]
    public void Shell_output_is_single_quoted_so_nothing_expands()
    {
        var text = EnvFormat.Render(EnvFormat.Shell, Variables, 5199, ["note"]);

        Assert.Equal("# note\nexport A='it'\\''s \"quoted\" \\ $HOME'\nexport B='plain'\n", text);
    }

    [Fact]
    public void Dotenv_and_powershell_escape_their_quotes()
    {
        Assert.Equal("A=\"it's \\\"quoted\\\" \\\\ $HOME\"\nB=\"plain\"\n", EnvFormat.Render(EnvFormat.Dotenv, Variables, 5199, []));
        Assert.Equal("$env:A = 'it''s \"quoted\" \\ $HOME'\n$env:B = 'plain'\n", EnvFormat.Render(EnvFormat.PowerShell, Variables, 5199, []));
    }

    [Fact]
    public void Launch_settings_output_is_a_profile_with_the_application_url()
    {
        using var document = JsonDocument.Parse(EnvFormat.Render(EnvFormat.LaunchSettings, Variables, 5200, ["ignored"]));
        var profile = document.RootElement.GetProperty("opticli");

        Assert.Equal("http://127.0.0.1:5200", profile.GetProperty("applicationUrl").GetString());
        Assert.Equal("plain", profile.GetProperty("environmentVariables").GetProperty("B").GetString());
    }
}
