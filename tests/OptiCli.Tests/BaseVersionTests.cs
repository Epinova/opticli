using OptiCli.Cli;
using OptiCli.Core.Errors;

namespace OptiCli.Tests;

/// <summary><c>--base-version</c> takes a version as opticli prints it, catalog content's (<c>1_33_CatalogContent</c>) too.</summary>
public class BaseVersionTests
{
    [Theory]
    [InlineData("456", 456)]
    [InlineData("123_456", 456)]
    [InlineData("1_33_CatalogContent", 33)]
    [InlineData("-2147483646_2_CatalogContent", 2)]
    [InlineData(" 1073741825_7_CatalogContent ", 7)]
    public void A_version_is_taken_as_it_is_printed(string value, int version) =>
        Assert.Equal(version, WriteOptions.ParseVersion(value, "--base-version"));

    [Theory]
    [InlineData("1__CatalogContent")]
    [InlineData("CatalogContent")]
    [InlineData("1_x_CatalogContent")]
    [InlineData("yesterday")]
    public void Anything_else_is_a_usage_error(string value) =>
        Assert.Throws<UsageException>(() => WriteOptions.ParseVersion(value, "--base-version"));
}
