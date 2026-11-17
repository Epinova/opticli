using System.Text;
using OptiCli.Cli;

namespace OptiCli.Tests;

/// <summary>
/// Redirected stdin, stdout and stderr are UTF-8, also on Windows, where .NET would otherwise use the console's code
/// page: the npm package, scripts and agents read and write UTF-8. On Linux and macOS these pass either way; the Windows
/// CI job is where they bite.
/// </summary>
public sealed class StandardStreamsTests
{
    [Fact]
    public void A_legacy_code_page_is_replaced_and_utf8_or_a_captured_writer_is_not()
    {
        Assert.True(StandardStreams.NeedsUtf8(Encoding.Latin1));
        Assert.True(StandardStreams.NeedsUtf8(Encoding.ASCII));
        Assert.False(StandardStreams.NeedsUtf8(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
        Assert.False(StandardStreams.NeedsUtf8(Encoding.UTF8));
        // What a test's StringWriter reports: the console it captured must stay captured.
        Assert.False(StandardStreams.NeedsUtf8(new StringWriter().Encoding));
        Assert.False(StandardStreams.NeedsUtf8(Encoding.BigEndianUnicode));
    }

    [Fact]
    public async Task Output_with_characters_outside_ascii_is_utf8_json()
    {
        // The schema's descriptions have arrows (sections → rows → columns): a code page turned them into a control
        // character that broke the JSON.
        var run = await Opticli.RunProcessAsync("schema", "--json");

        Assert.Equal(0, run.ExitCode);
        Assert.True(run.Json().GetProperty("ok").GetBoolean(), run.ToString());
        Assert.Contains("→", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_on_stdin_is_read_as_utf8()
    {
        var run = await Opticli.RunProcessWithInputAsync(
            """{"operations": [{"op": "set", "ref": "$blåbær", "properties": {"Heading": "x"}}]}""",
            "apply", "-", "--dry-run", "--json");

        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
        Assert.Contains("'$blåbær' is not the id", run.Error().GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
