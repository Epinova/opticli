using OptiCli.Core.Errors;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class StartupFailureTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private StateStore StoreWithLog(params string[] lines)
    {
        var store = new StateStore(_root.Combine("state"), _root.Combine("site"));
        Directory.CreateDirectory(store.Directory);
        File.WriteAllLines(store.LogPath, lines);
        return store;
    }

    [Fact]
    public void The_agents_refusal_is_a_safety_refusal_with_the_log_tail()
    {
        var store = StoreWithLog(
            "info: starting",
            "[opticli] Refusing to start: connection string 'EPiServerDB' is not local. Server 'db.example.com' is not a local SQL Server.",
            "Unhandled exception. System.InvalidOperationException: ...");

        var error = SiteLauncher.StartupFailure(store, "The site exited during startup (exit code 134).");

        Assert.IsType<RefusedException>(error);
        Assert.Contains("[opticli] Refusing to start: connection string 'EPiServerDB' is not local.", error.Message);
        var log = Assert.IsType<StartupLog>(error.Details);
        Assert.Equal(3, log.Lines.Count);
        Assert.Equal(store.LogPath, log.Log);
    }

    [Fact]
    public void Any_other_exit_is_unreachable_and_keeps_only_the_last_lines()
    {
        var store = StoreWithLog(Enumerable.Range(1, 100).Select(i => $"line {i}").ToArray());

        var error = SiteLauncher.StartupFailure(store, "The site exited during startup (exit code 1).");

        Assert.IsType<UnreachableException>(error);
        var log = Assert.IsType<StartupLog>(error.Details);
        Assert.Equal(SiteLauncher.FailureLogLines, log.Lines.Count);
        Assert.Equal("line 100", log.Lines[^1]);
    }

    [Fact]
    public void A_timeout_suggests_raising_it()
    {
        Assert.Contains("--timeout", SiteLauncher.StartupFailure(StoreWithLog(), "No answer.", timedOut: true).Hint);
    }

    [Fact]
    public void A_timeout_names_the_addresses_a_site_listens_on_instead()
    {
        var elsewhere = StoreWithLog(
            "info: Microsoft.Hosting.Lifetime[14]",
            "      Now listening on: http://localhost:5000",
            "info: Microsoft.Hosting.Lifetime[14]",
            "      Now listening on: https://localhost:5001");
        var error = SiteLauncher.StartupFailure(elsewhere, "No answer.", timedOut: true, port: 5199);

        Assert.Contains("listens on http://localhost:5000, https://localhost:5001 instead of http://127.0.0.1:5199", error.Message);
        Assert.Contains("UseUrls", error.Hint);

        var ours = StoreWithLog("      Now listening on: http://127.0.0.1:5199", "      Now listening on: http://localhost:5000");
        Assert.Empty(SiteLauncher.ListeningElsewhere(ours.LogPath, 5199));
        Assert.Contains("--timeout", SiteLauncher.StartupFailure(ours, "No answer.", timedOut: true, port: 5199).Hint);
    }
}
