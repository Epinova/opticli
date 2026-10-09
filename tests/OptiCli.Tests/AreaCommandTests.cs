namespace OptiCli.Tests;

/// <summary><c>area add --type</c> (a new inline block) and its arguments, checked before opticli looks for a project.</summary>
[Collection(ConsoleCollection.Name)]
public class AreaCommandTests
{
    [Theory]
    [InlineData("--type only applies to add", "remove", "2", "--type", "TeaserBlock")]
    [InlineData("--values and --name are a new inline block's: give --type too", "add", "456", "--name", "Intro")]
    [InlineData("--values and --name are a new inline block's: give --type too", "add", "456", "--values", "{}")]
    [InlineData("area add takes a block ref or --type (a new inline block), not both ('456')", "add", "456", "--type", "TeaserBlock", "Heading=Hi")]
    [InlineData("area add takes 1 value(s), got 0.", "add")]
    public async Task Inline_block_arguments_that_dont_fit_are_a_usage_error(string message, params string[] args)
    {
        var run = await Opticli.RunAsync(["area", "123", "MainArea", .. args, "--json"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
        Assert.StartsWith(message, run.Error().GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Area_documents_inline_blocks()
    {
        var run = await Opticli.RunAsync("area", "--help");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("--type <type>", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("opticli set <ref> 'MainArea[2].Heading=New'", run.Stdout, StringComparison.Ordinal);
    }
}
