namespace OptiCli.Tests;

/// <summary><c>sites primary</c> and <c>sites host</c> arguments that are wrong before any database or site is asked.</summary>
[Collection(ConsoleCollection.Name)]
public class SitesCommandTests
{
    [Theory]
    [InlineData("sites", "primary")]
    [InlineData("sites", "primary", "Site A=localhost:5001", "--from-config")]
    [InlineData("sites", "primary", "--from-config", "--save")]
    [InlineData("sites", "primary", "Site A=localhost:5001", "--https", "maybe")]
    [InlineData("sites", "primary", "--forget", "Old site", "Site A=localhost:5001")]
    [InlineData("sites", "primary", "--forget", "Old site", "--dry-run")]
    [InlineData("sites", "host", "add", "Site A", "localhost:5001", "--type", "main")]
    [InlineData("sites", "host", "add", "Site A", "https://localhost:5001/en/")]
    [InlineData("sites", "host", "add", "Site A", "localhost:99999")]
    [InlineData("sites", "primary", "--forget", "Old site", "--keep-edit")]
    [InlineData("sites", "primary", "--forget", "Old site", "--https", "true")]
    public async Task Wrong_arguments_are_usage_errors(params string[] args)
    {
        var run = await Opticli.RunAsync([.. args, "--json"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_sites_list_still_takes_its_list_options()
    {
        var run = await Opticli.RunAsync("sites", "--limit", "5", "--help");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("primary", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("host", run.Stdout, StringComparison.Ordinal);
    }
}
