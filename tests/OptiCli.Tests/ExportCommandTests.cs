namespace OptiCli.Tests;

/// <summary><c>export</c>'s own usage rules, which it checks before it reads anything.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class ExportCommandTests
{
    [Theory]
    [InlineData("export", "Give refs, --site <name> or --all")]
    [InlineData("export 1 --all", "Give refs, --site <name> or --all")]
    [InlineData("export --site Alloy --all", "Give refs, --site <name> or --all")]
    [InlineData("export --all --descendants", "--descendants is for a ref")]
    [InlineData("export 1 --languages sv --all-languages", "not both")]
    [InlineData("export 1 --no-media --blobs .", "--no-media reads no files")]
    [InlineData("export 1 --blobs http://account.blob.core.windows.net/media?sig=secret", "https://")]
    [InlineData("export 1 --blobs ./no-such-blob-folder", "doesn't exist")]
    public async Task What_export_is_given_is_checked_first(string line, string message)
    {
        var run = await Opticli.RunAsync([.. line.Split(' '), "--json"]);

        Assert.True(run.ExitCode == 1, run.ToString());
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
        Assert.Contains(message, run.Error().GetProperty("message").GetString());
        Assert.DoesNotContain("secret", run.Stdout);
    }
}
