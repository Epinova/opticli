using System.Diagnostics;
using System.Text.Json;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Serve;

public class IdeSiteTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData("http://127.0.0.1:5444", "https://localhost:5443", "http://127.0.0.1:5444/")]
    [InlineData("https://localhost:5443", "http://localhost:5444", "http://127.0.0.1:5444/")]
    [InlineData("https://localhost:5001", null, "https://localhost:5001/")]
    [InlineData("https://[::]:443", null, "https://localhost/")]
    [InlineData("http://*:8080", null, "http://127.0.0.1:8080/")]
    [InlineData("http://+:8080", null, "http://127.0.0.1:8080/")]
    [InlineData("http://0.0.0.0:8080", null, "http://127.0.0.1:8080/")]
    [InlineData("http://[::1]:5000", null, "http://[::1]:5000/")]
    [InlineData("https://127.0.0.1:5001/", null, "https://127.0.0.1:5001/")]
    [InlineData("https://site.dev.localhost:9377/", null, "https://localhost:9377/")]
    [InlineData("http://site.LOCALHOST:8080", null, "http://127.0.0.1:8080/")]
    public void The_agent_is_reached_on_plain_http_on_this_machine_first(string first, string? second, string expected)
    {
        var addresses = second is null ? new[] { first } : new[] { first, second };

        Assert.Equal(expected, IdeSite.AgentUrl(addresses)?.ToString());
    }

    [Theory]
    [InlineData("https://site.example.local:443")]
    [InlineData("https://localhost.example.com:443")]
    [InlineData("http://192.168.1.20:5000")]
    [InlineData("http://unix:/tmp/kestrel.sock")]
    [InlineData("not an address")]
    public void Addresses_elsewhere_are_never_used(string address)
    {
        Assert.Null(IdeSite.AgentUrl([address]));
    }

    [Fact]
    public void A_running_sites_announcement_becomes_a_state_and_a_dead_ones_is_removed()
    {
        var store = new StateStore(_root.Combine("state"), _root.Combine("Web"));
        Directory.CreateDirectory(store.Directory);
        using var self = Process.GetCurrentProcess();
        var started = SiteProcess.StartTime(self)!.Value;
        Write(store, new IdeAnnouncement(1, "1.0.0", Environment.ProcessId, started, "token", ["https://localhost:5443"], DateTimeOffset.UtcNow, "localhost", "ExampleDb"));

        var state = IdeSite.Running(store);

        Assert.NotNull(state);
        Assert.Equal(ServeMode.Ide, state.Mode);
        Assert.Equal("https://localhost:5443/", state.BaseUrl.ToString());
        Assert.Equal("token", state.Token);
        Assert.Equal(("localhost", "ExampleDb"), (state.DbServer, state.DbName));

        // Same pid, another start time: the pid was reused by another process.
        Write(store, new IdeAnnouncement(1, "1.0.0", Environment.ProcessId, started.AddHours(-1), "token", ["https://localhost:5443"], DateTimeOffset.UtcNow, "localhost", "ExampleDb"));
        Assert.Null(IdeSite.Running(store));
        Assert.False(File.Exists(store.IdePath));
    }

    [Fact]
    public void An_unreadable_announcement_is_removed()
    {
        var store = new StateStore(_root.Combine("state"), _root.Combine("Web"));
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.IdePath, "{ not json");

        Assert.Null(IdeSite.Running(store));
        Assert.False(File.Exists(store.IdePath));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("site.example", false)]
    [InlineData("10.0.0.1", false)]
    public void The_development_certificate_is_only_accepted_on_loopback(string host, bool loopback)
    {
        Assert.Equal(loopback, IdeSite.IsLoopback(host));
    }

    private static void Write(StateStore store, IdeAnnouncement announcement) =>
        File.WriteAllText(store.IdePath, JsonSerializer.Serialize(announcement, AgentJson.Options));
}
