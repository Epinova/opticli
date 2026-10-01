using System.CommandLine;
using System.Text.RegularExpressions;
using OptiCli.Cli;

namespace OptiCli.Tests;

/// <summary>The real command tree: every command's help, and the options the bundled skill tells agents to use.</summary>
[Collection(ConsoleCollection.Name)]
public partial class CommandTreeTests
{
    private static readonly RootCommand Root = Program.CreateRoot(new GlobalOptions());

    public static TheoryData<string> CommandPaths()
    {
        var data = new TheoryData<string>();
        foreach (var path in Paths(Root, []))
        {
            data.Add(string.Join(' ', path));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CommandPaths))]
    public async Task Every_command_prints_its_help(string path)
    {
        var run = await Opticli.RunAsync([.. path.Split(' ', StringSplitOptions.RemoveEmptyEntries), "--help"]);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Empty(run.Stderr);
        // In this process the root command is named after the test host, not opticli.
        Assert.Matches($@"Usage:\s+\S+{Regex.Escape(path.Length == 0 ? "" : " " + path)} ", run.Stdout);
    }

    [Fact]
    public void Every_command_has_a_description()
    {
        var missing = Paths(Root, []).Select(p => (Path: string.Join(' ', p), Command: Find(p)))
            .Where(c => string.IsNullOrWhiteSpace(c.Command.Description))
            .Select(c => c.Path);

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("SKILL.md")]
    [InlineData("reference.md")]
    public void Every_option_the_skill_mentions_exists_on_its_command(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(Opticli.RepositoryRoot(), "skill", file));
        var problems = new List<string>();
        var checkedCount = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var span in CodeSpans(lines[i]))
            {
                foreach (var invocation in Invocations(span))
                {
                    checkedCount++;
                    problems.AddRange(Check(invocation).Select(p => $"{file}:{i + 1}: `{span}`: {p}"));
                }
            }
        }

        Assert.True(checkedCount > 10, $"Only {checkedCount} commands found in {file}; is the parsing broken?");
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void Every_option_in_a_commands_help_examples_exists_on_its_command()
    {
        var problems = new List<string>();
        foreach (var path in Paths(Root, []))
        {
            foreach (var line in (Find(path).Description ?? "").Split('\n'))
            {
                // Prose quotes commands in backticks; examples follow "Example:" or start their line.
                var spans = CodeSpans(line).ToList();
                if (ExampleLine().Match(line) is { Success: true } example
                    && (example.Groups["label"].Success || Root.Subcommands.Any(c => c.Name == example.Groups["command"].Value)))
                {
                    spans.Add(example.Groups["example"].Value);
                }
                foreach (var invocation in spans.SelectMany(Invocations))
                {
                    problems.AddRange(Check(invocation).Select(p => $"opticli {string.Join(' ', path)} --help: `{line.Trim()}`: {p}"));
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void The_skill_check_catches_an_option_that_does_not_exist()
    {
        Assert.Empty(Check("opticli tree 123 --depth 1"));
        Assert.Empty(Check("serve --status | --logs [--tail N] | --stop"));
        Assert.Single(Check("opticli tree 123 --deep 1"));
        Assert.Single(Check("opticli db use --tail 3"));
        Assert.Single(Check("opticli no-such-command"));
    }

    /// <summary>
    /// The text after each <c>opticli</c> in <paramref name="text"/> up to the next one, or all of it when it starts with
    /// a command name (the tables in reference.md leave out <c>opticli</c>). Arguments to <c>dotnet</c> (installing
    /// the tool) carry dotnet's own options, not opticli's.
    /// </summary>
    private static IEnumerable<string> Invocations(string text)
    {
        if (text.Contains("dotnet ", StringComparison.Ordinal))
        {
            yield break;
        }
        var parts = OpticliWord().Split(text);
        if (parts.Length > 1)
        {
            foreach (var part in parts.Skip(1))
            {
                yield return part;
            }
        }
        else if (FirstWord().Match(text) is { Success: true } first && Root.Subcommands.Any(c => c.Name == first.Value))
        {
            yield return text;
        }
    }

    /// <summary>Resolves the leading command words of <paramref name="invocation"/> and checks each <c>--option</c> against that command.</summary>
    private static List<string> Check(string invocation)
    {
        var text = invocation.StartsWith("opticli ", StringComparison.Ordinal) ? invocation["opticli ".Length..] : invocation;
        var tokens = text.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var path = new List<Command> { Root };
        var index = 0;
        for (; index < tokens.Length; index++)
        {
            var next = path[^1].Subcommands.FirstOrDefault(c => c.Name == tokens[index]);
            if (next is null)
            {
                break;
            }
            path.Add(next);
        }

        var problems = new List<string>();
        var command = path[^1];
        if (path.Count == 1 && index < tokens.Length && CommandWord().IsMatch(tokens[index]))
        {
            problems.Add($"`{tokens[index]}` is not an opticli command");
        }
        if (index < tokens.Length && command.Subcommands.Count > 0 && path.Count > 1 && CommandWord().IsMatch(tokens[index]))
        {
            problems.Add($"`{tokens[index]}` is not a subcommand of `{Usage(path)}`");
        }

        var known = command.Options.Concat(path.SelectMany(c => c.Options).Where(o => o.Recursive))
            .SelectMany(o => o.Aliases.Prepend(o.Name))
            .ToHashSet(StringComparer.Ordinal);
        foreach (Match option in OptionName().Matches(string.Join(' ', tokens.Skip(index))))
        {
            if (!known.Contains(option.Value))
            {
                problems.Add($"{option.Value} is not an option of `{Usage(path)}`");
            }
        }
        return problems;
    }

    private static string Usage(IEnumerable<Command> path) => string.Join(' ', path.Select(c => c == Root ? "opticli" : c.Name));

    /// <summary>Inline code spans (the skill's fenced blocks are JSON, not commands).</summary>
    private static IEnumerable<string> CodeSpans(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) ? [] : InlineCode().Matches(line).Select(m => m.Groups[1].Value);

    private static IEnumerable<List<string>> Paths(Command command, List<string> prefix)
    {
        yield return prefix;
        foreach (var subcommand in command.Subcommands)
        {
            foreach (var path in Paths(subcommand, [.. prefix, subcommand.Name]))
            {
                yield return path;
            }
        }
    }

    private static Command Find(List<string> path) =>
        path.Aggregate((Command)Root, (command, name) => command.Subcommands.Single(c => c.Name == name));

    [GeneratedRegex(@"(?<![\w-])opticli\s+")]
    private static partial Regex OpticliWord();

    [GeneratedRegex(@"^[a-z][a-z-]*(?=\s|$)")]
    private static partial Regex FirstWord();

    [GeneratedRegex(@"^[a-z][a-z-]*$")]
    private static partial Regex CommandWord();

    [GeneratedRegex(@"(?<![\w-])--[a-z][a-z0-9-]*")]
    private static partial Regex OptionName();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex InlineCode();

    /// <summary>"Example: opticli ...", or a line that is an opticli command (a prose line may start with "opticli" too).</summary>
    [GeneratedRegex(@"^\s*(?<label>Examples?:\s*)?(?<example>opticli\s+(?<command>\S*).*)$")]
    private static partial Regex ExampleLine();
}
