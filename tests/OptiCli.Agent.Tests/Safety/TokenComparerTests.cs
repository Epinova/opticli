using OptiCli.Agent.Safety;

namespace OptiCli.Agent.Tests.Safety;

public class TokenComparerTests
{
    private const string Expected = "3f9a0c61d2b84e7a9c55e0b1d7a2f6c4";

    [Fact]
    public void Accepts_the_exact_token() => Assert.True(TokenComparer.Matches(Expected, Expected));

    [Theory]
    [InlineData("3f9a0c61d2b84e7a9c55e0b1d7a2f6c5")]
    [InlineData("3F9A0C61D2B84E7A9C55E0B1D7A2F6C4")]
    [InlineData("3f9a0c61d2b84e7a9c55e0b1d7a2f6c")]
    [InlineData("3f9a0c61d2b84e7a9c55e0b1d7a2f6c44")]
    [InlineData(" 3f9a0c61d2b84e7a9c55e0b1d7a2f6c4")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_anything_else(string? given) => Assert.False(TokenComparer.Matches(given, Expected));

    [Fact]
    public void An_empty_expected_token_matches_nothing() => Assert.False(TokenComparer.Matches("", ""));
}
