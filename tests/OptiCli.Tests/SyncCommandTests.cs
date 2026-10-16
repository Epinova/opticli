namespace OptiCli.Tests;

/// <summary><c>sync pull</c>'s own usage rules, which it checks before it reads anything.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class SyncCommandTests
{
    [Theory]
    [InlineData("sync pull --from prod", "Give refs, --site <name> or --all")]
    [InlineData("sync pull --from prod 1 --all", "Give refs, --site <name> or --all")]
    [InlineData("sync pull --from prod --site Alloy --all", "Give refs, --site <name> or --all")]
    [InlineData("sync pull --all", "--from")]
    public async Task What_sync_pull_is_given_is_checked_first(string line, string message)
    {
        var run = await Opticli.RunAsync([.. line.Split(' '), "--json"]);

        Assert.True(run.ExitCode == 1, run.ToString());
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
        Assert.Contains(message, run.Error().GetProperty("message").GetString());
    }

    [Fact]
    public void Out_must_be_a_new_or_empty_folder()
    {
        var root = Directory.CreateTempSubdirectory("opticli-sync-out-").FullName;
        try
        {
            Assert.Equal(Path.Combine(root, "plan"), Commands.SyncCommand.Folder("plan", root));
            Assert.Equal(Path.Combine(root, "plan"), Commands.SyncCommand.Folder("plan", root));
            File.WriteAllText(Path.Combine(root, "plan", "plan.json"), "{}");
            Assert.Throws<Core.Errors.UsageException>(() => Commands.SyncCommand.Folder("plan", root));
            Assert.Throws<Core.Errors.UsageException>(() => Commands.SyncCommand.Folder(Path.Combine("plan", "plan.json"), root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
