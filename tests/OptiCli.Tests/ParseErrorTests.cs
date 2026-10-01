namespace OptiCli.Tests;

/// <summary>Parse errors through opticli's real entry point and command tree.</summary>
[Collection(ConsoleCollection.Name)]
public class ParseErrorTests
{
    [Fact]
    public async Task A_parse_error_is_an_error_envelope_on_redirected_stdout()
    {
        var run = await Opticli.RunProcessAsync("get");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stderr);
        Assert.Single(run.Stdout.TrimEnd().Split('\n'));
        var json = run.Json();
        Assert.False(json.GetProperty("ok").GetBoolean());
        var error = json.GetProperty("error");
        Assert.Equal("usage", error.GetProperty("code").GetString());
        Assert.Contains("Run `opticli get --help` for usage.", error.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_option_is_an_error_envelope_on_redirected_stdout()
    {
        var run = await Opticli.RunProcessAsync("db", "list", "--no-such-option");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stderr);
        var error = run.Error();
        Assert.Equal("usage", error.GetProperty("code").GetString());
        Assert.Contains("--no-such-option", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("opticli db list --help", error.GetProperty("hint").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_sends_a_parse_error_to_stderr_even_when_stdout_is_redirected()
    {
        var run = await Opticli.RunProcessAsync("get", "--text");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.StartsWith("error (usage): ", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("hint: Run `opticli get --help` for usage.", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mistyped_command_gets_a_did_you_mean_hint()
    {
        var run = await Opticli.RunAsync("tre", "--json");

        Assert.Equal(1, run.ExitCode);
        var hint = run.Error().GetProperty("hint").GetString();
        Assert.Contains("tree", hint, StringComparison.Ordinal);
        Assert.Contains("Run `opticli --help` for usage.", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mistyped_subcommand_names_its_parent_in_the_hint()
    {
        var run = await Opticli.RunAsync("db", "lst", "--json");

        Assert.Equal(1, run.ExitCode);
        var hint = run.Error().GetProperty("hint").GetString();
        Assert.Contains("list", hint, StringComparison.Ordinal);
        Assert.Contains("Run `opticli db --help` for usage.", hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jsonl_on_a_command_that_returns_no_list_is_a_usage_error()
    {
        var run = await Opticli.RunAsync("get", "123", "--jsonl", "--json");

        Assert.Equal(1, run.ExitCode);
        var error = run.Error();
        Assert.Equal("usage", error.GetProperty("code").GetString());
        Assert.Contains("--jsonl only applies to commands that return a list, not to `opticli get`", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_forces_the_envelope_for_a_parse_error()
    {
        var run = await Opticli.RunAsync("get", "--json");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stderr);
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
    }

    [Fact]
    public async Task Text_forces_text_for_a_parse_error()
    {
        var run = await Opticli.RunAsync("get", "--text");

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Stdout);
        Assert.StartsWith("error (usage): ", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_and_text_together_are_a_usage_error_in_json()
    {
        // doctor's action reports the conflict before it looks for a project.
        var run = await Opticli.RunAsync("doctor", "--json", "--text");

        Assert.Equal(1, run.ExitCode);
        var error = run.Error();
        Assert.Equal("usage", error.GetProperty("code").GetString());
        Assert.Equal("Pass only one of --json, --jsonl and --text.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_bare_opticli_prints_the_overview_instead_of_an_error()
    {
        var run = await Opticli.RunProcessAsync();

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Usage:", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("Exit codes:", run.Stdout, StringComparison.Ordinal);
        Assert.Empty(run.Stderr);
    }
}
