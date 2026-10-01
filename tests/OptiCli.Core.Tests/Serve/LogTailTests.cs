using System.Text;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class LogTailTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private string Log(string content) => _root.Write("site.log", content);

    [Theory]
    [InlineData("a\nb\nc\n", 2, new[] { "b", "c" })]
    [InlineData("a\nb\nc", 2, new[] { "b", "c" })]
    [InlineData("a\r\nb\r\nc\r\n", 2, new[] { "b", "c" })]
    [InlineData("a\nb\n", 10, new[] { "a", "b" })]
    [InlineData("a\n\nb\n", 2, new[] { "", "b" })]
    [InlineData("only", 1, new[] { "only" })]
    [InlineData("", 3, new string[0])]
    public void Returns_the_last_lines(string content, int count, string[] expected)
    {
        Assert.Equal(expected, LogTail.Read(Log(content), count));
    }

    [Fact]
    public void A_long_log_is_read_from_the_end_across_chunks()
    {
        // Lines with multi-byte characters, well over one 64 KiB chunk.
        var lines = Enumerable.Range(1, 20_000).Select(i => $"line {i} æøå").ToList();
        var path = Log(string.Join("\n", lines) + "\n");

        Assert.Equal(lines[^3..], LogTail.Read(path, 3));
        Assert.Equal(lines[^5000..], LogTail.Read(path, 5000));
        Assert.Equal(lines, LogTail.Read(path, 50_000));
    }

    [Fact]
    public void A_missing_log_or_no_lines_asked_for_is_empty()
    {
        Assert.Empty(LogTail.Read(_root.Combine("missing.log"), 10));
        Assert.Empty(LogTail.Read(Log("a\n"), 0));
    }

    [Fact]
    public void A_byte_order_mark_is_not_part_of_the_first_line()
    {
        var path = _root.Combine("bom.log");
        File.WriteAllText(path, "first\nsecond\n", new UTF8Encoding(true));

        Assert.Equal(["first", "second"], LogTail.Read(path, 5));
    }
}
