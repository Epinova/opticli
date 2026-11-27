using System.CommandLine;
using OptiCli.Cli;

namespace OptiCli.Tests;

/// <summary>
/// The npm client puts a command's options first and its arguments last, after <c>--</c>, so an argument that starts
/// with <c>-</c> (a name, a search) is never read as an option. Every command with arguments takes that order.
/// </summary>
public sealed class DoubleDashTests
{
    private static readonly GlobalOptions Options = new();
    private static readonly RootCommand Root = Program.CreateRoot(Options);

    public static TheoryData<string> CommandsWithArguments()
    {
        var data = new TheoryData<string>();
        foreach (var (path, _) in Commands(Root, []).Where(c => c.Command.Arguments.Count > 0))
        {
            data.Add(path);
        }
        return data;
    }

    [Fact]
    public void Commands_with_arguments_are_found()
    {
        Assert.True(CommandsWithArguments().Count > 30);
    }

    [Theory]
    [MemberData(nameof(CommandsWithArguments))]
    public void Arguments_after_a_double_dash_are_arguments_even_when_they_start_with_a_dash(string path)
    {
        var command = Commands(Root, []).Single(c => c.Path == path).Command;
        var given = new List<(Argument Argument, string[] Values)>();
        var n = 0;
        foreach (var argument in command.Arguments)
        {
            var count = argument.Arity.MaximumNumberOfValues > 1 ? 2 : 1;
            // The first is an option every command has; the others look like options too.
            given.Add((argument, [.. Enumerable.Range(0, count).Select(_ => n++ == 0 ? "--json" : $"--{argument.Name}-{n}")]));
        }

        var parse = Program.Parse(Root, [.. path.Split(' '), "--text", "--", .. given.SelectMany(g => g.Values)]);

        Assert.Same(command, parse.CommandResult.Command);
        Assert.Empty(parse.UnmatchedTokens);
        Assert.True(parse.GetValue(Options.Text));
        Assert.False(parse.GetValue(Options.Json));
        foreach (var (argument, values) in given)
        {
            Assert.Equal(values, parse.GetResult(argument)?.Tokens.Select(t => t.Value) ?? []);
        }
    }

    [Fact]
    public void No_option_takes_a_double_dash_as_its_value()
    {
        // The npm client adds options (--json, --cursor) before the first `--` of a command line it made.
        var parse = Program.Parse(Root, ["set", "--name", "--", "1"]);

        Assert.NotEmpty(parse.Errors);
    }

    private static IEnumerable<(string Path, Command Command)> Commands(Command command, List<string> prefix)
    {
        if (prefix.Count > 0)
        {
            yield return (string.Join(' ', prefix), command);
        }
        foreach (var subcommand in command.Subcommands)
        {
            foreach (var found in Commands(subcommand, [.. prefix, subcommand.Name]))
            {
                yield return found;
            }
        }
    }
}
