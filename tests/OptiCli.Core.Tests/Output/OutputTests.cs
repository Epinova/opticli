using System.Text.Json;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;

namespace OptiCli.Core.Tests.Output;

public class OutputTests
{
    private sealed record Item(string Name, int Count, bool Enabled, string? Missing = null);

    private enum Colour { DarkBlue }

    [Fact]
    public void Success_envelope_is_compact_camel_case_json()
    {
        var (writer, stdout, _) = Create(OutputFormat.Json);

        var exit = writer.Success(new[] { new Item("Teaser", 2, true) }, new Meta("db", "1.2.3", "50"));

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal(
            """{"ok":true,"data":[{"name":"Teaser","count":2,"enabled":true}],"meta":{"source":"db","version":"1.2.3","next":"50"}}""",
            stdout.ToString().TrimEnd());
    }

    [Theory]
    [InlineData(ErrorCode.Usage, "usage", 1)]
    [InlineData(ErrorCode.NotFound, "not_found", 2)]
    [InlineData(ErrorCode.Refused, "refused", 3)]
    [InlineData(ErrorCode.Unreachable, "unreachable", 4)]
    [InlineData(ErrorCode.Conflict, "conflict", 5)]
    [InlineData(ErrorCode.Validation, "validation", 5)]
    [InlineData(ErrorCode.Internal, "internal", 1)]
    public void Error_envelope_carries_code_message_hint_and_exit_code(ErrorCode code, string name, int exitCode)
    {
        var (writer, stdout, stderr) = Create(OutputFormat.Json);

        var exit = writer.Failure(code, "Something failed.", "Do this next.");

        Assert.Equal(exitCode, exit);
        Assert.Empty(stderr.ToString());
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(name, error.GetProperty("code").GetString());
        Assert.Equal("Something failed.", error.GetProperty("message").GetString());
        Assert.Equal("Do this next.", error.GetProperty("hint").GetString());
        Assert.False(json.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void Typed_exceptions_map_to_their_exit_codes()
    {
        var (writer, _, _) = Create(OutputFormat.Json);

        Assert.Equal(1, writer.Failure(new UsageException("x")));
        Assert.Equal(2, writer.Failure(new NotFoundException("x")));
        Assert.Equal(3, writer.Failure(new RefusedException("x")));
        Assert.Equal(4, writer.Failure(new UnreachableException("x")));
        Assert.Equal(5, writer.Failure(new ConflictException("x")));
    }

    [Fact]
    public void Text_errors_go_to_stderr()
    {
        var (writer, stdout, stderr) = Create(OutputFormat.Text);

        writer.Failure(new NotFoundException("No content type 'Foo'.", "Did you mean Bar?"));

        Assert.Empty(stdout.ToString());
        Assert.Contains("error (not_found): No content type 'Foo'.", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("hint: Did you mean Bar?", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false, true, OutputFormat.Json)]
    [InlineData(false, false, false, false, OutputFormat.Text)]
    [InlineData(true, false, false, false, OutputFormat.Json)]
    [InlineData(false, false, true, true, OutputFormat.Text)]
    [InlineData(false, true, false, false, OutputFormat.JsonLines)]
    [InlineData(false, true, false, true, OutputFormat.JsonLines)]
    public void Format_follows_redirection_unless_forced(bool json, bool jsonLines, bool text, bool redirected, OutputFormat expected)
    {
        Assert.Equal(expected, OutputFormats.Select(json, jsonLines, text, redirected));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public void Combining_format_flags_is_a_usage_error(bool json, bool jsonLines, bool text)
    {
        Assert.Throws<UsageException>(() => OutputFormats.Select(json, jsonLines, text, false));
    }

    [Fact]
    public void Json_lines_write_one_item_per_line_and_no_meta_line_for_a_complete_list()
    {
        var (writer, stdout, _) = Create(OutputFormat.JsonLines);

        writer.Success(new[] { new Item("Teaser", 2, true), new Item("ArticlePage", 10, false) }, new Meta("db", "1"));

        Assert.Equal(
            [
                """{"name":"Teaser","count":2,"enabled":true}""",
                """{"name":"ArticlePage","count":10,"enabled":false}""",
            ],
            stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Json_lines_end_with_a_meta_line_when_there_is_a_next_page_or_a_warning()
    {
        var (writer, stdout, _) = Create(OutputFormat.JsonLines);

        writer.Success(new[] { new Item("Teaser", 2, true) }, new Meta("db", "1", "50", ["capped"]));

        var lines = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("""{"meta":{"source":"db","version":"1","next":"50","warnings":["capped"]}}""", lines[1]);
    }

    [Fact]
    public void Json_lines_errors_are_the_one_line_error_envelope_on_stdout()
    {
        var (writer, stdout, stderr) = Create(OutputFormat.JsonLines);

        Assert.Equal(2, writer.Failure(new NotFoundException("No content 123.")));

        Assert.Empty(stderr.ToString());
        Assert.Equal("""{"ok":false,"error":{"code":"not_found","message":"No content 123."}}""", stdout.ToString().TrimEnd());
    }

    [Fact]
    public void Enums_serialise_as_camel_case_strings_and_non_ascii_stays_readable()
    {
        Assert.Equal("""{"colour":"darkBlue","name":"Blåbær <b>"}""", JsonOutput.Serialize(new { Colour = Colour.DarkBlue, Name = "Blåbær <b>" }));
    }

    [Fact]
    public void Text_renders_lists_of_objects_as_tables()
    {
        var (writer, stdout, _) = Create(OutputFormat.Text);

        writer.Success(new[] { new Item("Teaser", 2, true), new Item("ArticlePage", 10, false) }, new Meta("db", "1", "2"));

        var lines = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("name         count  enabled", lines[0]);
        Assert.Equal("-----------  -----  -------", lines[1]);
        Assert.Equal("Teaser       2      yes", lines[2]);
        Assert.Equal("ArticlePage  10     no", lines[3]);
        Assert.Equal("(more: add --cursor 2)", lines[4]);
    }

    [Fact]
    public void Text_renders_objects_as_aligned_keys_with_nested_sections()
    {
        var (writer, stdout, _) = Create(OutputFormat.Text);

        writer.Success(new
        {
            Name = "ArticlePage",
            Instances = 3,
            Tags = new[] { "a", "b" },
            Properties = new[] { new { Name = "Heading", Type = "String" } },
            Empty = Array.Empty<string>(),
        }, new Meta("db", "1"));

        Assert.Equal(
            string.Join(Environment.NewLine,
                "name:      ArticlePage",
                "instances: 3",
                "tags:      a, b",
                "properties:",
                "  name     type",
                "  -------  ------",
                "  Heading  String",
                "empty:     (none)",
                ""),
            stdout.ToString());
    }

    [Fact]
    public void Paging_returns_a_cursor_until_the_last_page()
    {
        var items = Enumerable.Range(1, 5).ToList();

        var first = Paging.Apply(items, 2, null);
        var second = Paging.Apply(items, 2, first.Next);
        var last = Paging.Apply(items, 2, second.Next);

        Assert.Equal([1, 2], first.Items);
        Assert.Equal([3, 4], second.Items);
        Assert.Equal([5], last.Items);
        Assert.Null(last.Next);
        Assert.Equal(Paging.DefaultLimit, Paging.Apply(Enumerable.Range(1, 80).ToList(), null, null).Items.Count);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(10, "abc")]
    [InlineData(10, "-1")]
    [InlineData(10, "99")]
    public void Paging_rejects_bad_limits_and_cursors(int limit, string? cursor)
    {
        Assert.Throws<UsageException>(() => Paging.Apply(Enumerable.Range(1, 5).ToList(), limit, cursor));
    }

    private static (OutputWriter Writer, StringWriter Stdout, StringWriter Stderr) Create(OutputFormat format)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        return (new OutputWriter(format, stdout, stderr), stdout, stderr);
    }
}
