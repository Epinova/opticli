namespace OptiCli.Tests;

/// <summary><c>types remove|remove-property|prune</c> arguments that are wrong before any database or site is asked.</summary>
[Collection(ConsoleCollection.Name)]
public class TypesRemoveCommandTests
{
    [Theory]
    [InlineData("types", "remove")]
    [InlineData("types", "remove-property", "ArticlePage")]
    [InlineData("types", "remove-property", "ArticlePage", " ")]
    [InlineData("types", "prune", "--allow-destructive")]
    [InlineData("types", "remove", "OldPage", "--allow-destructive")]
    public async Task Wrong_arguments_are_usage_errors(params string[] args)
    {
        var run = await Opticli.RunAsync([.. args, "--json"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_types_list_still_takes_its_options()
    {
        var run = await Opticli.RunAsync("types", "--orphaned", "--help");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("remove-property", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("prune", run.Stdout, StringComparison.Ordinal);
    }
}
