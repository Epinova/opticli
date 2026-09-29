using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class AreaItemArgumentTests
{
    [Theory]
    [InlineData("0", 0, null)]
    [InlineData("12", 12, null)]
    [InlineData("ref:123", null, "123")]
    [InlineData("REF:123", null, "123")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", null, "0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("/en/teasers/news/", null, "/en/teasers/news/")]
    public void Numbers_are_positions_and_anything_else_names_content(string value, int? index, string? reference)
    {
        Assert.Equal((index, reference), AreaItemArgument.Parse(value));
    }

    [Theory]
    [InlineData("ref:")]
    [InlineData("99999999999")]
    public void Invalid_values_are_usage_errors(string value)
    {
        Assert.Throws<UsageException>(() => AreaItemArgument.Parse(value));
    }
}
