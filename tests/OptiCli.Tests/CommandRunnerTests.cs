using System.CommandLine;
using System.Text.RegularExpressions;
using OptiCli.Cli;
using OptiCli.Core.Errors;

namespace OptiCli.Tests;

/// <summary>How <see cref="CommandRunner"/> turns a command body's result or exception into output and an exit code.</summary>
[Collection(ConsoleCollection.Name)]
public partial class CommandRunnerTests
{
    public static TheoryData<ErrorCode> ErrorCodes()
    {
        var data = new TheoryData<ErrorCode>();
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            data.Add(code);
        }
        return data;
    }

    [Fact]
    public void The_readme_exit_code_table_lists_every_error_code_once()
    {
        var table = ReadmeExitCodes();

        Assert.Equal(
            Enum.GetValues<ErrorCode>().Select(ExitCodes.Name).Order(StringComparer.Ordinal),
            table.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ErrorCodes))]
    public async Task Each_error_code_exits_as_the_readme_says(ErrorCode code)
    {
        var name = ExitCodes.Name(code);
        Assert.True(ReadmeExitCodes().TryGetValue(name, out var expected), $"README.md's exit code table has no `{name}`.");
        if (code == ErrorCode.NeedsSelection && DatabasePrompt.CanAsk)
        {
            // On a real terminal CommandRunner asks which database to use instead of failing; never under a test runner.
            return;
        }

        var run = await Throwing(OptiCliException.Create(code, "Probe failed.", "Probe hint.", new { reason = "probe" }));

        Assert.Equal(expected, run.ExitCode);
        var error = run.Error();
        Assert.Equal(name, error.GetProperty("code").GetString());
        Assert.Equal("Probe failed.", error.GetProperty("message").GetString());
        Assert.Equal("Probe hint.", error.GetProperty("hint").GetString());
        Assert.Equal("probe", error.GetProperty("details").GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException), "not_found")]
    [InlineData(typeof(DirectoryNotFoundException), "not_found")]
    [InlineData(typeof(IOException), "usage")]
    [InlineData(typeof(UnauthorizedAccessException), "usage")]
    [InlineData(typeof(InvalidOperationException), "internal")]
    [InlineData(typeof(NullReferenceException), "internal")]
    public async Task Other_exceptions_map_to_a_code_and_its_readme_exit_code(Type type, string name)
    {
        var run = await Throwing((Exception)Activator.CreateInstance(type, "Probe failed.")!);

        Assert.Equal(ReadmeExitCodes()[name], run.ExitCode);
        Assert.Equal(name, run.Error().GetProperty("code").GetString());
        Assert.Contains("Probe failed.", run.Error().GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_exception_names_its_type_and_asks_for_a_report()
    {
        var run = await Throwing(new InvalidOperationException("Probe failed."));

        Assert.Equal("InvalidOperationException: Probe failed.", run.Error().GetProperty("message").GetString());
        Assert.Contains("bug in opticli", run.Error().GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ctrl_c_while_a_command_runs_is_cancelled_with_its_readme_exit_code()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var run = await Opticli.RunCommandAsync(
            (command, options) => CommandRunner.SetHandler(command, options, (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new CommandResult("not reached"));
            }),
            ["--json"],
            cancel.Token);

        Assert.Equal(ReadmeExitCodes()["cancelled"], run.ExitCode);
        Assert.Equal("cancelled", run.Error().GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_timeout_nobody_asked_for_is_an_internal_error_not_a_cancellation()
    {
        var run = await Throwing(new TaskCanceledException("The request timed out."));

        Assert.Equal(ReadmeExitCodes()["internal"], run.ExitCode);
        Assert.Equal("internal", run.Error().GetProperty("code").GetString());
    }

    [Fact]
    public async Task Json_forces_the_success_envelope()
    {
        var run = await Returning(new CommandResult(new[] { new { Name = "Start", Count = 2 } }, Next: "50", Warnings: ["capped"], Source: CommandResult.CliSource), "--json");

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Stderr);
        var json = run.Json();
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("Start", json.GetProperty("data")[0].GetProperty("name").GetString());
        var meta = json.GetProperty("meta");
        Assert.Equal("cli", meta.GetProperty("source").GetString());
        Assert.Equal(ToolInfo.Version, meta.GetProperty("version").GetString());
        Assert.Equal("50", meta.GetProperty("next").GetString());
        Assert.Equal("capped", meta.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Text_forces_tables_with_warnings_on_stderr()
    {
        var run = await Returning(new CommandResult(new[] { new { Name = "Start", Count = 2 } }, Next: "50", Warnings: ["capped"]), "--text");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Start", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("(more: add --cursor 50)", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ok\"", run.Stdout, StringComparison.Ordinal);
        Assert.Equal("warning: capped", run.Stderr.TrimEnd());
    }

    [Fact]
    public async Task A_text_rendering_replaces_the_table_only_in_text_mode()
    {
        var result = new CommandResult(new { Name = "Start" }, Text: "custom rendering\n");

        Assert.Equal("custom rendering\n", (await Returning(result, "--text")).Stdout);
        Assert.Equal("Start", (await Returning(result, "--json")).Json().GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Raw_output_is_printed_as_is_in_every_format()
    {
        var result = new CommandResult(null, Raw: "export A=1\n");

        Assert.Equal("export A=1\n", (await Returning(result, "--json")).Stdout);
        Assert.Equal("export A=1\n", (await Returning(result, "--text")).Stdout);
    }

    [Fact]
    public async Task Conflicting_format_options_fail_before_the_body_runs()
    {
        var ran = false;
        var run = await Opticli.RunCommandAsync(
            (command, options) => CommandRunner.SetHandler(command, options, (_, _) =>
            {
                ran = true;
                return Task.FromResult(new CommandResult(null));
            }),
            ["--json", "--text"]);

        Assert.False(ran);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
    }

    /// <summary>The exit code table in README.md ("Output and exit codes"): error code name to exit code.</summary>
    private static Dictionary<string, int> ReadmeExitCodes()
    {
        var readme = File.ReadAllText(Path.Combine(Opticli.RepositoryRoot(), "README.md"));
        var section = readme.IndexOf("## Output and exit codes", StringComparison.Ordinal);
        Assert.True(section >= 0, "README.md has no \"Output and exit codes\" section.");
        var next = readme.IndexOf("\n## ", section + 1, StringComparison.Ordinal);
        var text = readme[section..(next < 0 ? readme.Length : next)];

        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match row in TableRow().Matches(text))
        {
            var exit = int.Parse(row.Groups["exit"].Value, System.Globalization.CultureInfo.InvariantCulture);
            foreach (Match code in Code().Matches(row.Groups["codes"].Value))
            {
                Assert.True(table.TryAdd(code.Groups[1].Value, exit), $"README.md lists `{code.Groups[1].Value}` twice.");
            }
        }
        Assert.NotEmpty(table);
        return table;
    }

    private static Task<Run> Throwing(Exception exception) =>
        Opticli.RunCommandAsync((command, options) => CommandRunner.SetHandler(command, options, (_, _) => throw exception), ["--json"]);

    private static Task<Run> Returning(CommandResult result, string format) =>
        Opticli.RunCommandAsync((command, options) => CommandRunner.SetHandler(command, options, (_, _) => Task.FromResult(result)), [format]);

    [GeneratedRegex(@"^\|\s*(?<exit>\d+)\s*\|(?<codes>[^|]*)\|", RegexOptions.Multiline)]
    private static partial Regex TableRow();

    [GeneratedRegex("`([a-z_]+)`")]
    private static partial Regex Code();
}
