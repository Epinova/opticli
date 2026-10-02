using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Content;

public class RefSyntaxTests
{
    [Theory]
    [InlineData("123", 123, null)]
    [InlineData(" 123 ", 123, null)]
    [InlineData("123_456", 123, 456)]
    public void Parses_ids_and_versions(string input, int id, int? version)
    {
        Assert.True(RefSyntax.TryParse(input, out var parsed));
        Assert.Equal(new ParsedRef(id, version, null), parsed);
    }

    [Fact]
    public void Parses_guids()
    {
        var guid = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000001");

        Assert.True(RefSyntax.TryParse(guid.ToString("D"), out var dashed));
        Assert.True(RefSyntax.TryParse(guid.ToString("N"), out var compact));
        Assert.Equal(guid, dashed.Guid);
        Assert.Equal(guid, compact.Guid);
    }

    [Theory]
    [InlineData("63__dam-assets-provider", 63, "dam-assets-provider")]
    [InlineData(" 7__dam ", 7, "dam")]
    public void Parses_content_provider_refs(string input, int id, string provider)
    {
        Assert.True(RefSyntax.TryParse(input, out var parsed));
        Assert.Equal(new ParsedRef(id, null, null, provider), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("123_")]
    [InlineData("_456")]
    [InlineData("123_0")]
    [InlineData("123_456_789")]
    [InlineData("99999999999")]
    [InlineData("12a")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("/en/about/")]
    [InlineData("63_5__dam")]
    [InlineData("0__dam")]
    [InlineData("63__")]
    [InlineData("63___dam")]
    public void Rejects_everything_else(string? input) => Assert.False(RefSyntax.TryParse(input, out _));
}
