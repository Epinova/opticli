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
    public void An_existing_world_readable_log_is_tightened_and_truncated()
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

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.LogPath));
        Assert.Equal("", File.ReadAllText(store.LogPath));
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

    [Fact]
    public void A_corrupt_state_file_is_a_usage_error_that_says_how_to_recover()
    {
        var store = Store();
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.StatePath, "{ not json");

        var error = Assert.Throws<UsageException>(() => store.Read());
        Assert.Contains("serve --stop", error.Hint);
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
