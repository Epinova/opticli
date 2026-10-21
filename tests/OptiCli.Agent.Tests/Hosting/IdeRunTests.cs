using System.Text.Json;
using EPiServer.Data;
using OptiCli.Agent.Hosting;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Hosting;

[Collection(HostingCollection.Name)]
public class IdeRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "opticli-agent-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Func<string, string?> Properties(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void The_runtimeconfigs_ide_file_without_a_token_in_the_environment_is_ide_mode()
    {
        var previous = Environment.GetEnvironmentVariable(AgentProtocol.TokenVariable);
        Environment.SetEnvironmentVariable(AgentProtocol.TokenVariable, null);
        try
        {
            var settings = AgentSettings.FromEnvironment(Properties(
                (AgentProtocol.IdeFileProperty, "/state/Web.ide.json"),
                (AgentProtocol.IdeRemoteDatabaseProperty, RemoteApproval),
                (AgentProtocol.IdeConnectionNameProperty, "CmsDb")));

            Assert.True(settings.Ide);
            Assert.Equal("/state/Web.ide.json", settings.IdeFile);
            Assert.Equal(IdeRun.Token, settings.Token);
            Assert.Null(settings.PinnedConnection);
            Assert.Equal("CmsDb", settings.ConnectionName);
            Assert.True(settings.SharedDatabase);
            Assert.False(settings.SchedulerOff);
            Assert.Equal(IdeRun.Token, AgentSettings.FromEnvironment(Properties((AgentProtocol.IdeFileProperty, "/state/Web.ide.json"))).Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentProtocol.TokenVariable, previous);
        }
    }

    [Fact]
    public void A_token_from_serve_or_env_makes_the_run_theirs()
    {
        var previous = Environment.GetEnvironmentVariable(AgentProtocol.TokenVariable);
        Environment.SetEnvironmentVariable(AgentProtocol.TokenVariable, "from-serve");
        try
        {
            var settings = AgentSettings.FromEnvironment(Properties((AgentProtocol.IdeFileProperty, "/state/Web.ide.json")));

            Assert.False(settings.Ide);
            Assert.Equal("from-serve", settings.Token);
            Assert.True(settings.SchedulerOff);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentProtocol.TokenVariable, previous);
        }
    }

    private static AgentSettings Ide(string? approvedRemote = null) =>
        new(AgentProtocol.DefaultConnectionName, null, "token", AgentProtocol.ParseRemote(approvedRemote), Scheduler: true, IdeFile: "/state/Web.ide.json");

    private static DataAccessOptions Options(string connectionString)
    {
        var options = new DataAccessOptions();
        options.SetConnectionString(connectionString);
        return options;
    }

    [Fact]
    public void A_local_database_in_development_with_an_address_can_be_used()
    {
        Assert.Null(IdeRun.Problem("Development", Options(Local), Ide(), ["https://localhost:5001"], out var verdict));
        Assert.Equal("ExampleDb", verdict!.Database);
    }

    [Fact]
    public void Another_environment_a_remote_database_or_no_address_turns_the_agent_off()
    {
        Assert.Contains("only runs in Development", IdeRun.Problem("Staging", Options(Local), Ide(), ["https://localhost:5001"], out _), StringComparison.Ordinal);
        Assert.Contains("is not local", IdeRun.Problem("Development", Options(Remote), Ide(), ["https://localhost:5001"], out _), StringComparison.Ordinal);
        Assert.Null(IdeRun.Problem("Development", Options(Remote), Ide(RemoteApproval), ["https://localhost:5001"], out _));
        Assert.Contains("not local nor the approved", IdeRun.Problem("Development", Options(OtherRemote), Ide(RemoteApproval), ["https://localhost:5001"], out _), StringComparison.Ordinal);
        Assert.Contains("no address", IdeRun.Problem("Development", Options(Local), Ide(), [], out _), StringComparison.Ordinal);
    }

    [Fact]
    public void The_announcement_is_user_only_and_withdrawn_only_by_its_own_run()
    {
        var path = Path.Combine(_directory, "Web.ide.json");
        var announcement = new IdeAnnouncement(1, "1.0.0", 42, DateTimeOffset.UtcNow, "mine", ["https://localhost:5001"], DateTimeOffset.UtcNow, "localhost", "ExampleDb");

        IdeRun.Write(path, announcement);

        Assert.Equal(announcement.Token, JsonSerializer.Deserialize<IdeAnnouncement>(File.ReadAllText(path), AgentJson.Options)!.Token);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        IdeRun.Withdraw(path, "another run's");
        Assert.True(File.Exists(path));
        IdeRun.Withdraw(path, "mine");
        Assert.False(File.Exists(path));
    }
}
