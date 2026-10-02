using OptiCli.Core.Errors;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class StateStoreTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private StateStore Store(string project = "repo/src/Web") => new(_root.Combine("state"), _root.Combine(project));

    private static ServeState State(StateStore store) => new(
        ServeMode.Background, store.ProjectDirectory, 5199, "secret-token", DateTimeOffset.Parse("2025-01-31T10:00:00Z"), store.LogPath,
        "localhost", "ExampleDb", Pid: 4242, ProcessStartTime: DateTimeOffset.Parse("2025-01-31T10:00:01Z"), OutputDll: "/x/Web.dll", AgentDll: "/y/OptiCli.Agent.dll");

    [Fact]
    public void Round_trips_the_state()
    {
        var store = Store();
        var state = State(store);

        store.Write(state);

        Assert.Equal(state, store.Read());
    }

    [Fact]
    public void Missing_state_reads_as_null_and_delete_is_idempotent()
    {
        var store = Store();

        Assert.Null(store.Read());
        store.Delete();
        store.Write(State(store));
        store.Delete();
        Assert.Null(store.Read());
    }

    [Fact]
    public void State_and_log_are_readable_by_the_user_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var store = Store();
        store.Write(State(store));
        store.PrepareLog();

        const UnixFileMode userOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(userOnly, File.GetUnixFileMode(store.StatePath));
        Assert.Equal(userOnly, File.GetUnixFileMode(store.LogPath));
        Assert.Equal(userOnly | UnixFileMode.UserExecute, File.GetUnixFileMode(store.Directory));
    }

    [Fact]
    public void An_existing_world_readable_log_is_kept_as_the_previous_one_and_tightened()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var store = Store();
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.LogPath, "old run");
        File.SetUnixFileMode(store.LogPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        store.PrepareLog();

        const UnixFileMode userOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(userOnly, File.GetUnixFileMode(store.LogPath));
        Assert.Equal("", File.ReadAllText(store.LogPath));
        Assert.Equal(userOnly, File.GetUnixFileMode(store.PreviousLogPath(1)));
        Assert.Equal("old run", File.ReadAllText(store.PreviousLogPath(1)));
    }

    [Fact]
    public void Each_run_starts_a_new_log_and_only_the_last_three_are_kept()
    {
        var store = Store();
        for (var run = 1; run <= 5; run++)
        {
            using var log = new StreamWriter(store.CreateLog());
            log.Write($"run {run}");
        }

        Assert.Equal("run 5", File.ReadAllText(store.LogPath));
        Assert.Equal("run 4", File.ReadAllText(store.PreviousLogPath(1)));
        Assert.Equal("run 3", File.ReadAllText(store.PreviousLogPath(2)));
        Assert.Equal(StateStore.LogsKept, Directory.GetFiles(store.Directory, "*.log").Length);
    }

    [Fact]
    public async Task The_start_lock_is_exclusive_until_released()
    {
        var store = Store();
        var first = await store.LockAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        var error = await Assert.ThrowsAsync<UsageException>(() => store.LockAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.Contains("still starting", error.Message);

        var waiting = store.LockAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        first.Dispose();
        (await waiting).Dispose();
    }

    [Fact]
    public void What_serve_compared_before_the_start_is_user_only_and_goes_with_the_state()
    {
        var store = Store();
        store.Write(State(store));

        var path = store.WriteStartupDrift(new OptiCli.Protocol.StartupDrift { Notes = ["n"] });

        Assert.Equal(store.DriftPath, path);
        Assert.Contains("\"notes\":[\"n\"]", File.ReadAllText(path), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        store.Delete();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_run_only_removes_the_state_file_it_wrote()
    {
        var store = Store();
        store.Write(State(store) with { Token = "other-run" });

        store.DeleteIfOwned("secret-token");
        Assert.NotNull(store.Read());

        store.DeleteIfOwned("other-run");
        Assert.Null(store.Read());
    }

    [Fact]
    public void Each_project_directory_gets_its_own_stable_files()
    {
        var web = Store("repo/src/Web");
        var again = Store("repo/src/Web/");
        var other = Store("other/src/Web");

        Assert.Equal(web.StatePath, again.StatePath);
        Assert.NotEqual(web.StatePath, other.StatePath);
        Assert.StartsWith("Web-", Path.GetFileName(web.StatePath));
        Assert.Equal(Path.ChangeExtension(web.StatePath, ".log"), web.LogPath);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void A_corrupt_state_file_is_a_usage_error_that_says_how_to_recover(string content)
    {
        var store = Store();
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.StatePath, content);

        var error = Assert.Throws<CorruptStateException>(() => store.Read());
        Assert.Equal(ErrorCode.Usage, error.Code);
        Assert.Contains("serve --stop", error.Hint);
    }

    [Fact]
    public async Task A_corrupt_state_file_probes_as_stale_and_is_left_for_the_caller()
    {
        var store = Store();
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.StatePath, "{ not json");

        var status = await AgentProbe.ProbeAsync(store, null, CancellationToken.None);

        Assert.Equal(AgentState.Stale, status.State);
        Assert.True(status.CorruptState);
        Assert.Contains("corrupt", status.Message);
        Assert.True(File.Exists(store.StatePath));
        await Assert.ThrowsAsync<CorruptStateException>(() => SiteLauncher.StopAsync(store));
    }

    [Fact]
    public void The_state_directory_follows_platform_conventions()
    {
        var unix = new OptiCliEnvironment(_root.Path, new Dictionary<string, string> { ["HOME"] = "/home/me" });
        var xdg = new OptiCliEnvironment(_root.Path, new Dictionary<string, string> { ["HOME"] = "/home/me", ["XDG_STATE_HOME"] = "/state" });
        var windows = new OptiCliEnvironment(_root.Path, new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\me", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local" });

        Assert.Equal(Path.Combine("/home/me", ".local", "state", "opticli"), unix.StateDirectory);
        Assert.Equal(Path.Combine("/state", "opticli"), xdg.StateDirectory);
        Assert.Equal(Path.Combine(@"C:\Users\me\AppData\Local", "opticli"), windows.StateDirectory);
    }
}
