namespace OptiCli.Tests;

/// <summary><c>--from</c> on set and area, which is checked before opticli looks for a project.</summary>
[Collection(ConsoleCollection.Name)]
public class FromOptionTests
{
    [Theory]
    [InlineData("set", "123", "Heading=x")]
    [InlineData("area", "123", "MainArea", "add", "456")]
    public async Task A_from_that_is_no_version_is_a_usage_error(params string[] args)
    {
        var run = await Opticli.RunAsync([.. args, "--from", "yesterday", "--json"]);

        Assert.Equal(1, run.ExitCode);
        var error = run.Error();
        Assert.Equal("usage", error.GetProperty("code").GetString());
        Assert.Equal("--from 'yesterday' is not a version: give published, or a version (456 or 123_456), as `opticli versions` shows them.",
            error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("set")]
    [InlineData("area")]
    public async Task Set_and_area_document_from_next_to_base_version(string command)
    {
        var run = await Opticli.RunAsync(command, "--help");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("--from <version|published>", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("--base-version <version>", run.Stdout, StringComparison.Ordinal);
    }
}
