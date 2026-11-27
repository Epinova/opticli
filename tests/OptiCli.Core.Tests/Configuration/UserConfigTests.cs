using OptiCli.Core.Configuration;

namespace OptiCli.Core.Tests.Configuration;

public class UserConfigTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void The_config_file_is_user_only_also_when_it_was_readable_by_others()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var file = _root.Combine("config/opticli/config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        UserConfig.Replace(file, "{}");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        // Made by hand, readable by everyone: it may hold a connection string with a password.
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        UserConfig.Replace(file, """{"projects": {}}""");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal("""{"projects": {}}""", File.ReadAllText(file));
    }

    [Fact]
    public void Writes_leave_no_temporary_files_behind()
    {
        var file = _root.Combine("config.json");

        for (var run = 0; run < 3; run++)
        {
            UserConfig.Replace(file, $"{{\"run\": {run}}}");
        }

        Assert.Equal([file], Directory.GetFiles(_root.Path));
        Assert.Equal("{\"run\": 2}", File.ReadAllText(file));
    }
}
