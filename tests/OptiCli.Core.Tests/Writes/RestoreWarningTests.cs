using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

/// <summary>What <c>restore</c> says about published content: only a page has a URL to be live at.</summary>
public class RestoreWarningTests
{
    [Theory]
    [InlineData(true, false, "123 has a published version, so it is live again, at its URL below 45.")]
    [InlineData(true, true, "123 has a published version: once restored it is live again, at its URL below 45.")]
    [InlineData(false, false, "123 has a published version, so it is published again, and shows wherever it is used.")]
    [InlineData(false, true, "123 has a published version: once restored it is published again, and shows wherever it is used.")]
    public void Only_a_page_is_said_to_be_live_at_its_url(bool page, bool dryRun, string expected)
    {
        Assert.Equal(expected, WriteExecutor.RestoredLiveWarning("123", page, "45", dryRun));
    }
}
