using OptiCli.Core.Configuration;

namespace OptiCli.Core.Tests.Configuration;

public class UserConfigTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_new_config_file_is_user_only_and_an_existing_one_keeps_its_mode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var file = _root.Combine("config/opticli/config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        UserConfig.Replace(file, "{}");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        const UnixFileMode shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(file, shared);
        UserConfig.Replace(file, """{"projects": {}}""");
        Assert.Equal(shared, File.GetUnixFileMode(file));
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
