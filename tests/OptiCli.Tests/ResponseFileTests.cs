using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Tests;

/// <summary>
/// System.CommandLine's response files are off: a token starting with <c>@</c> is a value, not a file to splice in.
/// <c>Name=@file</c> property values are opticli's own and still read the file.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ResponseFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("opticli-at-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ParseResult Parse(params string[] args) => Program.Parse(Program.CreateRoot(new GlobalOptions()), args);

    private static IReadOnlyList<string> Tokens(ParseResult parse) => [.. parse.CommandResult.Tokens.Select(t => t.Value)];

    [Fact]
    public void An_argument_starting_with_at_is_plain_text()
    {
        var parse = Parse("search", "@alloy");

        Assert.Empty(parse.Errors);
        Assert.Equal(["@alloy"], Tokens(parse));
    }

    [Fact]
    public void An_existing_file_named_after_at_is_not_read()
    {
        // As a response file, this would turn `get @file` into `get --help`.
        var file = Path.Combine(_dir, "args.rsp");
        File.WriteAllText(file, "--help");

        var parse = Parse("get", "@" + file);

        Assert.Empty(parse.Errors);
        Assert.Equal(["@" + file], Tokens(parse));
    }

    [Fact]
    public void An_option_value_starting_with_at_is_plain_text()
    {
        var parse = Parse("create", "1", "--type", "StandardPage", "--name", "@Home");

        Assert.Empty(parse.Errors);
        var name = parse.CommandResult.Command.Options.Single(o => o.Name == "--name");
        Assert.Equal("@Home", parse.GetValue((Option<string>)name));
    }

    [Fact]
    public void A_property_value_from_a_file_still_reads_the_file()
    {
        File.WriteAllText(Path.Combine(_dir, "body.html"), "<p>Hi</p>");

        var parse = Parse("set", "1", "MainBody=@body.html", "Handle=@@example");

        Assert.Empty(parse.Errors);
        var properties = PropertyArguments.Parse(Tokens(parse).Skip(1), null, _dir);
        Assert.Equal("<p>Hi</p>", (string?)properties["MainBody"]);
        Assert.Equal("@example", (string?)properties["Handle"]);
    }

    [Fact]
    public async Task A_missing_file_after_at_is_no_usage_error_through_the_entry_point()
    {
        // v0.33.0: `usage: Response file not found '…'`.
        var run = await Opticli.RunProcessAsync("get", "@no-such-file", "--json");

        Assert.DoesNotContain("Response file", run.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("usage", run.Error().GetProperty("code").GetString());
    }
}
