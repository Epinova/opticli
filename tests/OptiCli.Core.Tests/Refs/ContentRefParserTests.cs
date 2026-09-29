using OptiCli.Core.Errors;
using OptiCli.Core.Refs;

namespace OptiCli.Core.Tests.Refs;

public class ContentRefParserTests
{
    [Theory]
    [InlineData("123", 123, null)]
    [InlineData(" 42 ", 42, null)]
    [InlineData("123_456", 123, 456)]
    [InlineData("1", 1, null)]
    public void Parses_ids_and_versions(string input, int id, int? version)
    {
        var parsed = ContentRefParser.Parse(input);

        Assert.Equal(ContentRefKind.Id, parsed.Kind);
        Assert.Equal(id, parsed.Id);
        Assert.Equal(version, parsed.VersionId);
    }

    [Theory]
    [InlineData("0b6f9c4e-3f1a-4d2b-9a8e-1c2d3e4f5a6b")]
    [InlineData("0B6F9C4E-3F1A-4D2B-9A8E-1C2D3E4F5A6B")]
    [InlineData("0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b")]
    [InlineData("{0b6f9c4e-3f1a-4d2b-9a8e-1c2d3e4f5a6b}")]
    [InlineData("~/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx")]
    [InlineData("/link/0b6f9c4e3f1a4d2b9a8e1c2d3e4f5a6b.aspx?epslanguage=en")]
    public void Parses_guids_and_permanent_links(string input)
    {
        var parsed = ContentRefParser.Parse(input);

        Assert.Equal(ContentRefKind.Guid, parsed.Kind);
        Assert.Equal(Guid.Parse("0b6f9c4e-3f1a-4d2b-9a8e-1c2d3e4f5a6b"), parsed.Guid);
    }

    [Theory]
    [InlineData("/en/about/team/", "/en/about/team/")]
    [InlineData("/", "/")]
    [InlineData("~/en/about/", "/en/about/")]
    [InlineData("https://www.example.com/en/about/", "https://www.example.com/en/about/")]
    [InlineData("http://localhost:5000/news?page=2", "http://localhost:5000/news?page=2")]
    public void Parses_urls_and_paths(string input, string expected)
    {
        var parsed = ContentRefParser.Parse(input);

        Assert.Equal(ContentRefKind.Url, parsed.Kind);
        Assert.Equal(expected, parsed.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("123_")]
    [InlineData("_456")]
    [InlineData("123_0")]
    [InlineData("123_456_789")]
    [InlineData("99999999999")]
    [InlineData("abc")]
    [InlineData("en/about")]
    [InlineData("ftp://example.com/file")]
    [InlineData("١٢٣")]
    public void Rejects_everything_else_with_a_usage_error(string input)
    {
        Assert.False(ContentRefParser.TryParse(input, out _, out var error));
        Assert.NotEmpty(error);

        var exception = Assert.Throws<UsageException>(() => ContentRefParser.Parse(input));
        Assert.Equal(ContentRefParser.Syntax, exception.Hint);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("123_456")]
    [InlineData("0b6f9c4e-3f1a-4d2b-9a8e-1c2d3e4f5a6b")]
    [InlineData("/en/about/")]
    public void ToString_round_trips(string input)
    {
        var parsed = ContentRefParser.Parse(input);

        Assert.Equal(parsed, ContentRefParser.Parse(parsed.ToString()));
        Assert.Equal(input, parsed.ToString());
    }
}
